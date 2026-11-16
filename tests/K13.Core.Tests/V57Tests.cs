using System.Text.Json.Nodes;
using Vorotex.K13;
using Xunit;

namespace K13.Core.Tests;

/// <summary>v5.7 (calmer UI): the only stored change is the «Технические подробности» switch — older profiles must load unchanged.</summary>
public class V57Tests
{
    static string TempDir() => Path.Combine(Path.GetTempPath(), "k13v57-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Profiles_from_v5_6_load_unchanged_and_tech_details_default_off()
    {
        var dir = TempDir();
        try
        {
            var st = new ProfileStore(dir);
            st.Data.Profiles.Add(new AppProfile { Name = "Основной" });
            st.Data.Profiles.Add(new AppProfile { Name = "1С" });
            st.Data.ActiveProfile = "1С";
            st.Data.FallbackProfile = "Основной";
            st.Data.AppRules.Add(new AppSwitchRule { ProfileName = "1С", ProcessName = "1cv8c.exe" });
            st.Data.PcLayer = new PcLayerState { Scheme = PcLayerState.ChordScheme, Keys = { new PcLayerKey { Column = 9, Code = 0x43 } } };
            st.Data.AcceptedHeader = "DA 1E E0 00 F0 0F 00 00";
            st.Save();
            // a v5.6.x file has no «ShowTechDetails»
            var json = JsonNode.Parse(File.ReadAllText(st.FilePath))!.AsObject();
            Assert.True(json.Remove("ShowTechDetails"));
            File.WriteAllText(st.FilePath, json.ToJsonString());
            var before = json.ToJsonString();

            var again = new ProfileStore(dir); again.Load();
            Assert.False(again.Data.ShowTechDetails);
            Assert.Equal("1С", again.Data.ActiveProfile);
            Assert.Equal("Основной", again.Data.FallbackProfile);
            Assert.Single(again.Data.AppRules);
            Assert.True(again.Data.PcLayer.Active);
            Assert.Equal(PcLayerState.ChordScheme, again.Data.PcLayer.Scheme);
            Assert.Equal(0x43, again.Data.PcLayer.Keys.Single().Code);
            Assert.Equal("DA 1E E0 00 F0 0F 00 00", again.Data.AcceptedHeader);
            Assert.Equal(before, File.ReadAllText(st.FilePath)); // loading does not rewrite the file

            again.Data.ShowTechDetails = true; again.Save();
            var third = new ProfileStore(dir); third.Load();
            Assert.True(third.Data.ShowTechDetails);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
