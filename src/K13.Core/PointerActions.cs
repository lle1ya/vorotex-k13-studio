namespace Vorotex.K13;

/// <summary>1.3.0: «Прокрутка и курсор» — PC-side actions (SendInput). The K13 mouse collection (COL09: X/Y, vertical wheel,
/// 5 buttons) has no AC Pan / horizontal wheel, and the vendor tool has no such code, so horizontal scrolling can only come from the PC.
/// A keypad button that runs a PC action arrives as a short tap (Scroll Lock + F chord), so there is no release to stop on:
/// <see cref="PointerMode.Step"/> = a fixed amount per press, <see cref="PointerMode.Toggle"/> = press starts, the next press stops.</summary>
public enum PointerKind { Scroll, Move }
public enum PointerMode { Step, Toggle }
public enum PointerDir { Left, Right, Up, Down }

public sealed record PointerSpec(PointerKind Kind, PointerDir Dir, PointerMode Mode, int Amount)
{
    public const int MinAmount = 1, MaxAmount = 10;
    /// <summary>Default: scroll 3 notches per press; cursor 4 (step = 4×10 px, toggle = speed 4).</summary>
    public static int DefaultAmount(PointerKind k) => k == PointerKind.Scroll ? 3 : 4;

    public static string DirKey(PointerDir d) => d switch { PointerDir.Left => "left", PointerDir.Right => "right", PointerDir.Up => "up", _ => "down" };
    public static string Arrow(PointerDir d) => d switch { PointerDir.Left => "←", PointerDir.Right => "→", PointerDir.Up => "↑", _ => "↓" };
    public static string DirWord(PointerDir d) => d switch { PointerDir.Left => "влево", PointerDir.Right => "вправо", PointerDir.Up => "вверх", _ => "вниз" };

    /// <summary>Stored as HostAction: Kind = Scroll / MouseMove, Value = "left|right|up|down", Arguments = "step:3" / "toggle:4".</summary>
    public HostAction ToAction() => new()
    {
        Kind = Kind == PointerKind.Scroll ? HostActionKind.Scroll : HostActionKind.MouseMove,
        Value = DirKey(Dir),
        Arguments = (Mode == PointerMode.Toggle ? "toggle:" : "step:") + Math.Clamp(Amount, MinAmount, MaxAmount),
    };

    public static PointerSpec? From(HostAction? a)
    {
        if (a == null || a.Kind is not (HostActionKind.Scroll or HostActionKind.MouseMove)) return null;
        var kind = a.Kind == HostActionKind.Scroll ? PointerKind.Scroll : PointerKind.Move;
        PointerDir? dir = (a.Value ?? "").Trim().ToLowerInvariant() switch
        {
            "left" => PointerDir.Left, "right" => PointerDir.Right, "up" => PointerDir.Up, "down" => PointerDir.Down, _ => null,
        };
        if (dir is not PointerDir d) return null;
        var args = (a.Arguments ?? "").Trim().ToLowerInvariant();
        var mode = args.StartsWith("toggle", StringComparison.Ordinal) ? PointerMode.Toggle : PointerMode.Step;
        int amount = DefaultAmount(kind);
        int colon = args.IndexOf(':');
        if (colon >= 0 && int.TryParse(args[(colon + 1)..], out int n)) amount = n;
        return new PointerSpec(kind, d, mode, Math.Clamp(amount, MinAmount, MaxAmount));
    }

    public string Title => (Kind == PointerKind.Scroll ? "Прокрутка " : "Курсор ") + DirWord(Dir) +
        (Mode == PointerMode.Toggle ? " (старт/стоп)" : "");

    public string Short => (Kind == PointerKind.Scroll ? "Прокр. " : "Курсор ") + Arrow(Dir);

    public string Details => Kind switch
    {
        PointerKind.Scroll when Mode == PointerMode.Step => $"{Amount} {Plural(Amount, "щелчок", "щелчка", "щелчков")} колеса за нажатие",
        PointerKind.Scroll => $"нажатие — прокрутка идёт ({Amount * PointerMotion.ScrollPerSecondPerAmount} щелчков в секунду), следующее нажатие — стоп",
        _ when Mode == PointerMode.Step => $"{Amount * PointerMotion.StepPxPerAmount} пикселей за нажатие, частые нажатия ускоряют",
        _ => $"нажатие — курсор едет (скорость {Amount}), следующее нажатие — стоп",
    };

    public static string Plural(int n, string one, string few, string many)
    {
        int m = n % 100, d = n % 10;
        return m is >= 11 and <= 14 ? many : d == 1 ? one : d is >= 2 and <= 4 ? few : many;
    }

