using Vorotex.K13;
using Xunit;

namespace K13.Core.Tests;

/// <summary>1.3.0: makers' feedback on 1.2.0 — horizontal scroll / cursor through the PC, two looped macros, lighting per Normal/Game,
/// blue mode indicator (0x000F bit 4).</summary>
public class V130Tests
{
    static byte[] Img(string f) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", f));
    static byte[] K13() => Img("exp15-postwrite-20261113-133715.bin").Take(0x1000).ToArray();
    static string Tmp() => Path.Combine(Path.GetTempPath(), "k13-v130-" + Guid.NewGuid().ToString("N"));

    // ───────── «Прокрутка и курсор»
    [Theory]
    [InlineData(PointerKind.Scroll, PointerDir.Left, PointerMode.Step, 3, "left", "step:3")]
    [InlineData(PointerKind.Scroll, PointerDir.Right, PointerMode.Toggle, 5, "right", "toggle:5")]
    [InlineData(PointerKind.Move, PointerDir.Up, PointerMode.Step, 10, "up", "step:10")]
    [InlineData(PointerKind.Move, PointerDir.Down, PointerMode.Toggle, 1, "down", "toggle:1")]
    public void Pointer_spec_round_trips_through_host_action(PointerKind k, PointerDir d, PointerMode m, int n, string value, string args)
    {
        var s = new PointerSpec(k, d, m, n);
        var a = s.ToAction();
        Assert.Equal(k == PointerKind.Scroll ? HostActionKind.Scroll : HostActionKind.MouseMove, a.Kind);
        Assert.Equal(value, a.Value); Assert.Equal(args, a.Arguments);
        Assert.Equal(s, PointerSpec.From(a));
        Assert.Equal(s.Title, a.ToString());
    }

    [Fact]
    public void Pointer_spec_parse_is_tolerant_and_clamped()
    {
        Assert.Null(PointerSpec.From(new HostAction { Kind = HostActionKind.Scroll, Value = "sideways" }));
        Assert.Null(PointerSpec.From(new HostAction { Kind = HostActionKind.KeyCombo, Value = "left" }));
        Assert.Equal(new PointerSpec(PointerKind.Scroll, PointerDir.Right, PointerMode.Step, 3), PointerSpec.From(new HostAction { Kind = HostActionKind.Scroll, Value = "Right" }));
        Assert.Equal(10, PointerSpec.From(new HostAction { Kind = HostActionKind.MouseMove, Value = "up", Arguments = "toggle:99" })!.Amount);
        Assert.Equal(1, PointerSpec.From(new HostAction { Kind = HostActionKind.MouseMove, Value = "up", Arguments = "step:-4" })!.Amount);
    }

    [Fact]
    public void Pointer_actions_survive_the_profile_json()
    {
        var p = new AppProfile { Name = "Excel" };
        p.Host.Keys.Add(new HostKeyBinding { TriggerVk = 0x7D, Tap = new PointerSpec(PointerKind.Scroll, PointerDir.Right, PointerMode.Step, 4).ToAction() });
        var json = System.Text.Json.JsonSerializer.Serialize(p, ProfileStore.JsonOptions);
        Assert.Contains("\"Scroll\"", json);
        var back = System.Text.Json.JsonSerializer.Deserialize<AppProfile>(json, ProfileStore.JsonOptions)!;
        Assert.Equal(new PointerSpec(PointerKind.Scroll, PointerDir.Right, PointerMode.Step, 4), PointerSpec.From(back.Host.Keys[0].Tap));
    }

    [Fact]
    public void Step_scroll_sends_horizontal_wheel_right_positive_left_negative()
    {
        var m = new PointerMotion();
        var (r, o) = m.Tap(new PointerSpec(PointerKind.Scroll, PointerDir.Right, PointerMode.Step, 3), 0);
        Assert.Equal(PointerMotion.TapResult.Stepped, r);
        Assert.Equal(new PointerOut(0, 0, 0, 360), o);
        Assert.Equal(new PointerOut(0, 0, 0, -120), m.Tap(new PointerSpec(PointerKind.Scroll, PointerDir.Left, PointerMode.Step, 1), 1000).Out);
        Assert.Equal(new PointerOut(0, 0, 240, 0), m.Tap(new PointerSpec(PointerKind.Scroll, PointerDir.Up, PointerMode.Step, 2), 2000).Out);
        Assert.Equal(new PointerOut(0, 0, -240, 0), m.Tap(new PointerSpec(PointerKind.Scroll, PointerDir.Down, PointerMode.Step, 2), 3000).Out);
        // fast taps do not accelerate scrolling (one notch = one column in Excel)
        Assert.Equal(new PointerOut(0, 0, -240, 0), m.Tap(new PointerSpec(PointerKind.Scroll, PointerDir.Down, PointerMode.Step, 2), 3100).Out);
        Assert.False(m.IsRunning);
    }

