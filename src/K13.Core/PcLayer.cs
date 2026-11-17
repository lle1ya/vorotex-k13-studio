namespace Vorotex.K13;

/// <summary>
/// v5.6: «Кнопки зависят от профиля (через ПК)». The keypad is prepared ONCE so that each of its keys sends its own
/// rare code (F14–F24); the app's keyboard hook turns that code into whatever the ACTIVE profile assigns to the key
/// (key, media, macro, PC action…). A profile switch then changes the keys instantly, without writing the keypad.
/// F13 stays reserved for the stick press (actions ring); the joystick and the stick press stay in the keypad.
/// </summary>
public sealed class PcLayerKey
{
    /// <summary>Keymap column of the physical key.</summary>
    public int Column { get; set; }
    /// <summary>Device code the keypad sends for it after preparation (one of <see cref="PcLayerPlanner.Pool"/>).</summary>
    public byte Code { get; set; }
}

public sealed class PcLayerState
{
    public List<PcLayerKey> Keys { get; set; } = new();
    public DateTime? PreparedAt { get; set; }

    /// <summary>0 = v5.6 scheme (keymap cells F14–F24 — this K13 firmware does NOT send them, proven 16.11.2026);
    /// 1 = v5.6.3 scheme: each key runs a keypad macro «Scroll Lock + F1…F11» that the PC recognises.</summary>
    public int Scheme { get; set; }
    public const int ChordScheme = 1;

    /// <summary>Prepared with the old F14–F24 scheme — must be prepared again.</summary>
    public bool Legacy => Active && Scheme != ChordScheme;

    /// <summary>The keypad was prepared → keys go through the PC.</summary>
    public bool Active => Keys.Count > 0;

    public int CodeColumn(byte code) => Keys.FirstOrDefault(k => k.Code == code)?.Column ?? -1;
    public bool IsMapped(int column) => Keys.Any(k => k.Column == column);

    public PcLayerState Clone() => new() { Keys = Keys.Select(k => new PcLayerKey { Column = k.Column, Code = k.Code }).ToList(), PreparedAt = PreparedAt, Scheme = Scheme };

    /// <summary>Drops anything a hand-edited / damaged file could contain: unknown columns, codes outside the pool, duplicates.</summary>
    public List<string> Normalize()
    {
        var notes = new List<string>();
        Keys ??= new();
        int before = Keys.Count;
        var cols = new HashSet<int>(); var codes = new HashSet<byte>();
        Keys = Keys.Where(k => k != null && PcLayerPlanner.Columns.Contains(k.Column) && (Scheme == ChordScheme ? PcLayerPlanner.ChordPool : PcLayerPlanner.Pool).Contains(k.Code) && cols.Add(k.Column) && codes.Add(k.Code)).ToList();
        if (Keys.Count != before) notes.Add($"Кнопки через ПК: отброшено неверных записей — {before - Keys.Count}.");
        if (Keys.Count == 0) PreparedAt = null;
        return notes;
    }
}

/// <summary>What a key does on the PC in the active profile.</summary>
public enum PcKeyKind { None, Key, Macro, Shell, FirmwareOnly }

public sealed record PcKeyAction(PcKeyKind Kind, byte Code, ushort Vk = 0, int MacroSlot = -1, string Shell = "")
{
    public static readonly PcKeyAction Nothing = new(PcKeyKind.None, KeyCatalog.Disabled);
}

public static class PcLayerPlanner
{
    /// <summary>F14…F24 — no ordinary keyboard has them; F13 is the stick press (ring).</summary>
    public static readonly byte[] Pool = Enumerable.Range(0x69, 11).Select(i => (byte)i).ToArray();

