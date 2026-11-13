using Vorotex.K13;
using Xunit;
using Xunit.Abstractions;

namespace K13.Core.Tests;

public class CodecTests(ITestOutputHelper log)
{
    static string D(string f) => Path.Combine(AppContext.BaseDirectory, "data", f);
    static byte[] Img(string f, int len = 0x300) => File.ReadAllBytes(D(f)).Take(len).ToArray();

    public static readonly string[] Dumps =
    {
        "k13-backup-20261112-171754.bin", "exp07-joy-up-A.bin", "exp07b-full-0000-1000.bin", "exp08-joy-lrd.bin",
        "exp09-brightness.bin", "exp10-ledmode.bin", "exp11-brightness-min.bin", "exp12-color.bin", "exp13-wheel.bin",
        "exp13b-wheel.bin", "exp14-macro.bin"
    };
    public static IEnumerable<object[]> AllDumps => Dumps.Select(d => new object[] { d });

    [Theory, MemberData(nameof(AllDumps))]
    public void RoundTrip_IsByteExact_FullImage(string file)
    {
        var img = File.ReadAllBytes(D(file));
        var cfg = K13MemoryCodec.Decode(img);
        var enc = K13MemoryCodec.Encode(cfg, img);
        Assert.Equal(img.Length, enc.Length);
        var diffs = Enumerable.Range(0, img.Length).Where(i => img[i] != enc[i]).Select(i => $"0x{i:X4}").ToList();
        Assert.True(diffs.Count == 0, file + ": " + string.Join(",", diffs));
    }

    [Theory, MemberData(nameof(AllDumps))]
    public void RoundTrip_FromBlankImage_ReproducesAllOwnedBytes(string file)
    {
        // Encoding onto an all-FF image must reproduce every byte the profile owns.
        var img = Img(file);
        var cfg = K13MemoryCodec.Decode(img);
        var enc = K13MemoryCodec.Encode(cfg, Enumerable.Repeat((byte)0xFF, 0x300).ToArray(), out var plan);
        foreach (var blk in plan)
            for (int i = 0; i < blk.Data.Length; i++)
                Assert.True(img[blk.Address + i] == enc[blk.Address + i], $"{file} @0x{blk.Address + i:X4} ({blk.Purpose})");
        Assert.Equal(img[0xD0..0xD8], enc[0xD0..0xD8]);
    }

    // Consecutive experiments: encode(decode(next), previousImage) must equal the next dump, except the vendor's
    // stack-garbage zone 0x23D..0x247 (PROTOCOL.md §13.1) where our encoder intentionally writes nothing extra.
    public static IEnumerable<object[]> Steps()
    {
        string[] seq = { "k13-backup-20261112-171754.bin", "exp07-joy-up-A.bin", "exp07b-full-0000-1000.bin", "exp08-joy-lrd.bin",
            "exp09-brightness.bin", "exp10-ledmode.bin", "exp11-brightness-min.bin", "exp12-color.bin", "exp13b-wheel.bin", "exp14-macro.bin" };
        for (int i = 1; i < seq.Length; i++) yield return new object[] { seq[i - 1], seq[i] };
    }

    [Theory, MemberData(nameof(Steps))]
    public void Encode_ProfileOntoPreviousImage_EqualsNextDump(string prev, string next)
    {
        var a = Img(prev); var b = Img(next);
        var enc = K13MemoryCodec.Encode(K13MemoryCodec.Decode(b), a, out var plan);
        var written = new HashSet<int>(plan.SelectMany(p => Enumerable.Range(p.Address, p.Data.Length)));
        var diffs = new List<string>();
        for (int i = 0; i < 0x300; i++)
        {
            if (i is >= 0x23D and < 0x248 && !written.Contains(i)) continue;
            if (enc[i] != b[i]) diffs.Add($"0x{i:X4}:{enc[i]:X2}≠{b[i]:X2}");
        }
        log.WriteLine($"{prev} -> {next}: plan {plan.Count} blocks, changed blocks {K13Protocol.DiffPlan(plan, a).Count}");
        Assert.True(diffs.Count == 0, string.Join(", ", diffs));
    }

