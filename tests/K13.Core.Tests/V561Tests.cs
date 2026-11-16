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
