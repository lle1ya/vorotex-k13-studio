using System.Text.Json;
using System.Text.Json.Nodes;
using Vorotex.K13;
using Xunit;

namespace K13.Core.Tests;

public class AutoSwitchTests
{
    static ProfileStoreData Data(params (string proc, string title, string profile)[] rules)
    {
        var d = new ProfileStoreData();
        foreach (var n in new[] { "Основной", "Фото", "Игры", "Браузер" }) d.Profiles.Add(new AppProfile { Name = n });
        foreach (var (p, t, pr) in rules) d.AppRules.Add(new AppSwitchRule { ProcessName = p, TitleContains = t, ProfileName = pr });
        return d;
    }

    [Theory]
    [InlineData(@"C:\Program Files\Adobe\Photoshop.EXE", "Photoshop")]
    [InlineData("  \"photoshop.exe\" ", "photoshop")]
    [InlineData("cs2", "cs2")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NormalizeProcess(string? input, string expected) => Assert.Equal(expected, AppMatch.NormalizeProcess(input));

    [Fact]
    public void Match_IsCaseInsensitive_AndTitleIsSubstring()
    {
        var r = new AppSwitchRule { ProcessName = "Photoshop.exe", TitleContains = "макет", ProfileName = "Фото" };
        Assert.True(AppMatch.Matches(r, "photoshop", "Новый МАКЕТ.psd @ 100%"));
        Assert.False(AppMatch.Matches(r, "photoshop", "Безымянный-1"));
        Assert.False(AppMatch.Matches(r, "illustrator", "макет"));
        Assert.False(AppMatch.Matches(new AppSwitchRule { ProfileName = "Фото" }, "x", "y"));   // empty rule never matches everything
    }

    [Fact]
    public void Find_MoreSpecificRuleWins_AndDeletedProfilesIgnored()
    {
        var d = Data(("chrome", "", "Браузер"), ("chrome", "YouTube", "Игры"), ("chrome", "Figma", "Удалённый"));
        Assert.Equal("Игры", AppMatch.Find(d.AppRules, d.Profiles, "chrome", "Видео - YouTube")!.ProfileName);
        Assert.Equal("Браузер", AppMatch.Find(d.AppRules, d.Profiles, "chrome", "Figma — макет")!.ProfileName);
    }

    [Fact]
    public void Engine_Debounces_AndDecidesOncePerWindow()
    {
        var d = Data(("photoshop", "", "Фото"));
        var e = new AutoSwitchEngine { DebounceMs = 300 };
        Assert.Null(e.Observe("photoshop", "a.psd", 0, d, "Основной"));
        Assert.Null(e.Observe("photoshop", "a.psd", 200, d, "Основной"));       // not stable yet
        var dec = e.Observe("photoshop", "a.psd", 320, d, "Основной");
        Assert.NotNull(dec);
        Assert.Equal("Фото", dec!.Profile);
        Assert.Contains("photoshop.exe", dec.Reason);
        Assert.Null(e.Observe("photoshop", "a.psd", 900, d, "Фото"));            // same window: no repeat (a manual pick stays)
    }

    [Fact]
    public void Engine_AltTabFlicker_NoSwitchForShortVisits()
    {
        var d = Data(("photoshop", "", "Фото"));
        d.FallbackProfile = "Основной";
        var e = new AutoSwitchEngine { DebounceMs = 300 };
        e.Observe("photoshop", "a", 0, d, "Основной");
        Assert.NotNull(e.Observe("photoshop", "a", 400, d, "Основной"));
        long t = 500;
        foreach (var w in new[] { "notepad", "chrome", "telegram" }) { Assert.Null(e.Observe(w, "x", t, d, "Фото")); t += 120; }
        Assert.Null(e.Observe("photoshop", "a", t, d, "Фото"));
        Assert.Null(e.Observe("photoshop", "a", t + 400, d, "Фото"));       // back where we were: already decided
    }

    [Fact]
    public void Engine_IgnoresOwnWindow_TaskbarAndShell()
    {
        var d = Data(("photoshop", "", "Фото")); d.FallbackProfile = "Основной";
        var e = new AutoSwitchEngine { DebounceMs = 0 };
        foreach (var (p, t) in new[] { ("VorotexK13Studio", "VOROTEX K13 Studio"), ("explorer", ""), ("ShellExperienceHost", "Пуск"), ("", "") })
        {
            Assert.Null(e.Observe(p, t, 0, d, "Фото"));
            Assert.Null(e.Observe(p, t, 1000, d, "Фото"));
        }
    }

    [Fact]
    public void Engine_Fallback_OnlyWhenRulesExist_AndNotWhenAccessDenied()
    {
        var d = Data(("photoshop", "", "Фото")); d.FallbackProfile = "Основной";
        var e = new AutoSwitchEngine { DebounceMs = 0 };
        e.Observe("notepad", "Блокнот", 0, d, "Фото");
        var dec = e.Observe("notepad", "Блокнот", 1, d, "Фото");
        Assert.True(dec!.IsFallback);
        Assert.Equal("Основной", dec.Profile);
        // elevated/protected process: name unknown → keep current profile
        e.Observe("", "Anti-cheat game", 10, d, "Фото");
        Assert.Null(e.Observe("", "Anti-cheat game", 20, d, "Фото"));
        // no fallback set → keep current
        d.FallbackProfile = "";
        e.Observe("calc", "Калькулятор", 30, d, "Фото");
        Assert.Null(e.Observe("calc", "Калькулятор", 40, d, "Фото"));
        // fallback without any rules does nothing
        var empty = Data(); empty.FallbackProfile = "Основной";
        var e2 = new AutoSwitchEngine { DebounceMs = 0 };
        e2.Observe("calc", "x", 0, empty, "Фото");
        Assert.Null(e2.Observe("calc", "x", 1, empty, "Фото"));
    }

    [Fact]
    public void Engine_TitleRule_WorksEvenWithoutProcessName()
    {
        var d = Data(("", "Elden Ring", "Игры"));
        var e = new AutoSwitchEngine { DebounceMs = 0 };
        e.Observe("", "ELDEN RING™", 0, d, "Основной");
        Assert.Equal("Игры", e.Observe("", "ELDEN RING™", 1, d, "Основной")!.Profile);
    }

    [Fact]
    public void Engine_PausedOrOff_NoSwitch_ResumeReevaluates()
    {
        var d = Data(("photoshop", "", "Фото"));
        var e = new AutoSwitchEngine { DebounceMs = 0, Paused = true };
        e.Observe("photoshop", "a", 0, d, "Основной");
        Assert.Null(e.Observe("photoshop", "a", 1, d, "Основной"));
        e.Paused = false;
        Assert.Equal("Фото", e.Observe("photoshop", "a", 2, d, "Основной")!.Profile);
        d.AutoSwitch = false; e.Reset();
        Assert.Null(e.Observe("photoshop", "a", 3, d, "Основной"));
    }

    [Fact]
    public void Normalize_FallbackToDeletedProfile_Cleared_RulesNormalized()
    {
        var d = Data(("C:\\x\\Photoshop.exe", "  t ", "Фото")); d.FallbackProfile = "Нет такого";
        d.Normalize();
        Assert.Equal("", d.FallbackProfile);
        Assert.Equal("Photoshop", d.AppRules[0].ProcessName);
        Assert.Equal("t", d.AppRules[0].TitleContains);
    }
}

public class ExchangeTests
{
    static byte[] Base() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", "exp14-macro.bin"));
    static AppProfile Prof(string name)
    {
        var p = new AppProfile { Name = name, Device = K13MemoryCodec.Decode(Base()) };
        p.Host.Keys.Add(new HostKeyBinding { TriggerVk = 0x7D, Tap = new HostAction { Kind = HostActionKind.KeyCombo, Value = "Ctrl+C" } });
        return p;
    }
    static ImportPreview Read(string json, params string[] existing) => ProfileExchange.Read(json, Base(), existing);