    [Fact]
    public void Decode_KnownExperimentFacts()
    {
        var b = K13MemoryCodec.Decode(Img("k13-backup-20261112-171754.bin"));
        Assert.Equal(0xE0, b.Base);
        Assert.Equal(0xCD, b.GetKey(0)); Assert.Equal(0xA6, b.GetKey(4)); Assert.Equal(0xA5, b.GetKey(11));
        Assert.Equal(0xF4, b.GetKey(16)); Assert.Equal(0xE9, b.GetKey(17));
        Assert.Equal(new[] { 0xE0, 0xE2, 0x11, 0xE0, 0x11, 0xE2 }, b.Macros[0].Events.Select(e => (int)e.Code));
        Assert.Equal(new[] { true, true, true, false, false, false }, b.Macros[0].Events.Select(e => e.Down));
        Assert.Equal(new[] { 16, 7, 6, 8, 2, 1 }, b.Macros[0].Events.Select(e => e.Delay));
        Assert.True(b.Macros[1].Mode1Bit);
        Assert.Equal((15, 0, 0), (b.Led.Colors[0].R, b.Led.Colors[0].G, b.Led.Colors[0].B)); // raw R0 G15 B15, inverted
        Assert.Equal(2, b.Led.Mode); Assert.Equal(1, b.Led.ColorIndex); Assert.Equal(3, b.Led.Speed); Assert.Equal(1, b.Led.Direction);

        var j = K13MemoryCodec.Decode(Img("exp08-joy-lrd.bin"));
        Assert.Equal(new byte[] { 0x04, 0x07, 0x16, 0x1A }, new[] { j.GetKey(12), j.GetKey(13), j.GetKey(14), j.GetKey(15) });

        Assert.Equal(6, K13MemoryCodec.Decode(Img("exp11-brightness-min.bin")).Led.Brightness);
        Assert.Equal(0, K13MemoryCodec.Decode(Img("exp11-brightness-min.bin")).Led.BrightnessLevel);
        var c = K13MemoryCodec.Decode(Img("exp12-color.bin")).Led.Colors[0];
        Assert.Equal((0, 15, 4), (c.R, c.G, c.B)); // raw R15 G0 B11, inverted
        var w = K13MemoryCodec.Decode(Img("exp13b-wheel.bin"));
        Assert.Equal((3, 2), (w.WheelFirst, w.WheelSecond));
        Assert.Equal(4, K13MemoryCodec.Decode(Img("exp10-ledmode.bin")).Led.Mode);
        var m = K13MemoryCodec.Decode(Img("exp14-macro.bin"));
        Assert.Equal(0xA5, m.GetKey(4)); Assert.True(m.Macros[0].IsUsed); Assert.False(m.Macros[1].IsUsed);
    }

    public static IEnumerable<object[]> IniPairs() => new[]
    {
        new object[] { "config-live.ini", "k13-backup-20261112-171754.bin" },
        new object[] { "config-exp07.ini", "exp07-joy-up-A.bin" },
        new object[] { "config-exp07b.ini", "exp07b-full-0000-1000.bin" },
        new object[] { "config-exp08.ini", "exp08-joy-lrd.bin" },
        new object[] { "config-exp09.ini", "exp09-brightness.bin" },
        new object[] { "config-exp10.ini", "exp10-ledmode.bin" },
        new object[] { "config-exp11.ini", "exp11-brightness-min.bin" },
        new object[] { "config-exp12.ini", "exp12-color.bin" },
        new object[] { "config-exp13.ini", "exp13-wheel.bin" },
        new object[] { "config-exp13b.ini", "exp13b-wheel.bin" },
        new object[] { "config-exp14.ini", "exp14-macro.bin" },
    };

