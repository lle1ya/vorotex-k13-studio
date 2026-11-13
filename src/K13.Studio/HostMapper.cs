using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Vorotex.K13;

namespace Vorotex.Studio;

/// <summary>
/// Host-side mapper. The keypad is programmed to send unique "trigger" keys (e.g. F13..F24); a low-level keyboard hook
/// turns them into richer actions: tap vs hold, joystick diagonals (two triggers together), programs, URLs, text,
/// key combinations and profile switching. Works without any device writes for keys that already send unique codes.
/// </summary>
public sealed class HostMapper : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    private const uint WM_QUIT = 0x12;

    private readonly Dispatcher _ui;
    private readonly Native.LowLevelKeyboardProc _proc;
    private IntPtr _hook;
    private Thread? _thread;
    private uint _threadId;
    private volatile HostSettings _settings = new();
    private volatile bool _resetPending;
    private volatile string _profileName = "";
    // hook-thread state (only touched inside HookProc)
    private readonly Dictionary<ushort, long> _downAt = new();
    private readonly HashSet<ushort> _consumed = new();
    private readonly Dictionary<ushort, long> _lastDown = new();
    private readonly HashSet<ushort> _ringKeys = new();
    private long _events;

    /// <summary>Keys that never reach other applications while the app runs (the stick press = F13).</summary>
    public static readonly HashSet<ushort> AlwaysSwallow = new() { ProfileMigrations.StickVk };

    public event Action<string>? Log;
    /// <summary>Diagnostics for the in-app log window only (already written to studio.log).</summary>
    public event Action<string>? UiLog;
    public event Action<string>? SwitchProfileRequested;

    /// <summary>The on-screen actions ring (set by the UI). Ring triggers and keys pressed while it is open go here.</summary>
    public IRingHost? Ring { get; set; }
    /// <summary>Plays a keypad macro host-side (slot → events).</summary>
    public Func<int, MacroSlot?>? MacroProvider { get; set; }
    private Action<ushort>? _probe;
    /// <summary>When set, the next physical key (down) is reported here and swallowed — used by «Проверить, что посылает кнопка».</summary>
    public Action<ushort>? ProbeNextKey { get => Volatile.Read(ref _probe); set => Volatile.Write(ref _probe, value); }

    public HostMapper(Dispatcher ui)
    {
        _ui = ui;
        _proc = HookProc;
    }

    public bool IsRunning => _hook != IntPtr.Zero;
    public long EventsSeen => Interlocked.Read(ref _events);

    private void Diag(string msg)
    {
        App.Log("[hook] " + msg);          // file, written directly (never waits for the UI)
        var l = UiLog;
        if (l != null) _ui.BeginInvoke(() => l("[перехват] " + msg));
    }

    /// <summary>
    /// Installs the WH_KEYBOARD_LL hook on its own thread with its own message loop. The hook must never wait for the
    /// UI thread: if a callback is slower than LowLevelHooksTimeout Windows silently REMOVES the hook (no error, no log).
    /// </summary>
    public void Start()
    {
        if (_thread != null) return;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
            int err = Marshal.GetLastWin32Error();
            if (_hook == IntPtr.Zero) Diag($"НЕ установлен: SetWindowsHookEx(WH_KEYBOARD_LL) ошибка Win32 {err}");
            else Diag($"установлен: WH_KEYBOARD_LL, поток {_threadId}, процесс {Environment.ProcessId}, профиль «{_profileName}», " +
                      $"привязка F13: {DescribeF13(_settings)}");
            ready.Set();
            if (_hook == IntPtr.Zero) return;
            while (GetMessage(out var m, IntPtr.Zero, 0, 0) > 0) { }
            Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            Diag("снят");
        }) { IsBackground = true, Name = "K13 keyboard hook", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        ready.Wait(3000);
    }

    public void Stop()
    {
        if (_thread == null) return;
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
        _thread = null;
    }

    private static string DescribeF13(HostSettings s)
    {
        var b = s.Keys.FirstOrDefault(k => k.TriggerVk == ProfileMigrations.StickVk);
        return (s.Enabled ? "" : "(действия на ПК ВЫКЛЮЧЕНЫ) ") + (b == null ? "нет" : b.Tap.ToString());
    }

    public void Update(HostSettings settings, string? profileName = null)
    {
        bool changed = !ReferenceEquals(settings, _settings) || profileName != null && profileName != _profileName;
        _settings = settings; _resetPending = true;
        if (profileName != null) _profileName = profileName;
        if (changed) App.Log($"[hook] профиль «{_profileName}»: F13 → {DescribeF13(settings)}");
    }

    private bool IsRingTrigger(ushort vk) => TriggerKeyPolicy.Classify(vk) != TriggerSafety.Forbidden && _settings.Keys.Any(k => k.TriggerVk == vk && k.Tap.Kind == HostActionKind.Ring);

    private bool IsTrigger(ushort vk) =>
        TriggerKeyPolicy.Classify(vk) != TriggerSafety.Forbidden && _settings.Keys.Any(k => k.TriggerVk == vk) || _settings.Chords.Any(c => c.VkA == vk || c.VkB == vk);

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                if (_resetPending) { _resetPending = false; _downAt.Clear(); _consumed.Clear(); _lastDown.Clear(); }
                var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
                ushort vk = (ushort)k.vkCode;
                bool injected = (k.flags & Native.LLKHF_INJECTED) != 0 && k.dwExtraInfo == Native.InjectedTag;
                int msg0 = wParam.ToInt32();
                bool down0 = msg0 is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
                if (Interlocked.Increment(ref _events) == 1) Diag($"первое событие клавиатуры получено (VK 0x{vk:X2}) — перехват работает");
                if (!injected)
                {
                    var probe = ProbeNextKey;
                    // modifiers are never K13 output on their own: keep waiting and let them through (Shift = language switch etc.)
                    bool modifier = vk is 0x10 or 0x11 or 0x12 or (>= 0xA0 and <= 0xA5) or 0x5B or 0x5C or 0x14;
                    if (probe != null && down0 && !modifier && Interlocked.CompareExchange(ref _probe, null, probe) == probe)
                    {
                        _ui.BeginInvoke(() => probe(vk));
                        return (IntPtr)1;
                    }
                    // keys while the ring is open (Esc / Enter / joystick): swallow the whole down…up pair
                    if (Ring is { } ring)
                    {
                        if (down0 && !IsRingTrigger(vk) && ring.WantsKey(vk)) { _ringKeys.Add(vk); _ui.BeginInvoke(() => ring.OnKey(vk, true)); return (IntPtr)1; }
                        if (!down0 && _ringKeys.Remove(vk)) { _ui.BeginInvoke(() => ring.OnKey(vk, false)); return (IntPtr)1; }
                    }
                    bool always = AlwaysSwallow.Contains(vk);
                    var s = _settings;
                    if (s.Enabled && IsTrigger(vk))
                    {
                        bool suppress = Handle(vk, down0);
                        if (suppress || always) return (IntPtr)1;
                    }
                    else if (always)
                    {
                        if (down0 && !_lastDown.ContainsKey(vk))
                            Diag($"F13 ↓ проглочен, но кольцо не открыто: в профиле «{_profileName}» " + (s.Enabled ? "нет привязки F13" : "выключены действия на ПК"));
                        if (down0) _lastDown[vk] = Stopwatch.GetTimestamp(); else _lastDown.Remove(vk);
                        return (IntPtr)1;
                    }
                }
            }
        }
        catch (Exception ex) { App.Log("hook: " + ex); }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private bool Handle(ushort vk, bool down)
    {
        var binding = _settings.Keys.FirstOrDefault(b => b.TriggerVk == vk);
        bool suppress = binding?.Suppress ?? true;
        long now = Stopwatch.GetTimestamp();
        if (binding != null && binding.Tap.Kind == HostActionKind.Ring)
        {
            if (down)
            {
                // OS auto-repeat: downs keep coming every ~30 ms while held. A stale entry (lost key-up) expires after 1 s of silence.
                bool repeat = _downAt.ContainsKey(vk) && _lastDown.TryGetValue(vk, out var last) && Stopwatch.GetElapsedTime(last).TotalMilliseconds < 1000;
                _lastDown[vk] = now;
                if (repeat) return true;
                _downAt[vk] = now;
                var ringSettings = _settings.Ring;
                Diag($"{KeyCombo.VkName(vk)} ↓ (стик) → открыть кольцо, профиль «{_profileName}», ячеек {ringSettings.Slots.Count}");
                _ui.BeginInvoke(() => Ring?.TriggerDown(ringSettings));
            }
            else
            {
                _lastDown.Remove(vk);
                double held = _downAt.Remove(vk, out var t1) ? Stopwatch.GetElapsedTime(t1).TotalMilliseconds : 0;
                Diag($"{KeyCombo.VkName(vk)} ↑ через {held:0} мс");
                _ui.BeginInvoke(() => Ring?.TriggerUp(held));
            }
            return true;
        }
        if (down)
        {
            if (_downAt.ContainsKey(vk)) return suppress; // auto-repeat
            _downAt[vk] = now;
            foreach (var ch in _settings.Chords.Where(c => c.VkA == vk || c.VkB == vk))
            {
                ushort other = ch.VkA == vk ? ch.VkB : ch.VkA;
                if (_downAt.ContainsKey(other))
                {
                    _consumed.Add(vk); _consumed.Add(other);
                    Run(ch.Action, $"аккорд {ch.Title}");
                    return true;
                }
            }
            if (binding != null && binding.Hold.Kind == HostActionKind.None && !_settings.Chords.Any(c => c.VkA == vk || c.VkB == vk))
            {
                _consumed.Add(vk);
                Run(binding.Tap, "нажатие " + KeyCombo.VkName(vk));
            }
            return suppress;
        }
        else
        {
            bool had = _downAt.Remove(vk, out var t0);
            if (_consumed.Remove(vk)) return suppress;
            if (binding == null) return suppress;
            double ms = had ? Stopwatch.GetElapsedTime(t0).TotalMilliseconds : 0;
            if (binding.Hold.Kind != HostActionKind.None && ms >= binding.HoldMs) Run(binding.Hold, "удержание " + KeyCombo.VkName(vk));
            else Run(binding.Tap, "нажатие " + KeyCombo.VkName(vk));
            return suppress;
        }
    }

    private void Run(HostAction a, string why)
    {
        if (a.Kind == HostActionKind.None) return;
        _ui.BeginInvoke(() => Execute(a, why));
    }

    /// <summary>Runs a host action now (UI thread).</summary>
    public void Execute(HostAction a, string why)
    {
        if (a.Kind == HostActionKind.None) return;
        try
        {
            Log?.Invoke($"{why}: {a}");
            switch (a.Kind)
            {
                case HostActionKind.KeyCombo when a.Value.Equals("BrowserHome", StringComparison.OrdinalIgnoreCase): ShellLaunch.OpenBrowser(Log); break;
                case HostActionKind.KeyCombo when a.Value.Equals("Mail", StringComparison.OrdinalIgnoreCase): ShellLaunch.Open("mailto:", Log); break;
                case HostActionKind.KeyCombo when a.Value.Replace(" ", "").Equals("Ctrl+Shift+Esc", StringComparison.OrdinalIgnoreCase): ShellLaunch.TaskManager(Log); break;
                case HostActionKind.KeyCombo: KeyCombo.Send(a.Value); break;
                case HostActionKind.Text: KeyCombo.TypeText(a.Value); break;
                case HostActionKind.LaunchProgram:
                    Process.Start(new ProcessStartInfo(a.Value, a.Arguments) { UseShellExecute = true }); break;
                case HostActionKind.OpenUrl:
                    Process.Start(new ProcessStartInfo(a.Value) { UseShellExecute = true }); break;
                case HostActionKind.SwitchProfile: SwitchProfileRequested?.Invoke(a.Value); break;
                case HostActionKind.Ring: Ring?.TriggerDown(_settings.Ring); Ring?.TriggerUp(0); break;
                case HostActionKind.PlayMacro:
                    if (int.TryParse(a.Value, out int slot) && MacroProvider?.Invoke(slot) is { IsUsed: true } m) MacroPlayer.Play(m);
                    break;
            }
        }
        catch (Exception ex) { Log?.Invoke("Ошибка действия: " + ex.Message); App.Log("action error: " + ex); }
    }

    public void Dispose() => Stop();
}