    /// <summary>Sign of the step: wheel up = +120, horizontal wheel right = +120 (MOUSEEVENTF_HWHEEL), screen Y grows downwards.</summary>
    public (int Dx, int Dy) Unit => Dir switch { PointerDir.Left => (-1, 0), PointerDir.Right => (1, 0), PointerDir.Up => (0, -1), _ => (0, 1) };
}

/// <summary>What the PC sends: relative cursor move and wheel deltas (120 = one notch).</summary>
public readonly record struct PointerOut(int Dx, int Dy, int Wheel, int HWheel)
{
    public bool IsEmpty => Dx == 0 && Dy == 0 && Wheel == 0 && HWheel == 0;
}

/// <summary>Pure state machine for «Прокрутка и курсор» (unit-tested; the app feeds it taps and a ~15 ms timer and sends the result).</summary>
public sealed class PointerMotion
{
    public const int StepPxPerAmount = 10;          // step mode: amount 4 = 40 px
    public const int ScrollPerSecondPerAmount = 4;  // toggle scroll: amount 3 = 12 notches/s
    public const int TickMs = 15;
    /// <summary>Taps of the same step action closer than this accelerate (×2, ×3, ×4).</summary>
    public const int AccelWindowMs = 450;
    public const int MaxAccel = 4;
    /// <summary>Safety: a toggle motion stops by itself after this long (lost «stop» press, the user walked away).</summary>
    public const int MaxRunMs = 20000;

    private PointerSpec? _running;
    private long _startedMs, _lastTickMs;
    private double _scrollAcc, _moveAcc;
    private PointerSpec? _lastStep;
    private long _lastStepMs;
    private int _accel = 1;

    public PointerSpec? Running => _running;
    public bool IsRunning => _running != null;
    public string? LastStopReason { get; private set; }

    public enum TapResult { Stepped, Started, Switched, Stopped }

    /// <summary>A press of a button with this action. Returns what happened and the immediate output (step mode).</summary>
    public (TapResult Result, PointerOut Out) Tap(PointerSpec s, long nowMs)
    {
        if (s.Mode == PointerMode.Step)
        {
            bool fast = _lastStep == s && nowMs - _lastStepMs <= AccelWindowMs;
            _accel = fast ? Math.Min(MaxAccel, _accel + 1) : 1;
            _lastStep = s; _lastStepMs = nowMs;
            var (ux, uy) = s.Unit;
            if (s.Kind == PointerKind.Scroll)   // scrolling is not accelerated: one notch = one row/column in Excel
                return (TapResult.Stepped, new PointerOut(0, 0, -uy * 120 * s.Amount, ux * 120 * s.Amount));
            int px = s.Amount * StepPxPerAmount * _accel;
            return (TapResult.Stepped, new PointerOut(ux * px, uy * px, 0, 0));
        }
        if (_running == s) { Stop("повторное нажатие"); return (TapResult.Stopped, default); }
        bool switched = _running != null;
        _running = s; _startedMs = nowMs; _lastTickMs = nowMs; _scrollAcc = 0; _moveAcc = 0; LastStopReason = null;
        if (s.Kind == PointerKind.Scroll)
        {   // the first notch at once, so a press is felt immediately
            _scrollAcc = 0;
            var (ux, uy) = s.Unit;
            return (switched ? TapResult.Switched : TapResult.Started, new PointerOut(0, 0, -uy * 120, ux * 120));
        }
        return (switched ? TapResult.Switched : TapResult.Started, default);
    }

    /// <summary>Called every ~15 ms while <see cref="IsRunning"/>.</summary>
    public PointerOut Tick(long nowMs)
    {
        if (_running is not { } s) return default;
        if (nowMs - _startedMs >= MaxRunMs) { Stop($"страховка: {MaxRunMs / 1000} с без нажатия «стоп»"); return default; }
        long dt = Math.Max(0, nowMs - _lastTickMs);
        _lastTickMs = nowMs;
        var (ux, uy) = s.Unit;
        if (s.Kind == PointerKind.Scroll)
        {
            _scrollAcc += dt * s.Amount * ScrollPerSecondPerAmount / 1000.0;
            int notches = (int)_scrollAcc;
            if (notches <= 0) return default;
            _scrollAcc -= notches;
            return new PointerOut(0, 0, -uy * 120 * notches, ux * 120 * notches);
        }
        // cursor: speed grows from 1× to 3× during the first 1.5 s (precise start, fast travel)
        double elapsed = nowMs - _startedMs;
        double factor = 1 + Math.Min(2.0, elapsed / 750.0);
        _moveAcc += dt * s.Amount * 0.06 * factor;   // amount 4: ≈240 px/s at start, ≈720 px/s later
        int px = (int)_moveAcc;
        if (px <= 0) return default;
        _moveAcc -= px;
        return new PointerOut(ux * px, uy * px, 0, 0);
    }

    public bool Stop(string reason)
    {
        if (_running == null) return false;
        _running = null; LastStopReason = reason;
        return true;
    }
}
