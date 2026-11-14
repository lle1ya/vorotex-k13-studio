using System.Text.Json;
using Vorotex.K13;
using Xunit;

// K13WriteGate is process-wide: tests that open it must not overlap with tests that expect it closed
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace K13.Core.Tests;

/// <summary>Fake keypad that can «be unplugged»: SetFeature returns false (as HidD_SetFeature does) for matching frames.</summary>
sealed class FlakyK13(FakeK13 inner) : IHidFeatureTransport
{
    /// <summary>Frames for which SetFeature fails. <see cref="FailCount"/> limits how many calls fail (≤0 = forever).</summary>
    public Func<byte[], bool>? FailWhen { get; set; }
    public int FailCount { get; set; }
    public bool Unplugged { get; set; }
    public List<byte[]> Attempts { get; } = new();
    public FakeK13 Inner => inner;

    public bool SetFeature(byte[] r)
    {
        Attempts.Add((byte[])r.Clone());
        if (Unplugged) return false;
        if (FailWhen != null && FailWhen(r))
        {
            if (FailCount > 0 && --FailCount == 0) FailWhen = null;
            return false;
        }
        return inner.SetFeature(r);
    }

    public bool GetFeature(byte[] b) => !Unplugged && inner.GetFeature(b);
    public void Dispose() { }
}


public class BugfixTests
{
    static byte[] Img(string f) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", f));
    static string Hex(byte[] f) => BitConverter.ToString(f);
    static string TempDir() => Path.Combine(Path.GetTempPath(), "k13-v53-" + Guid.NewGuid());

    // ───────── device session

    [Fact]
    public void EndSession_SendsAllThreeFrames_EvenIfFirstFails()
    {
        var fake = new FakeK13(Img("exp14-macro.bin"));
        var flaky = new FlakyK13(fake) { FailWhen = f => f[1] == 0x18 && f[2] == 0x11 };
        var dev = new K13Device(flaky) { DelayMs = 0 };
        Assert.Throws<IOException>(dev.EndSession);
        var sent = fake.Frames.Select(Hex).ToList();
        Assert.Contains("07-18-00-00-00-00-00-00", sent);
        Assert.Equal("07-20-00-00-00-00-00-00", sent.Last());
        dev.Dispose();
    }

    [Fact]
    public void Dispose_IsIdempotent_SessionEndSentOnce()
    {
        var fake = new FakeK13(Img("exp14-macro.bin"));
        var dev = new K13Device(fake) { DelayMs = 0 };
        dev.ReadBlocks(new List<(int, int)> { (0xD0, 8) });
        dev.Dispose();
        int n = fake.Frames.Count;
        dev.Dispose();
        Assert.Equal(n, fake.Frames.Count);
        Assert.Equal(1, fake.Frames.Count(f => f[1] == 0x20));
    }

    [Fact]
    public void Unplug_DuringRead_ThrowsIOException_AndDisposeDoesNotThrow()
    {
        var fake = new FakeK13(Img("exp14-macro.bin"));
        var flaky = new FlakyK13(fake);
        var dev = new K13Device(flaky) { DelayMs = 0, LoadDelayMs = 0 };
        int calls = 0;
        flaky.FailWhen = _ => ++calls > 20;     // cable pulled after 20 frames
        Assert.ThrowsAny<IOException>(() => dev.ReadImageVerified());
        dev.Dispose();                           // must not throw
        Assert.DoesNotContain(fake.Frames, f => f[1] == 0x18 && (f[2] == 0x03 || f[2] == 0x09));
    }

    // ───────── writer: write broken off half-way

    static (FakeK13 fake, FlakyK13 flaky, K13Device dev, K13DeviceConfig cfg, byte[] orig) WriteSetup()
    {
        var mem = Img("exp15-postwrite-20261113-133715.bin").Take(0x1000).ToArray();
        var orig = (byte[])mem.Clone();
        var fake = new FakeK13(mem) { Writable = true };
        var flaky = new FlakyK13(fake);
        var dev = new K13Device(flaky) { DelayMs = 0, LoadDelayMs = 0 };
        var cfg = K13MemoryCodec.Decode(mem);
        cfg.Led.Brightness = cfg.Led.Brightness == 0 ? 3 : 0;
        return (fake, flaky, dev, cfg, orig);
    }

