using Xunit;

namespace Vorotex.K13.Tests;

/// <summary>
/// v5.9.6 — true «пока зажата» through the PC: the key's Scroll Lock+F tap macro gets the vendor hold bit (mode1), the keypad
/// should repeat the tap while held, the PC counts the key as held while taps keep coming. If the first press after the write
/// brings only one tap, the key stays «до повторного нажатия» and the log says the keypad does not repeat.
/// </summary>
public class V596Tests
{
    static byte[] Raw() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", "exp15-postwrite-20261113-133715.bin")).Take(0x1000).ToArray();
    static readonly Func<ushort, bool> NoHost = _ => false;

    static (PcLayerRouter r, K13DeviceConfig main, ushort tabVk, PcLayerState st) Setup(bool written, MacroRepeatMode mode = MacroRepeatMode.WhileHeld)
    {
        var main = K13MemoryCodec.Decode(Raw());
        main.Macros[1] = new MacroSlot { Events = { new() { Down = true, Delay = 1, Code = MouseCodes.Left }, new() { Down = false, Delay = 1, Code = MouseCodes.Left } }, RepeatMode = mode };
        main.SetKey(9, (byte)(KeyCatalog.FirstMacro + 1));
        var st = PcLayerPlanner.NewState(new[] { main });
        var r = new PcLayerRouter(); r.SetLayer(st);
        var want = PcLayerPlanner.HoldColumns(new[] { main }, st);
        r.SetHold(want, written ? want : new HashSet<int>());
        return (r, main, KeyCatalog.ToVirtualKey(st.Keys.First(k => k.Column == 9).Code), st);
    }

    static List<PcOut> Tap(PcLayerRouter r, ushort fvk, K13DeviceConfig p, long t0)
    {
        var o = new List<PcOut>();
        foreach (var (vk, down, dt) in new (ushort, bool, int)[] { (0x91, true, 0), (fvk, true, 8), (fvk, false, 16), (0x91, false, 24) })
            o.AddRange(r.Feed(vk, down, p, NoHost, t0 + dt, out _));
        return o;
    }

    static List<PcOut> Hold(PcLayerRouter r, ushort vk, K13DeviceConfig p, long t0, int taps, int period)
    {
        var o = new List<PcOut>();
        for (int i = 0; i < taps; i++) { o.AddRange(Tap(r, vk, p, t0 + i * period)); o.AddRange(r.Tick(t0 + i * period + 30)); }
        return o;
    }

    [Fact]
    public void Planner_sets_the_hold_bit_only_on_hold_keys_and_reads_it_back()
    {
        var (_, main, _, st) = Setup(written: false);
        var hold = PcLayerPlanner.HoldColumns(new[] { main }, st);
        Assert.Equal(new[] { 9 }, hold);
        var kp = K13MemoryCodec.Decode(K13MemoryCodec.Encode(PcLayerPlanner.Apply(main, st, hold), Raw()));
        int idx = st.Keys.FindIndex(k => k.Column == 9);
        for (int i = 0; i < st.Keys.Count; i++) Assert.Equal(i == idx, kp.Macros[i].Mode1Bit);
        Assert.True(PcLayerPlanner.LooksPrepared(st, kp));
        Assert.Equal(new[] { 9 }, PcLayerPlanner.HoldColumnsWritten(st, kp));
        // without the bit nothing counts as written, and the image differs (so «Записать» is needed)
        var plain = PcLayerPlanner.Apply(main, st);
        Assert.Empty(PcLayerPlanner.HoldColumnsWritten(st, K13MemoryCodec.Decode(K13MemoryCodec.Encode(plain, Raw()))));
        Assert.NotEmpty(PcLayerHealth.PendingWrite(PcLayerPlanner.Apply(main, st, hold), K13MemoryCodec.Encode(plain, Raw())));
        // the hold bit is exactly the vendor's: table byte 3 bit 7
        var img = K13MemoryCodec.Encode(PcLayerPlanner.Apply(main, st, hold), Raw());
        int b = img[0x1C] | img[0x1D] << 8, t = img[b + 0x1E] | img[b + 0x1F] << 8;
        Assert.Equal(0x80, img[t + idx * 5 + 3] & 0xC0);
    }