    [Fact]
    public void Step_cursor_accelerates_on_fast_taps_and_resets_after_a_pause()
    {
        var m = new PointerMotion();
        var s = new PointerSpec(PointerKind.Move, PointerDir.Left, PointerMode.Step, 4);
        Assert.Equal(new PointerOut(-40, 0, 0, 0), m.Tap(s, 0).Out);
        Assert.Equal(new PointerOut(-80, 0, 0, 0), m.Tap(s, 200).Out);
        Assert.Equal(new PointerOut(-120, 0, 0, 0), m.Tap(s, 400).Out);
        Assert.Equal(new PointerOut(-160, 0, 0, 0), m.Tap(s, 600).Out);
        Assert.Equal(new PointerOut(-160, 0, 0, 0), m.Tap(s, 800).Out);   // capped ×4
        Assert.Equal(new PointerOut(-40, 0, 0, 0), m.Tap(s, 2000).Out);   // pause → ×1
        Assert.Equal(new PointerOut(0, 40, 0, 0), m.Tap(s with { Dir = PointerDir.Down }, 2100).Out); // other direction → ×1
    }

    [Fact]
    public void Toggle_starts_switches_direction_and_stops_on_the_same_press()
    {
        var m = new PointerMotion();
        var right = new PointerSpec(PointerKind.Move, PointerDir.Right, PointerMode.Toggle, 4);
        Assert.Equal(PointerMotion.TapResult.Started, m.Tap(right, 0).Result);
        Assert.True(m.IsRunning);
        int x = 0;
        for (long t = 15; t <= 1000; t += 15) { var o = m.Tick(t); Assert.Equal(0, o.Dy); Assert.True(o.Dx >= 0); x += o.Dx; }
        Assert.InRange(x, 250, 700);                                    // ≈ 240 px/s, accelerating
        Assert.Equal(PointerMotion.TapResult.Switched, m.Tap(right with { Dir = PointerDir.Up }, 1000).Result);
        int y = 0; for (long t = 1015; t <= 1300; t += 15) y += m.Tick(t).Dy;
        Assert.True(y < 0);                                              // up = screen Y decreases
        Assert.Equal(PointerMotion.TapResult.Stopped, m.Tap(right with { Dir = PointerDir.Up }, 1300).Result);
        Assert.False(m.IsRunning);
        Assert.Equal(default, m.Tick(1400));
    }

    [Fact]
    public void Toggle_scroll_rate_and_safety_stop()
    {
        var m = new PointerMotion();
        var s = new PointerSpec(PointerKind.Scroll, PointerDir.Right, PointerMode.Toggle, 3);
        var (r, first) = m.Tap(s, 0);
        Assert.Equal(PointerMotion.TapResult.Started, r);
        Assert.Equal(120, first.HWheel);                                 // the first notch at once
        int h = 0; for (long t = 15; t <= 1000; t += 15) h += m.Tick(t).HWheel;
        Assert.InRange(h / 120, 11, 12);                                 // 3 × 4 = 12 notches per second
        for (long t = 1015; t < PointerMotion.MaxRunMs + 100; t += 15) m.Tick(t);
        Assert.False(m.IsRunning);
        Assert.Contains("страховка", m.LastStopReason);
        Assert.True(new PointerMotion().Stop("x") == false);
    }

    // ───────── blue indicator (0x000F bit 4) and «remember mode» together
    [Theory]
    [InlineData(0x91, false, 0x81)] [InlineData(0x93, false, 0x83)] [InlineData(0x81, true, 0x91)] [InlineData(0x91, true, 0x91)]
    public void Indicator_target_flips_only_bit4(byte cur, bool on, byte want)
    {
        var (t, err) = K13ModeIndicator.Target(cur, on);
        Assert.Null(err); Assert.Equal(want, t);
    }

    [Theory]
    [InlineData(0x81)] [InlineData(0x83)] [InlineData(0x91)] [InlineData(0x93)]
    public void Remember_mode_still_works_with_the_indicator_off(byte cur)
    {
        Assert.True(K13RememberMode.IsKnownPattern(cur));
        Assert.Equal((byte)(cur | 0x02), K13RememberMode.Target(cur, true).Target);
    }

    [Theory]
    [InlineData(0xB1)] [InlineData(0xA1)] [InlineData(0x01)] [InlineData(0x55)]
    public void Indicator_refuses_unknown_values(byte cur) => Assert.Null(K13ModeIndicator.Target(cur, false).Target);

