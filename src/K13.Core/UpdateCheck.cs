using System.Text.Json;

namespace Vorotex.K13;

/// <summary>v5.10: «есть ли новая версия» — GitHub Releases of lle1ya/vorotex-k13-studio. Only the check; downloading/installing is UpdateInstall (1.1.0), after the user confirms.</summary>
public static class UpdateCheck
{
    public const string Repo = "lle1ya/vorotex-k13-studio";
    public const string ApiUrl = "https://api.github.com/repos/" + Repo + "/releases/latest";
    public const string ReleasesPage = "https://github.com/" + Repo + "/releases";
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    public sealed record Release(string Tag, Version Version, string Url, string Name);

    /// <summary>Tag → comparable version. Tags before 1.0 were «v5.x.y» while the assembly was 0.5.x.y, so v5.* maps to 0.5.*.</summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var t = tag.Trim().TrimStart('v', 'V');
        int cut = t.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0) t = t[..cut];
        if (!Version.TryParse(t.Contains('.') ? t : t + ".0", out var v)) return null;
        if (v.Major == 5) return new Version(0, 5, v.Minor, Math.Max(0, v.Build));
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
    }

    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    /// <summary>GitHub «releases/latest» JSON → release; null when the answer is not usable (drafts/prereleases are skipped).</summary>
    public static Release? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            if (r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
            if (r.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) return null;
            string? tag = r.TryGetProperty("tag_name", out var tg) && tg.ValueKind == JsonValueKind.String ? tg.GetString() : null;
            var v = ParseTag(tag);
            if (v == null) return null;
            string url = r.TryGetProperty("html_url", out var u) && u.ValueKind == JsonValueKind.String && IsReleaseUrl(u.GetString()) ? u.GetString()! : ReleasesPage;
            string name = r.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? tag! : tag!;
            return new Release(tag!, v, url, name);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>v5.10.2: non-API fallback — github.com/&lt;repo&gt;/releases/latest answers 302 with
    /// Location: https://github.com/&lt;repo&gt;/releases/tag/&lt;tag&gt; (no API quota). Relative locations are accepted.</summary>
    public const string LatestPage = ReleasesPage + "/latest";

    public static Release? ParseLatestRedirect(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return null;
        string loc = location.Trim();
        if (loc.StartsWith('/')) loc = "https://github.com" + loc;
        const string marker = "/releases/tag/";
        if (!IsReleaseUrl(loc)) return null;
        int i = loc.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;                                   // e.g. …/releases — no release published yet
        string tag = Uri.UnescapeDataString(loc[(i + marker.Length)..].Split('?', '#', '/')[0]);
        var v = ParseTag(tag);
        return v == null ? null : new Release(tag, v, ReleasesPage + "/tag/" + Uri.EscapeDataString(tag), tag);
    }

    public enum Source { Api, Redirect, None }

    /// <summary>Result of one check. <see cref="Error"/> is a Russian text when both ways failed.</summary>
    public sealed record Outcome(Release? Latest, Source From, string? Error, string Log);

    public static bool IsRateLimit(int status) => status is 403 or 429;

    /// <summary>Tries the API first; on rate limit (403/429), any other HTTP error, an unusable answer or an exception it
    /// asks the release page redirect. Transport is injected (tests); nothing is downloaded here.</summary>
    /// <param name="api">GET <see cref="ApiUrl"/> → (status, body).</param>
    /// <param name="redirect">HEAD <see cref="LatestPage"/> without following redirects → (status, Location).</param>
    public static async Task<Outcome> ResolveAsync(Func<Task<(int Status, string? Body)>> api, Func<Task<(int Status, string? Location)>> redirect)
    {
        string apiNote;
        try
        {
            var (st, body) = await api();
            if (st == 200 && body != null && Parse(body) is { } r) return new Outcome(r, Source.Api, null, $"API: {r.Tag}");
            apiNote = st == 200 ? "API: ответ не разобран" : IsRateLimit(st) ? $"API: лимит запросов GitHub ({st})" : $"API: HTTP {st}";
        }
        catch (Exception ex) { apiNote = "API: " + ex.GetType().Name + " " + ex.Message; }
        try
        {
            var (st, loc) = await redirect();
            if (st is >= 300 and < 400 && ParseLatestRedirect(loc) is { } r)
                return new Outcome(r, Source.Redirect, null, $"{apiNote}; страница выпусков: {r.Tag}");
            string why = st is >= 300 and < 400 ? $"перенаправление «{loc}» не на выпуск" : $"HTTP {st}";
            return new Outcome(null, Source.None, FailText, $"{apiNote}; страница выпусков: {why}");
        }
        catch (Exception ex)
        {
            return new Outcome(null, Source.None, FailText, $"{apiNote}; страница выпусков: {ex.GetType().Name} {ex.Message}");
        }
    }

    public const string FailText = "Не удалось узнать, есть ли новая версия: GitHub не ответил (нет связи или временный лимит запросов). " +
                                   "Программа попробует позже сама. Посмотреть вручную: " + ReleasesPage;

    /// <summary>Only pages of this repository are ever opened.</summary>
    public static bool IsReleaseUrl(string? url) => url != null && url.StartsWith("https://github.com/" + Repo + "/releases", StringComparison.OrdinalIgnoreCase);

    public static bool IsNewer(Release r, Version current) => r.Version > Normalize(current);

    /// <summary>Automatic check: only when switched on and the last SUCCESSFUL check is a day old (failures retry on the next timer tick).</summary>
    public static bool AutoCheckDue(bool enabled, DateTime lastUtc, DateTime nowUtc) => enabled && Due(lastUtc, nowUtc);

    public static bool Due(DateTime lastUtc, DateTime nowUtc) => nowUtc - lastUtc >= Interval || lastUtc > nowUtc;

    /// <summary>Human tag for the running build: 0.5.10.0 → «v5.10.0», 1.0.0 → «v1.0.0».</summary>
    public static string Display(Version v) => v.Major == 0 && v.Minor == 5 ? $"v5.{v.Build}.{Math.Max(0, v.Revision)}" : $"v{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
}
