using System.Security.Cryptography;
using System.Text;

namespace Vorotex.K13;

/// <summary>1.1.0: «Скачать и обновить». Pure, testable parts: asset names and URLs, SHA256SUMS parsing, the file checks
/// (Inno setup of THIS app with the expected version / our portable exe), download progress text, the installer arguments
/// and the portable swap plan. Only files of github.com/lle1ya/vorotex-k13-studio releases are ever downloaded.</summary>
public static class UpdateInstall
{
    /// <summary>Inno AppId of the installer (installer/VorotexK13Studio.iss). Never change it: it is what makes an upgrade in place.</summary>
    public const string AppId = "6E1F3A7B-0C5D-4B8E-9A21-3D5C0B13A7F1";
    /// <summary>Written by the .iss into the setup's version resource (VersionInfoCopyright) so a downloaded file can be checked.</summary>
    public const string AppIdMarker = "AppId " + AppId;
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{" + AppId + "}_is1";
    public const string SumsAsset = "SHA256SUMS.txt";
    public const string Mutex = @"Local\VorotexK13Studio.Single";
    public const long MinSize = 1_000_000, MaxSize = 400_000_000;

    public enum Kind { Installed, Portable }

    public static string SetupAsset(string tag) => $"VorotexK13Studio-{tag}-setup.exe";
    public static string PortableAsset(string tag) => $"VorotexK13Studio-{tag}.exe";
    public static string Asset(Kind k, string tag) => k == Kind.Installed ? SetupAsset(tag) : PortableAsset(tag);

    /// <summary>Direct download URL (no API, no quota): github.com/&lt;repo&gt;/releases/download/&lt;tag&gt;/&lt;asset&gt;.</summary>
    public static string DownloadUrl(string tag, string asset)
    {
        if (!IsSafeTag(tag)) throw new ArgumentException("неверная метка выпуска: " + tag);
        return $"https://github.com/{UpdateCheck.Repo}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(asset)}";
    }

