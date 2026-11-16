using System.Text.Json;
using Vorotex.K13;
using Xunit;

namespace K13.Core.Tests;

public class PcLayerTests
{
    static byte[] Img(string f) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", f));
    static K13DeviceConfig Base() => K13MemoryCodec.Decode(Img("exp15-postwrite-20261113-133715.bin").Take(0x1000).ToArray());
    const int Tab = 9;

    /// <summary>The user's case: macro on Tab in «Основной», plain Tab in «1С».</summary>
    static (K13DeviceConfig main, K13DeviceConfig oneC) UserProfiles()
    {
        var main = Base();
        main.Macros[0] = new MacroSlot { Events = { new() { Down = true, Code = 0x04 }, new() { Down = false, Code = 0x04, Delay = 2 } } };
        main.SetKey(Tab, KeyCatalog.FirstMacro);
        var oneC = Base();
        oneC.SetKey(Tab, 0x2B);
        oneC.SetKey(0, 0x3A); // F1 on key 1
        return (main, oneC);
    }

    [Fact]
    public void Pool_is_F14_to_F24_and_F13_stays_for_the_ring()
    {
        Assert.Equal(11, PcLayerPlanner.Pool.Length);
        Assert.DoesNotContain((byte)0x68, PcLayerPlanner.Pool);
        Assert.Equal("F14", KeyCatalog.Name(PcLayerPlanner.Pool[0]));
        Assert.Equal("F24", KeyCatalog.Name(PcLayerPlanner.Pool[^1]));
        Assert.Equal(12, PcLayerPlanner.Columns.Length);
        Assert.All(PcLayerPlanner.Columns, c => Assert.Equal(ControlKind.Key, K13Layout.ByColumn(c).Kind));
    }

    [Fact]
    public void Plan_maps_11_keys_with_unique_codes_and_leaves_the_same_everywhere_key_native()
    {
        var (main, oneC) = UserProfiles();
        var plan = PcLayerPlanner.Plan(new[] { main, oneC });
        Assert.Equal(11, plan.Count);
        Assert.Equal(plan.Count, plan.Select(k => k.Code).Distinct().Count());
        Assert.Equal(plan.Count, plan.Select(k => k.Column).Distinct().Count());
        Assert.Contains(plan, k => k.Column == Tab);   // differs between profiles → must follow the profile
        Assert.Contains(plan, k => k.Column == 0);
        int native = PcLayerPlanner.Columns.Single(c => plan.All(k => k.Column != c));
        Assert.Equal(main.GetKey(native), oneC.GetKey(native));
    }

    [Fact]
    public void Plan_avoids_codes_the_joystick_already_sends()
    {
        var a = Base(); a.SetKey(12, 0x69); // joystick ← sends F14 in this profile
        var plan = PcLayerPlanner.Plan(new[] { a, Base() });
        Assert.DoesNotContain(plan, k => k.Code == 0x69);
        Assert.Equal(10, plan.Count);
    }

    [Fact]
    public void Apply_writes_codes_moves_mapped_macros_to_the_pc_and_keeps_the_rest()
    {
        var (main, oneC) = UserProfiles();
        var st = new PcLayerState { Keys = PcLayerPlanner.Plan(new[] { main, oneC }) };
        var w = PcLayerPlanner.Apply(main, st);
        foreach (var k in st.Keys) Assert.Equal(k.Code, w.GetKey(k.Column));
        Assert.False(w.Macros[0].IsUsed);                      // macro on Tab runs on the PC now
        Assert.Equal(KeyCatalog.FirstMacro, main.GetKey(Tab)); // profile untouched
        Assert.True(main.Macros[0].IsUsed);
        Assert.Equal(main.GetKey(12), w.GetKey(12));           // joystick stays
        Assert.Equal(main.Led.Mode, w.Led.Mode);
        // switching profiles does not make the keypad «differ» in its keys/macros
        var w2 = PcLayerPlanner.Apply(oneC, st);
        Assert.Equal(w.Keymap, w2.Keymap);
        Assert.Equal(w.Macros.Select(m => m.IsUsed), w2.Macros.Select(m => m.IsUsed));
        // inactive state = the profile as is
        Assert.Equal(main.Keymap, PcLayerPlanner.Apply(main, new PcLayerState()).Keymap);
    }

