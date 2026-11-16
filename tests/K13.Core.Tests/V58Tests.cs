using Vorotex.K13;
using Xunit;

namespace K13.Core.Tests;

/// <summary>v5.8: hardware mode Normal/Game (flags bit 7 = vendor KEYSMODE; status byte 3 = live flags).</summary>
public class V58Tests
{
    static byte[] Img() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", "exp15-prewrite-20261113-133452.bin"));
    static K13Status St(byte b3) => new(new byte[] { 0x07, 0x00, 0x41, b3, 0, 0, 0, 0x12, 0, 0, 0, 0, 0, 0, 0xE0, 0x00, 0, 0, 0, 0, 0 });

    [Fact]
    public void ModeFromStatusByte3Bit7()
    {
        Assert.Equal(HwMode.Normal, K13Mode.FromStatus(St(0x40)));   // makers at connect 15:02:40
        Assert.Equal(HwMode.Game, K13Mode.FromStatus(St(0xC0)));     // after the read exchange
        Assert.Equal(HwMode.Unknown, K13Mode.FromStatus(null));
        Assert.Equal(HwMode.Unknown, K13Mode.FromStatus(new K13Status(new byte[] { 0x07, 0x00 })));
        Assert.Equal(HwMode.Unknown, K13Mode.FromStatus(new K13Status(new byte[] { 0x01, 0, 0, 0xC0 })));
    }

    [Fact]
    public void PrepareTargetSetsKeysModeOnlyWhenFlagsAreWritten()
    {
        var img = Img();                                  // flags 0x40 in flash
        var cfg = K13MemoryCodec.Decode(img);
        StickTilts.Fill(cfg);
        Assert.Same(cfg, K13Mode.PrepareTarget(cfg, img)); // nothing else changed in flags → no flags write

        cfg.Led.Flags = 0x41;                              // e.g. lock bit
        var t = K13Mode.PrepareTarget(cfg, img);
        Assert.Equal(0xC1, t.Led.Flags);
        Assert.Equal(0x41, cfg.Led.Flags);                 // caller untouched

        var dev = (byte[])img.Clone(); dev[0xE0] = 0xF0;   // device-owned bits 5–4 kept from the keypad
        cfg.Led.Flags = 0x40;
        Assert.Equal(0xF0, K13Mode.PrepareTarget(cfg, dev).Led.Flags);

        Assert.Equal(0xC0, K13Mode.PrepareTarget(cfg, null).Led.Flags);
    }

    [Fact]
    public void ProfileWithGameFlagsAgainstGameDeviceNeedsNoFlagsWrite()
    {
        var img = Img(); img[0xE0] = 0xC0;
        var cfg = K13MemoryCodec.Decode(img); cfg.Led.Flags = 0x40;  // old profile read in Normal
        StickTilts.Fill(cfg);
        Assert.Equal(0xC0, K13Mode.PrepareTarget(cfg, img).Led.Flags);
        Assert.DoesNotContain(PcLayerHealth.PendingWrite(cfg, img), b => b.Address <= 0xE0 && b.Address + b.Data.Length > 0xE0);
    }

    [Fact]
    public void WriterWritesGameFlagsWhenFlagsChange()
    {
        var img = Img();
        var fake = new FakeK13((byte[])img.Clone()) { Writable = true };
        var dev = new K13Device(fake) { DelayMs = 0 };
        var cfg = K13MemoryCodec.Decode(img); StickTilts.Fill(cfg);
        cfg.Led.Flags = 0x41;
        var tmp = Path.Combine(Path.GetTempPath(), "k13t-" + Guid.NewGuid().ToString("N"));
        try
        {
            K13WriteGate.Unlock(K13WriteGate.ConfirmationPhrase);
            var r = K13Writer.WriteProfile(dev, cfg, _ => { }, null, false, tmp);
            Assert.True(r.Ok, r.Message);
            Assert.Equal(0xC1, fake.Memory[0xE0]);
        }
        finally { K13WriteGate.Lock(); dev.Dispose(); try { Directory.Delete(tmp, true); } catch { } }
    }

    [Fact]
    public void NewProfilesKeepFactoryStickToggle()
    {
        var img = Img(); img[0x1A8] = 0xF4;
        var p = new AppProfile { Name = "n", Device = K13MemoryCodec.Decode(img) };
        ProfileMigrations.EnsureStickRing(p, setStickKey: false);
        Assert.Equal(0xF4, p.Device!.GetKey(16));
        Assert.Contains(p.Host.Keys, k => k.TriggerVk == ProfileMigrations.StickVk);
        Assert.NotEmpty(p.Host.Ring.Slots);
        ProfileMigrations.EnsureStickRing(p);              // the old migration still converts
        Assert.Equal(0x68, p.Device!.GetKey(16));
    }

    [Fact]
    public void KbdModeIsFirmwareOnly() => Assert.True(PcLayerPlanner.IsFirmwareOnly(0xF4));
}
