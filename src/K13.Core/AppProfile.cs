using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vorotex.K13;

public enum HostActionKind { None, KeyCombo, Text, LaunchProgram, OpenUrl, SwitchProfile, Ring, PlayMacro }

/// <summary>A host-side action executed by the background mapper.</summary>
public sealed class HostAction
{
    public HostActionKind Kind { get; set; }
    /// <summary>KeyCombo: e.g. "Ctrl+Shift+Esc"; Text: text to type; LaunchProgram: path; OpenUrl: URL; SwitchProfile: profile name.</summary>
    public string Value { get; set; } = "";
    public string Arguments { get; set; } = "";
    public override string ToString() => Kind switch
    {
        HostActionKind.None => "—",
        HostActionKind.KeyCombo => "Комбинация: " + Value,
        HostActionKind.Text => "Текст: " + Value,
        HostActionKind.LaunchProgram => "Программа: " + Path.GetFileName(Value),
        HostActionKind.OpenUrl => "Ссылка: " + Value,
        HostActionKind.SwitchProfile => "Профиль: " + Value,
        HostActionKind.Ring => "Кольцо действий",
        HostActionKind.PlayMacro => "Макрос " + (int.TryParse(Value, out var n) ? n + 1 : 0) + " (через ПК)",
        _ => Kind.ToString()
    };
}

/// <summary>Host rule for one trigger key (the keypad sends a unique key, e.g. F13..F24, and the PC does the rest).</summary>
public sealed class HostKeyBinding
{
    /// <summary>Virtual-key code the keypad sends (e.g. 0x7C = F13).</summary>
    public ushort TriggerVk { get; set; }
    public HostAction Tap { get; set; } = new();
    public HostAction Hold { get; set; } = new();
    public int HoldMs { get; set; } = 400;
    /// <summary>Swallow the original key so applications do not see it.</summary>
    public bool Suppress { get; set; } = true;
}

/// <summary>Two trigger keys pressed together (e.g. joystick ↑ + ← = diagonal).</summary>
public sealed class HostChordBinding
{
    public string Title { get; set; } = "";
    public ushort VkA { get; set; }
    public ushort VkB { get; set; }
    public HostAction Action { get; set; } = new();
    public int WindowMs { get; set; } = 60;
}

public sealed class AppSwitchRule
{
    /// <summary>Process name without .exe (case-insensitive), empty = any.</summary>
    public string ProcessName { get; set; } = "";
    /// <summary>Substring of the window title, empty = any.</summary>
    public string TitleContains { get; set; } = "";
    public string ProfileName { get; set; } = "";
    /// <summary>v5.4: friendly program name for the UI («Adobe Photoshop 2025»); cosmetic.</summary>
    public string DisplayName { get; set; } = "";
    /// <summary>v5.4: full .exe path on THIS computer, only to show the program icon (never exported).</summary>
    public string IconPath { get; set; } = "";
}

public sealed class HostSettings
{
    public bool Enabled { get; set; } = true;
    public List<HostKeyBinding> Keys { get; set; } = new();
    public List<HostChordBinding> Chords { get; set; } = new();
    /// <summary>The «Кольцо действий» of this profile (shown while a ring trigger is held).</summary>
    public RingSettings Ring { get; set; } = RingSettings.CreateDefault();
}

/// <summary>One slot of the actions ring.</summary>
public sealed class RingSlot
{
    public string Title { get; set; } = "";
    /// <summary>Segoe Fluent Icons glyph.</summary>
    public string Glyph { get; set; } = "\uE734";
    public HostAction Action { get; set; } = new();
    public RingSlot Clone() => new() { Title = Title, Glyph = Glyph, Action = new HostAction { Kind = Action.Kind, Value = Action.Value, Arguments = Action.Arguments } };
}

/// <summary>Logitech-style actions ring: hold the trigger → ring around the cursor → point/tilt → release to run.</summary>
public sealed class RingSettings
{
    public const int MinSlots = 4, MaxSlots = 8;
    public List<RingSlot> Slots { get; set; } = new();
    /// <summary>Releasing later than this (ms) runs the highlighted slot; a quicker tap leaves the ring open for a click.</summary>
    public int TapThresholdMs { get; set; } = 250;
    public bool DarkStyle { get; set; } = true;