    [Theory, MemberData(nameof(IniPairs))]
    public void VendorIni_CurrentProfile_MatchesDevice(string ini, string dump)
    {
        var dev = K13MemoryCodec.Decode(Img(dump));
        // template deliberately different so that every imported field is really checked
        var template = dev.Clone();
        for (int col = 0; col < 16; col++) template.SetKey(col, 0x00);
        template.WheelFirst = template.WheelSecond = 9;
        foreach (var c in template.Led.Colors) { c.R = c.G = c.B = 7; }
        template.Led.Brightness = 5; template.Led.Speed = 5; template.Led.Mode = 9; template.Led.ColorIndex = 7; template.Led.Direction = 3;
        template.Led.CycleMask = 0; template.Led.ModeEnableMask = 0;

        var profiles = VendorIniImporter.Import(D(ini), template);
        var cur = profiles.Single(p => p.IsCurrent);
        Assert.Equal("Normal", cur.Name);
        var cfg = cur.Config;
        for (int col = 0; col < 16; col++) Assert.True(dev.GetKey(col) == cfg.GetKey(col), $"col {col}: dev {dev.GetKey(col):X2} ini {cfg.GetKey(col):X2}");
        Assert.Equal(dev.Fn, cfg.Fn);
        Assert.Equal((dev.WheelFirst, dev.WheelSecond), (cfg.WheelFirst, cfg.WheelSecond));
        for (int i = 0; i < 7; i++) Assert.Equal(dev.Led.Colors[i].ToString(), cfg.Led.Colors[i].ToString());
        Assert.Equal((dev.Led.Direction, dev.Led.Brightness, dev.Led.Speed, dev.Led.Mode, dev.Led.ColorIndex, dev.Led.Symmetry),
                     (cfg.Led.Direction, cfg.Led.Brightness, cfg.Led.Speed, cfg.Led.Mode, cfg.Led.ColorIndex, cfg.Led.Symmetry));
        Assert.Equal(dev.Led.CycleMask, cfg.Led.CycleMask);
        Assert.Equal(dev.Led.ModeEnableMask, cfg.Led.ModeEnableMask);
        // full encode of the imported profile onto the dump must give the dump back
        var enc = K13MemoryCodec.Encode(cfg, Img(dump));
        Assert.Equal(Img(dump), enc.Take(0x300).ToArray());
    }

    [Fact]
    public void VendorIni_ListsAllProfiles()
    {
        var p = VendorIniImporter.Import(D("config-exp14.ini"), K13MemoryCodec.Decode(Img("exp14-macro.bin")));
        log.WriteLine(string.Join(" | ", p.Select(x => $"{x.Number}:{x.Name}{(x.IsCurrent ? "*" : "")}")));
        Assert.True(p.Count >= 2);
        Assert.Contains(p, x => x.Name == "GAME");
    }

    [Fact]
    public void Colors_AreInverted_BlueIsRawRG()
    {
        var map = new LedColorMap();
        Assert.Equal((15, 15, 0), map.ToRaw(0, 0, 15));     // logical blue → raw R+G (raw blue lit yellow on the device)
        Assert.Equal((0, 0, 15), map.ToLogical(15, 15, 0));
        var perm = new LedColorMap { Invert = false, Perm = new[] { 2, 0, 1 } };
        var raw = perm.ToRaw(1, 2, 3);
        Assert.Equal((1, 2, 3), perm.ToLogical(raw.R, raw.G, raw.B));
        var img = Img("exp14-macro.bin");
        var cfg = K13MemoryCodec.Decode(img);
        cfg.Led.Colors[0].R = 0; cfg.Led.Colors[0].G = 0; cfg.Led.Colors[0].B = 15;
        var o = K13MemoryCodec.Encode(cfg, img);
        Assert.Equal(0xF0, o[0xE1]);                      // rawR 15, rawB 0
        Assert.Equal(0x0F, o[0xE2] & 0x0F);               // rawG 15
    }

    [Fact]
    public void ApplyFrames_HaveVendorShape()
    {
        var img = Img("exp14-macro.bin");
        K13MemoryCodec.Encode(K13MemoryCodec.Decode(img), img, out var plan);
        var frames = K13Protocol.BuildApplyFrames(plan, programmingMode: true);
        Assert.Equal(new byte[] { 7, 9, 0x41, 0, 0, 0, 0, 0 }, frames[0].Data);
        Assert.Equal(0x03, K13Protocol.BuildApplyFrames(plan)[0].Data[2]); // default: proven exp15 sequence, no 07 09 41
        Assert.Equal(new byte[] { 7, 0x18, 0x11, 0, 0, 0, 0, 0 }, frames[^2].Data);
        Assert.All(plan, b => Assert.InRange(b.Data.Length, 1, 8));
        Assert.DoesNotContain(plan, b => b.Address >= 0x101 && b.Address < 0x128); // BASE+0x21..0x47 never written
        Assert.DoesNotContain(plan, b => b.Address <= 0x23C && b.Address + b.Data.Length > 0x23C); // trailer untouched
        log.WriteLine($"plan: {plan.Count} blocks, {frames.Count} frames");
    }
}

