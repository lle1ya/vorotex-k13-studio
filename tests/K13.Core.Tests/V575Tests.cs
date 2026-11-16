using Vorotex.K13;
using Xunit;

namespace K13.Core.Tests;

/// <summary>v5.7.5: stick tilts are never FF (the makers' K13: FF tilt = sends nothing once the stored layout is active).</summary>
public class V575Tests
{
    static byte[] Img() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", "exp15-prewrite-20261113-133452.bin"));
    static readonly int[] Tilts = { 0x188, 0x190, 0x198, 0x1A0 };

    /// <summary>Like the makers' dumps: tilt columns FF, stick press F13.</summary>
    static byte[] MakersLike()
    {
        var img = Img();
        foreach (var a in Tilts) img[a] = 0xFF;
        img[0x1A8] = 0x68;
        return img;
    }

    [Fact]
    public void TiltAddressesAreColumns12To15()
    {
        var cfg = K13MemoryCodec.Decode(MakersLike());
        Assert.Equal(new[] { 12, 13, 14, 15 }, StickTilts.Unassigned(cfg));
        Assert.Equal(new byte[] { 0x50, 0x4F, 0x51, 0x52 }, StickTilts.Columns.Select(StickTilts.Arrow));
    }

    [Fact]
    public void WriterNeverWritesFfTilts()
    {
        var mem = MakersLike();
        var fake = new FakeK13(mem) { Writable = true };
        var dev = new K13Device(fake) { DelayMs = 0 };
        var cfg = K13MemoryCodec.Decode(MakersLike());     // profile straight from the read: tilts FF
        cfg.SetKey(3, 0x3E);                                // key 4 = F5
        var tmp = Path.Combine(Path.GetTempPath(), "k13t-" + Guid.NewGuid().ToString("N"));
        var lines = new List<string>();
        try
        {
            K13WriteGate.Unlock(K13WriteGate.ConfirmationPhrase);
            var r = K13Writer.WriteProfile(dev, cfg, lines.Add, null, false, tmp);
            Assert.True(r.Ok, r.Message);
            Assert.Equal(new byte[] { 0x50, 0x4F, 0x51, 0x52 }, Tilts.Select(a => fake.Memory[a]));
            Assert.Equal(0x3E, fake.Memory[0x140]); // key 4 (BASE+0x48+3*8 = 0x140)
            Assert.Equal(0x68, fake.Memory[0x1A8]);
            Assert.Contains(lines, l => l.Contains("Наклоны стика"));
            Assert.Equal(0xFF, cfg.GetKey(12));            // caller's object untouched
        }
        finally { K13WriteGate.Lock(); dev.Dispose(); try { Directory.Delete(tmp, true); } catch { } }
    }

    [Fact]
    public void ProfilesGetArrowsOnLoadAndAfterRead()
    {
        var data = new ProfileStoreData { Profiles = { new AppProfile { Name = "Основной", Device = K13MemoryCodec.Decode(MakersLike()) } } };
        var notes = ProfileMigrations.Migrate(data);
        Assert.Contains(notes, n => n.Contains("стрелки"));
        Assert.Empty(StickTilts.Unassigned(data.Profiles[0].Device!));
        Assert.DoesNotContain(ProfileMigrations.Migrate(data), n => n.Contains("стрелки")); // once

        var p = new AppProfile { Name = "x", Device = K13MemoryCodec.Decode(Img()) };
        ProfileMigrations.ReplaceDevice(p, K13MemoryCodec.Decode(MakersLike()));
        Assert.Equal(new byte[] { 0x50, 0x4F, 0x51, 0x52 }, StickTilts.Columns.Select(c => p.Device!.GetKey(c)));
    }

    [Fact]
    public void ReadWithFfTiltsMakesProfileDirty()
    {
        var img = MakersLike();
        var profile = K13MemoryCodec.Decode(img);
        StickTilts.Fill(profile);
        var pending = PcLayerHealth.PendingWrite(profile, img);
        Assert.Contains(pending, b => b.Address <= 0x188 && b.Address + b.Data.Length > 0x188);
        Assert.Contains(pending, b => b.Address <= 0x1A0 && b.Address + b.Data.Length > 0x1A0);
    }

    [Fact]
    public void UnassignedKeysAreListed()
    {
        var cfg = K13MemoryCodec.Decode(MakersLike());
        for (int c = 0; c < 12; c++) cfg.SetKey(c, K13Layout.FactoryColumnCodes[c]);
        cfg.SetKey(5, 0xFF);
        var names = StickTilts.UnassignedControls(cfg).Select(c => c.Column).ToList();
        Assert.Equal(new[] { 5, 12, 13, 14, 15 }, names.OrderBy(x => x));
        Assert.DoesNotContain(16, names);
    }

    [Fact]
    public void CodecStaysByteExact()
    {
        var img = MakersLike();
        var outImg = K13MemoryCodec.Encode(K13MemoryCodec.Decode(img), img, out _);
        Assert.Equal(0xFF, outImg[0x188]); // the codec itself is faithful; tilts are filled in the profile and the writer
    }
}
