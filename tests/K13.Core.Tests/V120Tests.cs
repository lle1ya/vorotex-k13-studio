using Vorotex.K13;
using Xunit;

namespace K13.Core.Tests;

/// <summary>1.2.0: makers' feedback after 1.1.1 — factory assignments, key tester, «удерживать до отпускания», K12 (same VID/PID).</summary>
public class V120Tests
{
    static byte[] Img(string f) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", f));
    static byte[] K13() => Img("exp15-postwrite-20261113-133715.bin").Take(0x1000).ToArray();
    static byte[] K12() => Img("k12-prewrite-20261117-113402.bin");
    static string Tmp() => Path.Combine(Path.GetTempPath(), "k13-v120-" + Guid.NewGuid().ToString("N"));
    static bool IsWrite(byte[] f) => f[1] == 0x18 && f[2] is 0x03 or 0x09;

    // ───────── K12 / model
    [Fact]
    public void Model_K13_dumps_are_K13()
    {
        foreach (var f in new[] { "exp15-postwrite-20261113-133715.bin", "k13-backup-20261112-171754.bin", "k13-postwrite-20261117-142145.bin", "exp07b-full-0000-1000.bin" })
            Assert.Equal(KeypadModel.K13, K13Model.Detect(Img(f)));
        Assert.True(K13Model.WriteAllowed(KeypadModel.K13));
    }

    [Fact]
    public void Model_makers_second_unit_17_11_is_not_K13()
    {
        Assert.Equal(KeypadModel.K12Likely, K13Model.Detect(K12()));
        Assert.Equal(KeypadModel.K12Likely, K13Model.Detect(Img("k12-postwrite-20261117-113428.bin")));   // our write did not touch 0x0020
        Assert.False(K13Model.WriteAllowed(KeypadModel.K12Likely));
        // same EEPROM ids and header as the K13 — the PID cannot tell them apart
        Assert.Equal(K13()[..8], K12()[..8]);
        Assert.Equal(K13().AsSpan(0xD0, 8).ToArray(), K12().AsSpan(0xD0, 8).ToArray());
    }

    [Fact]
    public void Model_unknown_and_not_read()
    {
        Assert.Equal(KeypadModel.NotRead, K13Model.Detect(null));
        Assert.Equal(KeypadModel.NotRead, K13Model.Detect(new byte[0x40]));
        var odd = K13(); odd[0x20] = 0x04;
        Assert.Equal(KeypadModel.Unknown, K13Model.Detect(odd));
        Assert.False(K13Model.WriteAllowed(KeypadModel.Unknown));
        Assert.False(K13Model.WriteAllowed(KeypadModel.NotRead));
        Assert.Contains("заблокирована", K13Model.Refusal(KeypadModel.K12Likely));
        Assert.Contains("K12", K13Model.Banner(KeypadModel.K12Likely));
    }

    [Fact]
    public void Writer_refuses_K12_and_sends_no_write_frame()
    {
        var mem = K12().Concat(Enumerable.Repeat((byte)0xFF, 0x1000 - 0x300)).ToArray();
        var before = (byte[])mem.Clone();
        var fake = new FakeK13(mem) { Writable = true };
        var tmp = Tmp();
        using var dev = new K13Device(fake) { DelayMs = 0, LoadDelayMs = 0 };
        var cfg = K13MemoryCodec.Decode(K13());
        K13WriteGate.Unlock(K13WriteGate.ConfirmationPhrase);
        try
        {
            var r = K13Writer.WriteProfile(dev, cfg, _ => { }, null, false, tmp);
            Assert.False(r.Ok);
            Assert.Equal(KeypadModel.K12Likely, r.Model);
            Assert.NotNull(r.BackupBefore);                       // the K12 dump is kept for the makers
            Assert.True(File.Exists(r.BackupBefore));
            Assert.NotNull(r.After);
            Assert.DoesNotContain(fake.Frames, IsWrite);
            Assert.Equal(before, mem);
        }
        finally { K13WriteGate.Lock(); try { Directory.Delete(tmp, true); } catch { } }
    }

    [Fact]
    public void Writer_quick_path_with_a_K13_image_still_catches_a_K12()
    {
        var mem = K12().Concat(Enumerable.Repeat((byte)0xFF, 0x1000 - 0x300)).ToArray();
        var before = (byte[])mem.Clone();
        var fake = new FakeK13(mem) { Writable = true };
        var tmp = Tmp();
        using var dev = new K13Device(fake) { DelayMs = 0, LoadDelayMs = 0 };
        var cache = K13().Take(0x300).ToArray();               // last known image = the K13 (17.11: 10:13 backup, then the K12 at 11:32)
        var cfg = K13MemoryCodec.Decode(cache);
        cfg.Led.Brightness = cfg.Led.Brightness == 0 ? 3 : 0;  // a live lighting change
        K13WriteGate.Unlock(K13WriteGate.ConfirmationPhrase);
        try
        {
            var r = K13Writer.WriteProfile(dev, cfg, _ => { }, null, false, tmp, cache);
            Assert.False(r.Ok);
            Assert.Equal(KeypadModel.K12Likely, r.Model);
            Assert.DoesNotContain(fake.Frames, IsWrite);
            Assert.Equal(before, mem);
        }
        finally { K13WriteGate.Lock(); try { Directory.Delete(tmp, true); } catch { } }
    }

