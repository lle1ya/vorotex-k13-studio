using Xunit;

namespace Vorotex.K13.Tests;

/// <summary>
/// v5.9.4 — DM-Shvyrkov's log 17.11.2026 12:19–12:24: «Автоклик ЛКМ» (пока зажата) on Tab with «Кнопки по профилю» on →
/// «[hook] кнопка 9 … Macro Макрос 1: StartMacroHeld» per press, nothing clicks. The keypad sends a ~16 ms Scroll Lock+F10 tap
/// whatever the finger does, so the release stopped the loop at once. Chord scheme: «пока зажата» now loops until the next press.
/// </summary>
public class V594Tests
{
    static byte[] Raw() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", "exp15-postwrite-20261113-133715.bin")).Take(0x1000).ToArray();
    static readonly Func<ushort, bool> NoHost = _ => false;

    static (PcLayerRouter r, K13DeviceConfig main, ushort tabVk, PcLayerState st) Chord(MacroRepeatMode mode)
    {
        var main = K13MemoryCodec.Decode(Raw());
        main.Macros[1] = new MacroSlot { Events = { new() { Down = true, Delay = 1, Code = MouseCodes.Left }, new() { Down = false, Delay = 1, Code = MouseCodes.Left } }, RepeatMode = mode };
        main.SetKey(9, (byte)(KeyCatalog.FirstMacro + 1));
        var st = PcLayerPlanner.NewState(new[] { main });
        var r = new PcLayerRouter(); r.SetLayer(st);
        return (r, main, KeyCatalog.ToVirtualKey(st.Keys.First(k => k.Column == 9).Code), st);
    }

    static List<PcOut> Tap(PcLayerRouter r, ushort fvk, K13DeviceConfig p, long t0)
    {
        var o = new List<PcOut>();
        foreach (var (vk, down, dt) in new (ushort, bool, int)[] { (0x91, true, 0), (fvk, true, 8), (fvk, false, 16), (0x91, false, 24) })
            o.AddRange(r.Feed(vk, down, p, NoHost, t0 + dt, out _));
        return o;
    }

    [Fact]
    public void Chord_scheme_hold_macro_loops_and_the_release_does_not_stop_it()
    {
        var (r, main, vk, st) = Chord(MacroRepeatMode.WhileHeld);
        Assert.True(PcLayerRouter.HoldBecomesToggle(st));
        var o = Tap(r, vk, main, 1000).Where(x => x.Kind != PcOutKind.Note).ToList(); // v5.9.6: + «нажмите Записать» note
        var start = Assert.Single(o);                       // no StopMacro from the 16 ms release
        Assert.Equal(PcOutKind.StartMacroLoop, start.Kind);
        Assert.Equal(1, start.Slot);
        Assert.Equal(0, r.HeldCount);
        Assert.Equal(PcOutKind.StartMacroLoop, Assert.Single(Tap(r, vk, main, 3000), x => x.Kind != PcOutKind.Note).Kind); // 2nd press → host toggles it off
    }

    [Fact]
    public void Toggle_and_count_macros_are_unchanged_in_the_chord_scheme()
    {
        var (r, main, vk, _) = Chord(MacroRepeatMode.Toggle);
        Assert.Equal(PcOutKind.StartMacro, Assert.Single(Tap(r, vk, main, 1000)).Kind);
        main.Macros[1].RepeatMode = MacroRepeatMode.Count;
        Assert.Equal(PcOutKind.StartMacro, Assert.Single(Tap(r, vk, main, 5000)).Kind);
    }

    [Fact]
    public void Hold_is_only_converted_while_the_chord_layer_is_on()
    {
        Assert.False(PcLayerRouter.HoldBecomesToggle(new PcLayerState()));
        Assert.False(PcLayerRouter.HoldBecomesToggle(new PcLayerState { Keys = { new PcLayerKey { Column = 9, Code = 0x72 } } })); // old scheme
    }

    [Fact]
    public void Loop_plays_endlessly_on_the_pc()
    {
        // the host plays StartMacroLoop with RepeatMode = Toggle → endless passes until the next press
        Assert.Equal(int.MaxValue, MacroSequencer.Passes(MacroRepeatMode.Toggle, 1));
        Assert.Equal(1, MacroSequencer.Passes(MacroRepeatMode.WhileHeld, 1));
    }
}