    [Fact]
    public void Export_Import_RoundTrip_WithRules_AndFormatVersion()
    {
        var a = Prof("Фото"); var b = Prof("Игры");
        var rules = new[] { new AppSwitchRule { ProcessName = "photoshop", ProfileName = "Фото", IconPath = @"C:\Program Files\Adobe\Photoshop.exe", DisplayName = "Adobe Photoshop" },
                            new AppSwitchRule { ProcessName = "chrome", ProfileName = "Другой" } };
        var ex = ProfileExchange.Export(new[] { a, b }, rules, "5.4");
        var node = JsonNode.Parse(ex.Json)!;
        Assert.Equal(ProfilePackage.FormatId, (string?)node["Format"]);
        Assert.Equal(1, (int?)node["FormatVersion"]);
        Assert.DoesNotContain("Program Files", ex.Json);          // icon path is machine-specific → not exported
        Assert.Single(node["AppRules"]!.AsArray());               // rule of a profile that is not exported is skipped

        var pv = Read(ex.Json);
        Assert.Equal(2, pv.Items.Count);
        var foto = pv.Items[0];
        Assert.Equal("Фото", foto.Profile.Name);
        Assert.Single(foto.Rules);
        Assert.Equal("Adobe Photoshop", foto.Rules[0].DisplayName);
        Assert.Equal(1, foto.PcActions);
        Assert.True(foto.Macros >= 0);
    }

