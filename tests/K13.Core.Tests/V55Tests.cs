using System.Text.Json;
using Vorotex.K13;
using Xunit;

namespace K13.Core.Tests;

public class SwitchToastTests
{
    private static readonly PixelRect Work = new(0, 0, 1920, 1040);

    [Fact]
    public void Shows_only_for_switches_you_did_not_make_here()
    {
        Assert.True(SwitchToastLogic.ShouldShow(true, SwitchSource.Auto, true));
        Assert.True(SwitchToastLogic.ShouldShow(true, SwitchSource.KeypadKey, true));
        Assert.False(SwitchToastLogic.ShouldShow(true, SwitchSource.Manual, true));
        Assert.False(SwitchToastLogic.ShouldShow(true, SwitchSource.Tray, true));
        Assert.False(SwitchToastLogic.ShouldShow(false, SwitchSource.Auto, true));
        Assert.False(SwitchToastLogic.ShouldShow(true, SwitchSource.Auto, false));
    }

    [Fact]
    public void Content_for_rule_fallback_and_key()
    {
        var a = SwitchToastLogic.For("1С", "по программе 1cv8c.exe", SwitchSource.Auto)!;
        Assert.Equal("Профиль: 1С", a.Title);
        Assert.Equal("Профиль: 1С · по программе 1cv8c.exe", a.Text);
        var f = SwitchToastLogic.For("Основной", "по умолчанию (chrome.exe — без правила)", SwitchSource.Auto, isFallback: true)!;
        Assert.True(f.IsFallback);
        Assert.Equal("Профиль: Основной", f.Title);
        Assert.Equal("кнопкой на клавиатуре", SwitchToastLogic.For("Игры", "что угодно", SwitchSource.KeypadKey)!.Detail);
        Assert.Null(SwitchToastLogic.For("Игры", "вручную", SwitchSource.Manual));
        Assert.Null(SwitchToastLogic.For("Игры", "трей", SwitchSource.Tray));
        Assert.Null(SwitchToastLogic.For("  ", "x", SwitchSource.Auto));
        Assert.Equal("Профиль: 1С", SwitchToastLogic.For("1С", "", SwitchSource.Auto)!.Text);
    }

    [Fact]
    public void Content_is_cleaned_and_capped()
    {
        var c = SwitchToastLogic.For("A\r\nB\t", new string('x', 500) + "\u0007", SwitchSource.Auto)!;
        Assert.Equal("AB", c.Profile);
        Assert.True(c.Detail.Length <= SwitchToastLogic.MaxDetail);
        Assert.EndsWith("…", c.Detail);
        Assert.DoesNotContain('\u0007', c.Detail);
    }

    [Fact]
    public void Bottom_right_and_top_center()
    {
        Assert.Equal((1920 - 20 - 300, 1040 - 20 - 80), SwitchToastLogic.Place(ToastPlacement.BottomRight, Work, 5, 5, 300, 80, 20));
        Assert.Equal(((1920 - 300) / 2, 20), SwitchToastLogic.Place(ToastPlacement.TopCenter, Work, 5, 5, 300, 80, 20));
    }

    [Fact]
    public void Near_cursor_flips_at_screen_edges()
    {
        Assert.Equal((100 + 20, 100 + 20), SwitchToastLogic.Place(ToastPlacement.NearCursor, Work, 100, 100, 300, 80, 20));
        var (x, y) = SwitchToastLogic.Place(ToastPlacement.NearCursor, Work, 1900, 1030, 300, 80, 20);
        Assert.Equal(1900 - 20 - 300, x);
        Assert.Equal(1030 - 20 - 80, y);
    }

    [Theory]
    [InlineData(ToastPlacement.BottomRight)]
    [InlineData(ToastPlacement.TopCenter)]
    [InlineData(ToastPlacement.NearCursor)]
    public void Always_inside_the_work_area_even_on_a_left_monitor(ToastPlacement pl)
    {
        var left = new PixelRect(-1280, 200, 0, 1224); // second monitor on the left, lower
        foreach (var (cx, cy) in new[] { (-1279, 201), (-1, 1223), (-640, 700), (500, 50) })
        {
            var (x, y) = SwitchToastLogic.Place(pl, left, cx, cy, 360, 90, 20);
            Assert.InRange(x, left.Left, left.Right - 360);
            Assert.InRange(y, left.Top, left.Bottom - 90);
        }
        // a notice bigger than a tiny work area does not throw and starts at its corner
        var (tx, ty) = SwitchToastLogic.Place(pl, new PixelRect(0, 0, 200, 50), 10, 10, 360, 90, 20);
        Assert.Equal((0, 0), (tx, ty));
    }

    [Fact]
    public void Settings_default_on_and_survive_old_or_bad_files()
    {
        var fresh = new ProfileStoreData();
        Assert.True(fresh.SwitchToast);
        Assert.Equal(ToastPlacement.BottomRight, fresh.ToastPlacement);
        Assert.False(fresh.ToastSystem);

        var old = JsonSerializer.Deserialize<ProfileStoreData>("{\"Profiles\":[],\"AutoSwitch\":true}")!;
        Assert.True(old.SwitchToast);

        var bad = JsonSerializer.Deserialize<ProfileStoreData>("{\"Profiles\":[],\"ToastPlacement\":42,\"SwitchToast\":false}")!;
        var notes = bad.Normalize();
        Assert.Equal(ToastPlacement.BottomRight, bad.ToastPlacement);
        Assert.False(bad.SwitchToast);
        Assert.Contains(notes, n => n.Contains("уведомления"));

        var round = JsonSerializer.Deserialize<ProfileStoreData>(JsonSerializer.Serialize(new ProfileStoreData { SwitchToast = false, ToastPlacement = ToastPlacement.NearCursor, ToastSystem = true }))!;
        Assert.False(round.SwitchToast);
        Assert.Equal(ToastPlacement.NearCursor, round.ToastPlacement);
        Assert.True(round.ToastSystem);
    }

    [Fact]
    public void Placement_names_are_russian_and_distinct()
    {
        var names = Enum.GetValues<ToastPlacement>().Select(SwitchToastLogic.PlacementName).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Contains("Внизу справа", names);
        Assert.Contains("У курсора мыши", names);
    }
}