    /// <summary>v5.6.3 scheme. The K13 firmware only sends the standard 104-key codes (+ F13): a keymap cell with F14–F24
    /// is ignored and the key sends its factory code (log 16.11.2026 12:45:56: Tab key with F23 in its cell sent «Tab»).
    /// So each key runs a keypad macro «Scroll Lock ↓, F<i> ↓↑, Scroll Lock ↑» — only standard codes — and the PC turns
    /// «F<i> while Scroll Lock is held» into the active profile's action.</summary>
    public const byte ChordPrefix = 0x47;      // Scroll Lock
    public const ushort ChordPrefixVk = 0x91;
    public static readonly byte[] ChordPool = Enumerable.Range(0x3A, 11).Select(i => (byte)i).ToArray(); // F1…F11

    /// <summary>The keypad macro a prepared key runs in the chord scheme. v5.9.6: <paramref name="hold"/> sets the vendor's
    /// «пока зажата» bit (table byte 3 bit 7, mode1) — the keypad should then repeat the tap while the key is held.</summary>
    public static MacroSlot ChordMacro(byte fkey, bool hold = false) => new()
    {
        Repeat = 1,
        Mode1Bit = hold,
        Events =
        {
            new() { Down = true, Code = ChordPrefix, Delay = 1 }, new() { Down = true, Code = fkey, Delay = 1 },
            new() { Down = false, Code = fkey, Delay = 1 }, new() { Down = false, Code = ChordPrefix, Delay = 1 },
        },
    };

    /// <summary>A new state for «Подготовить клавиатуру» (chord scheme).</summary>
    public static PcLayerState NewState(IEnumerable<K13DeviceConfig> profiles)
    {
        var legacy = Plan(profiles.ToList(), ignoreAvoid: true);
        return new PcLayerState
        {
            Scheme = PcLayerState.ChordScheme,
            Keys = legacy.Select((k, i) => new PcLayerKey { Column = k.Column, Code = ChordPool[i] }).ToList(),
        };
    }

    /// <summary>Keymap cell a mapped key gets in the keypad.</summary>
    public static byte CellFor(PcLayerState state, PcLayerKey k) =>
        state.Scheme == PcLayerState.ChordScheme ? (byte)(KeyCatalog.FirstMacro + state.Keys.IndexOf(k)) : k.Code;

    /// <summary>The 12 keys (joystick and stick press stay in the keypad — they drive the actions ring).</summary>
    public static readonly int[] Columns = K13Layout.Controls.Where(c => c.Kind == ControlKind.Key).Select(c => c.Column).ToArray();

    /// <summary>Codes the PC cannot reproduce (lighting control, key lock, factory stick function).</summary>
    public static bool IsFirmwareOnly(byte code)
    {
        var k = KeyCatalog.Get(code);
        return k.Category == KeyCategory.Lighting || code is 0xF3 or 0xF4 || k.Category == KeyCategory.Unknown;
    }

    /// <summary>
    /// Which keys go through the PC. There are 12 keys and 11 free codes, so one key may stay in the keypad: the one
    /// that is the same in every profile (preferably a plain key), otherwise the one that differs least.
    /// </summary>
    public static List<PcLayerKey> Plan(IEnumerable<K13DeviceConfig> profiles) => Plan(profiles, ignoreAvoid: false);

    private static List<PcLayerKey> Plan(IEnumerable<K13DeviceConfig> profiles, bool ignoreAvoid)
    {
        var cfgs = profiles.Where(c => c != null).ToList();
        if (cfgs.Count == 0) cfgs.Add(new K13DeviceConfig());
        var scored = Columns.Select(col =>
        {
            var codes = cfgs.Select(c => c.GetKey(col)).ToList();
            int distinct = codes.Distinct().Count();
            bool macro = codes.Any(KeyCatalog.IsMacro);
            bool fw = codes.All(IsFirmwareOnly);
            // lower = better candidate to stay in the keypad
            int keepScore = (fw ? -1000 : 0) + (distinct - 1) * 100 + (macro ? 50 : 0) + (codes.Any(IsFirmwareOnly) ? -20 : 0);
            return (col, keepScore);
        }).ToList();
        // codes the joystick / stick press already send in some profile cannot double as key codes
        var avoid = new HashSet<byte>(cfgs.SelectMany(c => K13Layout.Controls.Where(k => !Columns.Contains(k.Column)).Select(k => c.GetKey(k.Column))));
        var pool = ignoreAvoid ? Pool : Pool.Where(p => !avoid.Contains(p)).ToArray();
        var stay = new HashSet<int>(scored.OrderBy(s => s.keepScore).ThenByDescending(s => s.col).Take(Math.Max(0, Columns.Length - pool.Length)).Select(s => s.col));
        // keys that only ever do firmware things (all profiles) are better left in the keypad too
        foreach (var s in scored.Where(s => s.keepScore <= -1000)) stay.Add(s.col);
        var result = new List<PcLayerKey>();
        int i = 0;
        foreach (int col in Columns)
        {
            if (stay.Contains(col) || i >= pool.Length) continue;
            result.Add(new PcLayerKey { Column = col, Code = pool[i++] });
        }
        return result;
    }

