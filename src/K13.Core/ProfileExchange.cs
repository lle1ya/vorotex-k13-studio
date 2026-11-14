using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Vorotex.K13;

/// <summary>v5.4: the shareable file (.k13profile): one or more profiles + their «Включать в программах» rules.</summary>
public sealed class ProfilePackage
{
    public const string FormatId = "vorotex-k13-profile";
    public const int CurrentVersion = 1;

    public string Format { get; set; } = FormatId;
    public int FormatVersion { get; set; } = CurrentVersion;
    public string App { get; set; } = "";
    public DateTime ExportedAt { get; set; }
    public List<AppProfile> Profiles { get; set; } = new();
    /// <summary>Rules whose ProfileName refers to a profile inside this package.</summary>
    public List<AppSwitchRule> AppRules { get; set; } = new();
}

public sealed class ProfileImportException(string message) : Exception(message);

public enum ImportConflict { KeepBoth, Replace }

/// <summary>One profile of an import, with what the preview shows.</summary>
public sealed class ImportItem
{
    public required AppProfile Profile { get; init; }
    public string OriginalName { get; init; } = "";
    public int KeysChanged { get; init; }
    public int Macros { get; init; }
    public int PcActions { get; init; }
    public List<AppSwitchRule> Rules { get; init; } = new();
    /// <summary>Programs this profile would start or links it would open (shown as a warning: imported files are untrusted).</summary>
    public List<string> Launches { get; init; } = new();
    public bool Conflicts { get; set; }
}

public sealed class ImportPreview
{
    public string Source { get; init; } = "";
    public int FormatVersion { get; init; }
    public List<ImportItem> Items { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
}

public sealed class ExportResult
{
    public string Json { get; init; } = "";
    public List<string> Notes { get; init; } = new();
}

/// <summary>
/// v5.4: export/import of profiles. Import is strict — size, depth, counts, string lengths, enum values and the device
/// part (decoded and re-encoded against a base image) are checked; anything wrong rejects the whole file with a
/// Russian message and nothing is changed. Old single-profile .json exports (v4.7–v5.3) and a whole profiles.json
/// are accepted too.
/// </summary>
public static class ProfileExchange
{
    public const string Extension = ".k13profile";
    public const int MaxBytes = 4 * 1024 * 1024, MaxProfiles = 64, MaxRules = 256, MaxString = 2048, MaxHostKeys = 64, MaxChords = 64, MaxEvents = 4000;

    private static readonly JsonSerializerOptions ReadOptions = new(ProfileStore.JsonOptions) { PropertyNameCaseInsensitive = true, MaxDepth = 32, AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip };

    // ───────────────────────────── export

    /// <param name="userProfile">%USERPROFILE% of this computer (paths under it become %USERPROFILE%\…).</param>
    public static ExportResult Export(IReadOnlyList<AppProfile> profiles, IEnumerable<AppSwitchRule> allRules, string appVersion,
        bool includeRules = true, bool stripPaths = true, string? userProfile = null, DateTime? now = null)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        userProfile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var notes = new List<string>();
        var pkg = new ProfilePackage { App = "VOROTEX K13 Studio " + appVersion, ExportedAt = now ?? DateTime.Now };
        var names = new HashSet<string>(profiles.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var p in profiles)
        {
            var c = p.Clone();
            foreach (var a in Actions(c))
            {
                if (a.Kind != HostActionKind.LaunchProgram) continue;
                if (stripPaths && userProfile.Length > 3 && a.Value.StartsWith(userProfile.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    a.Value = "%USERPROFILE%" + a.Value[userProfile.TrimEnd('\\').Length..];
                    notes.Add($"«{c.Name}»: путь к программе заменён на %USERPROFILE%\\… — на другом компьютере он найдётся в папке пользователя.");
                }
                else if (Regex.IsMatch(a.Value, @"^([A-Za-z]:[\\/]|\\\\)"))
                    notes.Add($"«{c.Name}»: запуск «{a.Value}» — путь этого компьютера, на другом программа может лежать в другом месте.");
            }
            pkg.Profiles.Add(c);
        }
        if (includeRules)
            foreach (var r in (allRules ?? Enumerable.Empty<AppSwitchRule>()).Where(r => names.Contains(r.ProfileName) && AppMatch.IsUsable(r)))
                pkg.AppRules.Add(new AppSwitchRule { ProcessName = AppMatch.NormalizeProcess(r.ProcessName), TitleContains = r.TitleContains.Trim(), ProfileName = r.ProfileName, DisplayName = r.DisplayName, IconPath = "" });
        return new ExportResult { Json = JsonSerializer.Serialize(pkg, ProfileStore.JsonOptions), Notes = notes.Distinct().ToList() };
    }