    [Fact]
    public void Applied_config_encodes_and_validates()
    {
        var (main, oneC) = UserProfiles();
        var st = new PcLayerState { Keys = PcLayerPlanner.Plan(new[] { main, oneC }) };
        var img = Img("exp15-postwrite-20261113-133715.bin").Take(0x1000).ToArray();
        var w = PcLayerPlanner.Apply(main, st);
        K13MemoryCodec.Validate(w);
        var back = K13MemoryCodec.Decode(K13MemoryCodec.Encode(w, img));
        Assert.True(PcLayerPlanner.LooksPrepared(st, back));
        Assert.Empty(PcLayerPlanner.Compare(main, st, back));
        Assert.Empty(PcLayerPlanner.Compare(oneC, st, back)); // the other profile needs no write either
    }

    [Fact]
    public void Resolve_follows_the_active_profile()
    {
        var (main, oneC) = UserProfiles();
        var a = PcLayerPlanner.Resolve(main, Tab);
        Assert.Equal(PcKeyKind.Macro, a.Kind);
        Assert.Equal(0, a.MacroSlot);
        var b = PcLayerPlanner.Resolve(oneC, Tab);
        Assert.Equal(PcKeyKind.Key, b.Kind);
        Assert.Equal((ushort)0x09, b.Vk);
        Assert.Equal((ushort)0x70, PcLayerPlanner.Resolve(oneC, 0).Vk);
    }

    [Theory]
    [InlineData(0xFF, PcKeyKind.None, (ushort)0)]
    [InlineData(0x04, PcKeyKind.Key, (ushort)0x41)]
    [InlineData(0xE0, PcKeyKind.Key, (ushort)0xA2)]  // Left Ctrl: held like a normal modifier
    [InlineData(0xCB, PcKeyKind.Key, (ushort)0xAF)]  // volume +
    [InlineData(0x58, PcKeyKind.Key, (ushort)0x0D)]  // Num Enter → Enter
    [InlineData(0xD9, PcKeyKind.Shell, (ushort)0)]   // calculator
    [InlineData(0xDF, PcKeyKind.Shell, (ushort)0)]   // screenshot
    [InlineData(0xE9, PcKeyKind.FirmwareOnly, (ushort)0)] // LED mode
    [InlineData(0xF3, PcKeyKind.FirmwareOnly, (ushort)0)] // key lock
    [InlineData(0x7A, PcKeyKind.FirmwareOnly, (ushort)0)] // unknown code
    [InlineData(0x6A, PcKeyKind.Key, (ushort)0x7E)]  // F15 → a PC-action trigger
    public void Resolve_kinds(int code, PcKeyKind kind, ushort vk)
    {
        var c = Base(); c.SetKey(Tab, (byte)code);
        var a = PcLayerPlanner.Resolve(c, Tab);
        Assert.Equal(kind, a.Kind);
        Assert.Equal(vk, a.Vk);
        Assert.False(string.IsNullOrWhiteSpace(PcLayerPlanner.Describe(a)));
    }

    [Fact]
    public void Compare_reports_what_the_keypad_really_does()
    {
        var (main, oneC) = UserProfiles();
        // classic mode, keypad holds «Основной», UI shows «1С» → Tab and key 1 differ
        var m = PcLayerPlanner.Compare(oneC, new PcLayerState(), main);
        Assert.Contains(m, x => x.Column == Tab && x.InKeypad == KeyCatalog.FirstMacro && x.Expected == 0x2B);
        Assert.Contains(m, x => x.Column == 0);
        // prepared state but the keypad was rewritten by another app
        var st = new PcLayerState { Keys = PcLayerPlanner.Plan(new[] { main, oneC }) };
        Assert.False(PcLayerPlanner.LooksPrepared(st, main));
        Assert.False(PcLayerPlanner.LooksPrepared(new PcLayerState(), main));
    }

