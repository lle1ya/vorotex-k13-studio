using System.Text.Json;

namespace Vorotex.K13;

/// <summary>v5.10: «есть ли новая версия» — GitHub Releases of lle1ya/vorotex-k13-studio. Notify only: the program never downloads or installs anything.</summary>
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

    /// <summary>Only pages of this repository are ever opened.</summary>
    public static bool IsReleaseUrl(string? url) => url != null && url.StartsWith("https://github.com/" + Repo + "/releases", StringComparison.OrdinalIgnoreCase);

    public static bool IsNewer(Release r, Version current) => r.Version > Normalize(current);

    public static bool Due(DateTime lastUtc, DateTime nowUtc) => nowUtc - lastUtc >= Interval || lastUtc > nowUtc;

    /// <summary>Human tag for the running build: 0.5.10.0 → «v5.10.0», 1.0.0 → «v1.0.0».</summary>
    public static string Display(Version v) => v.Major == 0 && v.Minor == 5 ? $"v5.{v.Build}.{Math.Max(0, v.Revision)}" : $"v{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
}
