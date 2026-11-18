using Vorotex.K13;

namespace Vorotex.Studio;

/// <summary>1.3.0: «Прокрутка и курсор» — sends <see cref="PointerMotion"/> output with SendInput
/// (MOUSEEVENTF_MOVE / WHEEL / HWHEEL). A «старт/стоп» motion runs on a 15 ms timer until stopped.</summary>
public static class PointerDriver
{
    private static readonly object _lock = new();
    private static readonly PointerMotion _motion = new();
    private static System.Threading.Timer? _timer;
    /// <summary>For tests / smoke: replaces SendInput.</summary>
    public static Action<PointerOut>? SendOverride { get; set; }
    public static event Action<string>? Changed;

    public static bool IsRunning { get { lock (_lock) return _motion.IsRunning; } }
    public static PointerSpec? Running { get { lock (_lock) return _motion.Running; } }

    public static void Tap(PointerSpec s, Action<string>? log = null)
    {
        PointerMotion.TapResult r; PointerOut o;
        lock (_lock)
        {
            (r, o) = _motion.Tap(s, Environment.TickCount64);
            if (_motion.IsRunning) _timer ??= new System.Threading.Timer(_ => Tick(), null, PointerMotion.TickMs, PointerMotion.TickMs);
            else StopTimer();
        }
        if (!o.IsEmpty) Send(o);
        if (r != PointerMotion.TapResult.Stepped)
        {
            string text = r switch
            {
                PointerMotion.TapResult.Started => $"{s.Title}: пошла, следующее нажатие — стоп",
                PointerMotion.TapResult.Switched => $"{s.Title}: сменилось направление",
                _ => $"{s.Title}: стоп",
            };
            App.Log("[pointer] " + text);
            Changed?.Invoke(text);
        }
    }

    public static void Stop(string reason)
    {
        bool stopped;
        lock (_lock) { stopped = _motion.Stop(reason); StopTimer(); }
        if (stopped) { App.Log("[pointer] стоп: " + reason); Changed?.Invoke("Прокрутка/курсор: стоп (" + reason + ")"); }
    }

    private static void StopTimer() { _timer?.Dispose(); _timer = null; }

    private static void Tick()
    {
        PointerOut o; bool ended = false; string? why = null;
        lock (_lock)
        {
            if (!_motion.IsRunning) { StopTimer(); return; }
            o = _motion.Tick(Environment.TickCount64);
            if (!_motion.IsRunning) { ended = true; why = _motion.LastStopReason; StopTimer(); }
        }
        if (!o.IsEmpty) Send(o);
        if (ended) { App.Log("[pointer] стоп: " + why); Changed?.Invoke("Прокрутка/курсор: стоп (" + why + ")"); }
    }

    private const uint MOVE = 0x0001, WHEEL = 0x0800, HWHEEL = 0x1000;

    public static void Send(PointerOut o)
    {
        if (SendOverride is { } f) { f(o); return; }
        var list = new List<Native.INPUT>();
        if (o.Dx != 0 || o.Dy != 0)
            list.Add(new Native.INPUT { type = 0, U = new Native.InputUnion { mi = new Native.MOUSEINPUT { dx = o.Dx, dy = o.Dy, dwFlags = MOVE, dwExtraInfo = Native.InjectedTag } } });
        if (o.Wheel != 0)
            list.Add(new Native.INPUT { type = 0, U = new Native.InputUnion { mi = new Native.MOUSEINPUT { mouseData = unchecked((uint)o.Wheel), dwFlags = WHEEL, dwExtraInfo = Native.InjectedTag } } });
        if (o.HWheel != 0)
            list.Add(new Native.INPUT { type = 0, U = new Native.InputUnion { mi = new Native.MOUSEINPUT { mouseData = unchecked((uint)o.HWheel), dwFlags = HWHEEL, dwExtraInfo = Native.InjectedTag } } });
        if (list.Count > 0) Native.SendInput((uint)list.Count, list.ToArray(), System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>());
    }
}
