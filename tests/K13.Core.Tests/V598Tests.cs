using Xunit;

namespace Vorotex.K13.Tests;

/// <summary>
/// v5.9.8 — real «пока зажата» through the PC. The K13 firmware does not stop a hold-bit macro on release (logs 17.11 13:35 /
/// 14:05), so the v5.9.6/7 «repeated taps» scheme behaved like «до повторного нажатия». A hold key now gets the plain code
/// «Меню» (0x65) in the keypad: real press, OS auto-repeat, real release.
/// </summary>
public class V598Tests
{
    static byte[] Raw() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", "exp15-postwrite-20261113-133715.bin")).Take(0x1000).ToArray();
    static readonly Func<ushort, bool> NoHost = _ => false;
    const ushort Menu = 0x5D;

    static (PcLayerRouter r, K13DeviceConfig main, ushort fvk, PcLayerState st) Setup(bool written, MacroRepeatMode mode = MacroRepeatMode.WhileHeld)
    {
        var main = K13MemoryCodec.Decode(Raw());
        main.Macros[1] = new MacroSlot { Events = { new() { Down = true, Delay = 1, Code = MouseCodes.Left }, new() { Down = false, Delay = 1, Code = MouseCodes.Left } }, RepeatMode = mode };
        main.SetKey(9, (byte)(KeyCatalog.FirstMacro + 1));
        var st = PcLayerPlanner.NewState(new[] { main });
        var r = new PcLayerRouter(); r.SetLayer(st);
        var want = PcLayerPlanner.HoldColumns(new[] { main }, st);
        if (mode != MacroRepeatMode.WhileHeld) want = new HashSet<int> { 9 };
        r.SetHold(want, written ? PcLayerPlanner.HoldCodes(st, want) : new Dictionary<int, byte>());
        return (r, main, KeyCatalog.ToVirtualKey(st.Keys.First(k => k.Column == 9).Code), st);
    }

    static List<PcOut> Feed(PcLayerRouter r, ushort vk, bool down, K13DeviceConfig p, long t, out bool swallow) => r.Feed(vk, down, p, NoHost, t, out swallow);

    static List<PcOut> ChordTap(PcLayerRouter r, ushort fvk, K13DeviceConfig p, long t0)
    {
        var o = new List<PcOut>();
        foreach (var (vk, down, dt) in new (ushort, bool, int)[] { (0x91, true, 0), (fvk, true, 8), (fvk, false, 16), (0x91, false, 24) })
            o.AddRange(r.Feed(vk, down, p, NoHost, t0 + dt, out _));
        return o;
    }

    [Fact]
    public void Planner_gives_the_hold_key_the_plain_Menu_code_without_the_hold_bit()
    {
        var (_, main, _, st) = Setup(written: false);
        var hold = PcLayerPlanner.HoldColumns(new[] { main }, st);
        Assert.Equal(new[] { 9 }, hold);
        var codes = PcLayerPlanner.HoldCodes(st, hold);
        Assert.Equal((byte)0x65, codes[9]);
        Assert.Equal((ushort)Menu, KeyCatalog.ToVirtualKey(0x65));
        var target = PcLayerPlanner.Apply(main, st, hold);
        var kp = K13MemoryCodec.Decode(K13MemoryCodec.Encode(target, Raw()));
        Assert.Equal(0x65, kp.GetKey(9));
        Assert.DoesNotContain(kp.Macros, m => m.Mode1Bit);                                  // no firmware hold bit any more
        Assert.True(PcLayerPlanner.LooksPrepared(st, kp));
        Assert.Equal(new Dictionary<int, byte> { [9] = 0x65 }, PcLayerPlanner.HoldColumnsWritten(st, kp));
        Assert.Empty(PcLayerPlanner.Compare(main, st, kp, hold));
        // the v5.9.6/7 image (chord macro with the hold bit) is not a written hold → «Записать» is needed
        var plain = PcLayerPlanner.Apply(main, st);
        Assert.Empty(PcLayerPlanner.HoldColumnsWritten(st, K13MemoryCodec.Decode(K13MemoryCodec.Encode(plain, Raw()))));
        Assert.NotEmpty(PcLayerHealth.PendingWrite(target, K13MemoryCodec.Encode(plain, Raw())));
    }

