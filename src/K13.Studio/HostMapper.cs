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
    private sealed record LayerSnapshot(PcLayerState State, K13DeviceConfig Cfg, string Profile, int[] HoldWant, Dictionary<int, byte> HoldWritten);
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
        // v5.10: never write the file on the hook thread — the first K13 press after start-up wrote «первое событие…» here,
        // together with the JIT of the routing code that made Windows time the hook out: the press leaked to the active window
        // and came back through Raw Input («мимо режима»).
        Post(() => App.Log("[hook] " + msg));
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
            Warmup();
            _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
            int err = Marshal.GetLastWin32Error();
            if (_hook == IntPtr.Zero) Diag($"НЕ установлен: SetWindowsHookEx(WH_KEYBOARD_LL) ошибка Win32 {err}");
            else Diag($"установлен: WH_KEYBOARD_LL, поток {_threadId}, процесс {Environment.ProcessId}, профиль «{_profileName}», " +
                      $"привязка F13: {DescribeF13(_settings)}");
            ready.Set();
            if (_hook == IntPtr.Zero) return;
            while (GetMessage(out var m, IntPtr.Zero, 0, 0) > 0)
            {
                if (m.message == WM_APP_MISSED)
                {
                    ushort mvk = (ushort)m.wParam; bool mdown = m.lParam != IntPtr.Zero;
                    if (TakeLate(mvk, mdown)) { if (_dupLogs++ < 20) Diag($"повтор VK 0x{mvk:X2} через Raw Input отброшен — перехватчик уже выполнил это нажатие"); continue; }
                    _routingMissed = true;
                    try { RouteLayer(mvk, mdown, (uint)Environment.TickCount); } catch { }
                    finally { _routingMissed = false; }
                    MaybeReinstall();
                }
            }
            Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            Diag("снят");
        }) { IsBackground = true, Name = "K13 keyboard hook", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        ready.Wait(3000);
    }

    // ── v5.10: hook reliability ──────────────────────────────────────────────────────────────────────────────────────────
    private const uint LateHookMs = 150;
    private readonly List<(ushort Vk, bool Down, int At)> _late = new();   // hook thread only
    private int _dupLogs, _missedSinceHook, _missedFirstAt; private long _eventsAtMiss;

    private void NoteLate(ushort vk, bool down)
    {
        int now = Environment.TickCount;
        _late.RemoveAll(x => now - x.At > 2000);
        if (_late.Count < 64) _late.Add((vk, down, now));
    }

    private bool TakeLate(ushort vk, bool down)
    {
        int now = Environment.TickCount;
        _late.RemoveAll(x => now - x.At > 2000);
        int i = _late.FindIndex(x => x.Vk == vk && x.Down == down);
        if (i < 0) return false;
        _late.RemoveAt(i);
        return true;
    }

    /// <summary>Windows silently removes a low-level hook that timed out several times. If K13 presses keep coming only through
    /// Raw Input (8 events within 5 s, no hook call meanwhile), install the hook again.</summary>
    private void MaybeReinstall()
    {
        int now = Environment.TickCount; long ev = Interlocked.Read(ref _events);
        if (_missedSinceHook == 0 || ev != _eventsAtMiss || now - _missedFirstAt > 5000) { _missedSinceHook = 0; _missedFirstAt = now; _eventsAtMiss = ev; }
        if (++_missedSinceHook < 8) return;
        _missedSinceHook = 0;
        if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
        Diag(_hook == IntPtr.Zero ? "переустановить перехватчик не удалось" : "перехватчик не получал кнопки K13 — установлен заново");
    }

    /// <summary>JIT-compiles the hook path before the hook is installed (the first call must not be slow).</summary>
    private void Warmup()
    {
        try
        {
            foreach (var name in new[] { nameof(HookProc), nameof(RouteLayer), nameof(Do), nameof(Handle), nameof(NoteLate), nameof(TakeLate), nameof(Post) })
                foreach (var mi in typeof(HostMapper).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public).Where(m => m.Name == name && !m.IsGenericMethodDefinition))
                    System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(mi.MethodHandle);
            // run the router once on a scratch instance (generic dictionaries, records, LINQ in Feed/Route)
            var r = new PcLayerRouter();
            var cfg = K13MemoryCodec.Decode(MainWindow.DefaultImage);
            var st = new PcLayerState { Scheme = PcLayerState.ChordScheme, Keys = { new PcLayerKey { Column = 0, Code = PcLayerPlanner.ChordPool[0] } } };
            r.SetLayer(st); r.SetHold(Array.Empty<int>(), new Dictionary<int, byte>());
            ushort f = KeyCatalog.ToVirtualKey(PcLayerPlanner.ChordPool[0]);
            foreach (var (v, d) in new (ushort, bool)[] { (PcLayerPlanner.ChordPrefixVk, true), (f, true), (f, false), (PcLayerPlanner.ChordPrefixVk, false) })
                r.Feed(v, d, cfg, _ => false, 1000, out _);
            NoteLate(0, true); TakeLate(0, true);
            _ = Marshal.SizeOf<Native.KBDLLHOOKSTRUCT>();
        }
        catch (Exception ex) { App.Log("[hook] прогрев: " + ex.Message); }
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
    public void UpdateLayer(PcLayerState state, K13DeviceConfig profileCfg, string profileName, IEnumerable<int>? holdWant = null, IReadOnlyDictionary<int, byte>? holdWritten = null, bool hostOnly = false)
    {
        if (!state.Active) { if (_layer != null) App.Log(_hostOnly ? "[hook] действий на ПК через Scroll Lock + F нет" : "[hook] кнопки через ПК выключены"); _layer = null; _hostOnly = hostOnly; return; }
        if (hostOnly != _hostOnly) { _hostOnly = hostOnly; _layer = null; }
        bool was = _layer != null;
        var hw = (holdWant ?? Array.Empty<int>()).OrderBy(x => x).ToArray();
        var hr = new Dictionary<int, byte>(holdWritten ?? new Dictionary<int, byte>());
        var prev = _layer;
        string Desc(Dictionary<int, byte> d) => string.Join(", ", d.OrderBy(x => x.Key).Select(x => $"{x.Key} → {KeyCatalog.Name(x.Value)}"));
        if (!hostOnly && (prev == null || !prev.HoldWant.SequenceEqual(hw) || Desc(prev.HoldWritten) != Desc(hr)))
            App.Log($"[hold] «пока зажата» через ПК: кнопки с удержанием [{string.Join(", ", hw)}], код удержания записан в клавиатуре [{Desc(hr)}]");
        _layer = new LayerSnapshot(state.Clone(), profileCfg.Clone(), profileName, hw, hr);
        if (!was) App.Log(hostOnly ? $"[hook] действия на ПК через Scroll Lock + F: {state.Keys.Count} кнопок, профиль «{profileName}»"
                                   : $"[hook] кнопки через ПК: {state.Keys.Count} кнопок, профиль «{profileName}»");
    }

    public bool LayerActive => _layer != null;
    private bool _hostOnly; // v5.10: the routed state is only the PC-action keys (mode «через ПК» off)

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

    private readonly object _routerLock = new();

    private bool RouteLayer(ushort vk, bool down, long? timeMs = null)
    {
        lock (_routerLock) return RouteLayerCore(vk, down, timeMs); // uncontended except for KeypadGone()
    }

    /// <summary>v5.10: the keypad was unplugged — its releases will never come: stop a playing macro, release held keys,
    /// forget a half-finished Scroll Lock chord (otherwise the real keyboard's F keys could be taken).</summary>
    public void KeypadGone()
    {
        lock (_routerLock)
        {
            var outs = _router.ReleaseAll();
            foreach (var o in outs) Do(o, null);
            _routedDown.Clear();
        }
        Post(() => { MacroPlayer.StopEverything("клавиатура отключена"); PointerDriver.Stop("клавиатура отключена"); });
        Diag("клавиатура отключена — удержания сброшены");
    }

    private bool RouteLayerCore(ushort vk, bool down, long? timeMs)
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
        bool acted = outs.Any(x => x.Kind is not (PcOutKind.Note or PcOutKind.Alive));
        if (!acted) fresh = false; // v5.9.6: repeated taps of a held key — nothing new happened
        if (down && acted && (fresh || !ReferenceEquals(before, _router.LastResolved)) && _pressLogs < 400)
        {
            _pressLogs++;
            int col = _router.ColumnOf(vk); var a = _router.LastResolved; string prof = l.Profile;
            Post(() => App.Log($"[hook] кнопка {col} (VK 0x{vk:X2}) в профиле «{prof}» → {a?.Kind} {(a == null ? "" : KeyCatalog.Name(a.Code))}: " + string.Join(", ", outs.Where(x => x.Kind != PcOutKind.Alive).Select(x => x.Kind))));
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
                // v5.9.7: start / heartbeat / stop all go through the same ordered worker (was: UI thread — a busy UI could delay the stop)
                int slotKey = o.Slot;
                Post(() => { if (held) MacroPlayer.PlayWhileHeld(m); else MacroPlayer.Play(m, slotKey); });
                break;
            case PcOutKind.StopMacro: Post(() => MacroPlayer.Stop("отпускание")); break;
            case PcOutKind.Alive: Post(MacroPlayer.KeepAlive); break;
            case PcOutKind.Note:
                {
                    string text = o.Shell; int col = o.Slot; int code = o.Code;
                    Post(() => App.Log(text));
                    var ul = UiLog; if (ul != null) _ui.BeginInvoke(() => ul(text));
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
                        if (RouteLayer(vk, down0, k.time))
                    {
                        // v5.10: a slow hook call may have been timed out by Windows → the same event comes back via Raw Input
                        uint took = (uint)Environment.TickCount - k.time;
                        if (took > LateHookMs) NoteLate(vk, down0);
                        return (IntPtr)1;
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
        // 1.3.0: any other action of the keypad stops a running «старт/стоп» scroll or cursor
        if (a.Kind is not (HostActionKind.Scroll or HostActionKind.MouseMove)) PointerDriver.Stop("нажата другая кнопка с действием");
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
                case HostActionKind.Scroll or HostActionKind.MouseMove:
                    if (PointerSpec.From(a) is { } ps) PointerDriver.Tap(ps, Log);
                    break;
                case HostActionKind.PlayMacro:
                    if (int.TryParse(a.Value, out int slot) && MacroProvider?.Invoke(slot) is { IsUsed: true } m) MacroPlayer.Play(m, slot);
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

/// <summary>Replays a keypad macro on the PC with SendInput (keys + mouse buttons + wheel).
/// 1.3.0: every button (macro slot) has its own playback — two looped macros from different buttons run at the same time and
/// each stops with its own button. Only «пока зажата» (the one hold code) stays a single play.</summary>
public static class MacroPlayer
{
    private sealed record Play_(System.Threading.CancellationTokenSource Cts, System.Threading.Tasks.Task Task);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, Play_> _plays = new();
    /// <summary>Key of the «пока зажата» play (fed by the hold code; one at a time by design).</summary>
    public const int HeldKey = -100;
    /// <summary>Key for callers that do not know the slot (one shared play, the old behaviour).</summary>
    public const int AnyKey = -1;

    /// <summary>Plays the macro the full saved number of times (same value as on the keypad); «по кругу» repeats
    /// until triggered again. Triggering THE SAME button (<paramref name="key"/> = macro slot) again while it plays stops it;
    /// other buttons start their own macros alongside (1.3.0).</summary>
    public static void Play(MacroSlot m, int key = AnyKey)
    {
        if (_plays.TryRemove(key, out var prev)) { Cancel(prev.Cts); App.Log($"[macro] слот {key + 1}: повторное нажатие — стоп."); return; }
        if (m.Events.Count == 0) return;
        var cts = new System.Threading.CancellationTokenSource();
        var events = m.Events.Select(e => e.Clone()).ToList();
        int passes = MacroSequencer.Passes(m.RepeatMode, m.Repeat);
        var gate = new System.Threading.ManualResetEventSlim(false);
        var task = new System.Threading.Tasks.Task(() =>
        {
            try { gate.Wait(); MacroSequencer.Run(events, passes, Send, cts.Token); }
            catch (Exception ex) { App.Log("Макрос: " + ex.Message); }
            finally
            {
                if (_plays.TryGetValue(key, out var cur) && cur.Cts == cts) _plays.TryRemove(new KeyValuePair<int, Play_>(key, cur));
                cts.Dispose();
            }
        });
        _plays[key] = new Play_(cts, task);
        if (passes == int.MaxValue && _plays.Count > 1) App.Log($"[macro] слот {key + 1} по кругу; одновременно играют: {string.Join(", ", _plays.Keys.Select(Name))}.");
        task.Start(); gate.Set();
    }

    private static string Name(int key) => key == HeldKey ? "«пока зажата»" : key == AnyKey ? "без слота" : $"слот {key + 1}";

    private static void Cancel(System.Threading.CancellationTokenSource c) { try { c.Cancel(); } catch (ObjectDisposedException) { } }

    /// <summary>v5.9.7 safety stop: a «пока зажата» play fed by keypad taps (<see cref="KeepAlive"/>) stops by itself when no
    /// tap came for this long — even if the release was never detected (lost timer, router state, anything).</summary>
    public const int WatchdogMs = 1000;   // v5.9.8: heartbeat = OS auto-repeat of the held key (slowest ≈ 400 ms)
    private static long _lastAlive;            // Environment.TickCount64 of the last tap (0 = play not fed by taps)
    private static string? _stopReason;
    private static long _stopAsked;

    /// <summary>v5.6: «пока зажата» through the PC — plays in a loop until <see cref="Stop"/> (key released).</summary>
    public static void PlayWhileHeld(MacroSlot m)
    {
        if (_plays.TryRemove(HeldKey, out var prev)) Cancel(prev.Cts);
        System.Threading.Interlocked.Exchange(ref _lastAlive, 0); _stopReason = null; _stopAsked = 0;
        if (m.Events.Count == 0) return;
        var cts = new System.Threading.CancellationTokenSource();
        var events = m.Events.Select(e => e.Clone()).ToList();
        long started = Environment.TickCount64;
        var watch = new System.Threading.Timer(_ =>
        {
            long last = System.Threading.Interlocked.Read(ref _lastAlive);
            if (last == 0 || Environment.TickCount64 - last <= WatchdogMs) return;
            if (_plays.TryGetValue(HeldKey, out var cur) && cur.Cts == cts && _plays.TryRemove(new KeyValuePair<int, Play_>(HeldKey, cur)))
            {
                _stopReason = $"страховка: {Environment.TickCount64 - last} мс нет сигналов от K13"; _stopAsked = Environment.TickCount64;
                Cancel(cts);
            }
        }, null, 50, 50);
        var gate = new System.Threading.ManualResetEventSlim(false);
        var task = new System.Threading.Tasks.Task(() =>
        {
            int passes = 0;
            try { gate.Wait(); passes = MacroSequencer.Run(events, int.MaxValue, Send, cts.Token); }
            catch (Exception ex) { App.Log("Макрос: " + ex.Message); }
            finally
            {
                watch.Dispose();
                long last = System.Threading.Interlocked.Read(ref _lastAlive), now = Environment.TickCount64;
                if (last != 0)
                    App.Log($"[hold] макрос «пока зажата» остановлен ({_stopReason ?? "?"}): играл {now - started} мс, проходов {passes}; " +
                            $"после последнего сигнала K13 {now - last} мс, после команды остановки {(_stopAsked > 0 ? now - _stopAsked : -1)} мс");
                if (_plays.TryGetValue(HeldKey, out var cur) && cur.Cts == cts) _plays.TryRemove(new KeyValuePair<int, Play_>(HeldKey, cur));
                cts.Dispose();
            }
        });
        _plays[HeldKey] = new Play_(cts, task);
        task.Start(); gate.Set();
    }

    public static bool IsPlaying => !_plays.IsEmpty;
    public static bool IsHeldPlaying => _plays.ContainsKey(HeldKey);
    public static int PlayingCount => _plays.Count;
    public static bool IsPlayingKey(int key) => _plays.ContainsKey(key);

    /// <summary>v5.9.7: a keypad tap of the held key arrived — keeps the current «пока зажата» play alive (arms its watchdog).</summary>
    public static void KeepAlive()
    {
        if (_plays.ContainsKey(HeldKey)) System.Threading.Interlocked.Exchange(ref _lastAlive, Environment.TickCount64);
    }

    /// <summary>Stops the «пока зажата» play (the hold code was released) without waiting; its held keys are released by the sequencer.
    /// 1.3.0: looped macros of other buttons keep playing.</summary>
    public static void Stop(string? reason = null)
    {
        if (!_plays.TryRemove(HeldKey, out var prev)) return;
        if (reason != null) { _stopReason = reason; _stopAsked = Environment.TickCount64; }
        Cancel(prev.Cts);
    }

    /// <summary>1.3.0: stops every playing macro without waiting (keypad unplugged, PC actions switched off).</summary>
    public static void StopEverything(string reason)
    {
        foreach (var k in _plays.Keys.ToList())
            if (_plays.TryRemove(k, out var p)) { if (k == HeldKey) { _stopReason = reason; _stopAsked = Environment.TickCount64; } Cancel(p.Cts); }
    }

    /// <summary>v5.3: stops all playing macros and waits (briefly) until their held keys are released — called on exit,
    /// otherwise a key pressed by a macro could stay down after the app is gone.</summary>
    public static void StopAll(int waitMs = 1500)
    {
        var all = _plays.Values.ToList();
        _plays.Clear();
        foreach (var p in all) Cancel(p.Cts);
        try { System.Threading.Tasks.Task.WaitAll(all.Select(p => p.Task).ToArray(), waitMs); } catch (AggregateException) { } catch (ObjectDisposedException) { }
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