    public static string SafeFileName(string name)
    {
        var s = string.Concat((name ?? "").Select(ch => WinInvalid.Contains(ch) || ch < ' ' ? '_' : ch)).Trim(' ', '.');
        return s.Length == 0 ? "профиль" : s.Length > 60 ? s[..60] : s;
    }

    // ───────────────────────────── import

    public static ImportPreview ReadFile(string path, byte[] baseImage, IEnumerable<string> existingNames)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new ProfileImportException("Файл не найден.");
        if (fi.Length > MaxBytes) throw new ProfileImportException($"Файл слишком большой ({fi.Length / 1024} КБ). Профиль занимает обычно до 200 КБ.");
        string text;
        try { text = File.ReadAllText(path, Encoding.UTF8); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new ProfileImportException("Не удалось прочитать файл: " + ex.Message); }
        return Read(text, baseImage, existingNames, Path.GetFileName(path));
    }

    public static ImportPreview Read(string text, byte[] baseImage, IEnumerable<string> existingNames, string source = "буфер обмена")
    {
        ArgumentNullException.ThrowIfNull(baseImage);
        if (string.IsNullOrWhiteSpace(text)) throw new ProfileImportException("Пусто — здесь нет профиля.");
        if (text.Length > MaxBytes) throw new ProfileImportException("Слишком много данных для профиля.");
        text = text.Trim().TrimStart('\uFEFF');
        if (!text.StartsWith('{')) throw new ProfileImportException("Это не профиль VOROTEX K13 (ожидался текст JSON, начинающийся с «{»).");

        JsonObject root;
        try { root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = 32, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject ?? throw new ProfileImportException("Ожидался объект JSON."); }
        catch (JsonException ex) { throw new ProfileImportException("Файл повреждён или это не JSON: " + Short(ex.Message)); }

        var warnings = new List<string>();
        List<AppProfile> profiles; List<AppSwitchRule> rules; int version;
        try
        {
            if (Has(root, "Format") || Has(root, "FormatVersion"))
            {
                var fmt = Get(root, "Format")?.GetValue<string>();
                if (fmt != ProfilePackage.FormatId) throw new ProfileImportException($"Неизвестный формат «{fmt}».");
                version = Get(root, "FormatVersion")?.GetValue<int>() ?? 0;
                if (version < 1) throw new ProfileImportException("Неверная версия формата.");
                if (version > ProfilePackage.CurrentVersion) throw new ProfileImportException($"Файл создан более новой версией программы (формат {version}). Обновите VOROTEX K13 Studio.");
                var pkg = root.Deserialize<ProfilePackage>(ReadOptions) ?? throw new ProfileImportException("Пустой файл.");
                profiles = pkg.Profiles ?? new(); rules = pkg.AppRules ?? new();
            }
            else if (Has(root, "Profiles"))
            {
                var store = root.Deserialize<ProfileStoreData>(ReadOptions) ?? throw new ProfileImportException("Пустой файл.");
                profiles = store.Profiles ?? new(); rules = store.AppRules ?? new(); version = 0;
                warnings.Add("Это полный файл профилей (profiles.json) — будут импортированы профили и их правила, остальные настройки — нет.");
            }
            else if (Has(root, "Device") || Has(root, "Host") || Has(root, "Name"))
            {
                profiles = new() { root.Deserialize<AppProfile>(ReadOptions) ?? throw new ProfileImportException("Пустой файл.") };
                rules = new(); version = 0;
            }
            else throw new ProfileImportException("Это не профиль VOROTEX K13: нет ни «Profiles», ни «Device».");
        }
        catch (JsonException ex) { throw new ProfileImportException("Неверные данные в файле: " + Short(ex.Message)); }
        catch (InvalidOperationException ex) { throw new ProfileImportException("Неверные данные в файле: " + Short(ex.Message)); }
        catch (FormatException ex) { throw new ProfileImportException("Неверные данные в файле: " + Short(ex.Message)); }

        profiles.RemoveAll(p => p == null);
        if (profiles.Count == 0) throw new ProfileImportException("В файле нет профилей.");
        if (profiles.Count > MaxProfiles) throw new ProfileImportException($"Слишком много профилей ({profiles.Count}, не больше {MaxProfiles}).");
        rules.RemoveAll(r => r == null);
        if (rules.Count > MaxRules) throw new ProfileImportException($"Слишком много правил программ ({rules.Count}).");

        var existing = new HashSet<string>(existingNames ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<ImportItem>();
        foreach (var p in profiles)
        {
            var orig = (p.Name ?? "").Trim();
            Check(p, baseImage, orig.Length > 0 ? orig : "без имени");
            var name = CleanName(orig);
            for (int k = 2; seen.Contains(name); k++) name = CleanName(orig) + $" ({k})";
            seen.Add(name);
            if (p.Device == null) warnings.Add($"«{name}»: в файле нет настроек клавиатуры — взяты стандартные.");
            p.Device ??= K13MemoryCodec.Decode(baseImage);
            p.Normalize();
            var oldName = p.Name ?? "";
            p.Name = name;
            var myRules = rules.Where(r => string.Equals(r.ProfileName ?? "", oldName, StringComparison.OrdinalIgnoreCase)).Select(r => CleanRule(r, name)).Where(r => r != null).Select(r => r!).ToList();
            var cfg = p.Device!;
            items.Add(new ImportItem
            {
                Profile = p, OriginalName = oldName, Rules = myRules, Conflicts = existing.Contains(name),
                KeysChanged = K13Layout.Controls.Count(c => cfg.GetKey(c.Column) != c.FactoryCode),
                Macros = cfg.Macros.Count(m => m.IsUsed),
                PcActions = p.Host.Keys.Count(k => k.Tap.Kind != HostActionKind.None || k.Hold.Kind != HostActionKind.None) + p.Host.Chords.Count,
                Launches = Actions(p).Where(a => a.Kind is HostActionKind.LaunchProgram or HostActionKind.OpenUrl && a.Value.Length > 0)
                    .Select(a => a.Kind == HostActionKind.OpenUrl ? "ссылка " + a.Value : (a.Value + (a.Arguments.Length > 0 ? " " + a.Arguments : ""))).Distinct().ToList(),
            });
        }
        // SwitchProfile actions inside the file follow renamed profiles
        var map = items.Where(i => !string.Equals(i.OriginalName, i.Profile.Name, StringComparison.Ordinal)).ToDictionary(i => i.OriginalName, i => i.Profile.Name, StringComparer.OrdinalIgnoreCase);
        if (map.Count > 0) foreach (var a in items.SelectMany(i => Actions(i.Profile))) if (a.Kind == HostActionKind.SwitchProfile && map.TryGetValue(a.Value, out var nn)) a.Value = nn;
        int orphan = rules.Count(r => !items.Any(i => string.Equals(i.OriginalName, r.ProfileName ?? "", StringComparison.OrdinalIgnoreCase)));
        if (orphan > 0) warnings.Add($"Правил без профиля в файле: {orphan} — пропущены.");
        if (items.Any(i => i.Launches.Count > 0))
            warnings.Add("Профиль запускает программы или открывает ссылки по нажатию кнопок. Импортируйте только файлы от тех, кому доверяете.");
        return new ImportPreview { Source = source, FormatVersion = version, Items = items, Warnings = warnings };
    }

    /// <summary>Adds the previewed profiles to <paramref name="data"/>. Replace keeps the position of the existing profile
    /// and replaces its rules; KeepBoth gives the imported one a free name. Returns the names as stored.</summary>
    public static List<string> Apply(ImportPreview preview, ProfileStoreData data, ImportConflict mode)
    {
        ArgumentNullException.ThrowIfNull(preview); ArgumentNullException.ThrowIfNull(data);
        var result = new List<string>();
        var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in preview.Items)
        {
            var p = it.Profile.Clone();
            var idx = data.Profiles.FindIndex(x => string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0 && mode == ImportConflict.Replace)
            {
                p.Name = data.Profiles[idx].Name;
                data.Profiles[idx] = p;
                data.AppRules.RemoveAll(r => string.Equals(r.ProfileName, p.Name, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                if (idx >= 0)
                {
                    var baseName = p.Name.Length > 34 ? p.Name[..34].TrimEnd() : p.Name;
                    string n = baseName;
                    for (int k = 2; data.Profiles.Any(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase)); k++) n = $"{baseName} ({k})";
                    renamed[p.Name] = n;
                    p.Name = n;
                }
                data.Profiles.Add(p);
            }
            foreach (var r in it.Rules)
            {
                // a program that already switches to another profile keeps its rule (first match wins) — skip the duplicate
                if (data.AppRules.Any(x => AppMatch.NormalizeProcess(x.ProcessName).Equals(AppMatch.NormalizeProcess(r.ProcessName), StringComparison.OrdinalIgnoreCase)
                                           && x.TitleContains.Trim().Equals(r.TitleContains.Trim(), StringComparison.OrdinalIgnoreCase))) continue;
                data.AppRules.Add(new AppSwitchRule { ProcessName = r.ProcessName, TitleContains = r.TitleContains, ProfileName = p.Name, DisplayName = r.DisplayName });
            }
            result.Add(p.Name);
        }
        if (renamed.Count > 0)
            foreach (var name in result)
                foreach (var a in Actions(data.Profiles.First(x => x.Name == name)))
                    if (a.Kind == HostActionKind.SwitchProfile && renamed.TryGetValue(a.Value, out var nn)) a.Value = nn;
        return result;
    }

    // ───────────────────────────── validation

    private static void Check(AppProfile p, byte[] baseImage, string label)
    {
        string who = $"Профиль «{Trunc(label)}»";
        if (Str(p.Name) > 200 || Str(p.Comment) > MaxString || Str(p.Accent) > 16 || Str(p.Glyph) > 8) throw new ProfileImportException($"{who}: слишком длинное имя или описание.");
        if (p.MacroNames != null)
        {
            if (p.MacroNames.Count > K13Layout.MacroSlots) throw new ProfileImportException($"{who}: слишком много имён макросов.");
            if (p.MacroNames.Any(kv => kv.Key is < 0 or >= K13Layout.MacroSlots || Str(kv.Value) > 100)) throw new ProfileImportException($"{who}: неверные имена макросов.");
        }
        var h = p.Host;
        if (h != null)
        {
            if ((h.Keys?.Count ?? 0) > MaxHostKeys || (h.Chords?.Count ?? 0) > MaxChords) throw new ProfileImportException($"{who}: слишком много действий на ПК.");
            if ((h.Ring?.Slots?.Count ?? 0) > RingSettings.MaxSlots) throw new ProfileImportException($"{who}: в кольце больше {RingSettings.MaxSlots} ячеек.");
            foreach (var k in h.Keys ?? new())
                if (k != null && (k.TriggerVk is 0 or > 0xFE)) throw new ProfileImportException($"{who}: неверная клавиша действия ({k.TriggerVk}).");
            foreach (var c in h.Chords ?? new())
                if (c != null && (c.VkA is 0 or > 0xFE || c.VkB is 0 or > 0xFE || Str(c.Title) > 200)) throw new ProfileImportException($"{who}: неверное сочетание клавиш.");
            foreach (var a in ActionsRaw(h))
            {
                if (!Enum.IsDefined(a.Kind)) throw new ProfileImportException($"{who}: неизвестный тип действия.");
                if (Str(a.Value) > MaxString || Str(a.Arguments) > MaxString) throw new ProfileImportException($"{who}: слишком длинное значение действия.");
                if (HasControl(a.Value) && a.Kind != HostActionKind.Text) throw new ProfileImportException($"{who}: недопустимые символы в действии.");
            }
            foreach (var s in h.Ring?.Slots ?? new()) if (s != null && (Str(s.Title) > 200 || Str(s.Glyph) > 8)) throw new ProfileImportException($"{who}: неверная ячейка кольца.");
        }
        if (p.Device is { } d)
        {
            if (d.Keymap == null || d.Fn == null || d.FnPad == null || d.Macros == null || d.Led?.Colors == null || d.Led.Extra == null)
                throw new ProfileImportException($"{who}: настройки клавиатуры неполные.");
            if (d.Macros.Any(m => m == null || m.Events == null || m.Events.Count > MaxEvents || m.Events.Any(e => e == null || (e.Extra?.Length ?? 0) > 8 || e.Delay is < 0 or > 127)))
                throw new ProfileImportException($"{who}: неверный макрос.");
            if (d.Led.Colors.Any(c => c == null)) throw new ProfileImportException($"{who}: неверная подсветка.");
            try
            {
                K13MemoryCodec.Validate(d);
                var img = K13MemoryCodec.Encode(d, baseImage);   // must fit into the keypad memory
                K13MemoryCodec.Decode(img);
            }
            catch (K13FormatException ex) { throw new ProfileImportException($"{who}: настройки клавиатуры не подходят — {ex.Message}"); }
            catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or NullReferenceException or InvalidOperationException)
            { throw new ProfileImportException($"{who}: настройки клавиатуры повреждены."); }
        }
    }

