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

    /// <summary>The keypad was prepared → keys go through the PC.</summary>
    public bool Active => Keys.Count > 0;

    public int CodeColumn(byte code) => Keys.FirstOrDefault(k => k.Code == code)?.Column ?? -1;
    public bool IsMapped(int column) => Keys.Any(k => k.Column == column);

    public PcLayerState Clone() => new() { Keys = Keys.Select(k => new PcLayerKey { Column = k.Column, Code = k.Code }).ToList(), PreparedAt = PreparedAt };

    /// <summary>Drops anything a hand-edited / damaged file could contain: unknown columns, codes outside the pool, duplicates.</summary>
    public List<string> Normalize()
    {
        var notes = new List<string>();
        Keys ??= new();
        int before = Keys.Count;
        var cols = new HashSet<int>(); var codes = new HashSet<byte>();
        Keys = Keys.Where(k => k != null && PcLayerPlanner.Columns.Contains(k.Column) && PcLayerPlanner.Pool.Contains(k.Code) && cols.Add(k.Column) && codes.Add(k.Code)).ToList();
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
    public static List<PcLayerKey> Plan(IEnumerable<K13DeviceConfig> profiles)
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
        var pool = Pool.Where(p => !avoid.Contains(p)).ToArray();
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
    public static K13DeviceConfig Apply(K13DeviceConfig profile, PcLayerState state)
    {
        var c = profile.Clone();
        if (!state.Active) return c;
        foreach (var k in state.Keys) c.SetKey(k.Column, k.Code);
        var used = new HashSet<int>();
        for (int col = 0; col < K13Layout.KeymapColumns; col++)
            for (int row = 0; row < K13Layout.KeymapRows; row++)
                if (KeyCatalog.IsMacro(c.GetKey(col, row))) used.Add(KeyCatalog.MacroSlotOf(c.GetKey(col, row)));
        for (int s = 0; s < c.Macros.Count; s++)
            if (!used.Contains(s)) c.Macros[s] = new MacroSlot();
        return c;
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
        state.Active && state.Keys.All(k => keypad.GetKey(k.Column) == k.Code);
}

public enum PcOutKind { KeyDown, KeyUp, HostDown, HostUp, StartMacro, StartMacroHeld, StopMacro, Shell, FirmwareOnly }

/// <summary>One thing the hook must do for a keypad event.</summary>
public readonly record struct PcOut(PcOutKind Kind, ushort Vk = 0, int Slot = -1, string Shell = "", byte Code = 0);

/// <summary>
/// v5.6: turns «prepared keypad sends F15 ↓/↑» into actions of the active profile. Holding works like a normal key:
/// every OS auto-repeat down is passed on; macros start once. A release always undoes exactly what its press did,
/// even if the profile changed while the key was held. Runs on the keyboard-hook thread (no locks, no allocations of note).
/// </summary>
public sealed class PcLayerRouter
{
    private sealed record Held(PcKeyAction Action, bool ViaHost, MacroRepeatMode Mode);
    private Dictionary<ushort, int> _map = new();
    private readonly Dictionary<ushort, Held> _held = new();

    public void SetLayer(PcLayerState state)
    {
        var m = new Dictionary<ushort, int>();
        foreach (var k in state.Keys) { ushort v = KeyCatalog.ToVirtualKey(k.Code); if (v != 0) m[v] = k.Column; }
        _map = m;
    }

    public bool Active => _map.Count > 0;
    public int MappedCount => _map.Count;
    public bool Owns(ushort vk) => _map.ContainsKey(vk);
    public int HeldCount => _held.Count;

    /// <param name="isHostTrigger">The code is a PC-action trigger in the active profile (F13–F24 binding, chord, ring).</param>
    public List<PcOut> Route(ushort vk, bool down, K13DeviceConfig profile, Func<ushort, bool> isHostTrigger)
    {
        var o = new List<PcOut>(2);
        if (!_map.TryGetValue(vk, out int col)) { if (!down) _held.Remove(vk); return o; }
        if (down)
        {
            bool first = !_held.TryGetValue(vk, out var h);
            if (first)
            {
                var a = PcLayerPlanner.Resolve(profile, col);
                var mode = a.Kind == PcKeyKind.Macro && a.MacroSlot >= 0 && a.MacroSlot < profile.Macros.Count ? profile.Macros[a.MacroSlot].RepeatMode : MacroRepeatMode.Count;
                h = new Held(a, a.Kind == PcKeyKind.Key && isHostTrigger(a.Vk), mode);
                _held[vk] = h;
                switch (a.Kind)
                {
                    case PcKeyKind.Macro when a.MacroSlot < profile.Macros.Count && profile.Macros[a.MacroSlot].IsUsed:
                        o.Add(new PcOut(mode == MacroRepeatMode.WhileHeld ? PcOutKind.StartMacroHeld : PcOutKind.StartMacro, Slot: a.MacroSlot, Code: a.Code)); break;
                    case PcKeyKind.Shell: o.Add(new PcOut(PcOutKind.Shell, Shell: a.Shell, Code: a.Code)); break;
                    case PcKeyKind.FirmwareOnly: o.Add(new PcOut(PcOutKind.FirmwareOnly, Code: a.Code)); break;
                }
            }
            if (h!.Action.Kind == PcKeyKind.Key) o.Add(new PcOut(h.ViaHost ? PcOutKind.HostDown : PcOutKind.KeyDown, h.Action.Vk, Code: h.Action.Code));
        }
        else
        {
            if (!_held.Remove(vk, out var h)) return o;
            if (h.Action.Kind == PcKeyKind.Key) o.Add(new PcOut(h.ViaHost ? PcOutKind.HostUp : PcOutKind.KeyUp, h.Action.Vk, Code: h.Action.Code));
            else if (h.Action.Kind == PcKeyKind.Macro && h.Mode == MacroRepeatMode.WhileHeld) o.Add(new PcOut(PcOutKind.StopMacro, Slot: h.Action.MacroSlot));
        }
        return o;
    }

    /// <summary>Keys still held (e.g. on exit / when the mode is switched off) → their releases.</summary>
    public List<PcOut> ReleaseAll()
    {
        var o = new List<PcOut>();
        foreach (var h in _held.Values)
        {
            if (h.Action.Kind == PcKeyKind.Key) o.Add(new PcOut(h.ViaHost ? PcOutKind.HostUp : PcOutKind.KeyUp, h.Action.Vk));
            else if (h.Action.Kind == PcKeyKind.Macro && h.Mode == MacroRepeatMode.WhileHeld) o.Add(new PcOut(PcOutKind.StopMacro, Slot: h.Action.MacroSlot));
        }
        _held.Clear();
        return o;
    }
}
