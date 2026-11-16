using System.Text.Json;
using Vorotex.K13;
using Xunit;

namespace K13.Core.Tests;

/// <summary>v5.7.4: activation exchange (the makers' K13 applies its saved layout only after a host exchange).</summary>
public class V574Tests
{
    static byte[] Img() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", "exp15-prewrite-20261113-133452.bin"));
    static bool Safe(byte[] f) => K13Protocol.IsReadOnlyFrame(f) || K13Protocol.IsSessionEndFrame(f);

    [Fact]
    public void ActivationSendsOnlyReadAndSessionEnd_NoMemoryChange()
    {
        var mem = Img(); var copy = (byte[])mem.Clone();
        var fake = new FakeK13(mem) { Writable = true };
        using (var dev = new K13Device(fake) { DelayMs = 0 })
        {
            var head = K13Activation.Activate(dev);
            Assert.Equal(new byte[] { 0xE1, 0x1E, 0xE0, 0x00, 0xF0, 0x0F, 0x00, 0x00 }, head);
        }
        Assert.All(fake.Frames, f => Assert.True(Safe(f), BitConverter.ToString(f)));
        Assert.Equal(new[] { "07-18-11-00-00-00-00-00", "07-18-00-00-00-00-00-00", "07-20-00-00-00-00-00-00" },
            fake.Frames.TakeLast(3).Select(f => BitConverter.ToString(f)));
        Assert.Equal(3, fake.Frames.Count(f => f[1] == 0x20 || f[2] == 0x11 || (f[2] == 0 && f[4] == 0 && f[5] == 0)) );
        Assert.Equal(copy, mem);
        Assert.True(fake.Frames.Count < 20);   // short: ≈ 0.1 s
    }

    [Fact]
    public void BisectStepsAreSingleKnownFrames()
    {
        var mem = Img(); var copy = (byte[])mem.Clone();
        var expected = new[]
        {
            Array.Empty<string>(),
            null, // load + 8 reads
            new[] { "07-18-01-00-00-00-00-00" },
            new[] { "07-18-11-00-00-00-00-00" },
            new[] { "07-18-00-00-00-00-00-00" },
            new[] { "07-20-00-00-00-00-00-00" },
        };
        Assert.Equal(expected.Length, K13Activation.BisectSteps.Count);
        for (int n = 0; n < expected.Length; n++)
        {
            var fake = new FakeK13(mem) { Writable = true };
            using (var dev = new K13Device(fake) { DelayMs = 0, EndSessionOnDispose = false }) K13Activation.BisectSteps[n].Run(dev);
            var got = fake.Frames.Select(f => BitConverter.ToString(f)).ToArray();
            if (expected[n] != null) Assert.Equal(expected[n], got);
            else
            {
                Assert.Equal("07-18-00-00-D0-00-00-00", got[0]);
                Assert.Equal(9, got.Length);
                Assert.All(got.Skip(1), g => Assert.StartsWith("07-18-05-0", g));
            }
            Assert.All(fake.Frames, f => Assert.True(Safe(f)));
        }
        Assert.Equal(copy, mem);
    }

    [Fact]
    public void SendSafeRefusesWritesEvenWithGateOpen()
    {
        var fake = new FakeK13(Img()) { Writable = true };
        using var dev = new K13Device(fake) { DelayMs = 0, EndSessionOnDispose = false };
        K13WriteGate.Unlock(K13WriteGate.ConfirmationPhrase);
        try
        {
            Assert.Throws<WriteBlockedException>(() => dev.SendSafe(new byte[] { 7, 0x18, 0x09, 0, 0x40, 1, 0, 7 }));
            Assert.Throws<WriteBlockedException>(() => dev.SendSafe(new byte[] { 7, 0x18, 0x03, 0, 0x40, 1, 0x3E, 7 }));
            Assert.Throws<WriteBlockedException>(() => dev.SendSafe(new byte[] { 7, 0x09, 0x41, 0, 0, 0, 0, 0 }));
        }
        finally { K13WriteGate.Lock(); }
        Assert.Empty(fake.Frames);
    }

    [Fact]
    public void ClassifyKeys()
    {
        var mem = K13MemoryCodec.Decode(Img().Take(0x300).ToArray());
        for (int i = 0; i < mem.Keymap.Length; i++) mem.Keymap[i] = KeyCatalog.Disabled;
        foreach (var m in mem.Macros) m.Events.Clear();
        mem.SetKey(3, 0x3E); // key 4 = F5
        Assert.Equal(K13Activation.Seen.Saved, K13Activation.Classify(mem, new ushort[] { 0x74, 0x74 }));
        Assert.Equal(K13Activation.Seen.Factory, K13Activation.Classify(mem, new ushort[] { 0x64 }));
        Assert.Equal(K13Activation.Seen.Mixed, K13Activation.Classify(mem, new ushort[] { 0x64, 0x74 }));
        Assert.Equal(K13Activation.Seen.Nothing, K13Activation.Classify(mem, new ushort[] { 0xA0 }));
        Assert.Equal(K13Activation.Seen.Nothing, K13Activation.Classify(mem, Array.Empty<ushort>()));
    }

    [Fact]
    public void ActivateOnConnectDefaultsOnForOldSettings()
    {
        var d = JsonSerializer.Deserialize<ProfileStoreData>("{\"ActiveProfile\":\"x\",\"StartWithWindows\":false}")!;
        Assert.True(d.ActivateOnConnect);
        Assert.False(d.ActivateAutostartHintSeen);
        var back = JsonSerializer.Deserialize<ProfileStoreData>(JsonSerializer.Serialize(new ProfileStoreData { ActivateOnConnect = false }))!;
        Assert.False(back.ActivateOnConnect);
    }
}
