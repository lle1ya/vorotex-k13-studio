using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Vorotex.Studio;

/// <summary>v5.6.2: Raw Input listener that reports key events which physically came from the VOROTEX K13
/// (device path contains VID_30FA&amp;PID_2350). Only the keypad's own keys are reported — the main keyboard is never
/// looked at. Used to show what the keypad really sends (e.g. «Tab» instead of its F14–F24 code after preparing).
/// Keys swallowed by the low-level hook (routed F14–F24) produce no Raw Input — those are reported by the hook.</summary>
public sealed class K13KeyMonitor : IDisposable
{
    private const int WM_INPUT = 0x00FF, RID_INPUT = 0x10000003, RIM_TYPEMOUSE = 0, RIM_TYPEKEYBOARD = 1, RIM_TYPEHID = 2, RIDEV_INPUTSINK = 0x100, RIDEV_REMOVE = 0x1,
        RIDI_DEVICENAME = 0x20000007;
    private const ushort RI_MOUSE_WHEEL = 0x0400;
    private const ushort RI_KEY_BREAK = 1;
    private HwndSource? _src;
    private readonly Dictionary<IntPtr, bool> _isK13 = new();

    /// <summary>(vk, scan, down) — keypad events only, on the UI thread.</summary>
    public event Action<ushort, ushort, bool>? KeypadKey;
    /// <summary>1.2.0 (key tester only): consumer-control usage from the keypad (0 = released).</summary>
    public event Action<ushort>? KeypadConsumer;
    /// <summary>1.2.0 (key tester only): mouse event from the keypad: wheel delta (0 = none), raw button flags.</summary>
    public event Action<int, ushort>? KeypadMouse;

    private bool _extra;
    /// <summary>1.2.0: while the key tester is open, also listen to the consumer-control (page 0x0C) and mouse collections —
    /// the wheel's volume / scroll and mouse-button macros. Off otherwise (the real mouse would wake us for every move).</summary>
    public bool ListenExtra(bool on)
    {
        if (_src == null || on == _extra) return on == _extra;
        var dev = new RAWINPUTDEVICE[]
        {
            new() { usUsagePage = 0x0C, usUsage = 1, dwFlags = on ? (uint)RIDEV_INPUTSINK : RIDEV_REMOVE, hwndTarget = on ? _src.Handle : IntPtr.Zero },
            new() { usUsagePage = 1, usUsage = 2, dwFlags = on ? (uint)RIDEV_INPUTSINK : RIDEV_REMOVE, hwndTarget = on ? _src.Handle : IntPtr.Zero },
        };
        bool ok = RegisterRawInputDevices(dev, (uint)dev.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        if (ok) _extra = on;
        else App.Log($"[tester] Raw Input (медиа/мышь) {(on ? "не включился" : "не выключился")}: ошибка {Marshal.GetLastWin32Error()}");
        return ok;
    }

    public bool Start()
    {
        try
        {
            _src = new HwndSource(new HwndSourceParameters("K13 raw input") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) });
            _src.AddHook(WndProc);
            var dev = new RAWINPUTDEVICE[] { new() { usUsagePage = 1, usUsage = 6, dwFlags = RIDEV_INPUTSINK, hwndTarget = _src.Handle } };
            return RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        }
        catch (Exception ex) { App.Log("[k13] Raw Input недоступен: " + ex.Message); return false; }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_INPUT) return IntPtr.Zero;
        try
        {
            uint size = 0, hdr = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
            GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, hdr);
            if (size == 0) return IntPtr.Zero;
            var buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetRawInputData(lParam, RID_INPUT, buf, ref size, hdr) != size) return IntPtr.Zero;
                var h = Marshal.PtrToStructure<RAWINPUTHEADER>(buf);
                if (h.dwType is not (RIM_TYPEKEYBOARD or RIM_TYPEHID or RIM_TYPEMOUSE) || !IsK13(h.hDevice)) return IntPtr.Zero;
                if (h.dwType == RIM_TYPEHID)
                {   // RAWHID: dwSizeHid, dwCount, then dwCount reports
                    int sz = Marshal.ReadInt32(buf + (int)hdr), cnt = Marshal.ReadInt32(buf + (int)hdr + 4);
                    if (sz <= 0 || cnt <= 0 || sz > 64) return IntPtr.Zero;
                    var rep = new byte[sz];
                    for (int i = 0; i < cnt; i++)
                    {
                        Marshal.Copy(buf + (int)hdr + 8 + i * sz, rep, 0, sz);
                        KeypadConsumer?.Invoke(Vorotex.K13.KeyTester.ConsumerUsage(rep));
                    }
                    return IntPtr.Zero;
                }
                if (h.dwType == RIM_TYPEMOUSE)
                {
                    var m = Marshal.PtrToStructure<RAWMOUSE>(buf + (int)hdr);
                    int delta = (m.usButtonFlags & RI_MOUSE_WHEEL) != 0 ? (short)m.usButtonData : 0;
                    ushort btn = (ushort)(m.usButtonFlags & ~RI_MOUSE_WHEEL & 0x03FF);
                    if (delta != 0 || btn != 0) KeypadMouse?.Invoke(delta, btn);
                    return IntPtr.Zero;
                }
                var k = Marshal.PtrToStructure<RAWKEYBOARD>(buf + (int)hdr);
                if (k.VKey is 0 or 0xFF) return IntPtr.Zero;
                KeypadKey?.Invoke(k.VKey, k.MakeCode, (k.Flags & RI_KEY_BREAK) == 0);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { }
        return IntPtr.Zero;
    }

    private bool IsK13(IntPtr dev)
    {
        if (_isK13.TryGetValue(dev, out bool v)) return v;
        uint n = 0;
        GetRawInputDeviceInfo(dev, RIDI_DEVICENAME, IntPtr.Zero, ref n);
        string name = "";
        if (n > 0)
        {
            var p = Marshal.AllocHGlobal((int)n * 2);
            try { if (GetRawInputDeviceInfo(dev, RIDI_DEVICENAME, p, ref n) > 0) name = Marshal.PtrToStringUni(p) ?? ""; }
            finally { Marshal.FreeHGlobal(p); }
        }
        v = IsK13Path(name);
        _isK13[dev] = v;
        return v;
    }

    public static bool IsK13Path(string path) => Vorotex.K13.PcLayerHealth.IsK13Path(path);

    public void Dispose() { try { _src?.Dispose(); } catch { } _src = null; }

    [StructLayout(LayoutKind.Sequential)] private struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }
    [StructLayout(LayoutKind.Sequential)] private struct RAWINPUTHEADER { public uint dwType, dwSize; public IntPtr hDevice, wParam; }
    [StructLayout(LayoutKind.Sequential)] private struct RAWMOUSE { public ushort usFlags, pad; public ushort usButtonFlags, usButtonData; public uint ulRawButtons; public int lLastX, lLastY; public uint ulExtraInformation; }
    [StructLayout(LayoutKind.Sequential)] private struct RAWKEYBOARD { public ushort MakeCode, Flags, Reserved, VKey; public uint Message, ExtraInformation; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devs, uint n, uint size);
    [DllImport("user32.dll")] private static extern uint GetRawInputData(IntPtr hRaw, uint cmd, IntPtr data, ref uint size, uint hdrSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint GetRawInputDeviceInfo(IntPtr dev, uint cmd, IntPtr data, ref uint size);
}
