using Vorotex.K13;
using Xunit;

namespace K13.Core.Tests;

/// <summary>1.3.2: factory success text, hints aware of the blue indicator (0x000F bit 4), cursor default speed 3.</summary>
public class V132Tests
{
    [Fact]
    public void Factory_written_text_is_factory_specific()
    {
        var s = K13ModeHints.FactoryWritten("Основной", 1.23);
        Assert.Contains("Заводские назначения записаны и проверены", s);
        Assert.Contains("Профиль «Основной» сохранён на ПК", s);
        Assert.Contains("Применить профиль", s);
        Assert.DoesNotContain("Ваши назначения работают", s);
        Assert.DoesNotContain("индикатор", s);
    }

    [Theory]
    [InlineData((byte)0x81, true)]
    [InlineData((byte)0x83, true)]
    [InlineData((byte)0x91, false)]
    [InlineData((byte)0x93, false)]
    [InlineData((byte)0x00, false)]   // unknown pattern — do not claim anything
    public void Indicator_off_only_for_known_off_patterns(byte b, bool off) => Assert.Equal(off, K13ModeHints.IndicatorOff(b));

    [Fact]
    public void Hints_do_not_promise_the_LED_when_it_is_off()
    {
        foreach (var s in new[] { K13ModeHints.PressStickFor(0x81), K13ModeHints.GameShown(0x83), K13ModeHints.NormalBanner("", 0x81), K13ModeHints.StatusForeignKeyFor("Num 2", 0x81) })
        {
            Assert.DoesNotContain("загорится", s);
            Assert.DoesNotContain("горит синий", s);
            Assert.Contains("выключен", s);
            Assert.Contains("Studio", s);
        }
        Assert.Contains("загорится синий индикатор", K13ModeHints.PressStickFor(0x91));
        Assert.Contains("загорится синий индикатор", K13ModeHints.PressStickFor(null));
        Assert.Contains("горит синий индикатор", K13ModeHints.GameShown(0x93));
        Assert.Contains("«Num 2»", K13ModeHints.StatusForeignKeyFor("Num 2", 0x91));
    }

    [Fact]
    public void Cursor_default_speed_is_3_for_new_assignments()
    {
        Assert.Equal(3, PointerSpec.DefaultAmount(PointerKind.Move));
        Assert.Equal(3, PointerSpec.DefaultAmount(PointerKind.Scroll));
    }

    [Fact]
    public void Saved_cursor_speeds_are_kept()
    {
        foreach (var n in new[] { 1, 4, 6, 10 })
        {
            var a = new PointerSpec(PointerKind.Move, PointerDir.Right, PointerMode.Toggle, n).ToAction();
            Assert.Equal(n, PointerSpec.From(a)!.Amount);
        }
        // an older saved action without an explicit amount keeps its old meaning (cursor 4)
        var legacy = new PointerSpec(PointerKind.Move, PointerDir.Up, PointerMode.Toggle, 4).ToAction(); legacy.Arguments = "toggle";
        Assert.Equal(4, PointerSpec.From(legacy)!.Amount);
    }
}
