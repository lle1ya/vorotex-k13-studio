using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Vorotex.K13;

namespace Vorotex.Studio;

/// <summary>
/// Transparent, topmost, non-activating window with the actions ring centred on the mouse cursor.
/// Hold the trigger → point (mouse) or tilt (joystick, 8 directions) → release to run. A quick tap leaves it open
/// for a click. Esc or the centre × cancels. The foreground app keeps focus, so Ctrl+C/Ctrl+V go to it.
/// </summary>
public sealed class RingOverlay : Window, IRingHost
{
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr mon, int type, out uint dx, out uint dy);
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    private volatile int[] _wanted = Array.Empty<int>();
    private const int GWL_EXSTYLE = -20, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8;

    private readonly HostMapper _mapper;
    private RingView _view = new();
    private RingSettings _ring = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(15) };
    private POINT _origin;
    private double _scale = 1;
    private bool _holding, _open, _stickUsed;
    private readonly HashSet<ushort> _stick = new();
    private DateTime _openedAt;

    /// <summary>VK codes of the joystick directions (↑, ↓, ←, →) as the keypad currently sends them.</summary>
    public Func<(ushort Up, ushort Down, ushort Left, ushort Right)> StickKeys { get; set; } = () => (0x26, 0x28, 0x25, 0x27);

    public bool IsOpen => _open;

    public RingOverlay(HostMapper mapper)
    {
        _mapper = mapper;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; ResizeMode = ResizeMode.NoResize;
        Width = RingView.Size; Height = RingView.Size;
        Left = -10000; Top = -10000;
        _timer.Tick += (_, _) => Track();
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST);
        };
    }

    public void TriggerDown(RingSettings ring)
    {
        _triggerAt = DateTime.Now;
        if (_open)
        {   // second press while open (tap mode) = confirm
            Finish(_view.Highlight);
            return;
        }
        _ring = ring;
        _holding = true;
        Open();
    }

    public void TriggerUp(double heldMs)
    {
        _triggerAt = DateTime.Now;
        if (!_open || !_holding) return;
        _holding = false;
        // hold → release runs the highlighted slot; a quick tap keeps the ring open (unless the mouse already pointed somewhere)
        if (heldMs >= _ring.TapThresholdMs || _mouseUsed && _view.Highlight >= 0 && heldMs > 0)
        {
            if (_view.Highlight >= 0) Finish(_view.Highlight); else { App.Log($"[ring] отпущено через {heldMs:0} мс без выбора — закрыто"); Close(false); }
        }
        else App.Log($"[ring] короткое нажатие ({heldMs:0} мс) — кольцо остаётся открытым до щелчка / Esc");
        // else: quick tap → stays open, waits for a click / tilt / Enter
    }

    /// <summary>Hook thread: only a lookup in a snapshot taken when the ring opened.</summary>
    public bool WantsKey(ushort vk) => _open && Array.IndexOf(_wanted, vk) >= 0;

    private (ushort U, ushort D, ushort L, ushort R) _sk;

    private DateTime _triggerAt;
    private bool _mouseUsed;
    private POINT _lastMouse;

    public void OnKey(ushort vk, bool down)
    {
        if (vk == 0x1B) { if (down && _open) { App.Log("[ring] Esc"); Close(false); } return; }
        if (vk == 0x0D) { if (down && _open) Finish(_view.Highlight); return; }
        if (!_open) return;
        var (u, d, l, r) = _sk;
        if (!down) { _stick.Remove(vk); return; }
        if (!_stick.Add(vk)) return;                      // OS auto-repeat while the tilt is held: one tilt = one step
        string name = vk == u ? "↑" : vk == d ? "↓" : vk == l ? "←" : "→";
        if ((DateTime.Now - _triggerAt).TotalMilliseconds < 150)
        {   // pressing the stick often also tilts it a little — ignore that
            App.Log($"[ring] наклон {name} сразу после нажатия стика — пропущен");
            return;
        }
        int n = _view.Count, before = _view.Highlight, s;
        if (_stick.Count >= 2)                             // two directions held = diagonal
            s = RingMath.SlotFromStick(_stick.Contains(u), _stick.Contains(d), _stick.Contains(l), _stick.Contains(r), n);
        else
            s = RingMath.StepToward(before, RingMath.SlotFromStick(vk == u, vk == d, vk == l, vk == r, n), n);
        if (s >= 0) { _stickUsed = true; _mouseUsed = false; _view.Highlight = s; }
        _openedAt = DateTime.Now;                          // auto-close counts idle time, not time since opening
        App.Log($"[ring] наклон {name}: ячейка {before + 1} → {s + 1}" + (s >= 0 && s < _ring.Slots.Count ? $" «{_ring.Slots[s].Title}»" : ""));
    }

    private void Open()
    {
        _view = new RingView(_ring.DarkStyle, tooltips: false) { CenterHint = "Наклон стика — выбор, нажатие — ОК" };
        _view.SetSlots(_ring.Slots.Take(RingSettings.MaxSlots));
        _view.ItemClicked += i => Finish(i);
        _view.CenterClicked += () => { App.Log("[ring] × — закрыто"); Close(false); };
        _view.RenderTransformOrigin = new Point(0.5, 0.5);
        var sc = new ScaleTransform(0.82, 0.82);
        _view.RenderTransform = sc;
        Content = _view;
        GetCursorPos(out _origin);
        _sk = StickKeys();
        _wanted = new[] { 0x1B, 0x0D, (int)_sk.U, _sk.D, _sk.L, _sk.R };
        if (!IsVisible) Show();
        PlaceAtCursor(true);
        Dispatcher.BeginInvoke(() => PlaceAtCursor(false), DispatcherPriority.Loaded); // again after a possible DPI change (other monitor)
        _open = true; _stickUsed = false; _mouseUsed = false; _stick.Clear(); _openedAt = DateTime.Now;
        GetCursorPos(out _lastMouse);
        var dur = TimeSpan.FromMilliseconds(170);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur));
        sc.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.82, 1, dur) { EasingFunction = ease });
        sc.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.82, 1, dur) { EasingFunction = ease });
        _timer.Start();
    }

    /// <summary>Centres the window on the cursor in physical pixels (per-monitor DPI aware) and makes it topmost.</summary>
    private void PlaceAtCursor(bool log)
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return;
        uint dpi = 96;
        try { if (GetDpiForMonitor(MonitorFromPoint(_origin, 2), 0, out var dx, out _) == 0) dpi = dx; } catch { }
        _scale = dpi / 96.0;
        GetWindowRect(h, out var wr);
        int w = wr.R - wr.L, hgt = wr.B - wr.T;
        if (w <= 0) { w = (int)(Width * _scale); hgt = (int)(Height * _scale); }
        SetWindowPos(h, HWND_TOPMOST, _origin.X - w / 2, _origin.Y - hgt / 2, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        GetWindowRect(h, out var r2);
        uint wdpi = 0; try { wdpi = GetDpiForWindow(h); } catch { }
        if (log || r2.R - r2.L != w) App.Log($"[ring] открыто: курсор ({_origin.X},{_origin.Y}) px, DPI монитора {dpi}, DPI окна {wdpi}, окно ({r2.L},{r2.T})-({r2.R},{r2.B}), ячеек {_view.Count}, видно={IsVisible}");
    }

    private void Track()
    {
        if (!_open) return;
        GetCursorPos(out var p);
        double dx = (p.X - _origin.X) / _scale, dy = (p.Y - _origin.Y) / _scale;
        double dist = Math.Sqrt(dx * dx + dy * dy);
        // the mouse only takes over when it actually moves (otherwise a resting cursor would override every stick tilt)
        bool moved = Math.Abs(p.X - _lastMouse.X) + Math.Abs(p.Y - _lastMouse.Y) >= 3;
        if (moved) { _lastMouse = p; if (!_holding) _openedAt = DateTime.Now; }
        if (moved && dist >= 34) { _stickUsed = false; _mouseUsed = true; _view.Highlight = RingMath.SlotAt(dx, dy, _view.Count, 34); }
        else if (moved && !_stickUsed) { _mouseUsed = false; _view.Highlight = -1; }
        // a click outside the ring closes it
        bool click = (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0;
        if (click && dist > RingView.Size / 2) { App.Log("[ring] щелчок мимо — закрыто"); Close(false); }
        if (!_holding && (DateTime.Now - _openedAt).TotalSeconds > 12) { App.Log("[ring] 12 с без действий — закрыто"); Close(false); }
        // safety: key-up lost (e.g. an elevated window took the focus — Windows does not pass its keys to our hook)
        if (_holding && (DateTime.Now - _openedAt).TotalSeconds > 10) { App.Log("[ring] стик «зажат» > 10 с (отпускание не пришло) — закрыто"); Close(false); }
    }

    private void Finish(int slot)
    {
        var action = slot >= 0 && slot < _ring.Slots.Count ? _ring.Slots[slot] : null;
        App.Log(action == null ? "[ring] закрыто без выбора" : $"[ring] выбрано: ячейка {slot + 1} «{action.Title}» → {action.Action}");
        Close(true);
        if (action == null || action.Action.Kind is HostActionKind.None or HostActionKind.Ring) return;
        // let the overlay disappear and the target app regain input before acting
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        t.Tick += (_, _) => { t.Stop(); _mapper.Execute(action.Action, "кольцо: " + action.Title); };
        t.Start();
    }

    private void Close(bool ran)
    {
        if (!_open) return;
        _open = false; _holding = false; _timer.Stop(); _stick.Clear();
        var an = new DoubleAnimation(0, TimeSpan.FromMilliseconds(ran ? 90 : 130));
        an.Completed += (_, _) => { if (!_open) { Hide(); Left = -10000; } };
        BeginAnimation(OpacityProperty, an);
    }
}
