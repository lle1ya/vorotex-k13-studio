using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Vorotex.K13.Tests;

/// <summary>1.1.0: «Скачать и обновить» — URLs, SHA256SUMS, file checks, silent installer arguments, portable swap, installer script.</summary>
public class V110Tests
{
    static readonly Version V = new(1, 1, 0);

    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "installer", "VorotexK13Studio.iss"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }
    static string Iss() => File.ReadAllText(Path.Combine(Root(), "installer", "VorotexK13Studio.iss"));

    static byte[] Pe(params byte[][] parts)
    {
        var ms = new MemoryStream();
        ms.Write("MZ"u8);
        ms.Write(new byte[UpdateInstall.MinSize]);
        foreach (var p in parts) { ms.Write(p); ms.Write(new byte[64]); }
        return ms.ToArray();
    }
    static byte[] U(string s) => Encoding.Unicode.GetBytes(s);
    static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
    static byte[] GoodSetup(Version v) => Pe(Encoding.ASCII.GetBytes("Inno Setup Setup Data (6.7.0)"), U("VOROTEX K13 Studio Setup"),
        U(UpdateInstall.SetupMarker(v) + "      "));

    [Fact]
    public void Direct_download_urls_point_only_to_this_repository()
    {
        Assert.Equal("https://github.com/lle1ya/vorotex-k13-studio/releases/download/v1.1.0/VorotexK13Studio-v1.1.0-setup.exe",
            UpdateInstall.DownloadUrl("v1.1.0", UpdateInstall.SetupAsset("v1.1.0")));
        Assert.Equal("VorotexK13Studio-v1.1.0.exe", UpdateInstall.Asset(UpdateInstall.Kind.Portable, "v1.1.0"));
        Assert.Throws<ArgumentException>(() => UpdateInstall.DownloadUrl("../../evil", "x"));
        Assert.False(UpdateInstall.IsSafeTag("v1/../x"));
        Assert.True(UpdateInstall.IsSafeTag("v1.1.0-test"));
    }

    [Theory]
    [InlineData("https://github.com/lle1ya/x", true)]
    [InlineData("https://objects.githubusercontent.com/a", true)]
    [InlineData("https://release-assets.githubusercontent.com/a", true)]
    [InlineData("http://github.com/a", false)]
    [InlineData("https://evil.com/github.com", false)]
    [InlineData("https://githubusercontent.com.evil.io/a", false)]
    public void Only_github_hosts_are_accepted(string url, bool ok) => Assert.Equal(ok, UpdateInstall.IsAllowedDownloadHost(new Uri(url)));

    [Fact]
    public void Sha256sums_are_parsed_in_sha256sum_format()
    {
        var a = new string('a', 64); var b = new string('B', 64);
        var d = UpdateInstall.ParseSums($"\uFEFF{a}  VorotexK13Studio-v1.1.0.exe\r\n# comment\n{b} *VorotexK13Studio-v1.1.0-setup.exe\nbad line\n");
        Assert.Equal(2, d.Count);
        Assert.Equal(a, d["vorotexk13studio-v1.1.0.exe"]);
        Assert.Equal(b.ToLowerInvariant(), d["VorotexK13Studio-v1.1.0-setup.exe"]);
    }

    [Fact]
    public void Good_setup_passes_the_check()
    {
        var f = GoodSetup(V);
        Assert.Null(UpdateInstall.CheckFile(UpdateInstall.Kind.Installed, f, Sha(f), V));
        Assert.Null(UpdateInstall.CheckFile(UpdateInstall.Kind.Installed, f, Sha(f).ToUpperInvariant(), V));
    }

    [Fact]
    public void Wrong_checksum_is_refused()
    {
        var f = GoodSetup(V);
        Assert.Contains("контрольная сумма", UpdateInstall.CheckFile(UpdateInstall.Kind.Installed, f, new string('0', 64), V));
    }

    [Fact]
    public void Other_programs_or_versions_are_refused()
    {
        var other = Pe(Encoding.ASCII.GetBytes("Inno Setup Setup Data (6.7.0)"), U("VOROTEX K13 Studio"), U("AppId 00000000-0000-0000-0000-000000000000 v1.1.0 "));
        Assert.Contains("AppId", UpdateInstall.CheckFile(UpdateInstall.Kind.Installed, other, Sha(other), V));
        var notInno = Pe(U("VOROTEX K13 Studio"), U(UpdateInstall.SetupMarker(V)));
        Assert.Contains("Inno", UpdateInstall.CheckFile(UpdateInstall.Kind.Installed, notInno, Sha(notInno), V));
        var older = GoodSetup(new Version(1, 0, 9));
        Assert.Contains("версии 1.1.0", UpdateInstall.CheckFile(UpdateInstall.Kind.Installed, older, Sha(older), V));
        var longer = Pe(Encoding.ASCII.GetBytes("Inno Setup Setup Data (6.7.0)"), U("VOROTEX K13 Studio"), U(UpdateInstall.SetupMarker(V) + "5"));
        Assert.Contains("версии", UpdateInstall.CheckFile(UpdateInstall.Kind.Installed, longer, Sha(longer), V));
        var tiny = "MZ"u8.ToArray();
        Assert.Contains("размера", UpdateInstall.CheckFile(UpdateInstall.Kind.Installed, tiny, Sha(tiny), V));
        var noMz = GoodSetup(V); noMz[0] = (byte)'X';
        Assert.Contains("MZ", UpdateInstall.CheckFile(UpdateInstall.Kind.Installed, noMz, Sha(noMz), V));
    }

    [Fact]
    public void Portable_exe_check_needs_name_and_file_version()
    {
        var ok = Pe(U("VOROTEX K13 Studio"), U("1.1.0.0\0"));
        Assert.Null(UpdateInstall.CheckFile(UpdateInstall.Kind.Portable, ok, Sha(ok), V));
        var old = Pe(U("VOROTEX K13 Studio"), U("1.0.5.0\0"));
        Assert.NotNull(UpdateInstall.CheckFile(UpdateInstall.Kind.Portable, old, Sha(old), V));
    }

    [Fact]
    public void Installer_arguments_are_silent_and_mark_the_in_app_update()
    {
        var a = UpdateInstall.InstallerArgs(@"C:\Users\x\AppData\Local\VorotexK13Studio\updates\install-v1.1.0.log");
        foreach (var must in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/UPDATE", "\"/LOG=C:\\Users\\x\\AppData\\Local\\VorotexK13Studio\\updates\\install-v1.1.0.log\"" })
            Assert.Contains(must, a);
        Assert.DoesNotContain("/DIR", a);          // the installer keeps the previous folder (same AppId)
        Assert.DoesNotContain("/TASKS", a);        // and the previous tasks (desktop shortcut)
    }

    [Theory]
    [InlineData(@"C:\Users\a\AppData\Local\Programs\VorotexK13Studio", @"C:\Users\a\AppData\Local\Programs\VorotexK13Studio\", true)]
    [InlineData(@"C:\Users\a\AppData\Local\Programs\VorotexK13Studio", null, false)]
    [InlineData(@"C:\Users\a\Downloads", @"C:\Users\a\AppData\Local\Programs\VorotexK13Studio", false)]
    public void Installed_copy_is_the_one_in_the_recorded_folder(string dir, string? loc, bool installed)
    {
        if (!OperatingSystem.IsWindows()) { dir = dir.Replace('\\', '/'); loc = loc?.Replace('\\', '/'); }
        Assert.Equal(installed, UpdateInstall.IsInstalledCopy(dir, loc));
    }

    [Fact]
    public void Portable_swap_renames_and_rolls_back_on_failure()
    {
        var dir = Directory.CreateTempSubdirectory("k13swap").FullName;
        try
        {
            string exe = Path.Combine(dir, "VorotexK13Studio-v1.0.5.exe"), dl = Path.Combine(dir, "new.exe");
            File.WriteAllText(exe, "old"); File.WriteAllText(dl, "new"); File.WriteAllText(exe + ".old", "stale");
            var plan = UpdateInstall.PlanSwap(exe, dl);
            UpdateInstall.Swap(plan, (a, b) => File.Move(a, b), File.Exists, File.Delete);
            Assert.Equal("new", File.ReadAllText(exe));
            Assert.Equal("old", File.ReadAllText(exe + ".old"));
            Assert.False(File.Exists(dl));

            File.WriteAllText(exe, "cur"); File.Delete(exe + ".old");
            var bad = UpdateInstall.PlanSwap(exe, Path.Combine(dir, "missing.exe"));
            Assert.ThrowsAny<IOException>(() => UpdateInstall.Swap(bad, (a, b) => File.Move(a, b), File.Exists, File.Delete));
            Assert.Equal("cur", File.ReadAllText(exe));             // rolled back
            Assert.False(File.Exists(exe + ".old"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Progress_text_is_readable()
    {
        Assert.Equal("12,0 из 48,0 МБ (25 %)", UpdateInstall.ProgressText(12 * 1048576L, 48 * 1048576L));
        Assert.Equal("3,5 МБ", UpdateInstall.ProgressText(3_670_016, null));
    }

    [Fact]
    public void Installer_keeps_the_AppId_and_never_deletes_user_data()
    {
        var iss = Iss();
        Assert.Contains("AppId={{" + UpdateInstall.AppId + "}", iss);
        Assert.DoesNotContain("[InstallDelete]", iss);
        Assert.DoesNotContain("[UninstallDelete]", iss);
        Assert.DoesNotMatch(new Regex(@"DelTree|DeleteFile\s*\(", RegexOptions.IgnoreCase), iss);
        Assert.Contains("PrivilegesRequired=lowest", iss);
        Assert.Contains(@"DefaultDirName={localappdata}\Programs\VorotexK13Studio", iss);   // program folder ≠ data folder %LOCALAPPDATA%\VorotexK13Studio
    }

    [Fact]
    public void Installer_writes_the_marker_the_program_checks()
    {
        var line = Iss().Split('\n').Select(l => l.Trim()).Single(l => l.StartsWith("VersionInfoCopyright="));
        var text = line["VersionInfoCopyright=".Length..].Replace("{#AppVersion}", "1.1.0");
        Assert.EndsWith(UpdateInstall.SetupMarker(V), text);
    }

    [Fact]
    public void Installer_waits_for_the_program_and_starts_it_again_after_an_in_app_update()
    {
        var iss = Iss();
        Assert.Contains($"CheckForMutexes('{UpdateInstall.Mutex}')", iss);
        Assert.Contains($"AppMutex={UpdateInstall.Mutex}", iss);
        Assert.Matches(new Regex(@"Parameters:\s*""--updated"";\s*Flags:\s*nowait;\s*Check:\s*IsInAppUpdate"), iss);
        Assert.Contains("CompareText(ParamStr(i), '/UPDATE')", iss);
    }
}

/// <summary>Release check of the real build: K13_VERIFY_SETUP / K13_VERIFY_PORTABLE = path, K13_VERIFY_VERSION = 1.1.0 (no-op when unset).</summary>
public class V110BuildCheck
{
    [Fact]
    public void Real_build_passes_the_in_app_update_check()
    {
        var ver = Environment.GetEnvironmentVariable("K13_VERIFY_VERSION");
        if (ver == null) return;
        var v = Version.Parse(ver);
        foreach (var (env, kind) in new[] { ("K13_VERIFY_SETUP", UpdateInstall.Kind.Installed), ("K13_VERIFY_PORTABLE", UpdateInstall.Kind.Portable) })
        {
            var p = Environment.GetEnvironmentVariable(env);
            Assert.False(string.IsNullOrEmpty(p), env);
            var data = File.ReadAllBytes(p!);
            Assert.Null(UpdateInstall.CheckFile(kind, data, UpdateInstall.Sha256File(p!), v));
            Assert.NotNull(UpdateInstall.CheckFile(kind, data, UpdateInstall.Sha256File(p!), new Version(v.Major, v.Minor, v.Build + 1)));
        }
    }
}