    private static RingSlot S(string title, string glyph, HostActionKind kind, string value) =>
        new() { Title = title, Glyph = glyph, Action = new HostAction { Kind = kind, Value = value } };

    public static RingSettings CreateDefault() => new()
    {
        Slots =
        {
            S("Копировать", "\uE8C8", HostActionKind.KeyCombo, "Ctrl+C"),
            S("Вставить", "\uE77F", HostActionKind.KeyCombo, "Ctrl+V"),
            S("Скриншот", "\uE722", HostActionKind.KeyCombo, "Win+Shift+S"),
            S("Диспетчер задач", "\uE9D9", HostActionKind.KeyCombo, "Ctrl+Shift+Esc"),
            S("Рабочий стол", "\uE7F4", HostActionKind.KeyCombo, "Win+D"),
            S("Плеер", "\uE768", HostActionKind.KeyCombo, "PlayPause"),
            S("Браузер", "\uE774", HostActionKind.KeyCombo, "BrowserHome"),
            S("Заблокировать", "\uE72E", HostActionKind.KeyCombo, "Win+L"),
        }
    };
}

/// <summary>Pure geometry of the ring (testable): slot 0 is at the top, clockwise.</summary>
public static class RingMath
{
    /// <summary>Slot under a pointer offset (screen coords, y down) or -1 inside the dead zone.</summary>
    public static int SlotAt(double dx, double dy, int count, double deadZone)
    {
        if (count <= 0 || dx * dx + dy * dy < deadZone * deadZone) return -1;
        double ang = Math.Atan2(dx, -dy) * 180 / Math.PI;     // 0 = up, clockwise
        if (ang < 0) ang += 360;
        double step = 360.0 / count;
        return (int)Math.Floor((ang + step / 2) / step) % count;
    }

    /// <summary>Slot for a joystick tilt (8 directions from 4 switches) or -1 if neutral.</summary>
    public static int SlotFromStick(bool up, bool down, bool left, bool right, int count)
    {
        int dx = (right ? 1 : 0) - (left ? 1 : 0), dy = (down ? 1 : 0) - (up ? 1 : 0);
        if (dx == 0 && dy == 0) return -1;
        return SlotAt(dx, dy, count, 0.1);
    }

    /// <summary>
    /// Joystick navigation with only 4 directions: a tilt jumps to the slot in that direction, or — if the highlight is
    /// already close to it (≤ a quarter of the ring) — moves ONE step towards it. So ↑ then → = the up-right diagonal,
    /// → again = right; every slot is reachable.
    /// </summary>
    public static int StepToward(int current, int target, int count)
    {
        if (count <= 0 || target < 0) return current;
        if (current < 0 || current >= count) return target;
        int cw = (target - current + count) % count, ccw = (current - target + count) % count;
        if (cw == 0) return current;
        if (Math.Min(cw, ccw) > Math.Max(1, (count + 3) / 4)) return target;
        return cw <= ccw ? (current + 1) % count : (current - 1 + count) % count;
    }

    /// <summary>Centre angle (degrees, 0 = up, clockwise) of a slot.</summary>
    public static double AngleOf(int slot, int count) => 360.0 * slot / count;
}

/// <summary>A user profile: device configuration (what goes to the keypad) + host-side rules.</summary>
public sealed class AppProfile
{
    public string Name { get; set; } = "Профиль";
    public K13DeviceConfig? Device { get; set; }
    public HostSettings Host { get; set; } = new();
    public string Comment { get; set; } = "";
    /// <summary>User names of macro slots (PC-side only; the keypad stores no names). Key = slot index.</summary>
    public Dictionary<int, string> MacroNames { get; set; } = new();
    /// <summary>Card accent colour (#RRGGBB) and icon glyph (Segoe Fluent Icons) – cosmetic.</summary>
    public string Accent { get; set; } = "";
    public string Glyph { get; set; } = "";