    [Fact]
    public void Writer_ApplyFailsAfterCommit_RestoresTouchedBlocks()
    {
        var (fake, flaky, dev, cfg, orig) = WriteSetup();
        // the block is committed, then the «end» frame after it fails 4× (Send gives up) → IOException inside Apply
        flaky.FailWhen = f => f[1] == 0x18 && f[2] == 0x01 && (f[4] | f[5] << 8) != 0; flaky.FailCount = 4;
        var tmp = TempDir();
        try
        {
            K13WriteGate.Unlock(K13WriteGate.ConfirmationPhrase);
            var r = K13Writer.WriteProfile(dev, cfg, _ => { }, null, false, tmp);
            Assert.False(r.Ok);
            Assert.True(r.Restored, r.Message);
            Assert.NotNull(r.BackupBefore);
            Assert.True(File.Exists(r.BackupBefore));
            Assert.Equal(orig.Take(0x400), fake.Memory.Take(0x400));
        }
        finally { K13WriteGate.Lock(); dev.Dispose(); try { Directory.Delete(tmp, true); } catch { } }
    }

    [Fact]
    public void Writer_UnpluggedMidWrite_ReturnsFailureWithBackupPath_NoThrow()
    {
        var (fake, flaky, dev, cfg, _) = WriteSetup();
        flaky.FailWhen = f => f[1] == 0x18 && f[2] == 0x09;   // first commit: from here on the keypad is gone
        var tmp = TempDir();
        try
        {
            K13WriteGate.Unlock(K13WriteGate.ConfirmationPhrase);
            var orig = flaky.FailWhen;
            flaky.FailWhen = f => { if (orig(f)) flaky.Unplugged = true; return orig(f); };
            var r = K13Writer.WriteProfile(dev, cfg, _ => { }, null, false, tmp);
            Assert.False(r.Ok);
            Assert.False(r.Restored);
            Assert.Contains(r.BackupBefore!, r.Message);
        }
        finally { K13WriteGate.Lock(); dev.Dispose(); try { Directory.Delete(tmp, true); } catch { } }
        Assert.False(K13WriteGate.IsOpen);
    }

    // ───────── backups

    [Fact]
    public void BackupStore_SameSecond_NeverOverwrites()
    {
        var tmp = TempDir();
        try
        {
            var a = BackupStore.Save(new byte[] { 1, 2, 3 }, tmp, "k13-prewrite", null);
            var b = BackupStore.Save(new byte[] { 4, 5, 6 }, tmp, "k13-prewrite", null);
            var c = BackupStore.Save(new byte[] { 7, 8, 9 }, tmp, "k13-prewrite", null);
            Assert.Equal(3, new[] { a, b, c }.Distinct().Count());
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(a));
            Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(b));
            Assert.Empty(Directory.GetFiles(tmp, "*.tmp"));
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    // ───────── profiles.json

