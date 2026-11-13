using System.Diagnostics;
using System.Runtime.InteropServices;
using Vorotex.K13;

namespace Vorotex.Studio;

/// <summary>
/// Records keyboard AND mouse input system-wide while a macro is being recorded (low-level hooks, installed only
/// for the duration of the recording). Mouse buttons map to the macro mouse codes (ЛКМ 0xF1, СКМ 0xF2, ПКМ 0xF3),
/// the wheel to the 0xF7 wheel event. Clicks that land on the «Стоп» button are ignored (both press and release).
/// </summary>
public sealed class MacroRecorder : IDisposable
{
    public const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205,
        WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208, WM_MOUSEWHEEL = 0x20A, WM_XBUTTONDOWN = 0x20B, WM_XBUTTONUP = 0x20C;
    private const uint LLMHF_INJECTED = 0x01;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }

    public enum Kind { Key, MouseButton, Wheel, Unsupported }
    public readonly record struct Input(Kind Kind, bool Down, byte Code, int Wheel, long Ms, string? Note = null);

    private readonly Native.LowLevelKeyboardProc _kbProc, _msProc;
    private IntPtr _kb, _ms;
    private readonly Stopwatch _clock = new();
    private readonly HashSet<byte> _ignoredButtons = new();

    /// <summary>Screen-pixel hit test: true = do not record a click at this point (e.g. the «Стоп» button).</summary>
    public Func<int, int, bool>? IgnoreAt { get; set; }
    public bool RecordMouse { get; set; } = true;
    public event Action<Input>? Captured;

    public MacroRecorder() { _kbProc = KbHook; _msProc = MsHook; }

    public bool IsRunning => _kb != IntPtr.Zero || _ms != IntPtr.Zero;

    public void Start()
    {
        Stop();
        _ignoredButtons.Clear();
        _clock.Restart();
        var mod = Native.GetModuleHandle(null);
        _kb = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _kbProc, mod, 0);
        _ms = Native.SetWindowsHookEx(WH_MOUSE_LL, _msProc, mod, 0);
        if (_kb == IntPtr.Zero || _ms == IntPtr.Zero) App.Log("recorder: hook failed " + Marshal.GetLastWin32Error());
    }

    public void Stop()
    {
        if (_kb != IntPtr.Zero) { Native.UnhookWindowsHookEx(_kb); _kb = IntPtr.Zero; }
        if (_ms != IntPtr.Zero) { Native.UnhookWindowsHookEx(_ms); _ms = IntPtr.Zero; }
        _clock.Stop();
    }

    public void Dispose() => Stop();

    private void Emit(Input i)
    {
        // Hooks run on the UI thread; defer UI work so the hook returns immediately.
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d == null) return;
        d.BeginInvoke(() => Captured?.Invoke(i));
    }

    private IntPtr KbHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
                if ((k.flags & Native.LLKHF_INJECTED) == 0)
                {
                    int msg = wParam.ToInt32();
                    bool down = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
                    if (KeyCatalog.TryFromVirtualKey((ushort)k.vkCode, out var code))
                        Emit(new Input(Kind.Key, down, code, 0, _clock.ElapsedMilliseconds));
                }
            }
        }
        catch (Exception ex) { App.Log("rec kb: " + ex.Message); }
        return Native.CallNextHookEx(_kb, nCode, wParam, lParam);
    }

    private IntPtr MsHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && RecordMouse)
            {
                var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if ((m.flags & LLMHF_INJECTED) == 0) HandleMouse(wParam.ToInt32(), m);
            }
        }
        catch (Exception ex) { App.Log("rec mouse: " + ex.Message); }
        return Native.CallNextHookEx(_ms, nCode, wParam, lParam);
    }

    private void HandleMouse(int msg, MSLLHOOKSTRUCT m)
    {
        long t = _clock.ElapsedMilliseconds;
        (byte code, bool down) = msg switch
        {
            WM_LBUTTONDOWN => (MouseCodes.Left, true), WM_LBUTTONUP => (MouseCodes.Left, false),
            WM_RBUTTONDOWN => (MouseCodes.Right, true), WM_RBUTTONUP => (MouseCodes.Right, false),
            WM_MBUTTONDOWN => (MouseCodes.Middle, true), WM_MBUTTONUP => (MouseCodes.Middle, false),
            _ => ((byte)0, false),
        };
        if (code != 0)
        {
            if (down && IgnoreAt?.Invoke(m.pt.X, m.pt.Y) == true) { _ignoredButtons.Add(code); return; }
            if (!down && _ignoredButtons.Remove(code)) return;
            Emit(new Input(Kind.MouseButton, down, code, 0, t));
            return;
        }
        if (msg == WM_MOUSEWHEEL)
        {
            int delta = (short)(m.mouseData >> 16);
            if (delta == 0) return;
            int det = Math.Max(1, Math.Abs(delta) / 120) * Math.Sign(delta);
            Emit(new Input(Kind.Wheel, false, MouseCodes.Wheel, det, t));
            return;
        }
        if (msg == WM_XBUTTONDOWN)
            Emit(new Input(Kind.Unsupported, true, 0, 0, t, "Боковые кнопки мыши (X1/X2) клавиатура не умеет повторять — пропущено."));
    }
}
