using Xunit;

namespace Vorotex.K13.Tests;

/// <summary>1.1.1: user log 18.11.2026 13:13–13:15 — after the wheel was changed in one profile (13:02) every switch said «отличаются и
/// другие блоки» and the lighting waited for «Записать». The live path now writes the lighting area alone; the rest waits for «Записать».</summary>
public class V111Tests
{
    static byte[] Raw() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", "exp15-postwrite-20261113-133715.bin")).Take(0x1000).ToArray();

    static (K13DeviceConfig A, K13DeviceConfig B, byte[] Kp) WheelAndLightingDiffer()
    {
        var a = K13MemoryCodec.Decode(Raw());
        var b = a.Clone();
        b.Led.Colors[0] = new ColorSlot { R = a.Led.Colors[0].R == 15 ? 0 : 15, G = 0, B = 0, X = a.Led.Colors[0].X };
        b.Led.Brightness = a.Led.Brightness == 0 ? 1 : 0;
        b.WheelFirst = (a.WheelFirst + 1) % 4;
        var kp = K13MemoryCodec.Encode(K13Mode.PrepareTarget(a, Raw()), Raw());   // the keypad holds profile A
        return (a, b, kp);
    }

    [Fact]
    public void Wheel_and_lighting_differ_like_in_the_users_log()
    {
        var (_, b, kp) = WheelAndLightingDiffer();
        Assert.False(ProfileSwitchLighting.LightingOnlyPending(b, kp));      // the old gate refused here
        Assert.True(ProfileSwitchLighting.LightingDiffers(b, kp));
        int bs = K13MemoryCodec.ReadBase(kp);
        Assert.Contains(PcLayerHealth.PendingWrite(b, kp), w => w.Address >= bs + ProfileSwitchLighting.LightingSpan);
    }

    [Fact]
    public void Lighting_part_writes_only_the_lighting_area()
    {
        var (_, b, kp) = WheelAndLightingDiffer();
        var part = ProfileSwitchLighting.LightingPart(b, kp);
        Assert.NotNull(part);
        int bs = K13MemoryCodec.ReadBase(kp);
        var diff = PcLayerHealth.PendingWrite(part!, kp);
        Assert.NotEmpty(diff);
        Assert.All(diff, w => Assert.InRange(w.Address, bs, bs + ProfileSwitchLighting.LightingSpan - 1));
        Assert.Equal(b.Led.Colors[0].R, part!.Led.Colors[0].R);
        Assert.Equal(b.Led.Brightness, part.Led.Brightness);
        Assert.Equal(K13MemoryCodec.Decode(kp).WheelFirst, part.WheelFirst);    // the keypad's wheel stays
        // after that write only the wheel is left for «Записать»
        var after = K13MemoryCodec.Encode(part, kp);
        Assert.False(ProfileSwitchLighting.LightingDiffers(b, after));
        Assert.NotEmpty(PcLayerHealth.PendingWrite(b, after));
    }

    [Fact]
    public void Lighting_part_is_null_when_nothing_to_do_or_image_unknown()
    {
        var (a, b, kp) = WheelAndLightingDiffer();
        Assert.Null(ProfileSwitchLighting.LightingPart(b, null));
        var same = ProfileSwitchLighting.LightingPart(a, kp);
        Assert.True(same == null || PcLayerHealth.PendingWrite(same, kp).Count == 0);
    }

    [Fact]
    public void Keys_and_lighting_differ_through_PC_layer_still_apply_lighting()
    {
        var (a, b, _) = WheelAndLightingDiffer();
        b.SetKey(0, b.GetKey(0) == 0x59 ? (byte)0x5A : (byte)0x59);
        var kp = K13MemoryCodec.Encode(K13Mode.PrepareTarget(a, Raw()), Raw());
        var part = ProfileSwitchLighting.LightingPart(b, kp);
        Assert.NotNull(part);
        Assert.Equal(K13MemoryCodec.Decode(kp).GetKey(0), part!.GetKey(0));
    }

    [Fact]
    public void Note_says_lighting_applies_and_the_rest_needs_write()
    {
        var n = ProfileSwitchLighting.Note(false, true, true, true, SwitchSource.Manual, false);
        Assert.Contains("применяется", n);
        Assert.Contains("колесо", n);
        Assert.Contains("«Записать»", n);
        Assert.DoesNotContain("колесо", ProfileSwitchLighting.Note(true, true, true, true, SwitchSource.Manual, false));
    }

    [Fact]
    public void Installer_reuses_the_previous_folder_on_update()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "installer", "VorotexK13Studio.iss"))) d = d.Parent;
        var iss = File.ReadAllText(Path.Combine(d!.FullName, "installer", "VorotexK13Studio.iss"));
        Assert.Contains("UsePreviousAppDir=yes", iss);
        Assert.Contains("UsePreviousTasks=yes", iss);
    }
}