    [Fact]
    public void ProfileStore_CorruptFile_KeptAside_AndBackupUsed()
    {
        var tmp = TempDir();
        try
        {
            var s = new ProfileStore(tmp);
            s.Data.Profiles.Add(new AppProfile { Name = "Работа" });
            s.Data.ActiveProfile = "Работа";
            s.Save();                                   // first save: no .bak yet
            s.Data.Profiles.Add(new AppProfile { Name = "Игры" });
            s.Save();                                   // second save: previous version → .bak
            Assert.True(File.Exists(s.BackupPath));
            File.WriteAllText(s.FilePath, "{ \"Profiles\": [ { \"Name\": \"Раб");   // torn write / damaged disk

            var s2 = new ProfileStore(tmp);
            s2.Load();                                  // must not throw
            Assert.Contains(s2.Data.Profiles, p => p.Name == "Работа");
            Assert.Single(Directory.GetFiles(tmp, "profiles.json.corrupt-*"));
            Assert.Contains(s2.LoadNotes, n => n.Contains("повреждён"));
            Assert.Contains(s2.LoadNotes, n => n.Contains(".bak"));
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    [Fact]
    public void ProfileStore_CorruptWithoutBackup_EmptyStore_OriginalPreserved()
    {
        var tmp = TempDir();
        try
        {
            Directory.CreateDirectory(tmp);
            File.WriteAllText(Path.Combine(tmp, "profiles.json"), "not json at all");
            var s = new ProfileStore(tmp);
            s.Load();
            Assert.Empty(s.Data.Profiles);
            var kept = Assert.Single(Directory.GetFiles(tmp, "profiles.json.corrupt-*"));
            Assert.Equal("not json at all", File.ReadAllText(kept));
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    [Fact]
    public void ProfileStore_Save_IsAtomic_NoTempLeft_RoundTrips()
    {
        var tmp = TempDir();
        try
        {
            var s = new ProfileStore(tmp);
            s.Data.Profiles.Add(new AppProfile { Name = "Ёлка «тест»" });
            s.Save(); s.Save();
            Assert.False(File.Exists(s.FilePath + ".tmp"));
            var s2 = new ProfileStore(tmp); s2.Load();
            Assert.Equal("Ёлка «тест»", s2.Data.Profiles[0].Name);
            Assert.Empty(s2.LoadNotes);
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    [Fact]
    public void Normalize_RepairsNullsDuplicatesAndRanges()
    {
        const string json = """
        { "Profiles": [
            { "Name": "A", "Host": null, "MacroNames": null, "Comment": null },
            { "Name": "a", "Host": { "Keys": [ null, { "TriggerVk": 124, "HoldMs": 999999, "Tap": null } ], "Chords": null,
                                     "Ring": { "Slots": [ {}, {}, {}, {}, {}, {}, {}, {}, {}, {} ], "TapThresholdMs": -5 } } },
            { "Name": "   " },
            null
          ],
          "ActiveProfile": "нет такого", "AppRules": [ null, { "ProfileName": null } ], "LedColors": null }
        """;
        var d = JsonSerializer.Deserialize<ProfileStoreData>(json, ProfileStore.JsonOptions)!;
        var notes = d.Normalize();
        Assert.Equal(3, d.Profiles.Count);
        Assert.Equal(3, d.Profiles.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(d.Profiles, p => { Assert.NotNull(p.Host); Assert.NotNull(p.MacroNames); Assert.NotNull(p.Host.Ring); Assert.False(string.IsNullOrWhiteSpace(p.Name)); });
        var k = d.Profiles[1].Host.Keys.Single();
        Assert.Equal(5000, k.HoldMs);
        Assert.NotNull(k.Tap);
        Assert.Equal(RingSettings.MaxSlots, d.Profiles[1].Host.Ring.Slots.Count);
        Assert.InRange(d.Profiles[1].Host.Ring.TapThresholdMs, 50, 2000);
        Assert.Equal("A", d.ActiveProfile);
        Assert.Single(d.AppRules);
        Assert.Equal("", d.AppRules[0].ProfileName);
        Assert.NotNull(d.LedColors);
        Assert.NotEmpty(notes);
    }

    [Fact]
    public void Normalize_InvalidDevicePart_Dropped()
    {
        var p = new AppProfile { Name = "X", Device = K13MemoryCodec.Decode(Img("exp14-macro.bin")) };
        p.Device.Keymap = new byte[3];
        var notes = p.Normalize();
        Assert.Null(p.Device);
        Assert.Single(notes);
    }

    // ───────── input validation / Russian text

    [Theory]
    [InlineData("٣")]      // Arabic-Indic three: char.IsDigit == true, int.Parse threw FormatException
    [InlineData("１２")]   // full-width digits
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("-5")]
    [InlineData("1e3")]
    public void RepeatCount_NonAsciiOrGarbage_IsRejected_NoThrow(string text) => Assert.False(RepeatCount.TryParse(text, out _));

    [Theory]
    [InlineData("99999999999999999999", RepeatCount.Max)]
    [InlineData(" 7 ", 7)]
    [InlineData("1", RepeatCount.Min)]
    public void RepeatCount_Clamps(string text, int expected) { Assert.True(RepeatCount.TryParse(text, out var v)); Assert.Equal(expected, v); }

    [Theory]
    [InlineData(1, "1 блок")] [InlineData(2, "2 блока")] [InlineData(5, "5 блоков")] [InlineData(11, "11 блоков")]
    [InlineData(21, "21 блок")] [InlineData(22, "22 блока")] [InlineData(112, "112 блоков")] [InlineData(0, "0 блоков")]
    public void RuPlural_Blocks(int n, string expected) => Assert.Equal(expected, RuPlural.Blocks(n));

    // ───────── macro playback

    static MacroEvent K(byte code, bool down, int delay = 0) => new() { Code = code, Down = down, Delay = delay };

    [Fact]
    public void Sequencer_Cancelled_ReleasesHeldKeys_InReverseOrder()
    {
        var events = new List<MacroEvent> { K(0xE0, true), K(0x04, true), K(0x04, false, 100) };
        var sent = new List<string>();
        using var cts = new CancellationTokenSource();
        MacroSequencer.Run(events, 1, e => sent.Add($"{(e.Down ? "+" : "-")}{e.Code:X2}"), cts.Token,
            wait: _ => { cts.Cancel(); return true; });  // «Стоп» during the pause
        Assert.Equal(new[] { "+E0", "+04", "-04", "-E0" }, sent);
    }

    [Fact]
    public void Sequencer_CountMode_PlaysExactlyN()
    {
        var events = new List<MacroEvent> { K(0x04, true), K(0x04, false) };
        int downs = 0;
        int done = MacroSequencer.Run(events, MacroSequencer.Passes(MacroRepeatMode.Count, 7), e => { if (e.Down) downs++; }, CancellationToken.None, wait: _ => false);
        Assert.Equal(7, done); Assert.Equal(7, downs);
        Assert.Equal(1, MacroSequencer.Passes(MacroRepeatMode.Count, 0));
        Assert.Equal(1, MacroSequencer.Passes(MacroRepeatMode.WhileHeld, 50));
    }

    [Fact]
    public void Sequencer_ToggleMode_LoopsUntilStopped_WithMinimumPassTime()
    {
        Assert.Equal(int.MaxValue, MacroSequencer.Passes(MacroRepeatMode.Toggle, 1));
        var events = new List<MacroEvent> { K(0x04, true), K(0x04, false) };
        using var cts = new CancellationTokenSource();
        int downs = 0, waits = 0;
        int done = MacroSequencer.Run(events, MacroSequencer.Passes(MacroRepeatMode.Toggle, 1), e => { if (e.Down && ++downs == 5) cts.Cancel(); }, cts.Token,
            wait: ms => { waits++; Assert.InRange(ms, 1, MacroSequencer.MinLoopPassMs); return cts.IsCancellationRequested; });
        Assert.Equal(5, downs);
        Assert.Equal(5, done);
        Assert.True(waits >= 4); // no zero-pause busy loop flooding the input queue
    }

    [Fact]
    public void Sequencer_Filter_SkipsMouse_WithoutStuckState()
    {
        var events = new List<MacroEvent> { K(MouseCodes.Left, true), K(0x04, true), K(0x04, false) };
        var sent = new List<byte>();
        MacroSequencer.Run(events, 1, e => sent.Add(e.Code), CancellationToken.None, e => !e.IsMouseButton, _ => false);
        Assert.Equal(new byte[] { 0x04, 0x04 }, sent);
    }

    [Fact]
    public void Recording_DanglingPresses_GetReleased()
    {
        var ev = new List<MacroEvent> { K(0xE1, true), K(0x04, true), K(0x04, false), K(0x05, true), MacroEvent.Wheel(1) };
        Assert.Equal(2, MacroSequencer.CloseDanglingPresses(ev));
        Assert.Equal(new[] { (false, (byte)0x05), (false, (byte)0xE1) }, ev.TakeLast(2).Select(e => (e.Down, e.Code)));
        Assert.Equal(0, MacroSequencer.CloseDanglingPresses(ev));
    }

    [Fact]
    public void TypeText_Newlines_BecomeEnter_TabBecomesTab()
    {
        var keys = TextKeys.Split("a\r\nb\nc\td").ToList();
        Assert.Equal(new ushort[] { 0, TextKeys.VkReturn, 0, TextKeys.VkReturn, 0, TextKeys.VkTab, 0 }, keys.Select(k => k.Vk));
        Assert.Empty(TextKeys.Split(null));
    }

    // ───────── trigger keys

    [Fact]
    public void KeyTracker_LostKeyUp_NextPressStillWorks()
    {
        var t = new KeyDownTracker();
        Assert.True(t.Down(0x7D, 0));
        Assert.False(t.Down(0x7D, 500));   // typematic repeat
        Assert.False(t.Down(0x7D, 530));
        // key-up lost (UAC / Win+L); the user presses again 10 s later
        Assert.True(t.Down(0x7D, 10_530));
        Assert.Equal(70, t.Up(0x7D, 10_600));
        Assert.Null(t.Up(0x7D, 10_700));
    }
}
