using Xunit;

namespace Vorotex.K13.Tests;

/// <summary>1.0.2: profiles with different lighting — after a switch the keypad colour stayed the same.
/// Editing the lighting applied it live, a profile switch did not. Now a user switch uses the same LED-only path; auto never writes.</summary>
public class V102Tests
{

    static byte[] Raw() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", "exp15-postwrite-20261113-133715.bin")).Take(0x1000).ToArray();

    static (K13DeviceConfig A, K13DeviceConfig B) TwoProfiles()
    {
        var a = K13MemoryCodec.Decode(Raw());
        var b = a.Clone();
        b.Led.Colors[0] = new ColorSlot { R = a.Led.Colors[0].R == 15 ? 0 : 15, G = 0, B = 0, X = a.Led.Colors[0].X };
        b.Led.ColorIndex = 1;
        b.Led.Brightness = a.Led.Brightness == 0 ? 1 : 0;
        return (a, b);
    }

    [Fact]
    public void Lighting_only_difference_is_detected_after_switch()
    {
        var (a, b) = TwoProfiles();
        var kp = K13MemoryCodec.Encode(K13Mode.PrepareTarget(a, Raw()), Raw());   // the keypad holds profile A
        Assert.False(ProfileSwitchLighting.LightingDiffers(a, kp));
        Assert.False(ProfileSwitchLighting.LightingOnlyPending(a, kp));
        Assert.True(ProfileSwitchLighting.LightingOnlyPending(b, kp));
        Assert.True(ProfileSwitchLighting.LightingDiffers(b, kp));
        int bs = K13MemoryCodec.ReadBase(kp);
        Assert.All(PcLayerHealth.PendingWrite(b, kp), w => Assert.InRange(w.Address, bs, bs + ProfileSwitchLighting.LightingSpan - 1));
    }

    [Fact]
    public void Through_PC_layer_switch_differs_only_in_lighting()
    {
        var (a, b) = TwoProfiles();
        var st = PcLayerPlanner.NewState(new[] { a, b });
        var kp = K13MemoryCodec.Encode(K13Mode.PrepareTarget(PcLayerPlanner.Apply(a, st), Raw()), Raw());
        Assert.False(ProfileSwitchLighting.LightingDiffers(PcLayerPlanner.Apply(a, st), kp));
        Assert.True(ProfileSwitchLighting.LightingOnlyPending(PcLayerPlanner.Apply(b, st), kp));
    }

    [Fact]
    public void Key_and_lighting_difference_is_not_lighting_only()
    {
        var (a, b) = TwoProfiles();
        b.SetKey(0, b.GetKey(0) == 0x59 ? (byte)0x5A : (byte)0x59);
        var kp = K13MemoryCodec.Encode(K13Mode.PrepareTarget(a, Raw()), Raw());
        Assert.False(ProfileSwitchLighting.LightingOnlyPending(b, kp));
        Assert.True(ProfileSwitchLighting.LightingDiffers(b, kp));
    }

    [Fact]
    public void Unknown_keypad_image_never_triggers_a_write()
    {
        var (_, b) = TwoProfiles();
        Assert.False(ProfileSwitchLighting.LightingOnlyPending(b, null));
        Assert.False(ProfileSwitchLighting.LightingDiffers(b, null));
    }

    [Theory]
    [InlineData(SwitchSource.Manual, true)]
    [InlineData(SwitchSource.Tray, true)]
    [InlineData(SwitchSource.KeypadKey, true)]
    [InlineData(SwitchSource.Auto, false)]
    public void Only_user_switches_apply_lighting(SwitchSource s, bool live) => Assert.Equal(live, ProfileSwitchLighting.AppliesLive(s));

    [Fact]
    public void IsLightingOnly_bounds()
    {
        const int bs = 0xE0;
        Assert.False(ProfileSwitchLighting.IsLightingOnly(new List<WriteBlock>(), bs));
        Assert.True(ProfileSwitchLighting.IsLightingOnly(new[] { new WriteBlock(bs, new byte[8], ""), new WriteBlock(bs + 0x18, new byte[8], "") }, bs));
        Assert.False(ProfileSwitchLighting.IsLightingOnly(new[] { new WriteBlock(bs + 0x20, new byte[8], "") }, bs));
        Assert.False(ProfileSwitchLighting.IsLightingOnly(new[] { new WriteBlock(bs - 8, new byte[8], "") }, bs));
    }

    [Fact]
    public void Notes_are_honest()
    {
        Assert.Equal("", ProfileSwitchLighting.Note(false, false, false, true, SwitchSource.Manual, true));
        Assert.Contains("применяется", ProfileSwitchLighting.Note(true, true, true, true, SwitchSource.Manual, true));
        Assert.Contains("«Записать»", ProfileSwitchLighting.Note(true, true, false, true, SwitchSource.Auto, true));
        Assert.Contains("автопереключении", ProfileSwitchLighting.Note(true, true, false, true, SwitchSource.Auto, true));
        Assert.Contains("первого подтверждения", ProfileSwitchLighting.Note(true, true, false, false, SwitchSource.Manual, true));
        Assert.Contains("через ПК", ProfileSwitchLighting.Note(false, true, false, true, SwitchSource.Manual, true));
        Assert.Contains("Кнопки и подсветка", ProfileSwitchLighting.Note(false, true, false, true, SwitchSource.Manual, false));
        foreach (var n in new[] { ProfileSwitchLighting.Note(true, true, false, true, SwitchSource.Manual, true), ProfileSwitchLighting.Note(true, true, false, false, SwitchSource.Tray, false) })
            Assert.Contains("Подсветка в клавиатуре прежняя", n);
    }
}