    /// <summary>v5.3: null-safe defaults for everything a JSON file may omit or null out; an invalid device part is dropped
    /// (the caller fills it from the default image) instead of crashing later.</summary>
    public List<string> Normalize()
    {
        var notes = new List<string>();
        Comment ??= ""; Accent ??= ""; Glyph ??= "";
        MacroNames ??= new();
        foreach (var k in MacroNames.Where(kv => kv.Key is < 0 or >= K13Layout.MacroSlots || string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key).ToList()) MacroNames.Remove(k);
        Host ??= new();
        Host.Keys ??= new(); Host.Keys.RemoveAll(k => k == null);
        Host.Chords ??= new(); Host.Chords.RemoveAll(c => c == null);
        foreach (var k in Host.Keys)
        {
            k.Tap = Fix(k.Tap); k.Hold = Fix(k.Hold);
            k.HoldMs = Math.Clamp(k.HoldMs, 100, 5000);
        }
        foreach (var c in Host.Chords) { c.Action = Fix(c.Action); c.Title ??= ""; }
        Host.Ring ??= RingSettings.CreateDefault();
        Host.Ring.Slots ??= new();
        Host.Ring.Slots.RemoveAll(sl => sl == null);
        if (Host.Ring.Slots.Count > RingSettings.MaxSlots) Host.Ring.Slots.RemoveRange(RingSettings.MaxSlots, Host.Ring.Slots.Count - RingSettings.MaxSlots);
        foreach (var sl in Host.Ring.Slots) { sl.Title ??= ""; sl.Glyph ??= "\uE734"; sl.Action = Fix(sl.Action); }
        Host.Ring.TapThresholdMs = Math.Clamp(Host.Ring.TapThresholdMs, 50, 2000);
        if (Device != null)
        {
            try { K13MemoryCodec.Validate(Device); }
            catch (Exception ex) when (ex is K13FormatException or NullReferenceException or IndexOutOfRangeException)
            {
                notes.Add($"Профиль «{Name}»: настройки клавиатуры в файле повреждены ({ex.Message}) — взяты стандартные.");
                Device = null;
            }
        }
        return notes;

        static HostAction Fix(HostAction? a) { a ??= new(); a.Value ??= ""; a.Arguments ??= ""; return a; }
    }

    public AppProfile Clone(string? newName = null)
    {
        var json = JsonSerializer.Serialize(this, ProfileStore.JsonOptions);
        var p = JsonSerializer.Deserialize<AppProfile>(json, ProfileStore.JsonOptions)!;
        if (newName != null) p.Name = newName;
        return p;
    }
}

