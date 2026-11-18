using Vorotex.K13;
using Xunit;

namespace K13.Core.Tests;

/// <summary>1.3.1: «На клавиатуре — заводские назначения» is detected from the keypad memory (no separate profile any more).</summary>
public class V131Tests
{
    static byte[] Img(string f) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", f));
    static byte[] K13() => Img("exp15-postwrite-20261113-133715.bin").Take(0x1000).ToArray();

    static byte[] FactoryImage(byte[] from)
    {
        var plan = K13FactoryAssignments.Build(K13MemoryCodec.Decode(from), from);
        return K13MemoryCodec.Encode(plan.Target, from);
    }

    [Fact]
    public void Factory_write_result_is_recognised()
    {
        var img = FactoryImage(K13());
        Assert.True(K13FactoryAssignments.IsFactoryImage(img));
        Assert.True(K13FactoryAssignments.IsFactory(K13MemoryCodec.Decode(img)));
        Assert.Equal(K13().AsSpan(0, 8).ToArray(), img.AsSpan(0, 8).ToArray());   // 0x0000–0x0007 untouched
    }

    [Fact]
    public void Written_profile_is_not_factory()
    {
        Assert.False(K13FactoryAssignments.IsFactoryImage(K13()));   // the makers' unit after a profile write
    }

    [Fact]
    public void Any_single_difference_breaks_factory()
    {
        var f = K13MemoryCodec.Decode(FactoryImage(K13()));
        var a = f.Clone(); a.SetKey(3, 0x04); Assert.False(K13FactoryAssignments.IsFactory(a));
        var b = f.Clone(); b.WheelFirst = 2; Assert.False(K13FactoryAssignments.IsFactory(b));
        var c = f.Clone(); c.Fn[0] = 0x04; Assert.False(K13FactoryAssignments.IsFactory(c));
        var d = f.Clone(); d.Macros[0].Events.Add(new MacroEvent { Down = true }); Assert.False(K13FactoryAssignments.IsFactory(d));
        var e = f.Clone(); e.Led.Mode = (e.Led.Mode + 1) % 4; Assert.True(K13FactoryAssignments.IsFactory(e));   // lighting does not count
    }

    [Fact]
    public void K12_and_unread_are_never_factory()
    {
        Assert.False(K13FactoryAssignments.IsFactoryImage(null));
        Assert.False(K13FactoryAssignments.IsFactoryImage(Img("k12-prewrite-20261117-113402.bin")));
    }

    [Fact]
    public void Keeps_text_says_profiles_stay_on_PC()
    {
        var plan = K13FactoryAssignments.Build(K13MemoryCodec.Decode(K13()), K13());
        Assert.Contains(plan.Keeps, k => k.Contains("Применить профиль"));
        Assert.DoesNotContain(plan.Keeps, k => k.Contains("создаётся новый профиль"));
    }

    [Fact]
    public void Store_flag_round_trips_and_defaults_off()
    {
        Assert.False(new ProfileStoreData().KeypadFactory);
        var dir = Path.Combine(Path.GetTempPath(), "k13-v131-" + Guid.NewGuid().ToString("N"));
        var s = new ProfileStore(dir); s.Data.KeypadFactory = true; s.Save();
        var s2 = new ProfileStore(dir); s2.Load(); Assert.True(s2.Data.KeypadFactory);
    }
}
