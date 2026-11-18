using Vorotex.K13;
using Xunit;

namespace K13.Core.Tests;

/// <summary>v5.9: experimental «remember mode» (EEPROM 0x000F bit 1) and the hard guard for 0x0000–0x0007.</summary>
public class V59Tests
{
    static byte[] Img() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", "exp15-prewrite-20261113-133452.bin"));

    [Fact]
    public void KnownUnitsHold0x91()
    {
        var img = Img();
        Assert.Equal(0x91, img[0x0F]);
        Assert.Equal(new byte[] { 0xB4, 0x01, 0xB4, 0x50, 0x23, 0xFA, 0x30, 0x4B }, img.Take(8));
        Assert.False(K13RememberMode.IsOn(img[0x0F]));
    }

    [Theory]
    [InlineData(0x91, true, 0x93)]
    [InlineData(0x93, true, 0x93)]
    [InlineData(0x93, false, 0x91)]
    [InlineData(0x91, false, 0x91)]
    public void TargetFlipsOnlyBit1(byte cur, bool on, byte want)
    {
        var (t, err) = K13RememberMode.Target(cur, on);
        Assert.Null(err);
        Assert.Equal(want, t);
    }

    [Theory]
    [InlineData(0x00)] [InlineData(0xFF)] [InlineData(0x11)] [InlineData(0x99)] [InlineData(0x90)] [InlineData(0xB1)]
    public void UnknownPatternIsRefused(byte cur)
    {
        var (t, err) = K13RememberMode.Target(cur, true);
        Assert.Null(t);
        Assert.Contains("ничего не записывает", err);
    }

    [Fact]
    public void RestoreFromBackup()
    {
        var bak = Img();                                       // 0x91
        Assert.Equal((byte?)0x91, K13RememberMode.RestoreTarget(0x93, bak).Target);
        bak[0x0F] = 0x55;
        Assert.Null(K13RememberMode.RestoreTarget(0x93, bak).Target);
        Assert.Null(K13RememberMode.RestoreTarget(0x93, new byte[8]).Target);
        Assert.Null(K13RememberMode.RestoreTarget(0x10, Img()).Target);
    }

    [Fact]
    public void ProtectedBytesNeverInAPlan()
    {
        foreach (var a in new[] { 0, 3, 7 })
            Assert.Throws<WriteBlockedException>(() => K13Protocol.BuildApplyFrames(new[] { new WriteBlock(a, new byte[] { 1 }, "x") }));
        Assert.Throws<WriteBlockedException>(() => K13Protocol.BuildApplyFrames(new[] { new WriteBlock(0, new byte[8], "x") }));
        Assert.NotEmpty(K13Protocol.BuildApplyFrames(new[] { new WriteBlock(8, new byte[8], "x") }));
        Assert.True(K13Protocol.TouchesProtected(7, 1));
        Assert.False(K13Protocol.TouchesProtected(8, 8));
    }

    [Fact]
    public void ProtectedFrameRefusedEvenWithGateOpen()
    {
        var fake = new FakeK13(Img()) { Writable = true };
        using var dev = new K13Device(fake) { DelayMs = 0 };
        K13WriteGate.Unlock(K13WriteGate.ConfirmationPhrase);
        try
        {
            Assert.Throws<WriteBlockedException>(() => dev.SendSafe(new byte[] { 7, 0x18, 0x03, 0, 0x05, 0, 0x00, 0 }));
            Assert.True(K13Protocol.IsProtectedWriteFrame(new byte[] { 7, 0x18, 0x09, 0, 0x00, 0, 0, 7 }));
            Assert.True(K13Protocol.IsProtectedWriteFrame(new byte[] { 7, 0x18, 0x81, 0, 0x00, 0, 0, 0x60 }));
            Assert.False(K13Protocol.IsProtectedWriteFrame(new byte[] { 7, 0x18, 0x03, 0, 0x0F, 0, 0x93, 0 }));
            Assert.False(K13Protocol.IsProtectedWriteFrame(new byte[] { 7, 0x18, 0x00, 0, 0x00, 0, 0, 0 })); // read: load block 0
        }
        finally { K13WriteGate.Lock(); }
        Assert.Equal(0xB4, fake.Memory[0]);
    }

