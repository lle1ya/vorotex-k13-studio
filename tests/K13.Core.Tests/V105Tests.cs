using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Vorotex.K13.Tests;

/// <summary>1.0.5: «Пробовать включать Game при подключении» and the connect-time exchange are removed. The makers' diagnostics
/// (18.11.2026 09:10–09:18) showed the exchange never moved Normal → Game: 3 «successes» only after unplugging a keypad that was
/// already in Game, 6 of 6 failures after unplugging it in Normal.</summary>
public class V105Tests
{
    [Fact]
    public void Connect_setting_is_gone_from_the_studio_code()
    {
        var bad = V101Tests.CodeLines().Where(l => (l.Text.Contains(K13ModeHints.RemovedConnectSetting) && !l.File.StartsWith("SmokeTest")) || l.Text.Contains("ActivateOnConnect") || l.Text.Contains("SetActivateOnConnect"))
            .Select(l => $"{l.File}:{l.Line}").ToList();
        Assert.True(bad.Count == 0, "осталась настройка «Пробовать включать Game при подключении»: " + string.Join(", ", bad));
    }

    [Fact]
    public void Nothing_is_sent_to_the_keypad_on_connect()
    {
        var bad = V101Tests.CodeLines().Where(l => Regex.IsMatch(l.Text, @"\bActivateKeypad\s*\(|K13Activation\.Activate\s*\(")).Select(l => $"{l.File}:{l.Line}").ToList();
        Assert.True(bad.Count == 0, "осталась отправка обмена при подключении: " + string.Join(", ", bad));
    }

    [Fact]
    public void Old_settings_files_still_load_and_keep_the_unused_field()
    {
        var d = JsonSerializer.Deserialize<ProfileStoreData>("{\"ActiveProfile\":\"x\",\"ActivateOnConnect\":true}")!;
        Assert.Equal("x", d.ActiveProfile);
        var back = JsonSerializer.Deserialize<ProfileStoreData>(JsonSerializer.Serialize(d))!;
        Assert.Equal("x", back.ActiveProfile);
    }

    [Theory]
    [InlineData(null)]
    [InlineData((byte)0x91)]
    [InlineData((byte)0x93)]
    public void Normal_banner_never_claims_the_program_tried(byte? flag)
    {
        var s = K13ModeHints.NormalBanner("Клавиатура в режиме Normal. ", flag);
        Assert.Contains(K13ModeHints.PressStick, s);
        Assert.DoesNotContain("не смогла", s);
        Assert.DoesNotContain(K13ModeHints.RemovedConnectSetting, s);
    }
}