/// <summary>Implemented by the on-screen actions ring.</summary>
public interface IRingHost
{
    bool IsOpen { get; }
    /// <summary>Called on the HOOK thread (must be cheap and thread-safe): swallow this key because the ring is open?</summary>
    bool WantsKey(ushort vk);
    void TriggerDown(RingSettings ring);
    void TriggerUp(double heldMs);
    /// <summary>Keys while open (joystick tilt, Esc, Enter), on the UI thread.</summary>
    void OnKey(ushort vk, bool down);
}

/// <summary>Replays a keypad macro on the PC with SendInput (keys + mouse buttons + wheel).</summary>
public static class MacroPlayer
{
    public static void Play(MacroSlot m)
    {
        var events = m.Events.Select(e => e.Clone()).ToList();
        int repeat = m.RepeatMode == MacroRepeatMode.Count ? Math.Clamp(m.Repeat, 1, 50) : 1;
        System.Threading.Tasks.Task.Run(() =>
        {
            for (int r = 0; r < repeat; r++)
                foreach (var e in events)
                {
                    if (e.DelayMs > 0) System.Threading.Thread.Sleep(e.DelayMs);
                    Send(e);
                }
        });
    }

    private const uint INPUT_MOUSE = 0;

    public static void Send(MacroEvent e)
    {
        var inp = new Native.INPUT();
        if (e.IsWheel)
        {
            inp.type = INPUT_MOUSE;
            inp.U.mi = new Native.MOUSEINPUT { dwFlags = 0x0800, mouseData = unchecked((uint)(e.WheelAmount * 120)), dwExtraInfo = Native.InjectedTag };
        }
        else if (e.IsMouseButton)
        {
            uint f = e.Code switch { MouseCodes.Left => 0x02u, MouseCodes.Right => 0x08u, _ => 0x20u };
            inp.type = INPUT_MOUSE;
            inp.U.mi = new Native.MOUSEINPUT { dwFlags = e.Down ? f : f << 1, dwExtraInfo = Native.InjectedTag };
        }
        else
        {
            ushort vk = KeyCatalog.ToVirtualKey(e.Code);
            if (vk == 0) return;
            inp.type = Native.INPUT_KEYBOARD;
            inp.U.ki = new Native.KEYBDINPUT { wVk = vk, dwFlags = e.Down ? 0 : Native.KEYEVENTF_KEYUP, dwExtraInfo = Native.InjectedTag };
        }
        Native.SendInput(1, new[] { inp }, System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>());
    }
}