    [Fact]
    public void RunWritesOneByteWithVendorSequenceAndVerifies()
    {
        var mem = Img();
        var before = (byte[])mem.Clone();
        var fake = new FakeK13(mem) { Writable = true };
        var tmp = Path.Combine(Path.GetTempPath(), "k13t-" + Guid.NewGuid().ToString("N"));
        var log = new List<string>();
        using var dev = new K13Device(fake) { DelayMs = 0 };
        K13WriteGate.Unlock(K13WriteGate.ConfirmationPhrase);
        try
        {
            int f0 = fake.Frames.Count;
            var r = K13RememberMode.Run(dev, c => K13RememberMode.Target(c, true), log.Add, tmp);
            Assert.True(r.Ok, r.Message);
            Assert.True(r.Written);
            Assert.Equal(0x91, r.Before); Assert.Equal(0x93, r.After);
            Assert.True(File.Exists(r.BackupPath));
            Assert.Equal(0x91, File.ReadAllBytes(r.BackupPath)[0x0F]);
            // only 0x000F changed in the whole memory
            for (int i = 0; i < mem.Length; i++) if (i != 0x0F) Assert.Equal(before[i], mem[i]);
            Assert.Equal(0x93, mem[0x0F]);
            Assert.Equal(before[0xE0], mem[0xE0]);
            // exact write frames: 03 / 09 / 01 for one byte at 0x000F, then 11, 00
            var w = fake.Frames.Skip(f0).Where(f => f[2] is 0x03 or 0x09 or 0x11 || (f[2] == 0x01 && f[4] == 0x0F)).Select(Convert.ToHexString).ToList();
            Assert.Equal(new[] { "071803000F009300", "071809000F000000", "071801000F000000", "0718110000000000" }, w);
            Assert.DoesNotContain(fake.Frames, f => f[2] is 0x03 or 0x09 && (f[4] | f[5] << 8) < 8);

            var off = K13RememberMode.Run(dev, c => K13RememberMode.Target(c, false), log.Add, tmp);
            Assert.True(off.Ok, off.Message);
            Assert.Equal(0x91, mem[0x0F]);
            Assert.Equal(before, mem);
        }
        finally { K13WriteGate.Lock(); try { Directory.Delete(tmp, true); } catch { } }
    }

    [Fact]
    public void RunRefusesUnknownPatternAndWritesNothing()
    {
        var mem = Img(); mem[0x0F] = 0x55;
        var before = (byte[])mem.Clone();
        var fake = new FakeK13(mem) { Writable = true };
        var tmp = Path.Combine(Path.GetTempPath(), "k13t-" + Guid.NewGuid().ToString("N"));
        using var dev = new K13Device(fake) { DelayMs = 0 };
        K13WriteGate.Unlock(K13WriteGate.ConfirmationPhrase);
        try
        {
            var r = K13RememberMode.Run(dev, c => K13RememberMode.Target(c, true), _ => { }, tmp);
            Assert.False(r.Ok); Assert.False(r.Written);
            Assert.Equal(before, mem);
            Assert.DoesNotContain(fake.Frames, f => f[2] is 0x03 or 0x09);
        }
        finally { K13WriteGate.Lock(); try { Directory.Delete(tmp, true); } catch { } }
    }

    [Fact]
    public void RunWithoutGateWritesNothing()
    {
        var mem = Img(); var before = (byte[])mem.Clone();
        var fake = new FakeK13(mem) { Writable = true };
        var tmp = Path.Combine(Path.GetTempPath(), "k13t-" + Guid.NewGuid().ToString("N"));
        using var dev = new K13Device(fake) { DelayMs = 0 };
        try { Assert.Throws<WriteBlockedException>(() => K13RememberMode.Run(dev, c => K13RememberMode.Target(c, true), _ => { }, tmp)); }
        finally { try { Directory.Delete(tmp, true); } catch { } }
        Assert.Equal(before, mem);
    }
}