    [Fact]
    public void Writer_quick_path_reads_the_3_signature_blocks()
    {
        Assert.Equal(new[] { 0x48, 0x80, 0xA0 }, K13Model.SignatureBlocks.Select(b => b.Addr));
        var k13 = K13(); var k12 = K12();
        foreach (var (a, n) in K13Model.SignatureBlocks)
            Assert.NotEqual(k13.AsSpan(a, n).ToArray(), k12.AsSpan(a, n).ToArray());
    }

    [Fact]
    public void Remember_mode_refuses_K12()
    {
        var mem = K12().Concat(Enumerable.Repeat((byte)0xFF, 0x1000 - 0x300)).ToArray();
        mem[0x0F] = 0x91;   // even with a «known» byte the model check refuses
        var fake = new FakeK13(mem) { Writable = true };
        var tmp = Tmp();
        using var dev = new K13Device(fake) { DelayMs = 0, SettleAfterWriteMs = 0 };
        K13WriteGate.Unlock(K13WriteGate.ConfirmationPhrase);
        try
        {
            var r = K13RememberMode.Run(dev, c => K13RememberMode.Target(c, true), _ => { }, tmp);
            Assert.False(r.Ok); Assert.False(r.Written);
            Assert.Equal(0x91, mem[0x0F]);
            Assert.DoesNotContain(fake.Frames, IsWrite);
        }
        finally { K13WriteGate.Lock(); try { Directory.Delete(tmp, true); } catch { } }
    }

    // ───────── factory assignments
    static K13DeviceConfig Customised()
    {
        var c = K13MemoryCodec.Decode(K13());
        c.SetKey(0, 0x04); c.SetKey(9, 0xA5); c.SetKey(12, 0xFF); c.SetKey(16, 0x68);
        c.Macros[0] = new MacroSlot { Events = { new() { Down = true, Delay = 1, Code = 0xE0 }, new() { Down = true, Delay = 1, Code = 0x06 }, new() { Down = false, Delay = 1, Code = 0x06 }, new() { Down = false, Delay = 1, Code = 0xE0 } } };
        c.WheelFirst = 2; c.WheelSecond = 3;
        c.Fn[0] = 0x3A;
        return c;
    }

    [Fact]
    public void Factory_plan_resets_keys_wheel_fn_macros_and_keeps_lighting()
    {
        var cur = Customised();
        var p = K13FactoryAssignments.Build(cur, K13());
        Assert.True(p.KeysFromKeypad);
        for (int col = 0; col < 18; col++) Assert.Equal(K13Layout.FactoryColumnCodes[col], p.Target.GetKey(col));
        Assert.Equal((0, 1), (p.Target.WheelFirst, p.Target.WheelSecond));
        Assert.All(p.Target.Fn, b => Assert.Equal(0xFF, b));
        Assert.All(p.Target.Macros, m => Assert.False(m.IsUsed));
        Assert.Contains(0, p.ChangedColumns); Assert.Contains(9, p.ChangedColumns); Assert.Contains(12, p.ChangedColumns); Assert.Contains(16, p.ChangedColumns);
        Assert.True(K13FactoryAssignments.OnlyKnownParts(cur, p.Target));
        Assert.Contains(p.Keeps, k => k.Contains("Подсветка"));
        Assert.Contains(p.Resets, k => k.Contains("Колесо"));
        K13MemoryCodec.Validate(p.Target);
        Assert.NotSame(cur, p.Target); Assert.Equal(0x04, cur.GetKey(0));   // the source is not changed
    }

    [Fact]
    public void Factory_write_touches_only_assignment_blocks_never_lighting_or_service_area()
    {
        var img = K13MemoryCodec.Encode(K13Mode.PrepareTarget(Customised(), K13()), K13());
        var p = K13FactoryAssignments.Build(K13MemoryCodec.Decode(img), img);
        var target = K13MemoryCodec.Encode(K13Mode.PrepareTarget(p.Target, img), img, out var plan);
        var diff = K13Protocol.DiffPlan(plan, img);
        Assert.NotEmpty(diff);
        int bs = K13MemoryCodec.ReadBase(img);
        Assert.All(diff, b => Assert.True(b.Address >= bs + K13Layout.OffWheel, $"0x{b.Address:X4} {b.Purpose}"));
        for (int a = 0; a < bs + K13Layout.OffWheel; a++) Assert.Equal(img[a], target[a]);   // 0x0000–BASE+0x1F (service area + lighting) identical
        for (int a = bs + 0x21; a < bs + K13Layout.OffKeymap; a++) Assert.Equal(img[a], target[a]);   // unknown BASE+0x21..0x47 untouched
        Assert.Equal(img[bs], target[bs]);   // BASE+0 flags (KEYSMODE bit) as they were
    }