    public static bool IsSafeTag(string? tag) => !string.IsNullOrEmpty(tag) && tag.Length <= 40 && tag.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    /// <summary>Hosts a release download may be redirected to.</summary>
    public static bool IsAllowedDownloadHost(Uri? u) =>
        u != null && u.Scheme == Uri.UriSchemeHttps &&
        (u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>«sha256sum» format: «&lt;64 hex&gt;  name» or «&lt;hex&gt; *name». Names are compared case-insensitively.</summary>
    public static Dictionary<string, string> ParseSums(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length < 66 || line.StartsWith('#')) continue;
            var hex = line[..64];
            if (!hex.All(Uri.IsHexDigit) || !char.IsWhiteSpace(line[64])) continue;
            var name = line[65..].Trim().TrimStart('*').Trim();
            if (name.Length > 0) d[name] = hex.ToLowerInvariant();
        }
        return d;
    }

    public static string Sha256(Stream s) => Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    public static string Sha256File(string path) { using var f = File.OpenRead(path); return Sha256(f); }

    /// <summary>Checks a downloaded file. Returns null when fine, else a Russian reason.</summary>
    public static string? CheckFile(Kind kind, byte[] data, string expectedSha, Version expected)
    {
        if (data.LongLength < MinSize || data.LongLength > MaxSize) return $"файл неожиданного размера ({data.LongLength:N0} байт)";
        var sha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        if (!string.Equals(sha, expectedSha, StringComparison.OrdinalIgnoreCase)) return $"контрольная сумма не совпала (SHA256 {sha[..12]}…, ожидалось {expectedSha[..Math.Min(12, expectedSha.Length)]}…)";
        if (data[0] != 'M' || data[1] != 'Z') return "это не программа Windows (нет заголовка MZ)";
        string ver = VersionText(expected);
        if (kind == Kind.Installed)
        {
            if (!Contains(data, Encoding.ASCII.GetBytes("Inno Setup Setup Data ("))) return "это не установщик Inno Setup";
            if (!Contains(data, Encoding.Unicode.GetBytes(AppIdMarker))) return "установщик другой программы (не тот AppId)";
            if (!Contains(data, Encoding.Unicode.GetBytes("VOROTEX K13 Studio"))) return "в установщике нет названия VOROTEX K13 Studio";
            if (!ContainsWord16(data, SetupMarker(expected))) return $"в установщике нет версии {ver}";
        }
        else
        {
            if (!Contains(data, Encoding.Unicode.GetBytes("VOROTEX K13 Studio"))) return "это не VOROTEX K13 Studio";
            if (!ContainsWord16(data, ver + ".0")) return $"в файле нет версии {ver}";
        }
        return null;
    }

    /// <summary>1.1.0 → «1.1.0» (the text the .iss / csproj put into the version resource).</summary>
    public static string VersionText(Version v) => $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";

    public static bool Contains(ReadOnlySpan<byte> data, ReadOnlySpan<byte> what) => data.IndexOf(what) >= 0;

    /// <summary>The setup's version-resource marker: «AppId &lt;guid&gt; v1.1.0» (the .iss writes it into VersionInfoCopyright).</summary>
    public static string SetupMarker(Version v) => $"{AppIdMarker} v{VersionText(v)}";

    /// <summary>UTF-16 text that ends there (next char is NUL or the space padding Inno uses) — so «1.1.0» does not match «1.1.05».</summary>
    public static bool ContainsWord16(ReadOnlySpan<byte> data, string text)
    {
        var k = Encoding.Unicode.GetBytes(text);
        int from = 0;
        while (from < data.Length)
        {
            int i = data[from..].IndexOf(k);
            if (i < 0) return false;
            int end = from + i + k.Length;
            if (end + 1 >= data.Length || (data[end + 1] == 0 && (data[end] == 0 || data[end] == (byte)' '))) return true;
            from += i + 1;
        }
        return false;
    }

    /// <summary>Arguments for the silent upgrade. /UPDATE makes the setup wait for this program to exit (mutex) and start it again.</summary>
    public static string InstallerArgs(string logPath) =>
        $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOCANCEL /UPDATE \"/LOG={logPath}\"";

    /// <summary>Is <paramref name="exeDir"/> the folder recorded by the installer (HKCU uninstall key «InstallLocation»)?</summary>
    public static bool IsInstalledCopy(string exeDir, string? installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation)) return false;
        static string N(string p) => Path.GetFullPath(p.Trim().Trim('"')).TrimEnd('\\', '/');
        try { return string.Equals(N(exeDir), N(installLocation), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>Portable swap: the running exe can be renamed (not overwritten) on Windows. Plan: exe → exe.old, new → exe.</summary>
    public sealed record SwapPlan(string Exe, string Old, string New);

    public static SwapPlan PlanSwap(string exe, string downloaded) => new(exe, exe + ".old", downloaded);

    /// <summary>Does the swap through injected file operations (tests use a fake). On any failure everything is put back.</summary>
    public static void Swap(SwapPlan p, Action<string, string> move, Func<string, bool> exists, Action<string> delete)
    {
        if (exists(p.Old)) delete(p.Old);
        move(p.Exe, p.Old);
        try { move(p.New, p.Exe); }
        catch
        {
            try { if (!exists(p.Exe)) move(p.Old, p.Exe); } catch { }
            throw;
        }
    }

    /// <summary>«12,3 из 67,4 МБ (18 %)».</summary>
    public static string ProgressText(long done, long? total)
    {
        static string Mb(long b) => (b / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
        return total is long t && t > 0 ? $"{Mb(done)} из {Mb(t)} МБ ({Math.Min(100, done * 100 / t)} %)" : $"{Mb(done)} МБ";
    }

    public const string NoSumsText = "У этого выпуска нет файла контрольных сумм SHA256SUMS.txt, поэтому программа не будет устанавливать его сама. Откройте страницу выпуска и скачайте вручную.";
}
