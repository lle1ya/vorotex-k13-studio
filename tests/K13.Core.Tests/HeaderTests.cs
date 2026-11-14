using Vorotex.K13;
using Xunit;

namespace K13.Core.Tests;

/// <summary>v5.2: 0xD0 header is checked structurally (the makers' K13 reports DA 1E instead of E1 1E).</summary>
public class HeaderTests
{
    static string D(string f) => Path.Combine(AppContext.BaseDirectory, "data", f);
    static byte[] Img() => File.ReadAllBytes(D("exp15-prewrite-20261113-133452.bin")).Take(0x300).ToArray();

    [Fact]
    public void OurUnitIsExact()
    {
        var h = K13Header.Inspect(Img(), 0xE0);
        Assert.Equal(HeaderVerdict.Exact, h.Verdict);
        Assert.Equal(0xE0, h.Base);
        Assert.Equal("E1 1E E0 00 F0 0F 00 00", h.RawHex);
    }

    [Fact]
    public void Da1eWithSaneLayoutIsCompatibleAndWritable()
    {
        var img = Img(); img[0xD0] = 0xDA;
        var h = K13Header.Inspect(img, 0xE0);
        Assert.Equal(HeaderVerdict.Compatible, h.Verdict);
        Assert.True(h.CanWrite);
        Assert.Contains(h.Notes, n => n.Contains("DA 1E"));
        var cfg = K13MemoryCodec.Decode(img);            // used to throw «Нет сигнатуры E1 1E…»
        cfg.Keymap[3 * 8] = 0x69;                         // key 4 → F14
        var outImg = K13MemoryCodec.Encode(cfg, img, out var plan);
        Assert.Equal(0xDA, outImg[0xD0]);                 // unit's own marker preserved
        Assert.Equal(0x1E, outImg[0xD1]);
        Assert.DoesNotContain(K13Protocol.DiffPlan(plan, img), b => b.Address == 0xD0);
    }

    [Fact]
    public void BrokenPointersAreUncertainNotFatal()
    {
        var img = Img(); img[0xD0] = 0xDA; img[0xE0 + 0x1C] ^= 0x10;
        var h = K13Header.Inspect(img, 0xE0);
        Assert.Equal(HeaderVerdict.Uncertain, h.Verdict);
        Assert.False(h.CanWrite);
        Assert.Contains(h.Problems, p => p.Contains("Указатели"));
    }

    [Fact]
    public void ForeignSecondByteIsUncertain()
    {
        var img = Img(); img[0xD1] = 0x2A;
        Assert.Equal(HeaderVerdict.Uncertain, K13Header.Inspect(img, 0xE0).Verdict);
    }

    [Fact]
    public void DeviceBaseMismatchIsUncertain()
    {
        Assert.Equal(HeaderVerdict.Uncertain, K13Header.Inspect(Img(), 0x100).Verdict);
    }

    [Fact]
    public void NoBaseAnywhereIsUnusable()
    {
        var img = Img(); img[0xD2] = 0xFF; img[0xD3] = 0xFF;
        Assert.Equal(HeaderVerdict.Unusable, K13Header.Inspect(img).Verdict);
        Assert.Throws<K13FormatException>(() => K13MemoryCodec.Decode(img));
    }
}
