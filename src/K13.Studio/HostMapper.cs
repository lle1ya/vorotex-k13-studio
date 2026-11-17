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
    /// <summary>v5.9.5: a K13 key the low-level hook never saw (Raw Input reported it) — routed on the hook thread.</summary>
    private const uint WM_APP_MISSED = 0x8000 + 0x13;
    /// <summary>v5.9.6: end holds whose taps stopped (router.Tick on the hook thread).</summary>
    private const uint WM_APP_TICK = 0x8000 + 0x14;
    private System.Threading.Timer? _holdTimer;
    /// <summary>v5.9.6: (column, keypad repeats the tap) — the hold test result of the first press after a write. UI thread.</summary>
    public event Action<int, bool>? HoldVerdict;

    private readonly Dispatcher _ui;
    private readonly Native.LowLevelKeyboardProc _proc;
    private IntPtr _hook;
    private Thread? _thread;
    private uint _threadId;
    private volatile HostSettings _settings = new();
    private volatile bool _resetPending;
    private volatile string _profileName = "";
    // hook-thread state (only touched inside HookProc)
    private readonly KeyDownTracker _ringDown = new(1000), _keyDown = new();
    private readonly HashSet<ushort> _consumed = new();
    private readonly Dictionary<ushort, long> _lastDown = new();
    private readonly HashSet<ushort> _ringKeys = new();
    private long _events;
    // v5.6: keys through the PC (prepared keypad sends F14–F24 → action of the ACTIVE profile)
    private sealed record LayerSnapshot(PcLayerState State, K13DeviceConfig Cfg, string Profile, int[] HoldWant, int[] HoldWritten);
    private volatile LayerSnapshot? _layer;
    private readonly PcLayerRouter _router = new(); // hook thread only; keeps held keys across profile switches
    private LayerSnapshot? _routerFor;
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);

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

    /// <summary>
    /// v5.9.5: DM-Shvyrkov 17.11.2026 12:36:57–12:38:46 — the hook was installed but Windows did not call it at all (no
    /// «первое событие»), while Raw Input saw every K13 Scroll Lock + F10, so «через ПК» did nothing. Keys the hook did not
    /// swallow are fed to the same router on the hook thread (serialized with HookProc). They also reach the active window
    /// (nothing can be swallowed any more), but the action runs. Returns false when the hook thread is not running.
    /// </summary>
    private bool _routingMissed; // hook thread only
    /// <summary>v5.9.5: the hook itself (not the Raw Input fallback) routed a keypad press — UI thread.</summary>
    public event Action? HookRouted;

    public bool FeedMissed(ushort vk, bool down) =>
        _threadId != 0 && _layer != null && PostThreadMessage(_threadId, WM_APP_MISSED, (IntPtr)vk, down ? (IntPtr)1 : IntPtr.Zero);

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
            _holdTimer = new System.Threading.Timer(_ => { if (_router.StreamCount > 0 && _threadId != 0) PostThreadMessage(_threadId, WM_APP_TICK, IntPtr.Zero, IntPtr.Zero); }, null, 50, 50);
            while (GetMessage(out var m, IntPtr.Zero, 0, 0) > 0)
            {
                if (m.message == WM_APP_TICK)
                {
                    try { foreach (var o in _router.Tick((uint)Environment.TickCount)) Do(o, _layer); } catch { }
                    continue;
                }
                if (m.message == WM_APP_MISSED)
                {
                    _routingMissed = true;
                    try { RouteLayer((ushort)m.wParam, m.lParam != IntPtr.Zero, (uint)Environment.TickCount); } catch { }
                    finally { _routingMissed = false; }
                }
            }
            _holdTimer?.Dispose(); _holdTimer = null;
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

    /// <summary>v5.6: keys through the PC. Empty state = the keypad keeps its own keys (nothing is translated).</summary>
    public void UpdateLayer(PcLayerState state, K13DeviceConfig profileCfg, string profileName, IEnumerable<int>? holdWant = null, IEnumerable<int>? holdWritten = null)
    {
        if (!state.Active) { if (_layer != null) App.Log("[hook] кнопки через ПК выключены"); _layer = null; return; }
        bool was = _layer != null;
        var hw = (holdWant ?? Array.Empty<int>()).OrderBy(x => x).ToArray(); var hr = (holdWritten ?? Array.Empty<int>()).OrderBy(x => x).ToArray();
        var prev = _layer;
        if (prev == null || !prev.HoldWant.SequenceEqual(hw) || !prev.HoldWritten.SequenceEqual(hr))
            App.Log($"[hold] «пока зажата» через ПК: кнопки с удержанием [{string.Join(", ", hw)}], бит повтора записан в клавиатуре [{string.Join(", ", hr)}]");
        _layer = new LayerSnapshot(state.Clone(), profileCfg.Clone(), profileName, hw, hr);
        if (!was) App.Log($"[hook] кнопки через ПК: {state.Keys.Count} кнопок, профиль «{profileName}»");
    }

    public bool LayerActive => _layer != null;

    /// <summary>Hook thread. True = the event belongs to the PC layer (swallow it).</summary>
    /// <summary>v5.6.2: a fresh press went through the PC layer (column, vk, profile, action) — UI thread.</summary>
    public event Action<int, ushort, string, PcKeyAction?>? LayerRouted;
    private readonly HashSet<ushort> _routedDown = new();
    private int _pressLogs, _strayLogs;
    private bool _prefixLogged;

    /// <summary>Key injection and logging leave the hook thread: anything slow inside a low-level hook makes Windows
    /// skip the hook for that event (the keypad's raw code — or its release — then gets lost).</summary>
    private readonly System.Collections.Concurrent.BlockingCollection<Action> _work = new();
    private Thread? _worker;
    private void Post(Action a)
    {
        if (_worker == null)
        {
            _worker = new Thread(() => { foreach (var w in _work.GetConsumingEnumerable()) { try { w(); } catch { } } }) { IsBackground = true, Name = "K13 inject" };
            _worker.Start();
        }
        _work.Add(a);
    }

    private bool RouteLayer(ushort vk, bool down, long? timeMs = null)
    {
        var l = _layer;
        if (l == null)
        {
            if (_routerFor != null) { foreach (var o in _router.ReleaseAll()) Do(o, null); _router.SetLayer(new PcLayerState()); _routerFor = null; _routedDown.Clear(); }
            return false;
        }
        if (!ReferenceEquals(l, _routerFor))
        {
            if (_routerFor == null || !SameKeys(_routerFor.State, l.State)) _router.SetLayer(l.State);
            _router.SetHold(l.HoldWant, l.HoldWritten);
            _routerFor = l;
        }
        var before = _router.LastResolved;
        var outs = _router.Feed(vk, down, l.Cfg, v => _settings.Enabled && IsTrigger(v), timeMs, out bool mine);
        if (!mine)
        {
            if (down && vk is >= 0x7D and <= 0x87 && _strayLogs++ < 50) Diag($"F{vk - 0x6F} пришёл, но ни одной кнопке «через ПК» не назначен");
            return false;
        }
        if (vk == PcLayerPlanner.ChordPrefixVk && _router.IsChord)
        {
            if (down && !_prefixLogged) { _prefixLogged = true; Diag("Scroll Lock от клавиатуры — режим «через ПК» (Scroll Lock + F1…F11) принимает кнопки"); }
            return true;
        }
        bool fresh = down && !_routedDown.Contains(vk);
        if (down) _routedDown.Add(vk); else _routedDown.Remove(vk);
        foreach (var o in outs) Do(o, l);
        bool acted = outs.Any(x => x.Kind != PcOutKind.Note);
        if (!acted) fresh = false; // v5.9.6: repeated taps of a held key — nothing new happened
        if (down && acted && (fresh || !ReferenceEquals(before, _router.LastResolved)) && _pressLogs < 400)
        {
            _pressLogs++;
            int col = _router.ColumnOf(vk); var a = _router.LastResolved; string prof = l.Profile;
            Post(() => App.Log($"[hook] кнопка {col} (VK 0x{vk:X2}) в профиле «{prof}» → {a?.Kind} {(a == null ? "" : KeyCatalog.Name(a.Code))}: " + string.Join(", ", outs.Select(x => x.Kind))));
        }
        if (down && fresh && !_routingMissed && HookRouted is { } hr) _ui.BeginInvoke(hr);
        if (down && fresh) { var lr = LayerRouted; if (lr != null) { int c = _router.ColumnOf(vk); var a2 = _router.LastResolved; string pn = l.Profile; _ui.BeginInvoke(() => lr(c, vk, pn, a2)); } }
        return true;
    }

    private static bool SameKeys(PcLayerState a, PcLayerState b) =>
        a.Scheme == b.Scheme && a.Keys.Count == b.Keys.Count && a.Keys.Zip(b.Keys).All(p => p.First.Column == p.Second.Column && p.First.Code == p.Second.Code);

    private void Do(PcOut o, LayerSnapshot? l)
    {
        switch (o.Kind)
        {
            case PcOutKind.KeyDown: { var v = o.Vk; Post(() => InjectVk(v, true)); break; }
            case PcOutKind.KeyUp: { var v = o.Vk; Post(() => InjectVk(v, false)); break; }
            case PcOutKind.HostDown: Handle(o.Vk, true); break;
            case PcOutKind.HostUp: Handle(o.Vk, false); break;
            case PcOutKind.StartMacro or PcOutKind.StartMacroHeld or PcOutKind.StartMacroLoop when l != null && o.Slot < l.Cfg.Macros.Count:
                var m = l.Cfg.Macros[o.Slot].Clone(); bool held = o.Kind == PcOutKind.StartMacroHeld;
                if (o.Kind == PcOutKind.StartMacroLoop) m.RepeatMode = MacroRepeatMode.Toggle; // v5.9.4: second press stops (MacroPlayer.Play)
                _ui.BeginInvoke(() => { if (held) MacroPlayer.PlayWhileHeld(m); else MacroPlayer.Play(m); });
                break;
            case PcOutKind.StopMacro: _ui.BeginInvoke(() => MacroPlayer.Stop()); break;
            case PcOutKind.Note:
                {
                    string text = o.Shell; int col = o.Slot; int code = o.Code;
                    Post(() => App.Log(text));
                    var ul = UiLog; if (ul != null) _ui.BeginInvoke(() => ul(text));
                    if (code is 0 or 1 && HoldVerdict is { } hv) _ui.BeginInvoke(() => hv(col, code == 1));
                    break;
                }
            case PcOutKind.Shell: var sh = o.Shell; _ui.BeginInvoke(() => RunShell(sh)); break;
            case PcOutKind.FirmwareOnly: Diag($"«{KeyCatalog.Name(o.Code)}» умеет только сама клавиатура — в режиме «через ПК» не выполняется"); break;
        }
    }

    private static readonly HashSet<ushort> ExtendedVks = new()
    {
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x5B, 0x5C, 0x5D, 0x6F, 0xA3, 0xA5, 0x2C,
        0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xAB, 0xAC, 0xAD, 0xAE, 0xAF, 0xB0, 0xB1, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7,
    };

    /// <summary>Presses / releases a key as if typed (scan code + extended flag, so games and arrow/media keys work).</summary>
    public static void InjectVk(ushort vk, bool down)
    {
        var inp = new Native.INPUT { type = Native.INPUT_KEYBOARD };
        uint flags = down ? 0 : Native.KEYEVENTF_KEYUP;
        if (ExtendedVks.Contains(vk)) flags |= Native.KEYEVENTF_EXTENDEDKEY;
        ushort scan = 0;
        try { scan = (ushort)MapVirtualKey(vk, 0); } catch { }
        inp.U.ki = new Native.KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags, dwExtraInfo = Native.InjectedTag };
        Native.SendInput(1, new[] { inp }, Marshal.SizeOf<Native.INPUT>());
    }

    private void RunShell(string what)
    {
        try
        {
            switch (what)
            {
                case "calc": Process.Start(new ProcessStartInfo("calc.exe") { UseShellExecute = true })?.Dispose(); break;
                case "computer": Process.Start(new ProcessStartInfo("explorer.exe", "shell:MyComputerFolder") { UseShellExecute = true })?.Dispose(); break;
                case "minimize": ShowWindow(Native.GetForegroundWindow(), 6); break;
                case "screenshot": KeyCombo.Send("Win+Shift+S"); break;
            }
        }
        catch (Exception ex) { Log?.Invoke("Ошибка действия: " + ex.Message); }
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
                if (_resetPending) { _resetPending = false; _ringDown.Clear(); _keyDown.Clear(); _consumed.Clear(); _lastDown.Clear(); }
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
                    if (RouteLayer(vk, down0, k.time)) return (IntPtr)1;
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
        long nowMs = Environment.TickCount64;
        if (binding != null && binding.Tap.Kind == HostActionKind.Ring)
        {
            if (down)
            {
                // OS auto-repeat: downs keep coming every ~30 ms while held. A stale entry (lost key-up) expires after 1 s of silence.
                if (!_ringDown.Down(vk, nowMs)) return true;
                var ringSettings = _settings.Ring;
                Diag($"{KeyCombo.VkName(vk)} ↓ (стик) → открыть кольцо, профиль «{_profileName}», ячеек {ringSettings.Slots.Count}");
                _ui.BeginInvoke(() => Ring?.TriggerDown(ringSettings));
            }
            else
            {
                double held = _ringDown.Up(vk, nowMs) ?? 0;
                Diag($"{KeyCombo.VkName(vk)} ↑ через {held:0} мс");
                _ui.BeginInvoke(() => Ring?.TriggerUp(held));
            }
            return true;
        }
        if (down)
        {
            if (!_keyDown.Down(vk, nowMs)) return suppress; // auto-repeat (a lost key-up expires, see KeyDownTracker)
            _consumed.Remove(vk);
            foreach (var ch in _settings.Chords.Where(c => c.VkA == vk || c.VkB == vk))
            {
                ushort other = ch.VkA == vk ? ch.VkB : ch.VkA;
                if (_keyDown.IsDown(other))
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
            double ms = _keyDown.Up(vk, nowMs) ?? 0;
            if (_consumed.Remove(vk)) return suppress;
            if (binding == null) return suppress;
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
                    // v5.4: %USERPROFILE%\… from an imported profile points into this user's folder
                    Process.Start(new ProcessStartInfo(Environment.ExpandEnvironmentVariables(a.Value), a.Arguments) { UseShellExecute = true })?.Dispose(); break;
                case HostActionKind.OpenUrl:
                    Process.Start(new ProcessStartInfo(a.Value) { UseShellExecute = true })?.Dispose(); break;
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
    private static System.Threading.CancellationTokenSource? _playing;
    private static System.Threading.Tasks.Task? _task;

    /// <summary>Plays the macro the full saved number of times (same value as on the keypad); «по кругу» repeats
    /// until triggered again. Triggering it again while it is still playing stops it.</summary>
    public static void Play(MacroSlot m)
    {
        var prev = System.Threading.Interlocked.Exchange(ref _playing, null);
        if (prev != null) { prev.Cancel(); return; }
        if (m.Events.Count == 0) return;
        var cts = new System.Threading.CancellationTokenSource();
        _playing = cts;
        var events = m.Events.Select(e => e.Clone()).ToList();
        int passes = MacroSequencer.Passes(m.RepeatMode, m.Repeat);
        _task = System.Threading.Tasks.Task.Run(() =>
        {
            try { MacroSequencer.Run(events, passes, Send, cts.Token); }
            catch (Exception ex) { App.Log("Макрос: " + ex.Message); }
            finally
            {
                System.Threading.Interlocked.CompareExchange(ref _playing, null, cts);
                cts.Dispose();
            }
        });
    }

    /// <summary>v5.6: «пока зажата» through the PC — plays in a loop until <see cref="Stop"/> (key released).</summary>
    public static void PlayWhileHeld(MacroSlot m)
    {
        var prev = System.Threading.Interlocked.Exchange(ref _playing, null);
        try { prev?.Cancel(); } catch (ObjectDisposedException) { }
        if (m.Events.Count == 0) return;
        var cts = new System.Threading.CancellationTokenSource();
        _playing = cts;
        var events = m.Events.Select(e => e.Clone()).ToList();
        _task = System.Threading.Tasks.Task.Run(() =>
        {
            try { MacroSequencer.Run(events, int.MaxValue, Send, cts.Token); }
            catch (Exception ex) { App.Log("Макрос: " + ex.Message); }
            finally { System.Threading.Interlocked.CompareExchange(ref _playing, null, cts); cts.Dispose(); }
        });
    }

    /// <summary>Stops a playing macro without waiting (its held keys are released by the sequencer).</summary>
    public static void Stop()
    {
        var prev = System.Threading.Interlocked.Exchange(ref _playing, null);
        try { prev?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>v5.3: stops a playing macro and waits (briefly) until its held keys are released — called on exit,
    /// otherwise a key pressed by the macro could stay down after the app is gone.</summary>
    public static void StopAll(int waitMs = 1500)
    {
        var prev = System.Threading.Interlocked.Exchange(ref _playing, null);
        try { prev?.Cancel(); } catch (ObjectDisposedException) { }
        try { _task?.Wait(waitMs); } catch (AggregateException) { }
    }

    private static int _sendFailLogged;

    private const uint INPUT_MOUSE = 0;

    public static void Send(MacroEvent e)
    {
        var inp = new Native.INPUT();
        if (e.IsWheel)
        {
            inp.type = INPUT_MOUSE;
            inp.U.mi = new Native.MOUSEINPUT { dwFlags = 0x0800, mouseData = unchecked((uint)(e.WheelAmount * 120)), dwExtraInfo = Native.InjectedTag };
        }
        else if (e.Code == MouseCodes.Move)
        {
            int dx = e.Extra is { Length: >= 4 } x ? (short)(x[0] | x[1] << 8) : 0, dy = e.Extra is { Length: >= 4 } y ? (short)(y[2] | y[3] << 8) : 0;
            inp.type = INPUT_MOUSE;
            inp.U.mi = new Native.MOUSEINPUT { dx = dx, dy = dy, dwFlags = 0x0001, dwExtraInfo = Native.InjectedTag };
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
        if (Native.SendInput(1, new[] { inp }, System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>()) == 0 &&
            System.Threading.Interlocked.Exchange(ref _sendFailLogged, 1) == 0)
            App.Log("Windows не принял нажатие от программы (SendInput = 0). Обычно так бывает, когда активно окно, запущенное от администратора.");
    }
}

/// <summary>Watches the foreground window; <see cref="AutoSwitchEngine"/> decides (debounce, own window, fallback).</summary>
public sealed class ForegroundWatcher : IDisposable
{
    private readonly DispatcherTimer _timer;
    public AutoSwitchEngine Engine { get; } = new();
    public Func<ProfileStoreData?> Data { get; set; } = () => null;
    public Func<string> CurrentProfile { get; set; } = () => "";
    /// <summary>decision, process, title, exe path</summary>
    public event Action<AutoSwitchDecision, string, string>? Decided;
    public (string Process, string Title) Current { get; private set; } = ("", "");
    public string CurrentPath { get; private set; } = "";

    public ForegroundWatcher()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += (_, _) => Tick();
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    private void Tick()
    {
        try
        {
            var (proc, title, path, pid) = Native.ForegroundEx();
            // v5.6: our own windows (main window, dialogs, the notice) by process id — the exe may be renamed (VorotexK13Studio-v5.5.exe)
            if (pid == (uint)Environment.ProcessId || AppMatch.IsOwn(proc)) proc = AppMatch.OwnProcess;
            else if (proc.Length > 0 || title.Length > 0) { Current = (proc, title); CurrentPath = path; }
            if (Data() is not { } d) return;
            var dec = Engine.Observe(proc, title, Environment.TickCount64, d, CurrentProfile());
            if (dec != null) Decided?.Invoke(dec, proc, title);
        }
        catch (Exception ex) { App.Log("[auto] " + ex.Message); }
    }

    public void Dispose() => _timer.Stop();
}
