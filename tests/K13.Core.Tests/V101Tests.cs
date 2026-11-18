using System.Text.RegularExpressions;
using Xunit;

namespace Vorotex.K13.Tests;

/// <summary>1.0.1: «Включить Game сейчас» removed — the read exchange did not switch Normal → Game on the makers' K13
/// (17 of 17 manual tries only blinked the backlight). The UI must point to the stick and «Запоминать режим».</summary>
public class V101Tests
{
    static string StudioSrc()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src", "K13.Studio"))) d = d.Parent;
        Assert.NotNull(d);
        return Path.Combine(d!.FullName, "src", "K13.Studio");
    }

    internal static IEnumerable<(string File, int Line, string Text)> CodeLines()
    {
        foreach (var f in Directory.EnumerateFiles(StudioSrc(), "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".cs") || f.EndsWith(".xaml")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            int n = 0;
            foreach (var line in File.ReadLines(f))
            {
                n++;
                var t = line.TrimStart();
                if (t.StartsWith("//")) continue;
                int c = t.IndexOf(" //", StringComparison.Ordinal);
                yield return (Path.GetFileName(f), n, c >= 0 ? t[..c] : t);
            }
        }
    }

    [Fact]
    public void Manual_switch_text_is_gone_from_the_UI_sources()
    {
        // SmokeTest.cs only checks that the text is absent from the window
        var bad = CodeLines().Where(l => l.File != "SmokeTest.cs" && l.Text.Contains(K13ModeHints.RemovedManualAction)).Select(l => $"{l.File}:{l.Line}").ToList();
        Assert.True(bad.Count == 0, "осталось «Включить Game сейчас»: " + string.Join(", ", bad));
    }

    [Fact]
    public void Old_setting_name_that_promised_Game_is_gone()
    {
        var bad = CodeLines().Where(l => l.Text.Contains("Автоматически включать Game")).Select(l => $"{l.File}:{l.Line}").ToList();
        Assert.True(bad.Count == 0, "осталось «Автоматически включать Game»: " + string.Join(", ", bad));
    }

    [Fact]
    public void Manual_activation_is_not_wired_to_any_button_or_menu()
    {
        var bad = CodeLines().Where(l => Regex.IsMatch(l.Text, @"ActivateKeypad\s*\(\s*auto\s*:\s*false")).Select(l => $"{l.File}:{l.Line}").ToList();
        Assert.True(bad.Count == 0, "ручной вызов ActivateKeypad: " + string.Join(", ", bad));
    }

    [Fact]
    public void Normal_banner_points_to_the_stick()
    {
        var s = K13ModeHints.NormalBanner("Клавиатура в режиме Normal (заводские клавиши). ", 0x91);
        Assert.Contains("нажмите центр стика", s);
        Assert.Contains("синий индикатор", s);
        Assert.DoesNotContain(K13ModeHints.RemovedManualAction, s);
        Assert.DoesNotContain("не смогла включить Game", s);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData((byte)0x91, true)]
    [InlineData((byte)0x93, false)]
    public void Remember_is_offered_only_when_off_or_unknown(byte? flag, bool offer)
    {
        Assert.Equal(offer, K13ModeHints.OfferRemember(flag));
        Assert.Equal(offer, K13ModeHints.NormalBanner("", flag).Contains("Запоминать режим"));
    }

    [Fact]
    public void Foreign_key_status_is_honest()
    {
        Assert.Contains("нажмите центр стика", string.Format(K13ModeHints.StatusForeignKey, "Num 2"));
        Assert.Contains("«Num 2»", string.Format(K13ModeHints.StatusForeignKey, "Num 2"));
    }
}