    [Fact]
    public void Repeating_taps_hold_the_macro_and_the_gap_stops_it()
    {
        var (r, main, vk, _) = Setup(written: true);
        var o = Hold(r, vk, main, 1000, taps: 12, period: 40);
        Assert.Equal(PcOutKind.StartMacroHeld, Assert.Single(o, x => x.Kind is PcOutKind.StartMacroHeld or PcOutKind.StartMacroLoop or PcOutKind.StartMacro).Kind);
        Assert.Contains(o, x => x.Kind == PcOutKind.Note && x.Code == 1);
        Assert.DoesNotContain(o, x => x.Kind == PcOutKind.StopMacro);       // still held while taps come
        Assert.Equal(1, r.StreamCount);
        Assert.Empty(r.Tick(1000 + 11 * 40 + PcLayerRouter.DefaultGapMs - 10));
        var end = r.Tick(1000 + 11 * 40 + PcLayerRouter.DefaultGapMs + 20);
        Assert.Contains(end, x => x.Kind == PcOutKind.StopMacro && x.Slot == 1);
        Assert.Contains(end, x => x.Kind == PcOutKind.Note && x.Shell.Contains("интервал ≈ 40 мс"));
        Assert.Equal(0, r.StreamCount);
        Assert.Equal(160, r.GapFor(9));                                     // adaptive: 4 × 40 ms
        // next hold: ends after the shorter adaptive gap
        Hold(r, vk, main, 5000, taps: 5, period: 40);
        Assert.Contains(r.Tick(5000 + 4 * 40 + 170), x => x.Kind == PcOutKind.StopMacro);
    }

    [Fact]
    public void One_tap_after_the_write_means_no_repeat_and_the_key_toggles()
    {
        var (r, main, vk, _) = Setup(written: true);
        var o = Tap(r, vk, main, 1000);
        Assert.Equal(PcOutKind.StartMacroHeld, Assert.Single(o).Kind);
        var end = r.Tick(1000 + 16 + PcLayerRouter.DefaultGapMs + 50);
        Assert.DoesNotContain(end, x => x.Kind == PcOutKind.StopMacro);    // keeps clicking
        var verdict = Assert.Single(end, x => x.Kind == PcOutKind.Note);
        Assert.Equal(0, verdict.Code);
        Assert.Contains("НЕ повторяет", verdict.Shell);
        Assert.True(r.KnownNoRepeat(9));
        // the next press stops it (host Play toggles)
        Assert.Equal(PcOutKind.StartMacroLoop, Assert.Single(Tap(r, vk, main, 3000)).Kind);
        Assert.DoesNotContain(r.Tick(3000 + 1000), x => x.Kind == PcOutKind.StopMacro);
    }

    [Fact]
    public void Without_the_written_bit_hold_is_a_toggle_and_says_to_write()
    {
        var (r, main, vk, _) = Setup(written: false);
        var o = Tap(r, vk, main, 1000);
        Assert.Contains(o, x => x.Kind == PcOutKind.StartMacroLoop);
        Assert.Contains(o, x => x.Kind == PcOutKind.Note && x.Shell.Contains("Записать"));
        Assert.Equal(0, r.StreamCount);
        Assert.DoesNotContain(Tap(r, vk, main, 2000), x => x.Kind == PcOutKind.Note);   // said once
    }

    [Fact]
    public void A_key_action_on_a_repeating_key_is_held_down_once_not_retyped()
    {
        var (r, main, vk, st) = Setup(written: true);
        var other = K13MemoryCodec.Decode(Raw()); other.SetKey(9, 0x2B);           // another profile: plain Tab
        var o = Hold(r, vk, other, 1000, taps: 8, period: 40);
        Assert.Single(o, x => x.Kind == PcOutKind.KeyDown);
        Assert.DoesNotContain(o, x => x.Kind == PcOutKind.KeyUp);
        Assert.Single(r.Tick(1000 + 7 * 40 + 400), x => x.Kind == PcOutKind.KeyUp);
    }

    [Fact]
    public void Toggle_macro_on_a_repeating_key_is_not_stopped_by_releasing()
    {
        var (r, main, vk, _) = Setup(written: true);
        main.Macros[1].RepeatMode = MacroRepeatMode.Toggle;
        var o = Hold(r, vk, main, 1000, taps: 6, period: 40);
        Assert.Single(o, x => x.Kind == PcOutKind.StartMacro);
        Assert.DoesNotContain(r.Tick(1000 + 5 * 40 + 400), x => x.Kind == PcOutKind.StopMacro);
    }
}