    private static AppSwitchRule? CleanRule(AppSwitchRule r, string profile)
    {
        var proc = AppMatch.NormalizeProcess(r.ProcessName);
        var title = (r.TitleContains ?? "").Trim();
        if (proc.Length > 260 || title.Length > 260 || HasControl(proc) || HasControl(title) || proc.IndexOfAny(WinInvalid) >= 0) return null;
        var rule = new AppSwitchRule { ProcessName = proc, TitleContains = title, ProfileName = profile, DisplayName = Trunc((r.DisplayName ?? "").Trim(), 80), IconPath = "" };
        return AppMatch.IsUsable(rule) ? rule : null;
    }

    private static string CleanName(string name)
    {
        var s = new string((name ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        s = Regex.Replace(s, @"\s+", " ");
        if (s.Length == 0) s = "Импорт";
        return s.Length > 36 ? s[..36].TrimEnd() : s;
    }

    internal static IEnumerable<HostAction> Actions(AppProfile p) => ActionsRaw(p.Host);

    private static IEnumerable<HostAction> ActionsRaw(HostSettings? h)
    {
        if (h == null) yield break;
        foreach (var k in h.Keys ?? new()) { if (k?.Tap != null) yield return k.Tap; if (k?.Hold != null) yield return k.Hold; }
        foreach (var c in h.Chords ?? new()) if (c?.Action != null) yield return c.Action;
        foreach (var s in h.Ring?.Slots ?? new()) if (s?.Action != null) yield return s.Action;
    }

    /// <summary>Characters Windows does not allow in file names (the same on every OS the tests run on).</summary>
    private static readonly char[] WinInvalid = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

    private static int Str(string? s) => s?.Length ?? 0;
    private static bool HasControl(string? s) => s != null && s.Any(ch => ch < ' ' && ch != '\t');
    private static string Trunc(string s, int n = 40) => s.Length > n ? s[..n] + "…" : s;
    private static string Short(string m) => m.Length > 160 ? m[..160] + "…" : m;
    private static bool Has(JsonObject o, string key) => Get(o, key) != null || o.Any(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase));
    private static JsonNode? Get(JsonObject o, string key) => o.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
}