    /// <summary>
    /// What to write to the keypad in this mode: the profile, with every mapped key replaced by its code. Firmware macros
    /// that only mapped keys used are dropped (they run on the PC now), so switching profiles does not make the keypad «differ».
    /// </summary>
    public static K13DeviceConfig Apply(K13DeviceConfig profile, PcLayerState state, IReadOnlyCollection<int>? holdColumns = null)
    {
        var c = profile.Clone();
        if (!state.Active) return c;
        if (state.Scheme == PcLayerState.ChordScheme)
        {
            // every keypad macro slot is a chord now; a macro left on a key that stays in the keypad cannot be kept
            for (int col = 0; col < K13Layout.KeymapColumns; col++)
                for (int row = 0; row < K13Layout.KeymapRows; row++)
                    if (KeyCatalog.IsMacro(c.GetKey(col, row))) c.SetKey(col, KeyCatalog.Disabled, row);
            for (int s2 = 0; s2 < c.Macros.Count; s2++) c.Macros[s2] = new MacroSlot();
            for (int i = 0; i < state.Keys.Count && i < c.Macros.Count; i++)
            {
                c.SetKey(state.Keys[i].Column, (byte)(KeyCatalog.FirstMacro + i));
                c.Macros[i] = ChordMacro(state.Keys[i].Code, holdColumns != null && holdColumns.Contains(state.Keys[i].Column));
            }
            return c;
        }
        foreach (var k in state.Keys) c.SetKey(k.Column, k.Code);
        var used = new HashSet<int>();
        for (int col = 0; col < K13Layout.KeymapColumns; col++)
            for (int row = 0; row < K13Layout.KeymapRows; row++)
                if (KeyCatalog.IsMacro(c.GetKey(col, row))) used.Add(KeyCatalog.MacroSlotOf(c.GetKey(col, row)));
        for (int s = 0; s < c.Macros.Count; s++)
            if (!used.Contains(s)) c.Macros[s] = new MacroSlot();
        return c;
    }

    /// <summary>v5.9.6: mapped keys (chord scheme) whose action is a «пока зажата» macro (incl. mouse «Автоклик») in any profile —
    /// their keypad tap macro gets the hold bit. One image for all profiles, so switching profiles still needs no write.</summary>
    public static HashSet<int> HoldColumns(IEnumerable<K13DeviceConfig?> profiles, PcLayerState state)
    {
        var set = new HashSet<int>();
        if (!state.Active || state.Scheme != PcLayerState.ChordScheme) return set;
        foreach (var p in profiles)
        {
            if (p == null) continue;
            foreach (var k in state.Keys)
            {
                var a = Resolve(p, k.Column);
                if (a.Kind == PcKeyKind.Macro && a.MacroSlot >= 0 && a.MacroSlot < p.Macros.Count && p.Macros[a.MacroSlot].IsUsed &&
                    p.Macros[a.MacroSlot].RepeatMode == MacroRepeatMode.WhileHeld) set.Add(k.Column);
            }
        }
        return set;
    }