/// <summary>Fake device implementing the read path semantics observed on the real K13.</summary>
sealed class FakeK13(byte[] mem) : IHidFeatureTransport
{
    readonly byte[] _buf = new byte[8];
    readonly byte[] _stage = new byte[8];
    byte _resp;
    public List<byte[]> Frames { get; } = new();
    public bool Writable { get; set; }
    public bool CorruptNextCommit { get; set; }
    public byte[] Memory => mem;

    public bool SetFeature(byte[] r)
    {
        Frames.Add((byte[])r.Clone());
        if (r[1] == 0x18 && r[2] == 0x00) { int a = r[4] | r[5] << 8; for (int i = 0; i < 8; i++) _buf[i] = a + i < mem.Length ? mem[a + i] : (byte)0xFF; }
        else if (r[1] == 0x18 && r[2] == 0x05) _resp = _buf[r[3] & 7];
        else if (Writable && r[1] == 0x18 && r[2] == 0x03) _stage[r[3] & 7] = r[6];
        else if (Writable && r[1] == 0x18 && r[2] == 0x09)
        {
            int a = r[4] | r[5] << 8, n = r[7] + 1;
            for (int i = 0; i < n; i++) mem[a + i] = _stage[i];
            if (CorruptNextCommit) { mem[a] ^= 0x5A; CorruptNextCommit = false; }
        }
        return true;
    }

    public bool GetFeature(byte[] b) { b[1] = _resp; for (int i = 2; i < b.Length; i++) b[i] = 0; if (b.Length > 15) { b[14] = 0xE0; b[15] = 0; } return true; }
    public void Dispose() { }
}

public class SafetyTests
{
    static byte[] Img(string f) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", f));

    [Fact]
    public void Read_UsesOnlyApprovedCommands_AndReturnsImage()
    {
        var mem = Img("exp14-macro.bin");
        var fake = new FakeK13(mem);
        using var dev = new K13Device(fake) { DelayMs = 0 };
        var img = dev.ReadImageVerified();
        Assert.Equal(mem.Take(img.Length), img);
        Assert.All(fake.Frames, f => Assert.True(K13Protocol.IsReadOnlyFrame(f), BitConverter.ToString(f)));
        Assert.All(fake.Frames, f => Assert.Contains(f[2], new byte[] { 0x00, 0x05, 0x01 }));
        Assert.Equal(0xE0, dev.GetStatus().Base);
    }

    [Fact]
    public void Session_EndsWithVendorExitSequence_AfterRead_NoMemoryWrite()
    {
        var mem = Img("exp14-macro.bin"); var copy = (byte[])mem.Clone();
        var fake = new FakeK13(mem) { Writable = true };
        var dev = new K13Device(fake) { DelayMs = 0 };
        dev.ReadBlocks(new List<(int, int)> { (0xD0, 8) });
        dev.Dispose();
        var tail = fake.Frames.TakeLast(3).Select(f => BitConverter.ToString(f)).ToList();
        Assert.Equal(new[] { "07-18-11-00-00-00-00-00", "07-18-00-00-00-00-00-00", "07-20-00-00-00-00-00-00" }, tail);
        Assert.DoesNotContain(fake.Frames, f => f[1] == 0x18 && (f[2] == 0x03 || f[2] == 0x09));
        Assert.Equal(copy, mem);
        Assert.False(K13WriteGate.IsOpen);
    }

    [Fact]
    public void Session_StatusOnly_SendsNothing()
    {
        var fake = new FakeK13(new byte[0x300]);
        var dev = new K13Device(fake);
        dev.GetStatus();
        dev.Dispose();
        Assert.Empty(fake.Frames);
    }