    [Fact]
    public void Export_StripsUserFolder_AndWarnsAboutOtherPaths()
    {
        var p = Prof("Работа");
        p.Host.Keys.Add(new HostKeyBinding { TriggerVk = 0x7E, Tap = new HostAction { Kind = HostActionKind.LaunchProgram, Value = @"C:\Users\ivan\AppData\Local\Programs\Tool\tool.exe" } });
        p.Host.Keys.Add(new HostKeyBinding { TriggerVk = 0x7F, Tap = new HostAction { Kind = HostActionKind.LaunchProgram, Value = @"D:\Games\game.exe" } });
        var ex = ProfileExchange.Export(new[] { p }, Array.Empty<AppSwitchRule>(), "5.4", userProfile: @"C:\Users\ivan");
        Assert.Contains(@"%USERPROFILE%\\AppData\\Local\\Programs\\Tool\\tool.exe", ex.Json);
        Assert.DoesNotContain("ivan", ex.Json);
        Assert.Contains(ex.Notes, n => n.Contains(@"D:\Games\game.exe"));
        var keep = ProfileExchange.Export(new[] { p }, Array.Empty<AppSwitchRule>(), "5.4", stripPaths: false, userProfile: @"C:\Users\ivan");
        Assert.Contains("ivan", keep.Json);
        Assert.Contains(Read(ex.Json).Warnings, w => w.Contains("доверяете"));     // launches are flagged on import
    }

    [Fact]
    public void Import_LegacySingleProfileJson_StillWorks()
    {
        var legacy = JsonSerializer.Serialize(Prof("Старый"), ProfileStore.JsonOptions);   // v4.7–v5.3 «Экспорт в файл .json»
        var pv = Read(legacy);
        Assert.Equal("Старый", Assert.Single(pv.Items).Profile.Name);
        Assert.Equal(0, pv.FormatVersion);
    }