public sealed class ProfileStoreData
{
    public List<AppProfile> Profiles { get; set; } = new();
    public string ActiveProfile { get; set; } = "";
    public List<AppSwitchRule> AppRules { get; set; } = new();
    public bool AutoSwitch { get; set; } = true;
    /// <summary>v5.4: profile used when the active program matches no rule ("" = keep the current profile).</summary>
    public string FallbackProfile { get; set; } = "";
    /// <summary>v5.5: small on-screen notice when the profile changes by itself (program switch or keypad key).</summary>
    public bool SwitchToast { get; set; } = true;
    /// <summary>v5.5: where the notice appears.</summary>
    public ToastPlacement ToastPlacement { get; set; } = ToastPlacement.BottomRight;
    /// <summary>v5.5: also send a Windows notification (notification centre). Off by default.</summary>
    public bool ToastSystem { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool DarkTheme { get; set; }
    public bool WelcomeShown { get; set; }
    /// <summary>Start with Windows (HKCU Run, minimized to tray). Off by default.</summary>
    public bool StartWithWindows { get; set; }
    /// <summary>The user confirmed (once) that the app may write to the keypad.</summary>
    public bool WriteConfirmed { get; set; }
    /// <summary>v5.2: 0xD0 header (8 bytes hex) the user allowed writing with although the layout check was uncertain.</summary>
    public string? AcceptedHeader { get; set; }
    /// <summary>Colour channel mapping (default: inverted, see LedColorMap).</summary>
    public LedColorMap LedColors { get; set; } = new();
    /// <summary>What each device mode number does (from the calibration wizard); null/empty = not calibrated.</summary>
    public Dictionary<int, string> LedModeObserved { get; set; } = new();
    /// <summary>Data version for one-time migrations (see <see cref="ProfileMigrations"/>).</summary>
    public int SchemaVersion { get; set; }

    /// <summary>v5.3: repairs what a hand-edited / older / partly damaged profiles.json can contain (nulls, duplicates,
    /// out-of-range values) so the app never crashes on it. Returns notes for the log.</summary>
    public List<string> Normalize()
    {
        var notes = new List<string>();
        Profiles ??= new();
        int nulls = Profiles.RemoveAll(p => p == null);
        if (nulls > 0) notes.Add($"Удалено пустых записей профилей: {nulls}.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < Profiles.Count; i++)
        {
            var p = Profiles[i];
            var name = (p.Name ?? "").Trim();
            if (name.Length == 0) name = $"Профиль {i + 1}";
            if (name.Length > 40) name = name[..40].TrimEnd();
            string unique = name;
            for (int k = 2; seen.Contains(unique); k++) unique = $"{name} ({k})";
            if (unique != p.Name) notes.Add($"Профиль «{p.Name}» назван «{unique}» (пустое, длинное или повторяющееся имя).");
            p.Name = unique; seen.Add(unique);
            notes.AddRange(p.Normalize());
        }
        AppRules ??= new();
        AppRules.RemoveAll(r => r == null);
        foreach (var r in AppRules) { r.ProcessName = AppMatch.NormalizeProcess(r.ProcessName); r.TitleContains = (r.TitleContains ?? "").Trim(); r.ProfileName ??= ""; r.DisplayName ??= ""; r.IconPath ??= ""; }
        FallbackProfile ??= "";
        if (FallbackProfile.Length > 0 && !Profiles.Any(p => string.Equals(p.Name, FallbackProfile, StringComparison.OrdinalIgnoreCase))) FallbackProfile = "";
        if (!Enum.IsDefined(ToastPlacement)) { notes.Add($"Место уведомления «{(int)ToastPlacement}» неизвестно — внизу справа."); ToastPlacement = ToastPlacement.BottomRight; }
        LedColors ??= new();
        LedModeObserved ??= new();
        ActiveProfile ??= "";
        if (Profiles.Count > 0 && !Profiles.Any(p => string.Equals(p.Name, ActiveProfile, StringComparison.OrdinalIgnoreCase)))
            ActiveProfile = Profiles[0].Name;
        return notes;
    }
}

/// <summary>
/// Which keys may be intercepted by the PC-side mapper (v4.6). A low-level hook cannot tell the K13 from the main keyboard,
/// so an ordinary key bound here would stop working everywhere (13.11.2026: Left Shift → ring broke language switching).
/// </summary>
public enum TriggerSafety { Safe, NeedsConfirmation, Forbidden }

public static class TriggerKeyPolicy
{
    /// <summary>F13–F24: never on a normal keyboard – the K13 is programmed to send them.</summary>
    public static bool IsServiceKey(ushort vk) => vk is >= 0x7C and <= 0x87;

    public static TriggerSafety Classify(ushort vk) => vk switch
    {
        _ when IsServiceKey(vk) => TriggerSafety.Safe,
        0x10 or 0x11 or 0x12 or (>= 0xA0 and <= 0xA5) or 0x5B or 0x5C or 0x14 or 0x90 or 0x91 => TriggerSafety.Forbidden, // Shift/Ctrl/Alt/Win (both), Caps/Num/Scroll lock
        >= 0x30 and <= 0x39 => TriggerSafety.Forbidden,   // digits
        >= 0x41 and <= 0x5A => TriggerSafety.Forbidden,   // letters
        0x0D or 0x1B or 0x20 or 0x09 or 0x08 or 0x2E or 0x2D => TriggerSafety.Forbidden, // Enter Esc Space Tab Backspace Delete Insert
        >= 0x21 and <= 0x28 => TriggerSafety.Forbidden,   // PgUp PgDn End Home ←↑→↓
        >= 0xBA and <= 0xC0 or >= 0xDB and <= 0xDF or 0xE2 => TriggerSafety.Forbidden, // punctuation ; = , - . / ` [ \ ] '
        0x5D => TriggerSafety.Forbidden,                   // Menu
        0 => TriggerSafety.Forbidden,
        _ => TriggerSafety.NeedsConfirmation              // F1–F12, numpad, media… exist on many keyboards
    };

    /// <summary>Removes bindings on ordinary keys from every profile; returns warnings (empty = nothing removed).</summary>
    public static List<string> DropUnsafe(ProfileStoreData data, Func<ushort, string> name)
    {
        var notes = new List<string>();
        foreach (var p in data.Profiles)
        {
            foreach (var k in p.Host.Keys.Where(k => Classify(k.TriggerVk) == TriggerSafety.Forbidden).ToList())
            {
                p.Host.Keys.Remove(k);
                notes.Add($"Профиль «{p.Name}»: удалён перехват обычной клавиши {name(k.TriggerVk)} (VK 0x{k.TriggerVk:X2}) → {k.Tap.Kind} — она мешала работе основной клавиатуры.");
            }
            foreach (var c in p.Host.Chords.Where(c => Classify(c.VkA) == TriggerSafety.Forbidden || Classify(c.VkB) == TriggerSafety.Forbidden).ToList())
            {
                p.Host.Chords.Remove(c);
                notes.Add($"Профиль «{p.Name}»: удалён аккорд «{c.Title}» на обычных клавишах {name(c.VkA)}+{name(c.VkB)}.");
            }
        }
        return notes;
    }
}

/// <summary>
/// One-time profile upgrades. v2 (Studio 0.4.1): the stick press (keymap column 16) is confirmed to send F13
/// once 0x68 is written there (exp15, 13.11.2026), so every profile gets «стик → F13 → Кольцо действий».
/// </summary>
public static class ProfileMigrations
{
    public const int CurrentVersion = 2;
    public const ushort StickVk = 0x7C;            // VK_F13
    public const byte StickCode = 0x68;            // HID F13
    public const int StickColumn = 16;

    /// <summary>Applies migrations; returns human-readable notes (empty = nothing changed).</summary>
    public static List<string> Migrate(ProfileStoreData data)
    {
        var notes = new List<string>();
        if (data.SchemaVersion < 2)
        {
            foreach (var p in data.Profiles)
                if (EnsureStickRing(p)) notes.Add($"Профиль «{p.Name}»: нажатие стика → F13 → Кольцо действий.");
        }
        data.SchemaVersion = Math.Max(data.SchemaVersion, CurrentVersion);
        return notes;
    }

    /// <summary>Stick press = F13 = ring (keeps any existing F13 binding with another action untouched).</summary>
    public static bool EnsureStickRing(AppProfile p)
    {
        bool changed = false;
        if (p.Device != null && p.Device.GetKey(StickColumn) == 0xF4) { p.Device.SetKey(StickColumn, StickCode); changed = true; }
        if (p.Host.Ring.TapThresholdMs == 300) { p.Host.Ring.TapThresholdMs = 250; changed = true; }
        if (p.Host.Ring.Slots.Count == 0) { p.Host.Ring.Slots = RingSettings.CreateDefault().Slots; changed = true; }
        if (!p.Host.Keys.Any(k => k.TriggerVk == StickVk))
        {
            p.Host.Keys.Add(new HostKeyBinding { TriggerVk = StickVk, Tap = new HostAction { Kind = HostActionKind.Ring }, Suppress = true });
            changed = true;
        }
        return changed;
    }
}

/// <summary>Persists profiles as JSON under %APPDATA%\VorotexK13Studio.</summary>
public sealed class ProfileStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string Folder { get; }
    public string FilePath => Path.Combine(Folder, "profiles.json");
    public ProfileStoreData Data { get; private set; } = new();

    public ProfileStore(string? folder = null)
    {
        Folder = folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VorotexK13Studio");
        Directory.CreateDirectory(Folder);
    }

    public string BackupPath => FilePath + ".bak";

    /// <summary>Notes from the last <see cref="Load"/> (corrupt file kept aside, backup used, fields repaired…).</summary>
    public List<string> LoadNotes { get; } = new();

    /// <summary>
    /// v5.3: never throws for a bad file and never lets a damaged profiles.json be overwritten silently:
    /// the broken file is kept as <c>profiles.json.corrupt-…</c>, then <c>profiles.json.bak</c> (previous good save) is tried;
    /// missing/null fields are repaired by <see cref="ProfileStoreData.Normalize"/>.
    /// </summary>
    public void Load()
    {
        LoadNotes.Clear();
        if (!File.Exists(FilePath) && !File.Exists(BackupPath)) { Data = new ProfileStoreData(); return; }
        ProfileStoreData? d = null;
        if (File.Exists(FilePath))
        {
            d = TryRead(FilePath, out var err);
            if (d == null)
            {
                var keep = FilePath + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
                try { File.Copy(FilePath, keep, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                LoadNotes.Add($"Файл профилей повреждён ({err}). Он сохранён как {Path.GetFileName(keep)}.");
            }
        }
        if (d == null && File.Exists(BackupPath))
        {
            d = TryRead(BackupPath, out var err2);
            LoadNotes.Add(d != null ? "Профили восстановлены из предыдущего сохранения (profiles.json.bak)." : $"Резервная копия профилей тоже не читается ({err2}).");
        }
        Data = d ?? new ProfileStoreData();
        LoadNotes.AddRange(Data.Normalize());
    }

    private static ProfileStoreData? TryRead(string path, out string? error)
    {
        error = null;
        try
        {
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) { error = "пустой файл"; return null; }
            return JsonSerializer.Deserialize<ProfileStoreData>(text, JsonOptions) ?? throw new JsonException("null");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        { error = ex.Message; return null; }
    }

    /// <summary>Atomic save: write + flush a temp file, then replace profiles.json keeping the previous version as profiles.json.bak.</summary>
    public void Save()
    {
        var tmp = FilePath + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Data, JsonOptions);
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes);
            fs.Flush(true);
        }
        if (File.Exists(FilePath))
        {
            try { File.Replace(tmp, FilePath, BackupPath, ignoreMetadataErrors: true); return; }
            catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or UnauthorizedAccessException)
            {
                try { File.Copy(FilePath, BackupPath, true); } catch (IOException) { }
            }
        }
        File.Move(tmp, FilePath, true);
    }

    public AppProfile? Find(string name) => Data.Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public string UniqueName(string baseName)
    {
        if (Find(baseName) == null) return baseName;
        for (int i = 2; ; i++) if (Find($"{baseName} ({i})") == null) return $"{baseName} ({i})";
    }
}

/// <summary>Timestamped backups of raw device images.</summary>
public static class BackupStore
{
    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VorotexK13Studio", "backups");

    public static string Save(byte[] image, string? folder = null, string prefix = "k13-backup", K13Status? status = null)
    {
        folder ??= DefaultFolder;
        Directory.CreateDirectory(folder);
        var ts = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        // v5.3: two backups in the same second (quick lighting writes) used to overwrite each other — never reuse a name
        var bin = Path.Combine(folder, $"{prefix}-{ts}.bin");
        for (int n = 2; File.Exists(bin); n++) bin = Path.Combine(folder, $"{prefix}-{ts}-{n}.bin");
        var tmp = bin + ".tmp";
        File.WriteAllBytes(tmp, image);
        File.Move(tmp, bin);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# VOROTEX K13 backup {ts} len=0x{image.Length:X}");
        if (status != null) sb.AppendLine("# status: " + status.Hex);
        for (int i = 0; i < image.Length; i += 16)
            sb.AppendLine($"{i:X4}: " + string.Join(" ", image.Skip(i).Take(16).Select(b => b.ToString("X2"))));
        File.WriteAllText(Path.ChangeExtension(bin, ".hex.txt"), sb.ToString());
        return bin;
    }
}