    /// <summary>v5.9.6: mapped keys whose tap macro in the keypad carries the hold bit (from the last read / written image).</summary>
    public static HashSet<int> HoldColumnsWritten(PcLayerState state, K13DeviceConfig? keypad)
    {
        var set = new HashSet<int>();
        if (keypad == null || !state.Active || state.Scheme != PcLayerState.ChordScheme) return set;
        for (int i = 0; i < state.Keys.Count && i < keypad.Macros.Count; i++)
            if (keypad.GetKey(state.Keys[i].Column) == CellFor(state, state.Keys[i]) && keypad.Macros[i].Mode1Bit &&
                SameMacro(keypad.Macros[i], ChordMacro(state.Keys[i].Code))) set.Add(state.Keys[i].Column);
        return set;
    }

    /// <summary>What the key in <paramref name="column"/> must do on the PC in this profile.</summary>
    public static PcKeyAction Resolve(K13DeviceConfig profile, int column)
    {
        byte code = profile.GetKey(column);
        if (code == KeyCatalog.Disabled) return PcKeyAction.Nothing;
        if (KeyCatalog.IsMacro(code)) return new PcKeyAction(PcKeyKind.Macro, code, MacroSlot: KeyCatalog.MacroSlotOf(code));
        switch (code)
        {
            case 0xD7: return new PcKeyAction(PcKeyKind.Shell, code, Shell: "computer");
            case 0xD9: return new PcKeyAction(PcKeyKind.Shell, code, Shell: "calc");
            case 0xDE: return new PcKeyAction(PcKeyKind.Shell, code, Shell: "minimize");
            case 0xDF: return new PcKeyAction(PcKeyKind.Shell, code, Shell: "screenshot");
            case 0x58: return new PcKeyAction(PcKeyKind.Key, code, 0x0D); // Num Enter → Enter
        }
        if (IsFirmwareOnly(code)) return new PcKeyAction(PcKeyKind.FirmwareOnly, code);
        ushort vk = KeyCatalog.ToVirtualKey(code);
        return vk != 0 ? new PcKeyAction(PcKeyKind.Key, code, vk) : new PcKeyAction(PcKeyKind.FirmwareOnly, code);
    }

    /// <summary>Short text for the UI: what the key does through the PC.</summary>
    public static string Describe(PcKeyAction a) => a.Kind switch
    {
        PcKeyKind.None => "ничего",
        PcKeyKind.Macro => $"макрос {a.MacroSlot + 1} (выполняет компьютер)",
        PcKeyKind.FirmwareOnly => KeyCatalog.Name(a.Code) + " — только в режиме «в клавиатуре»",
        _ => KeyCatalog.Name(a.Code),
    };

    /// <summary>A key whose keypad contents do not match what the UI shows for the active profile.</summary>
    public sealed record Mismatch(int Column, byte Expected, byte InKeypad);

    /// <summary>
    /// Compares the keypad keymap (row 0) with what the active profile needs in the current mode — so the UI can warn
    /// instead of silently showing one thing while the keypad does another.
    /// </summary>
    public static List<Mismatch> Compare(K13DeviceConfig profile, PcLayerState state, K13DeviceConfig keypad)
    {
        var want = Apply(profile, state);
        var list = new List<Mismatch>();
        foreach (var c in K13Layout.Controls.Where(c => c.Kind != ControlKind.Hidden || c.Column == 16))
        {
            byte w = want.GetKey(c.Column), k = keypad.GetKey(c.Column);
            if (w != k) list.Add(new Mismatch(c.Column, w, k));
        }
        return list;
    }

    /// <summary>Does the keypad look prepared for this state (every mapped key sends its code)?</summary>
    public static bool LooksPrepared(PcLayerState state, K13DeviceConfig keypad) =>
        state.Active && state.Keys.All(k => keypad.GetKey(k.Column) == CellFor(state, k)) &&
        (state.Scheme != PcLayerState.ChordScheme || state.Keys.Select((k, i) => (k, i)).All(p => p.i < keypad.Macros.Count && SameMacro(keypad.Macros[p.i], ChordMacro(p.k.Code))));

    private static bool SameMacro(MacroSlot a, MacroSlot b) =>
        a.Events.Count == b.Events.Count && a.Events.Zip(b.Events).All(p => p.First.Down == p.Second.Down && p.First.Code == p.Second.Code);
}