    [Fact]
    public void Import_WholeProfilesJson_Works()
    {
        var d = new ProfileStoreData(); d.Profiles.Add(Prof("A")); d.Profiles.Add(Prof("B"));
        d.AppRules.Add(new AppSwitchRule { ProcessName = "cs2", ProfileName = "B" });
        var pv = Read(JsonSerializer.Serialize(d, ProfileStore.JsonOptions));
        Assert.Equal(2, pv.Items.Count);
        Assert.Single(pv.Items[1].Rules);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello")]
    [InlineData("[1,2,3]")]
    [InlineData("{")]
    [InlineData("{\"Format\":\"something-else\",\"FormatVersion\":1,\"Profiles\":[]}")]
    [InlineData("{\"Format\":\"vorotex-k13-profile\",\"FormatVersion\":99,\"Profiles\":[{}]}")]
    [InlineData("{\"Format\":\"vorotex-k13-profile\",\"FormatVersion\":1,\"Profiles\":[]}")]
    [InlineData("{\"Format\":\"vorotex-k13-profile\",\"FormatVersion\":\"x\",\"Profiles\":[]}")]
    [InlineData("{\"Format\":\"vorotex-k13-profile\",\"FormatVersion\":1,\"Profiles\":{\"a\":1}}")]
    [InlineData("{\"Name\":\"X\",\"Host\":{\"Keys\":[{\"TriggerVk\":125,\"Tap\":{\"Kind\":\"FormatDisk\"}}]}}")]
    [InlineData("{\"Name\":\"X\",\"Host\":{\"Keys\":[{\"TriggerVk\":0}]}}")]
    [InlineData("{\"Name\":\"X\",\"Device\":{\"Keymap\":\"AAAA\"}}")]
    [InlineData("{\"Name\":\"X\",\"Device\":{\"Base\":99999}}")]
    [InlineData("{\"Name\":\"X\",\"MacroNames\":{\"77\":\"x\"}}")]
    [InlineData("{\"Unrelated\":true}")]
    public void Import_BadFiles_RejectedWithMessage_NoCrash(string json)
    {
        var ex = Assert.Throws<ProfileImportException>(() => Read(json));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void Import_DeepNesting_And_HugeMacro_Rejected()
    {
        var deep = "{\"Name\":\"X\",\"Comment\":" + new string('[', 200) + new string(']', 200) + "}";
        Assert.Throws<ProfileImportException>(() => Read(deep));
        var p = Prof("Big");
        foreach (var m in p.Device!.Macros.Take(5))   // within the per-macro limit, but together far more than the 16 KB macro memory
            m.Events = Enumerable.Range(0, 3000).Select(i => new MacroEvent { Code = 4, Down = i % 2 == 0, Delay = 1 }).ToList();
        var fit = Assert.Throws<ProfileImportException>(() => Read(JsonSerializer.Serialize(p, ProfileStore.JsonOptions)));
        Assert.Contains("не подходят", fit.Message);
        p.Device.Macros[0].Events = Enumerable.Range(0, ProfileExchange.MaxEvents + 1).Select(_ => new MacroEvent { Code = 4 }).ToList();
        Assert.Throws<ProfileImportException>(() => Read(JsonSerializer.Serialize(p, ProfileStore.JsonOptions)));
        var many = new ProfileStoreData(); for (int i = 0; i < 70; i++) many.Profiles.Add(new AppProfile { Name = "P" + i });
        Assert.Throws<ProfileImportException>(() => Read(JsonSerializer.Serialize(many, ProfileStore.JsonOptions)));
    }

    [Fact]
    public void Import_TooLargeFile_Rejected()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{\"Name\":\"" + new string('x', ProfileExchange.MaxBytes + 10) + "\"}");
            Assert.Throws<ProfileImportException>(() => ProfileExchange.ReadFile(path, Base(), Array.Empty<string>()));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Import_ControlCharsAndLongNames_Cleaned_DuplicatesInFileRenamed()
    {
        var d = new ProfileStoreData();
        d.Profiles.Add(new AppProfile { Name = "Игры\u0007\n  ночь" });
        d.Profiles.Add(new AppProfile { Name = "игры ночь" });
        d.Profiles.Add(new AppProfile { Name = new string('Я', 120) });
        var pv = Read(JsonSerializer.Serialize(d, ProfileStore.JsonOptions));
        Assert.Equal("Игры ночь", pv.Items[0].Profile.Name);
        Assert.Equal("игры ночь (2)", pv.Items[1].Profile.Name);
        Assert.True(pv.Items[2].Profile.Name.Length <= 40);
        Assert.Contains(pv.Warnings, w => w.Contains("стандартные"));
    }

    [Fact]
    public void Import_BadRulesDropped()
    {
        var pkg = new ProfilePackage { Profiles = { Prof("A") }, AppRules =
        {
            new AppSwitchRule { ProcessName = "ok", ProfileName = "A", IconPath = @"C:\evil\x.exe" },
            new AppSwitchRule { ProcessName = "", TitleContains = "", ProfileName = "A" },
            new AppSwitchRule { ProcessName = "a<b", ProfileName = "A" },
            new AppSwitchRule { ProcessName = "lost", ProfileName = "Z" },
        } };
        var pv = Read(JsonSerializer.Serialize(pkg, ProfileStore.JsonOptions));
        var r = Assert.Single(pv.Items[0].Rules);
        Assert.Equal("ok", r.ProcessName);
        Assert.Equal("", r.IconPath);
        Assert.Contains(pv.Warnings, w => w.Contains("без профиля"));
    }

    [Fact]
    public void Apply_ConflictModes()
    {
        var store = new ProfileStoreData();
        store.Profiles.Add(Prof("Основной")); store.Profiles.Add(Prof("Фото"));
        store.AppRules.Add(new AppSwitchRule { ProcessName = "lightroom", ProfileName = "Фото" });
        var incoming = Prof("Фото"); incoming.Comment = "новый";
        incoming.Host.Keys.Add(new HostKeyBinding { TriggerVk = 0x80, Tap = new HostAction { Kind = HostActionKind.SwitchProfile, Value = "Фото" } });
        var json = ProfileExchange.Export(new[] { incoming }, new[] { new AppSwitchRule { ProcessName = "photoshop", ProfileName = "Фото" } }, "5.4").Json;

        var pv = Read(json, store.Profiles.Select(p => p.Name).ToArray());
        Assert.True(pv.Items[0].Conflicts);

        var keep = Clone(store);
        var names = ProfileExchange.Apply(pv, keep, ImportConflict.KeepBoth);
        Assert.Equal("Фото (2)", Assert.Single(names));
        Assert.Equal(3, keep.Profiles.Count);
        Assert.Equal("", keep.Profiles[1].Comment);
        Assert.Contains(keep.AppRules, r => r.ProcessName == "photoshop" && r.ProfileName == "Фото (2)");
        Assert.Contains(keep.Profiles[2].Host.Keys, k => k.Tap.Kind == HostActionKind.SwitchProfile && k.Tap.Value == "Фото (2)");

        var repl = Clone(store);
        ProfileExchange.Apply(Read(json, "Основной", "Фото"), repl, ImportConflict.Replace);
        Assert.Equal(2, repl.Profiles.Count);
        Assert.Equal("новый", repl.Profiles[1].Comment);
        Assert.DoesNotContain(repl.AppRules, r => r.ProcessName == "lightroom");
        Assert.Contains(repl.AppRules, r => r.ProcessName == "photoshop" && r.ProfileName == "Фото");
    }

    [Fact]
    public void Apply_ExistingRuleForSameProgram_IsKept()
    {
        var store = new ProfileStoreData(); store.Profiles.Add(Prof("Мой"));
        store.AppRules.Add(new AppSwitchRule { ProcessName = "photoshop", ProfileName = "Мой" });
        var json = ProfileExchange.Export(new[] { Prof("Чужой") }, new[] { new AppSwitchRule { ProcessName = "Photoshop", ProfileName = "Чужой" } }, "5.4").Json;
        ProfileExchange.Apply(Read(json, "Мой"), store, ImportConflict.KeepBoth);
        Assert.Equal("Мой", Assert.Single(store.AppRules).ProfileName);
    }

    [Theory]
    [InlineData("Фото: <лето>?", "Фото_ _лето__")]
    [InlineData("...", "профиль")]
    public void SafeFileName(string name, string expected) => Assert.Equal(expected, ProfileExchange.SafeFileName(name));

    static ProfileStoreData Clone(ProfileStoreData d) => JsonSerializer.Deserialize<ProfileStoreData>(JsonSerializer.Serialize(d, ProfileStore.JsonOptions), ProfileStore.JsonOptions)!;
}
