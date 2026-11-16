using System.Text.RegularExpressions;
using Xunit;

namespace Vorotex.K13.Tests;

/// <summary>v5.6.1: a ToolTip with StaysOpen=false assigned via the ToolTip property throws on real Windows
/// («По умолчанию свойство ToolTip не поддерживает элемент ToolTip, для которого StaysOpen=false»).</summary>
public class V561Tests
{
    static string StudioSrc()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src", "K13.Studio"))) d = d.Parent;
        Assert.NotNull(d);
        return Path.Combine(d!.FullName, "src", "K13.Studio");
    }

    [Fact]
    public void No_tooltip_sets_StaysOpen_false()
    {
        var bad = new List<string>();
        foreach (var f in Directory.EnumerateFiles(StudioSrc(), "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".cs") || f.EndsWith(".xaml")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var text = File.ReadAllText(f);
            // C#: new ToolTip { … StaysOpen = false … }   XAML: <ToolTip … StaysOpen="False" …>
            foreach (Match m in Regex.Matches(text, @"new\s+ToolTip\b[^;]*?StaysOpen\s*=\s*false|<ToolTip\b[^>]*StaysOpen\s*=\s*""False""|\bToolTip\s*\)\s*\.\s*StaysOpen\s*=\s*false", RegexOptions.IgnoreCase | RegexOptions.Singleline))
                bad.Add($"{Path.GetFileName(f)}: {m.Value[..Math.Min(80, m.Value.Length)]}");
        }
        Assert.Empty(bad);
    }

    [Fact]
    public void Guard_catches_the_v56_pattern()
    {
        const string v56 = "var tip = new ToolTip { Content = new TextBlock { Text = text }, StaysOpen = false };";
        Assert.Matches(new Regex(@"new\s+ToolTip\b[^;]*?StaysOpen\s*=\s*false", RegexOptions.Singleline), v56);
    }
}

/// <summary>v5.6.1: user's case — keypad prepared (Tab → F23), Tab pressed in «1С», its release never reached the hook,
/// then Tab in Word («Основной», Tab = macro) still typed Tab.</summary>
public class V561RouterTests
{
    static byte[] Img(string f) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", f));
    static K13DeviceConfig Base() => K13MemoryCodec.Decode(Img("exp15-postwrite-20261113-133715.bin").Take(0x1000).ToArray());
    static readonly Func<ushort, bool> NoHost = _ => false;

    static (PcLayerRouter r, K13DeviceConfig main, K13DeviceConfig oneC, ushort vk) Setup()
    {
        var main = Base();
        main.Macros[0] = new MacroSlot { Events = { new() { Down = true, Code = 0x1E }, new() { Down = false, Code = 0x1E } } };
        main.SetKey(9, KeyCatalog.FirstMacro);
        var oneC = Base(); oneC.SetKey(9, 0x2B);
        var st = new PcLayerState { Keys = PcLayerPlanner.Plan(new[] { main, oneC }) };
        var r = new PcLayerRouter(); r.SetLayer(st);
        return (r, main, oneC, KeyCatalog.ToVirtualKey(st.Keys.First(k => k.Column == 9).Code));
    }

    [Fact]
    public void Lost_release_does_not_replay_the_old_profile_action()
    {
        var (r, main, oneC, vk) = Setup();
        Assert.Equal(PcOutKind.KeyDown, Assert.Single(r.Route(vk, true, oneC, NoHost, 1000)).Kind);   // Tab in 1С; release lost
        var o = r.Route(vk, true, main, NoHost, 12_000);                                                 // later, in Word
        Assert.Equal(new[] { PcOutKind.KeyUp, PcOutKind.StartMacro }, o.Select(x => x.Kind));           // finish old Tab, run the macro
        Assert.Equal(0, o.Last().Slot);
        Assert.Empty(r.Route(vk, false, main, NoHost, 12_100));
        Assert.Equal(0, r.HeldCount);
    }

    [Fact]
    public void Auto_repeat_is_not_mistaken_for_a_new_press()
    {
        var (r, main, oneC, vk) = Setup();
        r.Route(vk, true, oneC, NoHost, 0);
        for (long t = 500; t <= 3000; t += 33) Assert.Equal(PcOutKind.KeyDown, Assert.Single(r.Route(vk, true, main, NoHost, t)).Kind);
        Assert.Equal(PcOutKind.KeyUp, Assert.Single(r.Route(vk, false, main, NoHost, 3010)).Kind);
    }

