using Xunit;

namespace Vorotex.K13.Tests;

/// <summary>
/// v5.6.3 — DM-Shvyrkov's log 16.11.2026: keypad prepared with F23 on Tab (readback confirmed) → with v5.6.2
/// «12:45:56 [k13] клавиатура прислала VK 0x09 (скан 0x0F) «Tab»» — the firmware ignores F14–F24 in a keymap cell and sends
/// the key's factory code. After «Кнопки по профилю» → off (macro written back) «12:46:43 [k13] … «1» «2» «3»» without a replug:
/// writes apply at once, only the codes were the problem. New scheme: each key runs a keypad macro «Scroll Lock + F1…F11».
/// </summary>
public class V563Tests
{
    static byte[] Img(string f) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", f));
    static byte[] Raw() => Img("exp15-postwrite-20261113-133715.bin").Take(0x1000).ToArray();
    static K13DeviceConfig Base() => K13MemoryCodec.Decode(Raw());
    static readonly Func<ushort, bool> NoHost = _ => false;

    static (K13DeviceConfig main, K13DeviceConfig oneC) Profiles()
    {
        var main = Base();
        main.Macros[0] = new MacroSlot { Events = { new() { Down = true, Code = 0x1E }, new() { Down = true, Code = 0x1F }, new() { Down = true, Code = 0x20 },
                                                    new() { Down = false, Code = 0x1E }, new() { Down = false, Code = 0x1F }, new() { Down = false, Code = 0x20 } } };
        main.SetKey(9, KeyCatalog.FirstMacro);
        main.SetKey(7, KeyCatalog.FirstMacro);       // «кнопка 8: Макрос 0» (12:19:49)
        var oneC = Base(); oneC.SetKey(9, 0x2B);
        return (main, oneC);
    }

    [Fact]
    public void Old_F14_F24_state_is_flagged_and_its_Tab_is_unexpected()
    {
        var (main, oneC) = Profiles();
        var legacy = new PcLayerState { Keys = PcLayerPlanner.Plan(new[] { main, oneC }) };
        Assert.True(legacy.Legacy);
        var kp = K13MemoryCodec.Decode(K13MemoryCodec.Encode(PcLayerPlanner.Apply(oneC, legacy), Raw()));
        Assert.Equal(PcHealth.Unexpected, PcLayerHealth.Check(legacy, kp, 0x09));
    }

    [Fact]
    public void New_state_uses_keypad_macros_with_standard_codes_only()
    {
        var (main, oneC) = Profiles();
        var st = PcLayerPlanner.NewState(new[] { main, oneC });
        Assert.False(st.Legacy);
        Assert.Equal(11, st.Keys.Count);
        Assert.True(st.IsMapped(9) && st.IsMapped(7));
        var target = PcLayerPlanner.Apply(main, st);
        K13MemoryCodec.Validate(target);
        var kp = K13MemoryCodec.Decode(K13MemoryCodec.Encode(target, Raw()));
        Assert.True(PcLayerPlanner.LooksPrepared(st, kp));
        for (int i = 0; i < st.Keys.Count; i++)
        {
            Assert.Equal(KeyCatalog.FirstMacro + i, kp.GetKey(st.Keys[i].Column));
            // only codes the K13 firmware is known to send: the vendor's standard 104 keys (Scroll Lock 0x47, F1–F11 0x3A–0x44)
            Assert.All(kp.Macros[i].Events, e => Assert.True(e.Code <= 0x65, $"0x{e.Code:X2}"));
            Assert.Equal(new byte[] { 0x47, st.Keys[i].Code, st.Keys[i].Code, 0x47 }, kp.Macros[i].Events.Select(e => e.Code));
        }
        // the same image for both profiles → switching profiles never needs a write
        Assert.Empty(PcLayerHealth.PendingWrite(PcLayerPlanner.Apply(oneC, st), K13MemoryCodec.Encode(target, Raw())));
    }

    static List<PcOut> Press(PcLayerRouter r, ushort fvk, K13DeviceConfig profile, long t0, List<bool> swallowed)
    {
        var o = new List<PcOut>();
        foreach (var (vk, down, dt) in new (ushort, bool, int)[] { (0x91, true, 0), (fvk, true, 8), (fvk, false, 16), (0x91, false, 24) })
        {
            o.AddRange(r.Feed(vk, down, profile, NoHost, t0 + dt, out bool sw));
            swallowed.Add(sw);
        }
        return o;
    }

    [Fact]
    public void Tab_follows_the_profile_through_the_chord()
    {
        var (main, oneC) = Profiles();
        var st = PcLayerPlanner.NewState(new[] { main, oneC });
        var r = new PcLayerRouter(); r.SetLayer(st);
        ushort tabVk = KeyCatalog.ToVirtualKey(st.Keys.First(k => k.Column == 9).Code);
        var sw = new List<bool>();
        var inMain = Press(r, tabVk, main, 1000, sw);                     // Word, «Основной»
        Assert.Equal(PcOutKind.StartMacro, Assert.Single(inMain).Kind);
        Assert.Equal(0, inMain[0].Slot);
        var in1C = Press(r, tabVk, oneC, 5000, sw);                       // 1С
        Assert.Equal(new[] { PcOutKind.KeyDown, PcOutKind.KeyUp }, in1C.Select(x => x.Kind));
        Assert.Equal(0x09, in1C[0].Vk);
        Assert.All(sw, Assert.True);                                     // nothing of the chord reaches Windows
        ushort key8 = KeyCatalog.ToVirtualKey(st.Keys.First(k => k.Column == 7).Code);
        Assert.Equal(PcOutKind.StartMacro, Assert.Single(Press(r, key8, main, 9000, sw)).Kind);
    }

    [Fact]
    public void Real_keyboard_F_keys_pass_and_Scroll_Lock_alone_does_nothing()
    {
        var (main, oneC) = Profiles();
        var st = PcLayerPlanner.NewState(new[] { main, oneC });
        var r = new PcLayerRouter(); r.SetLayer(st);
        Assert.Empty(r.Feed(0x74, true, main, NoHost, 1000, out bool sw1)); Assert.False(sw1);   // F5 on the main keyboard
        Assert.Empty(r.Feed(0x74, false, main, NoHost, 1050, out bool sw2)); Assert.False(sw2);
        Assert.Empty(r.Feed(0x91, true, main, NoHost, 2000, out bool sw3)); Assert.True(sw3);
        Assert.Empty(r.Feed(0x91, false, main, NoHost, 2080, out _));
        Assert.Empty(r.Feed(0x74, true, main, NoHost, 3000, out bool sw4)); Assert.False(sw4);    // long after Scroll Lock
        Assert.Empty(r.Feed(0x74, false, main, NoHost, 3050, out _));
    }

    [Fact]
    public void Legacy_state_still_routes_through_Feed()
    {
        var (main, oneC) = Profiles();
        var legacy = new PcLayerState { Keys = PcLayerPlanner.Plan(new[] { main, oneC }) };
        var r = new PcLayerRouter(); r.SetLayer(legacy);
        ushort vk = KeyCatalog.ToVirtualKey(legacy.Keys.First(k => k.Column == 9).Code);
        Assert.Equal(PcOutKind.StartMacro, Assert.Single(r.Feed(vk, true, main, NoHost, 0, out bool sw)).Kind);
        Assert.True(sw);
    }
}
