using System.Text.Json;
using Vorotex.K13;
using Xunit;

namespace K13.Core.Tests;

public class MigrationTests
{
    static byte[] Img(string f) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", f));

    [Fact]
    public void Exp15_StickPressColumn16_ChangedFromKbdModeToF13()
    {
        var pre = K13MemoryCodec.Decode(Img("exp15-prewrite-20261113-133452.bin"));
        var post = K13MemoryCodec.Decode(Img("exp15-postwrite-20261113-133715.bin"));
        Assert.Equal(0xF4, pre.GetKey(16));
        Assert.Equal(0x68, post.GetKey(16));
        var a = Img("exp15-prewrite-20261113-133452.bin"); var b = Img("exp15-postwrite-20261113-133715.bin");
        Assert.Equal(new[] { 0x01A8 }, Enumerable.Range(0, a.Length).Where(i => a[i] != b[i]).ToArray());
        Assert.Equal(0x7C, KeyCatalog.ToVirtualKey(0x68));
    }

    [Fact]
    public void Migrate_OldProfile_GetsF13Ring_AndStickCode()
    {
        var p = new AppProfile { Name = "Основной", Device = K13MemoryCodec.Decode(Img("exp15-prewrite-20261113-133452.bin")) };
        p.Host.Ring.TapThresholdMs = 300;
        var data = new ProfileStoreData { Profiles = { p } };
        var notes = ProfileMigrations.Migrate(data);
        Assert.Single(notes);
        Assert.Equal(ProfileMigrations.CurrentVersion, data.SchemaVersion);
        Assert.Equal(0x68, p.Device!.GetKey(16));
        var k = Assert.Single(p.Host.Keys, x => x.TriggerVk == 0x7C);
        Assert.Equal(HostActionKind.Ring, k.Tap.Kind);
        Assert.True(k.Suppress);
        Assert.Equal(250, p.Host.Ring.TapThresholdMs);
        // second run: nothing changes
        Assert.Empty(ProfileMigrations.Migrate(data));
        Assert.Single(p.Host.Keys, x => x.TriggerVk == 0x7C);
    }

    [Fact]
    public void Migrate_KeepsExistingF13Binding()
    {
        var p = new AppProfile { Device = K13MemoryCodec.Decode(Img("exp15-postwrite-20261113-133715.bin")) };
        p.Host.Keys.Add(new HostKeyBinding { TriggerVk = 0x7C, Tap = new HostAction { Kind = HostActionKind.KeyCombo, Value = "Ctrl+C" } });
        ProfileMigrations.Migrate(new ProfileStoreData { Profiles = { p } });
        var k = Assert.Single(p.Host.Keys);
        Assert.Equal(HostActionKind.KeyCombo, k.Tap.Kind);
    }

    [Fact]
    public void Migrate_JsonRoundtrip_FromV4File()
    {
        // a v4 profiles.json has no SchemaVersion / StartWithWindows
        var json = "{\"Profiles\":[{\"Name\":\"A\",\"Host\":{\"Enabled\":true,\"Keys\":[]}}],\"ActiveProfile\":\"A\",\"MinimizeToTray\":true}";
        var data = JsonSerializer.Deserialize<ProfileStoreData>(json, ProfileStore.JsonOptions)!;
        Assert.Equal(0, data.SchemaVersion);
        Assert.False(data.StartWithWindows);
        ProfileMigrations.Migrate(data);
        Assert.Contains(data.Profiles[0].Host.Keys, k => k.TriggerVk == 0x7C && k.Tap.Kind == HostActionKind.Ring);
    }
}
