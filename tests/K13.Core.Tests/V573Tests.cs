using System.Text.Json;
using Vorotex.K13;
using Xunit;

namespace K13.Core.Tests;

/// <summary>v5.7.3: VOROTEX report 16.11.2026 (DA 1E unit) — persistence, ring arrows, read display, diagnostics.</summary>
public class V573Tests
{
    static byte[] Img() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", "exp15-prewrite-20261113-133452.bin")).Take(0x300).ToArray();

    static K13DeviceConfig Blank()
    {
        var c = K13MemoryCodec.Decode(Img());
        for (int i = 0; i < c.Keymap.Length; i++) c.Keymap[i] = KeyCatalog.Disabled;
        foreach (var m in c.Macros) m.Events.Clear();
        return c;
    }

    [Fact]
    public void EncodeAlwaysWritesVendorMarker()
    {
        var img = Img(); img[0xD0] = 0xDA;
        var cfg = K13MemoryCodec.Decode(img);
        var outImg = K13MemoryCodec.Encode(cfg, img, out var plan);
        Assert.Equal(new byte[] { 0xE1, 0x1E }, outImg.AsSpan(0xD0, 2).ToArray());
        var diff = K13Protocol.DiffPlan(plan, img);
        Assert.Equal(0xD0, diff[0].Address);                       // header goes first, as in the vendor Apply
        Assert.Equal(HeaderVerdict.Exact, K13Header.Inspect(outImg, 0xE0).Verdict);
    }

    [Fact]
    public void ExactUnitHeaderIsNotRewritten()
    {
        var img = Img();
        var cfg = K13MemoryCodec.Decode(img);
        cfg.SetKey(3, 0x3A);
        K13MemoryCodec.Encode(cfg, img, out var plan);
        Assert.DoesNotContain(K13Protocol.DiffPlan(plan, img), b => b.Address == 0xD0);
    }

    [Fact]
    public void RingAcceptsWasdAndArrows()
    {
        var kp = Blank();
        kp.SetKey(RingStick.ColUp, 0x1A); kp.SetKey(RingStick.ColDown, 0x16); kp.SetKey(RingStick.ColLeft, 0x04); kp.SetKey(RingStick.ColRight, 0x07); // W S A D
        var k = RingStick.Keys(kp, null);
        Assert.Equal(new ushort[] { 0x57, RingStick.VkUp }, k.Up);
        Assert.Equal(new ushort[] { 0x53, RingStick.VkDown }, k.Down);
        Assert.Equal(new ushort[] { 0x41, RingStick.VkLeft }, k.Left);
        Assert.Equal(new ushort[] { 0x44, RingStick.VkRight }, k.Right);
    }

    [Fact]
    public void RingFactoryArrowsAndNoSharedKeys()
    {
        var kp = Blank(); // factory: the stick sends arrows
        kp.SetKey(RingStick.ColUp, 0x52); kp.SetKey(RingStick.ColDown, 0x51); kp.SetKey(RingStick.ColLeft, 0x50); kp.SetKey(RingStick.ColRight, 0x4F);
        var prof = Blank();
        prof.SetKey(RingStick.ColUp, 0x1A); prof.SetKey(RingStick.ColDown, 0x1A); // W twice → only «up»
        var k = RingStick.Keys(kp, prof);
        Assert.Equal(new ushort[] { RingStick.VkUp, 0x57 }, k.Up);
        Assert.Equal(new ushort[] { RingStick.VkDown }, k.Down);
        Assert.Equal(new ushort[] { RingStick.VkLeft }, k.Left);
        Assert.Equal(new ushort[] { RingStick.VkRight }, k.Right);
        var all = k.Up.Concat(k.Down).Concat(k.Left).Concat(k.Right).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.DoesNotContain((ushort)0x1B, all); Assert.DoesNotContain((ushort)0x0D, all);
    }