    [Fact]
    public void State_normalize_and_json()
    {
        var st = new PcLayerState
        {
            Keys =
            {
                new() { Column = 0, Code = 0x69 }, new() { Column = 0, Code = 0x6A }, // duplicate column
                new() { Column = 1, Code = 0x69 },                                    // duplicate code
                new() { Column = 12, Code = 0x6B },                                   // joystick is never mapped
                new() { Column = 2, Code = 0x04 },                                    // not a pool code
                new() { Column = 3, Code = 0x6C }, null!,
            },
            PreparedAt = DateTime.Now,
        };
        var notes = st.Normalize();
        Assert.Equal(new[] { 0, 3 }, st.Keys.Select(k => k.Column));
        Assert.NotEmpty(notes);
        var d = new ProfileStoreData { PcLayer = st };
        var r = JsonSerializer.Deserialize<ProfileStoreData>(JsonSerializer.Serialize(d))!;
        Assert.True(r.PcLayer.Active);
        Assert.Equal(3, r.PcLayer.CodeColumn(0x6C));
        Assert.Equal(-1, r.PcLayer.CodeColumn(0x73));
        var old = JsonSerializer.Deserialize<ProfileStoreData>("{\"Profiles\":[]}")!;
        old.Normalize();
        Assert.False(old.PcLayer.Active);
        var bad = JsonSerializer.Deserialize<ProfileStoreData>("{\"Profiles\":[],\"PcLayer\":null}")!;
        bad.Normalize();
        Assert.NotNull(bad.PcLayer);
        var empty = new PcLayerState { PreparedAt = DateTime.Now };
        empty.Normalize();
        Assert.Null(empty.PreparedAt);
    }

    [Fact]
    public void Prepared_image_writes_and_reads_back_on_a_fake_keypad()
    {
        var mem = Img("exp15-postwrite-20261113-133715.bin").Take(0x1000).ToArray();
        var fake = new FakeK13(mem) { Writable = true };
        var dev = new K13Device(fake) { DelayMs = 0, LoadDelayMs = 0 };
        var (main, oneC) = UserProfiles();
        var st = new PcLayerState { Keys = PcLayerPlanner.Plan(new[] { main, oneC }) };
        var tmp = Path.Combine(Path.GetTempPath(), "k13-v56-" + Guid.NewGuid());
        try
        {
            K13WriteGate.Unlock(K13WriteGate.ConfirmationPhrase);
            var r = K13Writer.WriteProfile(dev, PcLayerPlanner.Apply(main, st), _ => { }, null, false, tmp);
            Assert.True(r.Ok, r.Message);
            var kp = K13MemoryCodec.Decode(fake.Memory);
            Assert.True(PcLayerPlanner.LooksPrepared(st, kp));
            Assert.Equal(0x69, kp.GetKey(st.Keys[0].Column));
        }
        finally { K13WriteGate.Lock(); dev.Dispose(); try { Directory.Delete(tmp, true); } catch { } }
    }
}

public class PcLayerRouterTests
{
    static byte[] Img(string f) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", f));
    static K13DeviceConfig Base() => K13MemoryCodec.Decode(Img("exp15-postwrite-20261113-133715.bin").Take(0x1000).ToArray());
    const int Tab = 9;
    const ushort F14 = 0x7D;

    static (PcLayerRouter r, K13DeviceConfig main, K13DeviceConfig oneC, ushort tabVk) Setup()
    {
        var main = Base();
        main.Macros[0] = new MacroSlot { Events = { new() { Down = true, Code = 0x04 }, new() { Down = false, Code = 0x04 } } };
        main.SetKey(Tab, KeyCatalog.FirstMacro);
        var oneC = Base(); oneC.SetKey(Tab, 0x2B);
        var st = new PcLayerState { Keys = PcLayerPlanner.Plan(new[] { main, oneC }) };
        var r = new PcLayerRouter(); r.SetLayer(st);
        ushort vk = KeyCatalog.ToVirtualKey(st.Keys.First(k => k.Column == Tab).Code);
        return (r, main, oneC, vk);
    }

    static readonly Func<ushort, bool> NoHost = _ => false;

