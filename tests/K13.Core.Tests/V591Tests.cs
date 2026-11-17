using Vorotex.K13;
using Xunit;

namespace K13.Core.Tests;

/// <summary>v5.9.1: the first read after a write echoes the written byte into index 0 (hardware 17.11.2026) — verify must not misreport.</summary>
public class V591Tests
{
    static byte[] Img() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", "exp15-prewrite-20261113-133452.bin"));

    [Fact]
    public void ArtefactIsReproducedByTheFake()
    {
        var fake = new FakeK13(Img()) { Writable = true, EchoLoadsAfterCommit = 3 };
        using var dev = new K13Device(fake) { DelayMs = 0, SettleAfterWriteMs = 0 };
        K13WriteGate.Unlock(K13WriteGate.ConfirmationPhrase);
        try
        {
            dev.Apply(new[] { K13RememberMode.Plan(0x93) });
            var naive = dev.ReadBlocks(new[] { (0x08, 8) })[0];
            Assert.Equal("939B851007071093", Convert.ToHexString(naive));   // exactly the user's log
            Assert.Equal(0x80, fake.Memory[0x08]);                          // memory itself is fine
        }
        finally { K13WriteGate.Lock(); }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(3)] [InlineData(5)]
    public void RememberVerifyIgnoresEcho(int echoLoads)
    {
        var mem = Img();
        var fake = new FakeK13(mem) { Writable = true, EchoLoadsAfterCommit = echoLoads };
        var tmp = Path.Combine(Path.GetTempPath(), "k13t-" + Guid.NewGuid().ToString("N"));
        using var dev = new K13Device(fake) { DelayMs = 0, SettleAfterWriteMs = 0 };
        K13WriteGate.Unlock(K13WriteGate.ConfirmationPhrase);
        try
        {
            var r = K13RememberMode.Run(dev, c => K13RememberMode.Target(c, true), _ => { }, tmp);
            Assert.True(r.Ok, r.Message);
            Assert.Equal(0x93, r.After);
            Assert.Equal(0x93, mem[0x0F]); Assert.Equal(0x80, mem[0x08]);
            var off = K13RememberMode.Run(dev, c => K13RememberMode.Target(c, false), _ => { }, tmp);
            Assert.True(off.Ok, off.Message);
            Assert.Equal(0x91, mem[0x0F]);
        }
        finally { K13WriteGate.Lock(); try { Directory.Delete(tmp, true); } catch { } }
    }

    [Fact]
    public void RealNeighbourChangeIsReportedExactly()
    {
        var before = new byte[] { 0x80, 0x9B, 0x85, 0x10, 0x07, 0x07, 0x10, 0x91 };
        var after = new byte[] { 0x93, 0x9B, 0x85, 0x10, 0x07, 0x07, 0x10, 0x93 };
        var d = K13RememberMode.Differences(before, after, 0x93);
        Assert.Single(d);
        Assert.Contains("0x0008: было 80, прочитано 93", d[0]);
        var d2 = K13RememberMode.Differences(before, before, 0x93);
        Assert.Equal("0x000F: должно быть 93, прочитано 91", Assert.Single(d2));
        Assert.Empty(K13RememberMode.Differences(before, after.Select((b, i) => i == 0 ? (byte)0x80 : b).ToArray(), 0x93));
    }

    [Fact]
    public void ProfileWriteVerifyIgnoresEcho()
    {
        var mem = Img();
        var fake = new FakeK13(mem) { Writable = true, EchoLoadsAfterCommit = 3 };
        var dev = new K13Device(fake) { DelayMs = 0, SettleAfterWriteMs = 0 };
        var cfg = K13MemoryCodec.Decode(Img()); StickTilts.Fill(cfg);
        cfg.SetKey(3, 0x3F);
        var tmp = Path.Combine(Path.GetTempPath(), "k13t-" + Guid.NewGuid().ToString("N"));
        var lines = new List<string>();
        try
        {
            K13WriteGate.Unlock(K13WriteGate.ConfirmationPhrase);
            var r = K13Writer.WriteProfile(dev, cfg, lines.Add, null, false, tmp);
            Assert.True(r.Ok, r.Message + " | " + string.Join(" | ", lines));
            Assert.False(r.Restored);
            Assert.DoesNotContain(lines, l => l.Contains("Расхождения"));
            Assert.Equal(0x3F, mem[0x140]);
        }
        finally { K13WriteGate.Lock(); dev.Dispose(); try { Directory.Delete(tmp, true); } catch { } }
    }
}