    [Fact]
    public void Write_IsBlocked_UntilUnlocked_AndSendsNothing()
    {
        var mem = Img("exp14-macro.bin");
        var fake = new FakeK13(mem);
        using var dev = new K13Device(fake);
        K13MemoryCodec.Encode(K13MemoryCodec.Decode(mem), mem, out var plan);
        K13WriteGate.Lock();
        Assert.False(K13WriteGate.IsOpen);
        Assert.Throws<WriteBlockedException>(() => dev.Apply(plan));
        Assert.Throws<WriteBlockedException>(() => K13WriteGate.Unlock("неверно"));
        Assert.Empty(fake.Frames);
    }

    [Fact]
    public void Writer_WritesOnlyChangedBlocks_AndVerifies()
    {
        var mem = Img("exp15-postwrite-20261113-133715.bin").Take(0x1000).ToArray();
        var fake = new FakeK13(mem) { Writable = true };
        using var dev = new K13Device(fake) { DelayMs = 0 };
        var cfg = K13MemoryCodec.Decode(mem);
        cfg.Led.Brightness = cfg.Led.Brightness == 0 ? 3 : 0;
        var tmp = Path.Combine(Path.GetTempPath(), "k13writer-" + Guid.NewGuid());
        try
        {
            K13WriteGate.Unlock(K13WriteGate.ConfirmationPhrase);
            var r = K13Writer.WriteProfile(dev, cfg, _ => { }, null, false, tmp);
            Assert.True(r.Ok, r.Message);
            Assert.False(r.KeysChanged);
            Assert.True(r.LightingChanged);
            Assert.Equal(1, r.Blocks);
            Assert.DoesNotContain(fake.Frames, f => f[1] == 0x09); // no 07 09 41
            Assert.Equal(cfg.Led.Brightness, K13MemoryCodec.Decode(fake.Memory).Led.Brightness);
            // nothing else changed
            var orig = Img("exp15-postwrite-20261113-133715.bin");
            Assert.True(Enumerable.Range(0, 0x400).Count(i => fake.Memory[i] != orig[i]) <= 1);
            var again = K13Writer.WriteProfile(dev, cfg, _ => { }, null, false, tmp);
            Assert.True(again.NothingToDo);
        }
        finally { K13WriteGate.Lock(); try { Directory.Delete(tmp, true); } catch { } }
    }

    [Fact]
    public void Writer_FastPath_UsesCachedImage_ReadsOnlyTouchedBlocks()
    {
        var mem = Img("exp15-postwrite-20261113-133715.bin").Take(0x1000).ToArray();
        var cache = mem.Take(0x300).ToArray();
        var fake = new FakeK13(mem) { Writable = true };
        using var dev = new K13Device(fake) { DelayMs = 0, LoadDelayMs = 0 };
        var cfg = K13MemoryCodec.Decode(cache);
        cfg.Led.Brightness = cfg.Led.Brightness == 0 ? 3 : 0;
        var tmp = Path.Combine(Path.GetTempPath(), "k13writer-" + Guid.NewGuid());
        try
        {
            K13WriteGate.Unlock(K13WriteGate.ConfirmationPhrase);
            var r = K13Writer.WriteProfile(dev, cfg, _ => { }, null, false, tmp, cache);
            Assert.True(r.Ok, r.Message);
            int loads = fake.Frames.Count(f => f[1] == 0x18 && f[2] == 0x00 && f[7] == 0);
            Assert.InRange(loads, 3, 6);   // check 0xD0 + 0xF8, readback 0xF8, finish frame
            Assert.Equal(cfg.Led.Brightness, K13MemoryCodec.Decode(fake.Memory).Led.Brightness);
            // device changed behind our back → fast check fails → full read
            fake.Memory[0x00F8] ^= 0x01;
            cfg.Led.Speed = (cfg.Led.Speed + 1) % 6;
            var r2 = K13Writer.WriteProfile(dev, cfg, _ => { }, null, false, tmp, r.After);
            Assert.True(r2.Ok, r2.Message);
            Assert.Contains("полное чтение", r2.Timing);
        }
        finally { K13WriteGate.Lock(); try { Directory.Delete(tmp, true); } catch { } }
    }