    [Fact]
    public void Same_physical_key_does_what_the_active_profile_says()
    {
        var (r, main, oneC, vk) = Setup();
        var a = r.Route(vk, true, main, NoHost);
        Assert.Equal(PcOutKind.StartMacro, Assert.Single(a).Kind);
        Assert.Empty(r.Route(vk, false, main, NoHost));
        var b = r.Route(vk, true, oneC, NoHost);
        Assert.Equal(new PcOut(PcOutKind.KeyDown, 0x09, Code: 0x2B), Assert.Single(b));
        Assert.Equal(PcOutKind.KeyUp, Assert.Single(r.Route(vk, false, oneC, NoHost)).Kind);
    }

    [Fact]
    public void Holding_repeats_like_a_normal_key_and_a_macro_starts_once()
    {
        var (r, main, oneC, vk) = Setup();
        for (int i = 0; i < 5; i++) Assert.Equal(PcOutKind.KeyDown, Assert.Single(r.Route(vk, true, oneC, NoHost)).Kind);
        Assert.Equal(PcOutKind.KeyUp, Assert.Single(r.Route(vk, false, oneC, NoHost)).Kind);
        Assert.Single(r.Route(vk, true, main, NoHost));
        for (int i = 0; i < 5; i++) Assert.Empty(r.Route(vk, true, main, NoHost)); // auto-repeat does not restart the macro
        r.Route(vk, false, main, NoHost);
    }

    [Fact]
    public void Release_after_a_profile_switch_undoes_the_original_press()
    {
        var (r, main, oneC, vk) = Setup();
        r.Route(vk, true, oneC, NoHost);                 // Tab ↓ in «1С»
        var up = r.Route(vk, false, main, NoHost);       // profile switched to «Основной» while held
        Assert.Equal(new PcOut(PcOutKind.KeyUp, 0x09, Code: 0x2B), Assert.Single(up));
        Assert.Equal(0, r.HeldCount);
    }

    [Fact]
    public void While_held_macro_stops_on_release()
    {
        var (r, main, _, vk) = Setup();
        main.Macros[0].RepeatMode = MacroRepeatMode.WhileHeld;
        Assert.Equal(PcOutKind.StartMacroHeld, Assert.Single(r.Route(vk, true, main, NoHost)).Kind);
        Assert.Equal(PcOutKind.StopMacro, Assert.Single(r.Route(vk, false, main, NoHost)).Kind);
    }

    [Fact]
    public void Pc_action_triggers_go_to_the_host_mapper_with_tap_and_hold()
    {
        var (r, _, oneC, vk) = Setup();
        oneC.SetKey(Tab, 0x6A); // F15 — bound to a PC action in this profile
        Func<ushort, bool> host = v => v == 0x7E;
        Assert.Equal(new PcOut(PcOutKind.HostDown, 0x7E, Code: 0x6A), Assert.Single(r.Route(vk, true, oneC, host)));
        Assert.Equal(PcOutKind.HostDown, Assert.Single(r.Route(vk, true, oneC, host)).Kind); // repeat → host tracker ignores it
        Assert.Equal(PcOutKind.HostUp, Assert.Single(r.Route(vk, false, oneC, host)).Kind);
    }

    [Fact]
    public void Unmapped_keys_firmware_only_and_disabled()
    {
        var (r, main, oneC, vk) = Setup();
        Assert.False(r.Owns(0x41));
        Assert.Empty(r.Route(0x41, true, main, NoHost));
        Assert.False(r.Owns(0x7C)); // F13 = stick press stays with the ring
        oneC.SetKey(Tab, 0xE9);
        Assert.Equal(PcOutKind.FirmwareOnly, Assert.Single(r.Route(vk, true, oneC, NoHost)).Kind);
        Assert.Empty(r.Route(vk, false, oneC, NoHost));
        oneC.SetKey(Tab, 0xFF);
        Assert.Empty(r.Route(vk, true, oneC, NoHost));
        Assert.Empty(r.Route(vk, false, oneC, NoHost));
    }

    [Fact]
    public void Release_all_on_mode_off()
    {
        var (r, main, oneC, vk) = Setup();
        r.Route(vk, true, oneC, NoHost);
        var rel = r.ReleaseAll();
        Assert.Equal(PcOutKind.KeyUp, Assert.Single(rel).Kind);
        Assert.Equal(0, r.HeldCount);
        r.SetLayer(new PcLayerState());
        Assert.False(r.Active);
        Assert.Empty(r.Route(vk, true, oneC, NoHost));
    }
}