    [Fact]
    public void Users_saved_setup_tab_is_routed_and_resolves_per_profile()
    {
        // what DM-Shvyrkov's keypad holds after «Подготовить»: keys 0–10 → F14–F24, Tab (col 9) → F23
        var (_, main, oneC, _) = Setup();
        var st = new PcLayerState { Keys = PcLayerPlanner.Plan(new[] { main, oneC }) };
        Assert.True(st.IsMapped(9));
        Assert.Equal(PcKeyKind.Macro, PcLayerPlanner.Resolve(main, 9).Kind);
        Assert.Equal(PcKeyKind.Key, PcLayerPlanner.Resolve(oneC, 9).Kind);
    }
}

/// <summary>v5.6.2 — from DM-Shvyrkov's log 12:18–12:20: «через ПК» on (11 keys), profiles 1С ↔ Основной switching,
/// the only key event the hook saw was VK 0x09 (Tab) and not one F14–F24 code — nothing to translate, so the macro never ran.
/// Then a macro was put on key 8 and «Записать» stayed grey with no explanation.</summary>
public class V562Tests
{
    static byte[] Img(string f) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", f));
    static byte[] Raw() => Img("exp15-postwrite-20261113-133715.bin").Take(0x1000).ToArray();
    static K13DeviceConfig Base() => K13MemoryCodec.Decode(Raw());

    static (PcLayerState st, K13DeviceConfig main, K13DeviceConfig oneC, byte[] keypad) UserSetup()
    {
        var main = Base();
        main.Macros[0] = new MacroSlot { Events = { new() { Down = true, Code = 0x1E }, new() { Down = true, Code = 0x1F }, new() { Down = true, Code = 0x20 },
                                                    new() { Down = false, Code = 0x1E }, new() { Down = false, Code = 0x1F }, new() { Down = false, Code = 0x20 } } };
        main.SetKey(9, KeyCatalog.FirstMacro);
        var oneC = Base(); oneC.SetKey(9, 0x2B);
        var st = new PcLayerState { Keys = PcLayerPlanner.Plan(new[] { main, oneC }) };
        // what «Подготовить» wrote (written while «1С» was active — like the user's 11:41 write)
        var keypad = K13MemoryCodec.Encode(PcLayerPlanner.Apply(oneC, st), Raw());
        return (st, main, oneC, keypad);
    }

    [Fact]
    public void Keypad_sending_plain_Tab_means_the_prepared_codes_do_not_arrive()
    {
        var (st, _, _, keypad) = UserSetup();
        var kp = K13MemoryCodec.Decode(keypad);
        Assert.Equal(PcHealth.Unexpected, PcLayerHealth.Check(st, kp, 0x09));                 // 12:18:25 «VK 0x09»
        Assert.Equal(PcHealth.Routed, PcLayerHealth.Check(st, kp, KeyCatalog.ToVirtualKey(st.Keys.First(k => k.Column == 9).Code)));
        Assert.Equal(PcHealth.Native, PcLayerHealth.Check(st, kp, 0x7C));                    // F13 — stick / ring
        Assert.Equal(PcHealth.Native, PcLayerHealth.Check(st, kp, KeyCatalog.ToVirtualKey(kp.GetKey(12)))); // joystick stays in the keypad
    }

    [Fact]
    public void Macro_on_key_8_in_pc_mode_needs_no_write()
    {
        var (st, main, _, keypad) = UserSetup();
        main.SetKey(7, KeyCatalog.FirstMacro);                     // «кнопка 8: Макрос 0» at 12:19:49
        Assert.Empty(PcLayerHealth.PendingWrite(PcLayerPlanner.Apply(main, st), keypad));
        // classic mode: the same edit DOES need a write
        Assert.NotEmpty(PcLayerHealth.PendingWrite(main, keypad));
    }

    [Fact]
    public void Lighting_change_in_pc_mode_still_needs_a_write()
    {
        var (st, main, _, keypad) = UserSetup();
        main.Led.Speed = (main.Led.Speed + 1) % 7;
        var pending = PcLayerHealth.PendingWrite(PcLayerPlanner.Apply(main, st), keypad);
        Assert.NotEmpty(pending);
        Assert.DoesNotContain(pending, b => b.Address >= 0x128 && b.Address < 0x1C8);         // keymap untouched
    }

    [Fact]
    public void Keypad_monitor_matches_only_the_K13()
    {
        Assert.True(PcLayerHealth.IsK13Path(@"\\?\HID#VID_30FA&PID_2350&MI_01&Col05#8&71661ae&0&0004#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}"));
        Assert.False(PcLayerHealth.IsK13Path(@"\\?\HID#VID_046D&PID_C31C&MI_00#7&1a2b#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}"));
    }
}