    [Fact]
    public void Only_one_key_gets_the_code_in_the_given_order()
    {
        var (_, _, _, st) = Setup(written: false);
        var two = st.Keys.Select(k => k.Column).Take(2).ToList();
        var codes = PcLayerPlanner.HoldCodes(st, two.AsEnumerable().Reverse());
        Assert.Single(codes);
        Assert.True(codes.ContainsKey(two[1]));
    }

    [Fact]
    public void A_held_plain_key_runs_the_macro_until_the_real_release()
    {
        var (r, main, _, _) = Setup(written: true);
        var down = Feed(r, Menu, true, main, 1000, out bool sw);
        Assert.True(sw);
        Assert.Equal(PcOutKind.StartMacroHeld, Assert.Single(down).Kind);                 // not StartMacroLoop (no toggle)
        var all = new List<PcOut>();
        for (int t = 1500; t < 4000; t += 33) all.AddRange(Feed(r, Menu, true, main, t, out _));   // OS auto-repeat
        Assert.All(all, x => Assert.Equal(PcOutKind.Alive, x.Kind));
        Assert.DoesNotContain(all, x => x.Kind is PcOutKind.StopMacro or PcOutKind.StartMacroHeld);
        var up = Feed(r, Menu, false, main, 4000, out sw);
        Assert.True(sw);
        Assert.Equal(PcOutKind.StopMacro, Assert.Single(up).Kind);
        Assert.Equal(0, r.HeldCount);
        // a quick tap also stops at once
        Assert.Single(Feed(r, Menu, true, main, 5000, out _), x => x.Kind == PcOutKind.StartMacroHeld);
        Assert.Single(Feed(r, Menu, false, main, 5040, out _), x => x.Kind == PcOutKind.StopMacro);
    }

    [Fact]
    public void Plain_code_is_not_taken_when_not_written_or_mode_off()
    {
        var (r, main, _, _) = Setup(written: false);
        Assert.Empty(Feed(r, Menu, true, main, 1000, out bool sw));
        Assert.False(sw);                                                                   // the user's own Menu key passes
        var (r2, main2, _, _) = Setup(written: true);
        r2.SetLayer(new PcLayerState());
        Assert.Empty(Feed(r2, Menu, true, main2, 1000, out sw));
        Assert.False(sw);
    }

    [Fact]
    public void Not_written_hold_is_a_toggle_and_says_to_write_once()
    {
        var (r, main, fvk, _) = Setup(written: false);
        var o = ChordTap(r, fvk, main, 1000);
        Assert.Contains(o, x => x.Kind == PcOutKind.StartMacroLoop);
        Assert.Contains(o, x => x.Kind == PcOutKind.Note && x.Shell.Contains("Записать"));
        Assert.DoesNotContain(ChordTap(r, fvk, main, 2000), x => x.Kind == PcOutKind.Note);
    }

    [Fact]
    public void Key_action_in_another_profile_is_a_real_held_key()
    {
        var (r, _, _, _) = Setup(written: true);
        var other = K13MemoryCodec.Decode(Raw()); other.SetKey(9, 0x2B);                  // another profile: plain Tab
        var o = new List<PcOut>();
        for (int t = 1000; t < 1700; t += 100) o.AddRange(Feed(r, Menu, true, other, t, out _));
        Assert.All(o, x => Assert.Equal(PcOutKind.KeyDown, x.Kind));
        Assert.Equal(7, o.Count);                                                           // auto-repeat passed on like a normal key
        Assert.Equal(PcOutKind.KeyUp, Assert.Single(Feed(r, Menu, false, other, 1700, out _)).Kind);
    }

    [Fact]
    public void Toggle_macro_on_the_plain_key_is_not_stopped_by_the_release()
    {
        var (r, main, _, _) = Setup(written: true, mode: MacroRepeatMode.Toggle);
        Assert.Single(Feed(r, Menu, true, main, 1000, out _), x => x.Kind == PcOutKind.StartMacro);
        Assert.DoesNotContain(Feed(r, Menu, false, main, 1200, out _), x => x.Kind == PcOutKind.StopMacro);
    }
}