/// <summary>StartMacroLoop (v5.9.4): «пока зажата» in the chord scheme — the PC cannot see a hold there (the keypad sends a
/// ~16 ms Scroll Lock+F tap), so the macro loops until the same key is pressed again, like «до повторного нажатия».
/// Note (v5.9.6): a log line for studio.log in <see cref="PcOut.Shell"/>; Slot = column; Code = 1 «keypad repeats»,
/// 0 «keypad does not repeat», 2 = information only.</summary>
public enum PcOutKind { KeyDown, KeyUp, HostDown, HostUp, StartMacro, StartMacroHeld, StopMacro, Shell, FirmwareOnly, StartMacroLoop, Note }

/// <summary>One thing the hook must do for a keypad event.</summary>
public readonly record struct PcOut(PcOutKind Kind, ushort Vk = 0, int Slot = -1, string Shell = "", byte Code = 0);

/// <summary>
/// v5.6: turns «prepared keypad sends F15 ↓/↑» into actions of the active profile. Holding works like a normal key:
/// every OS auto-repeat down is passed on; macros start once. A release always undoes exactly what its press did,
/// even if the profile changed while the key was held. Runs on the keyboard-hook thread (no locks, no allocations of note).
/// </summary>
public sealed class PcLayerRouter
{
    private sealed record Held(PcKeyAction Action, bool ViaHost, MacroRepeatMode Mode) { public long LastMs; public bool WasHold; }

    /// <summary>OS auto-repeat sends downs every ≤ 0.5 s (first one after ≤ 1 s). A down that comes later than this
    /// after the previous one is a NEW press whose release was lost (hook skipped by Windows, sleep, UAC desktop…) —
    /// v5.6 kept replaying the old action (e.g. «1С» Tab) for it instead of the active profile's.</summary>
    public const int StaleMs = 1500;
    private Dictionary<ushort, int> _map = new();
    private readonly Dictionary<ushort, Held> _held = new();

    private bool _chord, _prefix;
    private long _prefixUpAt = long.MinValue / 2;
    private readonly HashSet<ushort> _chordDown = new();
    /// <summary>F&lt;i&gt; counts as a keypad key this long after Scroll Lock was released (macro events come ~8 ms apart).</summary>
    public const int ChordGraceMs = 120;

    public void SetLayer(PcLayerState state)
    {
        _chord = state.Scheme == PcLayerState.ChordScheme && state.Active;
        _prefix = false; _chordDown.Clear();
        var m = new Dictionary<ushort, int>();
        foreach (var k in state.Keys) { ushort v = KeyCatalog.ToVirtualKey(k.Code); if (v != 0) m[v] = k.Column; }
        _map = m;
    }

    public bool Active => _map.Count > 0;
    public int MappedCount => _map.Count;
    public bool Owns(ushort vk) => _map.ContainsKey(vk);
    public int HeldCount => _held.Count;

    /// <param name="isHostTrigger">The code is a PC-action trigger in the active profile (F13–F24 binding, chord, ring).</param>
    /// <summary>
    /// Every hook event goes here. <paramref name="swallow"/> = the event belongs to the keypad layer (do not pass it on).
    /// Chord scheme: Scroll Lock is always taken while the mode is on; F1…F11 only while it is held (or right after).
    /// </summary>
    public List<PcOut> Feed(ushort vk, bool down, K13DeviceConfig profile, Func<ushort, bool> isHostTrigger, long? nowMs, out bool swallow)
    {
        if (!_chord)
        {
            swallow = Owns(vk);
            return swallow ? Route(vk, down, profile, isHostTrigger, nowMs) : new List<PcOut>();
        }
        if (vk == PcLayerPlanner.ChordPrefixVk)
        {
            swallow = true;
            if (down) _prefix = true; else { _prefix = false; _prefixUpAt = nowMs ?? _prefixUpAt; }
            return new List<PcOut>();
        }
        if (down && _map.ContainsKey(vk) && (_prefix || _chordDown.Contains(vk) || nowMs is long t && t - _prefixUpAt <= ChordGraceMs))
        {
            swallow = true; _chordDown.Add(vk);
            return Route(vk, true, profile, isHostTrigger, nowMs);
        }
        if (!down && _chordDown.Remove(vk))
        {
            swallow = true;
            return Route(vk, false, profile, isHostTrigger, nowMs);
        }
        swallow = false;
        return new List<PcOut>();
    }

