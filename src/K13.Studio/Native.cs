using System.Runtime.InteropServices;
using System.Text;

namespace Vorotex.Studio;

internal static class Native
{
    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    public const uint LLKHF_INJECTED = 0x10;
    public const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004, KEYEVENTF_EXTENDEDKEY = 0x0001;

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);

    public static readonly IntPtr InjectedTag = new(0x4B313353); // "K13S" – marks our own SendInput events

    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int cls, out int info, int len, out int ret);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20;

    /// <summary>v5.9.5: true = elevated (admin), false = not, null = unknown. Access denied to the token of another user's
    /// or a protected process is reported as null — the caller says «вероятно».</summary>
    public static bool? IsElevated(uint pid)
    {
        IntPtr h = pid == 0 ? GetCurrentProcess() : OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            if (!OpenProcessToken(h, TOKEN_QUERY, out var tok)) return null;
            try { return GetTokenInformation(tok, TokenElevation, out int e, 4, out _) ? e != 0 : null; }
            finally { CloseHandle(tok); }
        }
        finally { if (pid != 0) CloseHandle(h); }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr h, uint flags, StringBuilder name, ref uint size);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr h);
    private delegate bool EnumWindowsProc(IntPtr h, IntPtr lParam);
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, GW_OWNER = 4;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80;

    // pid → (exe path, name): processes do not change their image, and the foreground check runs several times a second
    private static readonly Dictionary<uint, (string Path, string Name, long At)> _procCache = new();

    /// <summary>
    /// v5.4: full path of a process. PROCESS_QUERY_LIMITED_INFORMATION also works for elevated (admin) programs and most
    /// games with anti-cheat; when even that is denied the result is ("", "") and the caller falls back to the window title.
    /// </summary>
    public static (string Path, string Name) ProcessInfo(uint pid)
    {
        if (pid == 0) return ("", "");
        long now = Environment.TickCount64;
        lock (_procCache)
        {
            if (_procCache.TryGetValue(pid, out var c) && now - c.At < 10_000) return (c.Path, c.Name);
            if (_procCache.Count > 512) _procCache.Clear();
        }
        string path = "", name = "";
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h != IntPtr.Zero)
        {
            try
            {
                var sb = new StringBuilder(1024); uint size = (uint)sb.Capacity;
                if (QueryFullProcessImageName(h, 0, sb, ref size)) { path = sb.ToString(); name = System.IO.Path.GetFileNameWithoutExtension(path); }
            }
            finally { CloseHandle(h); }
        }
        if (name.Length == 0)
        {
            // access denied: the process list still knows the name (not the path)
            try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); name = p.ProcessName; } catch { }
        }
        lock (_procCache) _procCache[pid] = (path, name, now);
        return (path, name);
    }

    public static (string Process, string Title) ForegroundInfo()
    {
        var f = Foreground();
        return (f.Process, f.Title);
    }

    public static (string Process, string Title, string Path) Foreground()
    {
        var (n, t, p, _) = ForegroundEx();
        return (n, t, p);
    }

    public static (string Process, string Title, string Path, uint Pid) ForegroundEx()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return ("", "", "", 0);
        var sb = new StringBuilder(512);
        GetWindowText(h, sb, sb.Capacity);
        if (GetWindowThreadProcessId(h, out uint pid) == 0 || pid == 0) return ("", sb.ToString(), "", 0);
        // v5.3: no Process objects per tick; v5.4: limited query works for elevated apps too
        var (path, name) = ProcessInfo(pid);
        return (name, sb.ToString(), path, pid);
    }

    /// <summary>v5.4: programs that have a normal visible window right now (for «Включать в программах»).</summary>
    public static List<(string Process, string Path, string Title)> RunningApps()
    {
        var list = new List<(string, string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        EnumWindows((h, _) =>
        {
            try
            {
                if (!IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;
                if ((GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0) return true;
                int len = GetWindowTextLength(h);
                if (len == 0) return true;
                var sb = new StringBuilder(len + 2);
                GetWindowText(h, sb, sb.Capacity);
                GetWindowThreadProcessId(h, out uint pid);
                var (path, name) = ProcessInfo(pid);
                if (name.Length == 0 || !seen.Add(name)) return true;
                list.Add((name, path, sb.ToString()));
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