    [Fact]
    public void Indicator_write_changes_one_bit_of_one_byte_with_backup_and_check()
    {
        var mem = K13();
        var before = (byte[])mem.Clone();
        var fake = new FakeK13(mem) { Writable = true };
        var tmp = Tmp();
        using var dev = new K13Device(fake) { DelayMs = 0 };
        K13WriteGate.Unlock(K13WriteGate.ConfirmationPhrase);
        try
        {
            int f0 = fake.Frames.Count;
            var r = K13RememberMode.Run(dev, c => K13ModeIndicator.Target(c, false), _ => { }, tmp,
                allowedBits: K13ModeIndicator.Bit, done: K13ModeIndicator.Done, what: "индикатор", backupTag: "k13-indicator-before");
            Assert.True(r.Ok, r.Message);
            Assert.Equal(0x91, r.Before); Assert.Equal(0x81, r.After);
            Assert.Contains("индикатор режима выключен", r.Message);
            Assert.Contains("k13-indicator-before", r.BackupPath);
            for (int i = 0; i < mem.Length; i++) if (i != 0x0F) Assert.Equal(before[i], mem[i]);
            var w = fake.Frames.Skip(f0).Where(f => f[2] is 0x03 or 0x09).Select(Convert.ToHexString).ToList();
            Assert.Equal(new[] { "071803000F008100", "071809000F000000" }, w);
            Assert.DoesNotContain(fake.Frames, f => f[2] is 0x03 or 0x09 && (f[4] | f[5] << 8) < 8);
            // the indicator mask never lets bit 1 through, and vice versa
            var bad = K13RememberMode.Run(dev, c => ((byte)(c ^ 0x12), null), _ => { }, tmp, allowedBits: K13ModeIndicator.Bit);
            Assert.False(bad.Written);
            Assert.Equal(0x81, mem[0x0F]);
            var bad2 = K13RememberMode.Run(dev, c => K13ModeIndicator.Target(c, true), _ => { }, tmp);   // default mask = bit 1 only
            Assert.False(bad2.Written);
            Assert.Equal(0x81, mem[0x0F]);
            var back = K13RememberMode.Run(dev, c => K13ModeIndicator.Target(c, true), _ => { }, tmp, allowedBits: K13ModeIndicator.Bit);
            Assert.True(back.Ok, back.Message);
            Assert.Equal(before, mem);
        }
        finally { K13WriteGate.Lock(); try { Directory.Delete(tmp, true); } catch { } }
    }

    // ───────── lighting for Normal / Game
    [Fact]
    public void Normal_lighting_choices()
    {
        var a = new AppProfile { Name = "A", Device = K13MemoryCodec.Decode(K13()) };
        a.Device!.Led.Mode = 2; a.Device.Led.Brightness = 3;
        var b = new AppProfile { Name = "B", Device = K13MemoryCodec.Decode(K13()) };
        b.Device!.Led.Mode = 8;
        var list = new[] { a, b };
        Assert.Null(ModeLighting.NormalLed("", list, a.Device.Led));
        Assert.Null(ModeLighting.NormalLed(null, list, a.Device.Led));
        Assert.Equal(0, ModeLighting.NormalLed(ModeLighting.Off, list, a.Device.Led)!.Mode);
        Assert.Equal(3, ModeLighting.NormalLed(ModeLighting.Off, list, a.Device.Led)!.Brightness);
        Assert.Equal(8, ModeLighting.NormalLed("B", list, a.Device.Led)!.Mode);
        Assert.Null(ModeLighting.NormalLed("deleted", list, a.Device.Led));
        Assert.Equal(2, a.Device.Led.Mode);                              // the profile itself is not changed
        Assert.False(ModeLighting.Enabled("")); Assert.True(ModeLighting.Enabled("B"));
    }

    [Fact]
    public void Normal_lighting_target_differs_only_in_the_lighting_area_and_keeps_the_mode_flags()
    {
        var img = K13();                                                  // BASE+0 = 0x40 (Normal)
        var game = K13MemoryCodec.Decode(img); game.Led.Flags = 0xC0; game.Led.Mode = 2;
        var normalLed = game.Led.Clone(); normalLed.Mode = 0;
        var t = ModeLighting.WithLed(game, normalLed, img);
        Assert.Equal(0x40, t.Led.Flags);
        Assert.Equal(0, t.Led.Mode);
        Assert.Equal(2, game.Led.Mode);                                   // a copy
        var part = ProfileSwitchLighting.LightingPart(t, img)!;
        var diff = PcLayerHealth.PendingWrite(K13Mode.PrepareTarget(part, img), img);
        Assert.NotEmpty(diff);
        Assert.All(diff, d => Assert.InRange(d.Address, 0xE0, 0xFF));
        var enc = K13MemoryCodec.Encode(K13Mode.PrepareTarget(part, img), img);
        Assert.Equal(0x40, enc[0xE0]);                                    // bit 7 (mode) not set by the lighting write
    }

