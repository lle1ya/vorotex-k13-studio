using Xunit;

namespace Vorotex.K13.Tests;

/// <summary>
/// v5.9.7 — «пока зажата» через ПК: the autoclick must never outlive the keypad taps. Every tap of a held key sends a
/// heartbeat (Alive) to the player's watchdog, the release gap is shorter and adaptive, a single tap always stops.
/// </summary>
public class V597Tests
{
    static byte[] Raw() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", "exp15-postwrite-20261113-133715.bin")).Take(0x1000).ToArray();
    static readonly Func<ushort, bool> NoHost = _ => false;

    static (PcLayerRouter r, K13DeviceConfig main, ushort vk) Setup()
    {
        var main = K13MemoryCodec.Decode(Raw());
        main.Macros[1] = new MacroSlot { Events = { new() { Down = true, Delay = 1, Code = MouseCodes.Left }, new() { Down = false, Delay = 1, Code = MouseCodes.Left } }, RepeatMode = MacroRepeatMode.WhileHeld };
        main.SetKey(9, (byte)(KeyCatalog.FirstMacro + 1));
        var st = PcLayerPlanner.NewState(new[] { main });
        var r = new PcLayerRouter(); r.SetLayer(st);
        var want = PcLayerPlanner.HoldColumns(new[] { main }, st);
        r.SetHold(want, want);
        return (r, main, KeyCatalog.ToVirtualKey(st.Keys.First(k => k.Column == 9).Code));
    }

    static List<PcOut> Tap(PcLayerRouter r, ushort fvk, K13DeviceConfig p, long t0)
    {
        var o = new List<PcOut>();
        foreach (var (vk, down, dt) in new (ushort, bool, int)[] { (0x91, true, 0), (fvk, true, 8), (fvk, false, 16), (0x91, false, 24) })
            o.AddRange(r.Feed(vk, down, p, NoHost, t0 + dt, out _));
        return o;
    }

    [Fact]
    public void Every_tap_of_a_held_key_is_a_heartbeat_and_the_release_stops_within_the_gap()
    {
        var (r, main, vk) = Setup();
        var all = new List<PcOut>();
        // the user's log: 83 taps every 31 ms
        for (int i = 0; i < 83; i++) { all.AddRange(Tap(r, vk, main, 1000 + i * 31)); all.AddRange(r.Tick(1000 + i * 31 + 20)); }
        Assert.Equal(83, all.Count(x => x.Kind == PcOutKind.Alive && x.Slot == 9));
        Assert.Single(all, x => x.Kind == PcOutKind.StartMacroHeld);
        Assert.DoesNotContain(all, x => x.Kind == PcOutKind.StopMacro);
        long last = 1000 + 82 * 31 + 8;
        Assert.DoesNotContain(r.Tick(last + PcLayerRouter.DefaultGapMs - 5), x => x.Kind == PcOutKind.StopMacro);
        Assert.Contains(r.Tick(last + PcLayerRouter.DefaultGapMs + 5), x => x.Kind == PcOutKind.StopMacro);
        Assert.Equal(0, r.StreamCount);
        // measured 31 ms → the next release ends after max(3 × 31, 31 + 30) = 93 ms
        Assert.Equal(93, r.GapFor(9));
        Assert.True(PcLayerRouter.DefaultGapMs <= 150 && PcLayerRouter.MaxGapMs <= 400);
    }

    [Fact]
    public void A_quick_tap_right_after_start_up_never_leaves_the_macro_running()
    {
        var (r, main, vk) = Setup();
        Tap(r, vk, main, 1000);
        var end = r.Tick(1000 + 400);
        Assert.Contains(end, x => x.Kind == PcOutKind.StopMacro && x.Slot == 1);
        Assert.Equal(0, r.StreamCount);
        Assert.Equal(0, r.HeldCount);
    }

    [Fact]
    public void Heartbeats_are_not_logged_as_presses_and_release_all_clears_streams()
    {
        var (r, main, vk) = Setup();
        var o = Tap(r, vk, main, 1000);
        Assert.Contains(o, x => x.Kind == PcOutKind.Alive);
        var o2 = Tap(r, vk, main, 1031);
        Assert.All(o2, x => Assert.True(x.Kind is PcOutKind.Alive or PcOutKind.Note));   // repeated taps: nothing new to do
        var rel = r.ReleaseAll();
        Assert.Contains(rel, x => x.Kind == PcOutKind.StopMacro);
        Assert.Equal(0, r.StreamCount);
    }
}