    [Fact]
    public void ReadReplaceShowsReadKeymapAndDropsStaleMacroNames()
    {
        var p = new AppProfile { Name = "Тест", Device = Blank() };
        p.Device.SetKey(0, 0xC9); // «Пауза» (media) written earlier
        p.Device.Macros[0].Events.Add(new MacroEvent { Down = true, Code = 0x04 });
        p.Device.Macros[1].Events.Add(new MacroEvent { Down = true, Code = 0x05 });
        p.MacroNames[0] = "Старый"; p.MacroNames[1] = "Тот же";
        var read = Blank();
        read.SetKey(0, 0x59); // keypad holds Num 1
        read.Macros[1].Events.Add(new MacroEvent { Down = true, Code = 0x05 });
        ProfileMigrations.ReplaceDevice(p, read);
        Assert.Same(read, p.Device);
        Assert.Equal(0x59, p.Device!.GetKey(0));
        Assert.False(p.MacroNames.ContainsKey(0));
        Assert.Equal("Тот же", p.MacroNames[1]);
    }

    [Fact]
    public void ForeignKeyMeansFactoryLayout()
    {
        var mem = Blank();
        mem.SetKey(0, 0xC9); mem.SetKey(1, 0xCC); mem.SetKey(2, 0xCD); mem.SetKey(4, 0x3E); // media ×3, F5
        mem.Macros[0].Events.Add(new MacroEvent { Down = true, Code = 0x04 });
        Assert.False(KeypadOutputCheck.Explains(mem, 0x61));           // Num 1
        Assert.False(KeypadOutputCheck.Explains(mem, 0x23));           // Num 1 with Num Lock off
        Assert.True(KeypadOutputCheck.Explains(mem, 0x74));            // F5
        Assert.True(KeypadOutputCheck.Explains(mem, 0x41));            // A from the macro
        Assert.True(KeypadOutputCheck.Explains(mem, 0xA0));            // modifiers are always fine
        mem.SetKey(5, 0x59);
        Assert.True(KeypadOutputCheck.Explains(mem, 0x23));
    }

    [Fact]
    public void ReplugCheckComparesWrittenBlocks()
    {
        var img = Img(); img[0xD0] = 0xDA;
        var cfg = K13MemoryCodec.Decode(img);
        cfg.SetKey(0, 0x3A);
        var after = K13MemoryCodec.Encode(cfg, img, out var plan);
        var check = PendingReplugCheck.From(K13Protocol.DiffPlan(plan, img), after, "post.bin");
        Assert.Contains(0xD0, check.Blocks.Keys);
        Assert.Empty(check.Differences(after));
        Assert.NotEmpty(check.Differences(img));                       // factory/old memory after replug
        var json = JsonSerializer.Serialize(new ProfileStoreData { ReplugCheck = check });
        var back = JsonSerializer.Deserialize<ProfileStoreData>(json)!;
        Assert.Empty(back.ReplugCheck!.Differences(after));
        Assert.Null(JsonSerializer.Deserialize<ProfileStoreData>("{\"ActiveProfile\":\"x\"}")!.ReplugCheck);
    }

    [Fact]
    public void HidLogNamesFrames()
    {
        Assert.Contains("07 18 11", K13Protocol.Describe(new byte[] { 7, 0x18, 0x11, 0, 0, 0, 0, 0 }));
        Assert.Contains("0x00D0", K13Protocol.Describe(new byte[] { 7, 0x18, 0x09, 0, 0xD0, 0, 0, 7 }));
        var dir = Path.Combine(Path.GetTempPath(), "k13hid-" + Guid.NewGuid().ToString("N"));
        var f = BackupStore.SaveHidLog(new[] { (5L, new byte[] { 7, 0x18, 0x09, 0, 0xD0, 0, 0, 7 }) }, "тест", dir);
        var text = File.ReadAllText(f);
        Assert.Contains("07 18 09 00 D0 00 00 07", text.Replace("-", " "));
        Directory.Delete(dir, true);
    }
}