    [Fact]
    public void Writer_RestoresOnMismatch()
    {
        var mem = Img("exp15-postwrite-20261113-133715.bin").Take(0x1000).ToArray();
        var orig = (byte[])mem.Clone();
        var fake = new FakeK13(mem) { Writable = true, CorruptNextCommit = true };
        using var dev = new K13Device(fake) { DelayMs = 0 };
        var cfg = K13MemoryCodec.Decode(mem);
        cfg.SetKey(3, 0x04);
        var tmp = Path.Combine(Path.GetTempPath(), "k13writer-" + Guid.NewGuid());
        try
        {
            K13WriteGate.Unlock(K13WriteGate.ConfirmationPhrase);
            var r = K13Writer.WriteProfile(dev, cfg, _ => { }, null, false, tmp);
            Assert.False(r.Ok);
            Assert.True(r.Restored);
            Assert.Equal(orig.Take(0x400), fake.Memory.Take(0x400));
        }
        finally { K13WriteGate.Lock(); try { Directory.Delete(tmp, true); } catch { } }
    }

    [Theory]
    [InlineData(new byte[] { 7, 0x18, 0x03, 0, 0xE0, 0, 0x11, 7 })]
    [InlineData(new byte[] { 7, 0x18, 0x09, 0, 0xE0, 0, 0, 7 })]
    [InlineData(new byte[] { 7, 0x18, 0x11, 0, 0, 0, 0, 0 })]
    [InlineData(new byte[] { 7, 0x18, 0x81, 0, 0, 0, 0, 0x60 })]
    [InlineData(new byte[] { 7, 0x09, 0x41, 0, 0, 0, 0, 0 })]
    [InlineData(new byte[] { 7, 0x20, 0, 0, 0, 0, 0, 0 })]
    public void WriteFrames_AreNotReadOnly(byte[] f) => Assert.False(K13Protocol.IsReadOnlyFrame(f));