    [Fact]
    public void Factory_write_end_to_end_on_a_fake_K13_verifies()
    {
        var img = K13MemoryCodec.Encode(K13Mode.PrepareTarget(Customised(), K13()), K13());
        var mem = img.Concat(Enumerable.Repeat((byte)0xFF, Math.Max(0, 0x1000 - img.Length))).ToArray();
        var fake = new FakeK13(mem) { Writable = true };
        var tmp = Tmp();
        using var dev = new K13Device(fake) { DelayMs = 0, LoadDelayMs = 0 };
        var p = K13FactoryAssignments.Build(K13MemoryCodec.Decode(img), img);
        K13WriteGate.Unlock(K13WriteGate.ConfirmationPhrase);
        try
        {
            var r = K13Writer.WriteProfile(dev, p.Target, _ => { }, null, false, tmp);
            Assert.True(r.Ok, r.Message);
            var back = K13MemoryCodec.Decode(mem);
            for (int col = 0; col < 18; col++) Assert.Equal(K13Layout.FactoryColumnCodes[col], back.GetKey(col));
            Assert.Equal((0, 1), (back.WheelFirst, back.WheelSecond));
            Assert.All(back.Macros, m => Assert.False(m.IsUsed));
            Assert.Equal(img.AsSpan(0, 0x100).ToArray(), mem.AsSpan(0, 0x100).ToArray());
            Assert.DoesNotContain(fake.Frames, f => IsWrite(f) && f[2] == 0x09 && (f[4] | f[5] << 8) < 0x100);
        }
        finally { K13WriteGate.Lock(); try { Directory.Delete(tmp, true); } catch { } }
    }

    // ───────── hold
    [Fact]
    public void Hold_macro_presses_only_and_is_while_held()
    {
        var m = HoldKeyAssign.Macro(new byte[] { 0xE2 });
        Assert.Equal(MacroRepeatMode.WhileHeld, m.RepeatMode);
        Assert.Single(m.Events); Assert.True(m.Events[0].Down); Assert.Equal(0xE2, m.Events[0].Code);
        Assert.Equal(new byte[] { 0xE2 }, HoldKeyAssign.HeldKeys(m));
        var combo = new MacroSlot { Events = { new() { Down = true, Code = 0xE2 }, new() { Down = false, Code = 0xE2 } } };
        combo.RepeatMode = MacroRepeatMode.WhileHeld;
        Assert.Null(HoldKeyAssign.HeldKeys(combo));
        var once = HoldKeyAssign.Macro(new byte[] { 0xE2 }); once.RepeatMode = MacroRepeatMode.Count;
        Assert.Null(HoldKeyAssign.HeldKeys(once));
        Assert.Throws<ArgumentException>(() => HoldKeyAssign.Macro(Array.Empty<byte>()));
    }

    [Fact]
    public void Hold_assign_uses_free_slot_then_reuses_own()
    {
        var c = K13MemoryCodec.Decode(K13());
        var (slot, err) = HoldKeyAssign.Assign(c, 4, new byte[] { 0xE2 }, 7);
        Assert.Null(err); Assert.Equal(7, slot);
        Assert.Equal(KeyCatalog.FirstMacro + 7, c.GetKey(4));
        var (again, err2) = HoldKeyAssign.Assign(c, 4, new byte[] { 0xE0, 0xE1 }, 9);
        Assert.Null(err2); Assert.Equal(7, again);                    // the key's own hold macro is reused
        Assert.Equal(new byte[] { 0xE0, 0xE1 }, HoldKeyAssign.HeldKeys(c.Macros[7]));
        Assert.NotNull(HoldKeyAssign.Assign(c, 5, new byte[] { 0xE2 }, -1).Error);
        Assert.NotNull(HoldKeyAssign.Assign(c, 5, Array.Empty<byte>(), 3).Error);
    }

    [Fact]
    public void Hold_assign_resolves_to_a_held_macro_through_the_pc()
    {
        var c = K13MemoryCodec.Decode(K13());
        HoldKeyAssign.Assign(c, 4, new byte[] { 0xE2 }, 7);
        var a = PcLayerPlanner.Resolve(c, 4);
        Assert.Equal(PcKeyKind.Macro, a.Kind); Assert.Equal(7, a.MacroSlot);
        var st = PcLayerPlanner.NewState(new[] { c });
        Assert.Contains(4, PcLayerPlanner.HoldColumns(new[] { c }, st));
    }