    public bool IsChord => _chord;

    // ── v5.9.6: hold through the PC. A key whose tap macro carries the hold bit should make the keypad repeat the
    // Scroll Lock+F tap while held; the key counts as held while taps keep coming and is released when they stop.
    public const int DefaultGapMs = 300, MinGapMs = 150, MaxGapMs = 600;
    private HashSet<int> _holdWant = new(), _holdWritten = new();
    private readonly HashSet<int> _noRepeat = new(), _repeats = new(), _toldWrite = new();
    private readonly Dictionary<int, int> _period = new();
    private sealed class TapStream { public int Col; public long First, Last; public int Taps; public readonly List<int> Gaps = new(); }
    private readonly Dictionary<ushort, TapStream> _streams = new();
    public int StreamCount => _streams.Count;

    /// <summary>Gap after the last tap that ends a hold: 4 × the measured tap period, else <see cref="DefaultGapMs"/>.</summary>
    public int GapFor(int col) => _period.TryGetValue(col, out int p) ? Math.Clamp(p * 4, MinGapMs, MaxGapMs) : DefaultGapMs;
    public bool KnownNoRepeat(int col) => _noRepeat.Contains(col);

    /// <param name="want">Columns whose action is a «пока зажата» macro (what «Записать» writes with the hold bit).</param>
    /// <param name="written">Columns whose tap macro in the keypad has the hold bit (last read / written image).</param>
    public void SetHold(IEnumerable<int> want, IEnumerable<int> written)
    {
        var w = new HashSet<int>(want); var wr = new HashSet<int>(written);
        if (!wr.SetEquals(_holdWritten)) { _noRepeat.Clear(); _repeats.Clear(); _period.Clear(); }
        if (!w.SetEquals(_holdWant)) _toldWrite.Clear();
        _holdWant = w; _holdWritten = wr;
    }

    private bool StreamCol(int col) => _chord && _holdWritten.Contains(col);

    /// <summary>Ends holds whose taps stopped (call every ~50 ms while <see cref="StreamCount"/> &gt; 0).</summary>
    public List<PcOut> Tick(long nowMs)
    {
        var o = new List<PcOut>();
        foreach (var (vk, s) in _streams.ToList())
        {
            int gap = GapFor(s.Col);
            if (nowMs - s.Last <= gap) continue;
            _streams.Remove(vk);
            _held.Remove(vk, out var h);
            if (s.Taps >= 2)
            {
                var sorted = s.Gaps.OrderBy(x => x).ToList();
                int med = sorted[sorted.Count / 2];
                _period[s.Col] = med;
                o.Add(new PcOut(PcOutKind.Note, Slot: s.Col, Code: 2, Shell:
                    $"[hold] кнопка {s.Col}: отпущена — сигналов {s.Taps} за {s.Last - s.First} мс, интервал ≈ {med} мс (мин {sorted[0]}, макс {sorted[^1]}); конец удержания — пауза больше {gap} мс"));
                if (h != null) { if (h.WasHold) o.Add(new PcOut(PcOutKind.StopMacro, Slot: h.Action.MacroSlot)); else Release(h, o); }
            }
            else if (h != null && h.Action.Kind == PcKeyKind.Macro && h.Mode == MacroRepeatMode.WhileHeld && !_repeats.Contains(s.Col))
            {
                // one tap only: the keypad does not repeat the macro → keep it running, the next press stops it (toggle)
                _noRepeat.Add(s.Col);
                o.Add(new PcOut(PcOutKind.Note, Slot: s.Col, Code: 0, Shell:
                    $"[hold] кнопка {s.Col}: за {gap} мс после нажатия пришёл только один сигнал — клавиатура НЕ повторяет макрос с битом «пока зажата». " +
                    "Кнопка работает как «до повторного нажатия»: макрос продолжается, следующее нажатие остановит."));
            }
            else if (h != null) Release(h, o);
        }
        return o;
    }