    [Fact]
    public void Writer_keepModeFlags_writes_BASE0_back_as_read()
    {
        var mem = K13();                                                  // BASE+0 = 0x40
        var fake = new FakeK13(mem) { Writable = true };
        var tmp = Tmp();
        using var dev = new K13Device(fake) { DelayMs = 0, LoadDelayMs = 0 };
        var cfg = K13MemoryCodec.Decode(K13()); cfg.Led.Flags = 0xC0; cfg.Led.Mode = (cfg.Led.Mode + 1) % 12;
        K13WriteGate.Unlock(K13WriteGate.ConfirmationPhrase);
        try
        {
            var r = K13Writer.WriteProfile(dev, cfg, _ => { }, null, false, tmp, keepModeFlags: true);
            Assert.True(r.Ok, r.Message);
            Assert.Equal(0x40, mem[0xE0]);
            Assert.Equal(cfg.Led.Mode, K13MemoryCodec.Decode(mem).Led.Mode);
            // without the option the vendor behaviour stays: KEYSMODE bit set when the flags byte is written
            var mem2 = K13(); var fake2 = new FakeK13(mem2) { Writable = true };
            using var dev2 = new K13Device(fake2) { DelayMs = 0, LoadDelayMs = 0 };
            var r2 = K13Writer.WriteProfile(dev2, cfg, _ => { }, null, false, tmp);
            Assert.True(r2.Ok, r2.Message);
            Assert.Equal(0xC0, mem2[0xE0] & 0xC0);
        }
        finally { K13WriteGate.Lock(); try { Directory.Delete(tmp, true); } catch { } }
    }

    [Fact]
    public void Mode_lighting_gate_caps_writes_per_hour()
    {
        var g = new ModeLightingGate();
        for (int i = 0; i < ModeLighting.MaxPerHour; i++) { Assert.True(g.CanWrite(i * 1000)); g.Wrote(i * 1000); }
        Assert.False(g.CanWrite(60_000));
        Assert.Equal(ModeLighting.MaxPerHour, g.WritesLastHour(60_000));
        Assert.True(g.CanWrite(3_600_000 + 500));                         // the first one expired
    }

    // ───────── two looped macros: the source keeps one play per button
    [Fact]
    public async Task Two_looped_macros_play_independently_in_the_sequencer()
    {
        var a = new List<MacroEvent> { new() { Code = 0x04, Down = true }, new() { Code = 0x04, Down = false, Delay = 1 } };
        var b = new List<MacroEvent> { new() { Code = 0x05, Down = true }, new() { Code = 0x05, Down = false, Delay = 1 } };
        var sentA = new System.Collections.Concurrent.ConcurrentQueue<byte>(); var sentB = new System.Collections.Concurrent.ConcurrentQueue<byte>();
        using var ca = new CancellationTokenSource(); using var cb = new CancellationTokenSource();
        var ta = Task.Run(() => MacroSequencer.Run(a, int.MaxValue, e => sentA.Enqueue(e.Code), ca.Token));
        var tb = Task.Run(() => MacroSequencer.Run(b, int.MaxValue, e => sentB.Enqueue(e.Code), cb.Token));
        await Task.Delay(200);
        ca.Cancel();                                                      // stop A only
        await ta.WaitAsync(TimeSpan.FromSeconds(2));
        int bBefore = sentB.Count;
        await Task.Delay(200);
        Assert.False(tb.IsCompleted);
        Assert.True(sentB.Count > bBefore);                               // B still plays
        cb.Cancel(); await tb.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(sentA.Count >= 2);
    }

    [Fact]
    public void Studio_macro_player_is_keyed_per_button()
    {
        var src = File.ReadAllText(Path.Combine(SourceRoot(), "src", "K13.Studio", "HostMapper.cs"));
        Assert.Contains("ConcurrentDictionary<int, Play_> _plays", src);
        Assert.Contains("MacroPlayer.Play(m, slotKey)", src);
        Assert.Contains("MacroPlayer.Play(m, slot)", src);
        Assert.DoesNotContain("private static System.Threading.CancellationTokenSource? _playing;", src);
    }

    static string SourceRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "VorotexStudio.sln"))) d = d.Parent;
        return d!.FullName;
    }
}