    [Fact]
    public void Decode_VendorLmbMacro_IsLeftMouseButton()
    {
        // Slot 1 was recorded in the vendor app as «ЛКМ» (VK 240) → device code 0xF1.
        var cfg = K13MemoryCodec.Decode(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", "k13-backup-20261112-171754.bin")));
        var m = cfg.Macros[1];
        Assert.Equal(2, m.Events.Count);
        Assert.True(m.Events[0].Down && m.Events[0].IsMouseButton && m.Events[0].Code == MouseCodes.Left);
        Assert.True(!m.Events[1].Down && m.Events[1].Code == MouseCodes.Left);
        Assert.True(MouseCodes.IsConfirmed(m.Events[0].Code));
        Assert.Equal(MacroRepeatMode.WhileHeld, m.RepeatMode);
        Assert.False(m.Unsupported);
    }

    [Fact]
    public void MouseMacro_WithWheel_RoundTrips()
    {
        var img = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", "k13-backup-20261112-171754.bin"));
        var cfg = K13MemoryCodec.Decode(img);
        cfg.Macros[2] = new MacroSlot
        {
            Repeat = 1,
            Events =
            {
                new MacroEvent { Down = true, Code = MouseCodes.Right },
                new MacroEvent { Down = false, Delay = 3, Code = MouseCodes.Right },
                MacroEvent.Wheel(+1, 2),
                MacroEvent.Wheel(-3),
                new MacroEvent { Down = true, Code = MouseCodes.Middle },
                new MacroEvent { Down = false, Delay = 1, Code = MouseCodes.Middle },
            }
        };
        var enc = K13MemoryCodec.Encode(cfg, img);
        var back = K13MemoryCodec.Decode(enc);
        var m = back.Macros[2];
        Assert.Equal(6, m.Events.Count);
        Assert.Equal(1, m.Events[2].WheelAmount);
        Assert.Equal(2, m.Events[2].Delay);
        Assert.Equal(-3, m.Events[3].WheelAmount);
        Assert.Equal(MouseCodes.Middle, m.Events[4].Code);
        Assert.Equal(2 + 2 * 6 + 2 * 2, m.BodyLength);
        // Body bytes: header + [83 F3][03 F3][02 F7 01 00][00 F7 FD 00][80 F2][01 F2]
        int e = cfg.Base + K13Layout.OffMacroTable + 2 * 5;
        int addr = enc[e + 2] | (enc[e + 3] & 0x3F) << 8;
        Assert.Equal(6, enc[e + 1] & 0x7F); // table length counts events, not bytes
        Assert.Equal(Convert.FromHexString("010080F303F302F7010000F7FD0080F201F2"), enc.AsSpan(addr, 18).ToArray());
        // Encoding the decoded image again is byte-exact.
        Assert.Equal(enc, K13MemoryCodec.Encode(back, enc));
        Assert.Equal(addr + 18, K13MemoryCodec.UsedEnd(enc));
    }

    [Fact]
    public void WheelEvent_WithoutPayload_FailsValidation()
    {
        var cfg = K13MemoryCodec.Decode(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", "k13-backup-20261112-171754.bin")));
        cfg.Macros[3] = new MacroSlot { Events = { new MacroEvent { Code = MouseCodes.Wheel } } };
        Assert.Throws<K13FormatException>(() => K13MemoryCodec.Validate(cfg));
    }
}

public class TriggerPolicyTests
{
    [Theory]
    [InlineData(0xA0)] [InlineData(0xA1)] [InlineData(0xA2)] [InlineData(0xA3)] [InlineData(0xA4)] [InlineData(0xA5)]
    [InlineData(0x10)] [InlineData(0x11)] [InlineData(0x12)] [InlineData(0x5B)] [InlineData(0x5C)]
    [InlineData(0x41)] [InlineData(0x5A)] [InlineData(0x30)] [InlineData(0x39)]
    [InlineData(0x0D)] [InlineData(0x1B)] [InlineData(0x20)] [InlineData(0x09)]
    [InlineData(0x25)] [InlineData(0x26)] [InlineData(0x27)] [InlineData(0x28)]
    public void OrdinaryKeys_AreForbidden(int vk) => Assert.Equal(TriggerSafety.Forbidden, TriggerKeyPolicy.Classify((ushort)vk));

    [Fact]
    public void ServiceKeys_AreSafe()
    {
        for (int vk = 0x7C; vk <= 0x87; vk++) Assert.Equal(TriggerSafety.Safe, TriggerKeyPolicy.Classify((ushort)vk));
        Assert.Equal(TriggerSafety.NeedsConfirmation, TriggerKeyPolicy.Classify(0x61)); // numpad 1
        Assert.Equal(TriggerSafety.NeedsConfirmation, TriggerKeyPolicy.Classify(0x70)); // F1
    }

    [Fact]
    public void DropUnsafe_RemovesShiftRing_KeepsF13()
    {
        var p = new AppProfile { Name = "Основной" };
        p.Host.Keys.Add(new HostKeyBinding { TriggerVk = 0x7C, Tap = new HostAction { Kind = HostActionKind.Ring }, Suppress = true });
        p.Host.Keys.Add(new HostKeyBinding { TriggerVk = 160, Tap = new HostAction { Kind = HostActionKind.Ring }, Suppress = true });
        var data = new ProfileStoreData(); data.Profiles.Add(p);
        var notes = TriggerKeyPolicy.DropUnsafe(data, v => $"VK{v}");
        Assert.Single(notes);
        Assert.Equal(new ushort[] { 0x7C }, p.Host.Keys.Select(k => k.TriggerVk).ToArray());
        Assert.Empty(TriggerKeyPolicy.DropUnsafe(data, v => ""));
    }

    [Fact]
    public void Macro_ToggleMode_RoundTrip_UsesTableBit7AndHeader01()
    {
        var img = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", "k13-backup-20261112-171754.bin")).Take(0x300).ToArray();
        var cfg = K13MemoryCodec.Decode(img);
        cfg.Macros[0].RepeatMode = MacroRepeatMode.Toggle;
        cfg.Macros[0].Repeat = 7; // ignored for toggle
        var enc = K13MemoryCodec.Encode(cfg, img);
        int e = cfg.Base + K13Layout.OffMacroTable;
        Assert.True((enc[e + 1] & 0x80) != 0);
        Assert.True((enc[e + 3] & 0x80) == 0);
        int addr = enc[e + 2] | (enc[e + 3] & 0x3F) << 8;
        Assert.Equal(0x01, enc[addr]); Assert.Equal(0x00, enc[addr + 1]);
        var back = K13MemoryCodec.Decode(enc);
        Assert.Equal(MacroRepeatMode.Toggle, back.Macros[0].RepeatMode);
        Assert.Equal(cfg.Macros[0].Events.Count, back.Macros[0].Events.Count);
    }
}