    /// <summary>v5.9.4: «пока зажата» cannot be detected in this layer state (chord scheme) and runs as «до повторного нажатия».</summary>
    public static bool HoldBecomesToggle(PcLayerState state) => state.Active && state.Scheme == PcLayerState.ChordScheme;

    /// <param name="nowMs">Event time (ms, monotonic); null = treat every down while held as auto-repeat.</param>
    public List<PcOut> Route(ushort vk, bool down, K13DeviceConfig profile, Func<ushort, bool> isHostTrigger, long? nowMs = null)
    {
        var o = new List<PcOut>(2);
        if (!_map.TryGetValue(vk, out int col)) { if (!down) _held.Remove(vk); return o; }
        bool stream = StreamCol(col) && nowMs is long;
        if (stream && _streams.TryGetValue(vk, out var ts))
        {
            // a repeated tap of a held key (or the release of a tap): the key stays held until the taps stop
            if (down)
            {
                int gap = (int)(nowMs!.Value - ts.Last);
                ts.Gaps.Add(gap); ts.Last = nowMs.Value; ts.Taps++;
                if (ts.Taps == 2 && _repeats.Add(col))
                {
                    _noRepeat.Remove(col);
                    o.Add(new PcOut(PcOutKind.Note, Slot: col, Code: 1, Shell: $"[hold] кнопка {col}: клавиатура повторяет сигнал, пока кнопка зажата (первый интервал {gap} мс) — «пока зажата» работает по удержанию"));
                }
            }
            return o;
        }
        if (stream && !down) return o;
        if (down)
        {
            bool first = !_held.TryGetValue(vk, out var h);
            if (!first && nowMs is long t && t - h!.LastMs > StaleMs)
            {
                // lost release: finish the old press, then handle this one as a fresh press in the ACTIVE profile
                _held.Remove(vk);
                Release(h, o);
                first = true;
            }
            if (first)
            {
                var a = PcLayerPlanner.Resolve(profile, col);
                var mode = a.Kind == PcKeyKind.Macro && a.MacroSlot >= 0 && a.MacroSlot < profile.Macros.Count ? profile.Macros[a.MacroSlot].RepeatMode : MacroRepeatMode.Count;
                // v5.9.4: chord scheme = the release comes ~16 ms after the press whatever the finger does → hold works as toggle.
                // v5.9.6: unless the tap macro has the hold bit (keypad repeats the tap) and the keypad was not seen ignoring it.
                bool loop = _chord && mode == MacroRepeatMode.WhileHeld && (!stream || _noRepeat.Contains(col));
                if (loop) mode = MacroRepeatMode.Toggle;
                if (_chord && a.Kind == PcKeyKind.Macro && profile.Macros.ElementAtOrDefault(a.MacroSlot)?.RepeatMode == MacroRepeatMode.WhileHeld && !_holdWritten.Contains(col) && _toldWrite.Add(col))
                    o.Add(new PcOut(PcOutKind.Note, Slot: col, Code: 2, Shell: $"[hold] кнопка {col}: «пока зажата» — в клавиатуре ещё нет бита повтора для этой кнопки, работает как «до повторного нажатия». Нажмите «Записать»."));
                if (stream) _streams[vk] = new TapStream { Col = col, First = nowMs!.Value, Last = nowMs.Value, Taps = 1 };
                h = new Held(a, a.Kind == PcKeyKind.Key && isHostTrigger(a.Vk), mode) { WasHold = a.Kind == PcKeyKind.Macro && profile.Macros.ElementAtOrDefault(a.MacroSlot)?.RepeatMode == MacroRepeatMode.WhileHeld };
                LastResolved = a;
                _held[vk] = h;
                switch (a.Kind)
                {
                    case PcKeyKind.Macro when a.MacroSlot < profile.Macros.Count && profile.Macros[a.MacroSlot].IsUsed:
                        o.Add(new PcOut(loop ? PcOutKind.StartMacroLoop : mode == MacroRepeatMode.WhileHeld ? PcOutKind.StartMacroHeld : PcOutKind.StartMacro, Slot: a.MacroSlot, Code: a.Code)); break;
                    case PcKeyKind.Shell: o.Add(new PcOut(PcOutKind.Shell, Shell: a.Shell, Code: a.Code)); break;
                    case PcKeyKind.FirmwareOnly: o.Add(new PcOut(PcOutKind.FirmwareOnly, Code: a.Code)); break;
                }
            }
            if (nowMs is long n) h!.LastMs = n;
            if (h!.Action.Kind == PcKeyKind.Key) o.Add(new PcOut(h.ViaHost ? PcOutKind.HostDown : PcOutKind.KeyDown, h.Action.Vk, Code: h.Action.Code));
        }
        else
        {
            if (!_held.Remove(vk, out var h)) return o;
            Release(h, o);
        }
        return o;
    }