    [Fact]
    public void Hold_status_texts()
    {
        string N(int c) => "кнопка " + (c + 1);
        Assert.Contains("через ПК", HoldKeyAssign.Status("кнопка 5", false, false, -1, 4, N));
        Assert.Contains("подготовьте", HoldKeyAssign.Status("кнопка 5", true, false, -1, 4, N));
        Assert.Contains("одна кнопка", HoldKeyAssign.Status("кнопка 5", true, true, -1, 4, N));
        var moving = HoldKeyAssign.Status("кнопка 5", true, true, 9, 4, N);
        Assert.Contains("кнопка 10", moving); Assert.Contains("перейдёт", moving);
    }

    // ───────── key tester
    [Fact]
    public void Tester_consumer_reports()
    {
        Assert.Equal(0x00E9, KeyTester.ConsumerUsage(new byte[] { 0x03, 0xE9, 0x00 }));
        Assert.Equal(0x00EA, KeyTester.ConsumerUsage(new byte[] { 0x02, 0xEA, 0x00 }));
        Assert.Equal(0, KeyTester.ConsumerUsage(new byte[] { 0x03, 0x00, 0x00 }));
        Assert.Equal(0x0192, KeyTester.ConsumerUsage(new byte[] { 0x03, 0x92, 0x01 }));
        Assert.Equal("Громкость +", KeyTester.ConsumerName(0xE9));
        Assert.Contains("0x123", KeyTester.ConsumerName(0x123));
        Assert.Equal((ushort)0xAF, KeyTester.Consumer[0xE9].Vk);
    }

    [Fact]
    public void Tester_normal_mode_uses_factory_table()
    {
        Assert.Equal(new[] { 0 }, KeyTester.ControlsFor(0x61, HwMode.Normal, null));   // Num 1 → key 1
        Assert.Equal(new[] { 8 }, KeyTester.ControlsFor(0x0D, HwMode.Normal, null));   // Enter
        Assert.Equal(new[] { 12 }, KeyTester.ControlsFor(0x25, HwMode.Normal, null));  // ← tilt
        Assert.Contains(KeyTester.RollUp, KeyTester.ControlsFor(0xAF, HwMode.Normal, null));    // factory wheel: Vol+
        Assert.Contains(KeyTester.RollDown, KeyTester.ControlsFor(0xAE, HwMode.Normal, null));
        Assert.Empty(KeyTester.ControlsFor(0x41, HwMode.Normal, null));
    }

    [Fact]
    public void Tester_game_mode_uses_keypad_keymap_and_macro_first_key()
    {
        var c = K13MemoryCodec.Decode(K13());
        c.SetKey(0, 0x04);   // A
        c.SetKey(1, 0x04);   // A as well → both lit
        c.Macros[2] = new MacroSlot { Events = { new() { Down = true, Code = 0xE0 }, new() { Down = true, Code = 0x06 }, new() { Down = false, Code = 0x06 }, new() { Down = false, Code = 0xE0 } } };
        c.SetKey(2, (byte)(KeyCatalog.FirstMacro + 2));
        var a = KeyTester.ControlsFor(KeyCatalog.ToVirtualKey(0x04), HwMode.Game, c);
        Assert.Contains(0, a); Assert.Contains(1, a);   // same code → both candidates
        Assert.All(a, col => Assert.Equal(0x04, c.GetKey(col)));
        Assert.Contains(2, KeyTester.ControlsFor(KeyCatalog.ToVirtualKey(0xE0), HwMode.Game, c));
        Assert.DoesNotContain(KeyTester.ControlsFor(0x61, HwMode.Game, c), x => x is 0 or 1);
        c.WheelFirst = 3; c.WheelSecond = 2;
        Assert.Equal(new[] { KeyTester.RollUp }, KeyTester.ControlsForScroll(true, c));
        Assert.Equal(new[] { KeyTester.RollDown }, KeyTester.ControlsForScroll(false, c));
        Assert.DoesNotContain(KeyTester.RollUp, KeyTester.ControlsFor(0xAF, HwMode.Game, c));
    }

    [Fact]
    public void Tester_chord_maps_to_column()
    {
        var c = K13MemoryCodec.Decode(K13());
        var st = PcLayerPlanner.NewState(new[] { c });
        st.PreparedAt = DateTime.Now;
        var k = st.Keys[0];
        Assert.Equal(k.Column, KeyTester.ChordColumn(KeyCatalog.ToVirtualKey(k.Code), st));
        Assert.Equal(-1, KeyTester.ChordColumn(0x41, st));
        Assert.Equal(-1, KeyTester.ChordColumn(KeyCatalog.ToVirtualKey(k.Code), new PcLayerState()));
        Assert.Equal(5, KeyTester.Limits(true).Count);
    }
}
