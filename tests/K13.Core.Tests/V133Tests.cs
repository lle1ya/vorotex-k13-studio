using Vorotex.K13;
using Xunit;

namespace K13.Core.Tests;

/// <summary>1.3.3: K12 read-only names from the makers' dumps of 19.11.2026; writes stay blocked.</summary>
public class V133Tests
{
    static byte[] Img(string f) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", f));
    static byte[] Reset() => Img("k12-reset-20261119-094139.bin");
    static byte[] Apply() => Img("k12-apply-20261119-095032.bin");

    [Fact]
    public void Both_dumps_are_K12_and_write_blocked()
    {
        foreach (var i in new[] { Reset(), Apply() })
        {
            Assert.Equal(KeypadModel.K12Likely, K13Model.Detect(i));
            Assert.False(K13Model.WriteAllowed(K13Model.Detect(i)));
            Assert.True(K12Layout.IsK12(i));
        }
        Assert.False(K12Layout.IsK12(Img("exp15-postwrite-20261113-133715.bin")));
    }

    [Fact]
    public void Apply_changed_exactly_the_ten_bytes()
    {
        var a = Reset(); var b = Apply();
        var diff = Enumerable.Range(0, a.Length).Where(i => a[i] != b[i]).ToArray();
        Assert.Equal(new[] { 0xE0, 0xE2, 0xE4, 0xF0, 0xF1, 0x1D8, 0x1D9, 0x1DA, 0x1DB, 0x1E4 }, diff);
        Assert.Equal(0x40, a[0xE0]); Assert.Equal(0xC0, b[0xE0]);   // BASE+0 bit 7 KEYSMODE
        Assert.Equal(a[0x0F], b[0x0F]);                              // 0x000F untouched by «Применить»
    }

    [Fact]
    public void Rows_match_the_makers_key_test()
    {
        Assert.Equal(K13Model.K12FactoryRow, K12Layout.NormalRow(Reset()));
        Assert.Equal(K12Layout.GameAfterReset, K12Layout.GameRow(Reset()));
        Assert.Equal(K12Layout.GameAfterReset, K12Layout.GameRow(Apply()));
        Assert.Equal((3, 2), K12Layout.GameWheel(Reset()));
    }

    [Theory]
    [InlineData((ushort)0x61, HwMode.Normal, 0)]    // Num 1 → key 1
    [InlineData((ushort)0x66, HwMode.Normal, 9)]    // Num 6 → key 6 (column 9)
    [InlineData((ushort)0x69, HwMode.Normal, 6)]    // Num 9 → key 9 (column 6)
    [InlineData((ushort)0x60, HwMode.Normal, 10)]   // Num 0 → VOROTEX
    [InlineData((ushort)0x6E, HwMode.Normal, 11)]   // Num . → dot
    [InlineData((ushort)0x31, HwMode.Game, 0)]      // 1
    [InlineData((ushort)0x30, HwMode.Game, 10)]     // 0 → VOROTEX
    [InlineData((ushort)0xC0, HwMode.Game, 11)]     // ` ~ → dot
    [InlineData((ushort)0x0D, HwMode.Game, 5)]      // Enter
    [InlineData((ushort)0xAF, HwMode.Normal, K12Layout.KnobRight)]
    [InlineData((ushort)0xAE, HwMode.Normal, K12Layout.KnobLeft)]
    public void Keys_are_named_by_K12_position(ushort vk, HwMode mode, int id)
        => Assert.Equal(new[] { id }, K12Layout.ControlsFor(vk, mode, Reset()));

    [Fact]
    public void Knob_scroll_in_Game()
    {
        Assert.Equal(new[] { K12Layout.KnobRight }, K12Layout.ControlsForScroll(true, HwMode.Game, Reset()));
        Assert.Equal(new[] { K12Layout.KnobLeft }, K12Layout.ControlsForScroll(false, HwMode.Game, Reset()));
        Assert.Empty(K12Layout.ControlsForScroll(true, HwMode.Normal, Reset()));
        Assert.Contains("клавиша VOROTEX: Num 0", K12Layout.Describe(Reset()));
    }
}
