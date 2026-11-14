namespace Vorotex.K13;

/// <summary>v5.4: matching of the active program/window against «Включать в программах» rules (pure, testable).</summary>
public static class AppMatch
{
    /// <summary>Our own process: never switches profiles (the user is editing them).</summary>
    public const string OwnProcess = "VorotexK13Studio";

    /// <summary>Shell pieces that are briefly in front while the user switches windows (taskbar, Alt+Tab, Start, search…):
    /// ignored, so the profile does not flicker to the fallback and back.</summary>
    public static readonly HashSet<string> Transient = new(StringComparer.OrdinalIgnoreCase)
    {
        "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp", "SearchUI", "LockApp", "TextInputHost",
        "ShellHost", "Widgets", "WidgetBoard", "ApplicationFrameHost", "dwm", "csrss", "LogonUI", "consent", "Idle",
    };

    /// <summary>"C:\\Program Files\\Adobe\\Photoshop.EXE" / "photoshop.exe " / "\\"photoshop\\"" → "photoshop" (lower case kept as typed).</summary>
    public static string NormalizeProcess(string? text)
    {
        var s = (text ?? "").Trim().Trim('"').Trim();
        int slash = Math.Max(s.LastIndexOf('\\'), s.LastIndexOf('/'));
        if (slash >= 0) s = s[(slash + 1)..];
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        return s.Trim();
    }

    /// <summary>A rule needs a program or a title; both given = both must match.</summary>
    public static bool IsUsable(AppSwitchRule r) => r.ProfileName.Length > 0 && (NormalizeProcess(r.ProcessName).Length > 0 || r.TitleContains.Trim().Length > 0);

    public static bool Matches(AppSwitchRule r, string process, string title)
    {
        if (!IsUsable(r)) return false;
        var rp = NormalizeProcess(r.ProcessName);
        bool okProc = rp.Length == 0 || string.Equals(rp, NormalizeProcess(process), StringComparison.OrdinalIgnoreCase);
        var t = r.TitleContains.Trim();
        bool okTitle = t.Length == 0 || (title ?? "").Contains(t, StringComparison.OrdinalIgnoreCase);
        return okProc && okTitle;
    }

    /// <summary>More specific rules win: program + title &gt; title only &gt; program only; then list order.</summary>
    public static int Specificity(AppSwitchRule r) =>
        (NormalizeProcess(r.ProcessName).Length > 0 ? 1 : 0) + (r.TitleContains.Trim().Length > 0 ? 2 : 0);

    public static AppSwitchRule? Find(IEnumerable<AppSwitchRule> rules, IEnumerable<AppProfile> profiles, string process, string title)
    {
        var names = new HashSet<string>(profiles.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        return rules.Select((r, i) => (r, i))
            .Where(x => names.Contains(x.r.ProfileName) && Matches(x.r, process, title))
            .OrderByDescending(x => Specificity(x.r)).ThenBy(x => x.i)
            .Select(x => x.r).FirstOrDefault();
    }

    /// <summary>«Photoshop.exe», «окно «Figma»», «Photoshop.exe, окно «Безымянный»».</summary>
    public static string Describe(AppSwitchRule r)
    {
        var p = NormalizeProcess(r.ProcessName);
        var t = r.TitleContains.Trim();
        var name = p.Length > 0 ? p + ".exe" : "";
        if (t.Length == 0) return name;
        return name.Length > 0 ? $"{name}, в заголовке «{t}»" : $"окно с «{t}» в заголовке";
    }

    public static string Label(AppSwitchRule r) => r.DisplayName.Trim().Length > 0 ? r.DisplayName.Trim() : NormalizeProcess(r.ProcessName) is { Length: > 0 } p ? p : $"«{r.TitleContains.Trim()}»";
}

/// <summary>What the auto-switch decided: switch to <see cref="Profile"/> because of <see cref="Reason"/>.</summary>
public sealed record AutoSwitchDecision(string Profile, string Reason, bool IsFallback, AppSwitchRule? Rule);

/// <summary>
/// v5.4: decides profile switches from the foreground window. Debounced (a window must stay in front for
/// <see cref="DebounceMs"/>, so Alt+Tab through several windows does not flicker), ignores our own window and transient
/// shell UI, decides once per window (so a profile picked by hand stays until the user goes to another window),
/// and falls back to <see cref="ProfileStoreData.FallbackProfile"/> when no rule matches.
/// </summary>
public sealed class AutoSwitchEngine
{
    public int DebounceMs { get; set; } = 350;
    public bool Paused { get; set; }

    private string _pendingKey = "";
    private long _pendingSince;
    private string _decidedKey = "";

    /// <summary>Forget the last decision (e.g. rules changed): the current window is evaluated again.</summary>
    public void Reset() => _decidedKey = "";

    /// <param name="process">Process name without .exe; "" when Windows denied access to it.</param>
    /// <returns>A decision when the profile should change (or be confirmed with a new reason), otherwise null.</returns>
    public AutoSwitchDecision? Observe(string process, string title, long nowMs, ProfileStoreData data, string currentProfile)
    {
        ArgumentNullException.ThrowIfNull(data);
        process ??= ""; title ??= "";
        if (process.Length == 0 && title.Length == 0) return null;                       // desktop / nothing in front
        if (string.Equals(process, AppMatch.OwnProcess, StringComparison.OrdinalIgnoreCase)) return null;
        if (AppMatch.Transient.Contains(process)) return null;
        if (string.Equals(process, "explorer", StringComparison.OrdinalIgnoreCase) && title.Length == 0) return null; // taskbar, desktop
        var key = process + "\u0001" + title;
        if (key != _pendingKey) { _pendingKey = key; _pendingSince = nowMs; return null; }
        if (nowMs - _pendingSince < DebounceMs) return null;
        if (key == _decidedKey) return null;
        if (!data.AutoSwitch || Paused) return null;                                       // decide again once resumed
        _decidedKey = key;

        var rule = AppMatch.Find(data.AppRules, data.Profiles, process, title);
        if (rule != null)
            return new(Canonical(data, rule.ProfileName), "по программе " + AppMatch.Describe(rule), false, rule);
        if (process.Length == 0) return null;   // access denied and no title rule: do not guess, keep the current profile
        var fb = data.FallbackProfile;
        if (fb.Length > 0 && data.AppRules.Any(AppMatch.IsUsable) && data.Profiles.Any(p => string.Equals(p.Name, fb, StringComparison.OrdinalIgnoreCase)))
            return new(Canonical(data, fb), $"по умолчанию ({process}.exe — без правила)", true, null);
        return null;
    }

    private static string Canonical(ProfileStoreData d, string name) =>
        d.Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Name ?? name;
}