/// <summary>Watches the foreground window and requests a profile switch by rules (.exe / title).</summary>
public sealed class ForegroundWatcher : IDisposable
{
    private readonly DispatcherTimer _timer;
    private string _last = "";
    public Func<List<AppSwitchRule>> Rules { get; set; } = () => new();
    public event Action<string, string, string>? Matched; // profile, process, title
    public (string Process, string Title) Current { get; private set; }

    public ForegroundWatcher()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _timer.Tick += (_, _) => Tick();
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    private void Tick()
    {
        var (proc, title) = Native.ForegroundInfo();
        Current = (proc, title);
        var key = proc + "|" + title;
        if (key == _last) return;
        _last = key;
        if (proc.Equals("VorotexK13Studio", StringComparison.OrdinalIgnoreCase)) return;
        foreach (var r in Rules())
        {
            bool okProc = string.IsNullOrWhiteSpace(r.ProcessName) || proc.Equals(r.ProcessName.Replace(".exe", "", StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase);
            bool okTitle = string.IsNullOrWhiteSpace(r.TitleContains) || title.Contains(r.TitleContains, StringComparison.OrdinalIgnoreCase);
            if (okProc && okTitle && !string.IsNullOrWhiteSpace(r.ProfileName)) { Matched?.Invoke(r.ProfileName, proc, title); return; }
        }
    }

    public void Dispose() => _timer.Stop();
}