    /// <summary>The action resolved for the last fresh press (diagnostics).</summary>
    public PcKeyAction? LastResolved { get; private set; }

    public int ColumnOf(ushort vk) => _map.TryGetValue(vk, out int c) ? c : -1;

    private static void Release(Held h, List<PcOut> o)
    {
        if (h.Action.Kind == PcKeyKind.Key) o.Add(new PcOut(h.ViaHost ? PcOutKind.HostUp : PcOutKind.KeyUp, h.Action.Vk, Code: h.Action.Code));
        else if (h.Action.Kind == PcKeyKind.Macro && h.Mode == MacroRepeatMode.WhileHeld) o.Add(new PcOut(PcOutKind.StopMacro, Slot: h.Action.MacroSlot));
    }

    /// <summary>Keys still held (e.g. on exit / when the mode is switched off) → their releases.</summary>
    public List<PcOut> ReleaseAll()
    {
        _prefix = false; _chordDown.Clear(); _streams.Clear();
        var o = new List<PcOut>();
        foreach (var h in _held.Values) Release(h, o);
        _held.Clear();
        return o;
    }
}

/// <summary>v5.6.2: what a key event that physically came from the K13 (Raw Input) says about the «через ПК» mode.</summary>
public enum PcHealth { Routed, Native, Unexpected }

public static class PcLayerHealth
{
    /// <param name="keypad">What the keypad holds (last read / written image); null = unknown.</param>
    public static PcHealth Check(PcLayerState state, K13DeviceConfig? keypad, ushort vk)
    {
        if (state.Keys.Any(k => KeyCatalog.ToVirtualKey(k.Code) == vk)) return PcHealth.Routed;
        if (state.Scheme == PcLayerState.ChordScheme && vk == PcLayerPlanner.ChordPrefixVk) return PcHealth.Routed;
        if (vk == ProfileMigrations.StickVk) return PcHealth.Native;
        if (keypad != null)
            for (int col = 0; col < K13Layout.KeymapColumns; col++)
                if (!state.IsMapped(col) && KeyCatalog.ToVirtualKey(keypad.GetKey(col)) == vk) return PcHealth.Native;
        return PcHealth.Unexpected;
    }

    /// <summary>Raw Input device path of the VOROTEX K13 (any of its HID collections).</summary>
    public static bool IsK13Path(string path) => path.Contains("VID_30FA&PID_2350", StringComparison.OrdinalIgnoreCase);

    /// <summary>Blocks the write would change (empty = keypad already matches; nothing to write).</summary>
    public static List<WriteBlock> PendingWrite(K13DeviceConfig target, byte[] keypadImage)
    {
        K13MemoryCodec.Encode(K13Mode.PrepareTarget(target, keypadImage), keypadImage, out var plan);
        return K13Protocol.DiffPlan(plan, keypadImage);
    }
}
