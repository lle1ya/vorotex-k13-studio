using Vorotex.K13;
using Xunit;

namespace K13.Core.Tests;

/// <summary>v5.8.1: no false «factory keys» warning from a stale image; the ring on the stick always takes F13.</summary>
public class V581Tests
{
    static byte[] Img() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", "exp15-prewrite-20261113-133452.bin"));
    const ushort F6 = 0x75, Num4 = 0x64, Left = 0x25;

    static K13DeviceConfig WithF6OnKey4()
    {
        var c = K13MemoryCodec.Decode(Img());
        c.SetKey(3, 0x3F);                                   // key 4 = F6 (vendor app)
        return c;
    }

    [Fact]
    public void KeyInFreshGameKeymapIsNeverFactory()
    {
        var mem = WithF6OnKey4();
        foreach (var m in new[] { HwMode.Unknown, HwMode.Normal, HwMode.Game })
            Assert.Equal(KeyVerdict.Expected, KeypadOutputCheck.Judge(mem, m, F6));
    }

    [Fact]
    public void StaleOrMissingImageNeverWarnsForeign()
    {
        // makers: cache predates F6 → before v5.8.1 «всё ещё присылает F6 — заводская раскладка»
        Assert.Equal(KeyVerdict.Unknown, KeypadOutputCheck.Judge(null, HwMode.Unknown, F6));
        Assert.Equal(KeyVerdict.Unknown, KeypadOutputCheck.Judge(null, HwMode.Game, F6));
        Assert.Equal(KeyVerdict.Unknown, KeypadOutputCheck.Judge(null, HwMode.Normal, F6));   // F6 is not a factory key
    }

    [Fact]
    public void FactoryKeyInNormalModeIsNormalMode()
    {
        var mem = WithF6OnKey4();
        for (int c = 0; c < K13Layout.KeymapColumns; c++) mem.SetKey(c, 0x3F);   // all F6: no factory key in the Game keymap
        Assert.True(KeypadOutputCheck.IsFactoryKey(Num4));
        Assert.True(KeypadOutputCheck.IsFactoryKey(Left));    // Num 4 with Num Lock off
        Assert.False(KeypadOutputCheck.IsFactoryKey(F6));
        Assert.Equal(KeyVerdict.NormalMode, KeypadOutputCheck.Judge(null, HwMode.Normal, Num4));
        Assert.Equal(KeyVerdict.NormalMode, KeypadOutputCheck.Judge(mem, HwMode.Unknown, Num4));
        Assert.Equal(KeyVerdict.NotInMemory, KeypadOutputCheck.Judge(mem, HwMode.Game, 0x7B)); // F12 in Game, not in memory → log only
    }

    [Fact]
    public void ModifiersAndStickAreAlwaysExpected()
    {
        Assert.Equal(KeyVerdict.Expected, KeypadOutputCheck.Judge(null, HwMode.Normal, 0x10));
        Assert.Equal(KeyVerdict.Expected, KeypadOutputCheck.Judge(null, HwMode.Game, ProfileMigrations.StickVk));
    }

    [Fact]
    public void RingOnStickTakesF13EvenWhenRingBindingWasPrepared()
    {
        var img = Img(); img[0x1A8] = 0xF4;                    // vendor read: stick = KBD Mode
        var p = new AppProfile { Name = "p", Device = K13MemoryCodec.Decode(img) };
        ProfileMigrations.EnsureStickRing(p, setStickKey: false); // F13 → Ring binding exists (made F13 look «busy» → F14 before)
        Assert.Equal(0x68, ProfileMigrations.RingTrigger(p.Device!, p.Host, 16));
        Assert.Equal("F13", KeyCatalog.Name(0x68));
    }

    [Fact]
    public void RingTriggerSkipsF13UsedByAnotherKeyOrAction()
    {
        var p = new AppProfile { Name = "p", Device = K13MemoryCodec.Decode(Img()) };
        p.Device!.SetKey(0, 0x68);                             // key 1 already sends F13
        Assert.Equal(0x69, ProfileMigrations.RingTrigger(p.Device, p.Host, 16));
        p.Device.SetKey(0, 0x59);
        p.Host.Keys.Add(new HostKeyBinding { TriggerVk = 0x7C, Tap = new HostAction { Kind = HostActionKind.LaunchProgram, Value = "calc.exe" } });
        Assert.Equal(0x69, ProfileMigrations.RingTrigger(p.Device, p.Host, 16));
        Assert.Equal(0x68, ProfileMigrations.RingTrigger(p.Device, new HostSettings(), 16));
    }
}
