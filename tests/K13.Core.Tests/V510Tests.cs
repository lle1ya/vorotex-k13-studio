using System.Text;
using Xunit;

namespace Vorotex.K13.Tests;

/// <summary>
/// v5.10 — «Действие на ПК» outside «через ПК» goes over the Scroll Lock + F chord (the K13 does not send F14–F24),
/// hold-key chooser, router robustness (lost Scroll Lock release, unplug), decoder never crashes on garbage,
/// Russian error texts, update check (notify only).
/// </summary>
public class V510Tests
{
    static byte[] Raw() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", "exp15-postwrite-20261113-133715.bin")).Take(0x1000).ToArray();

    static K13DeviceConfig WithHostKeys(params (int col, byte code)[] keys)
    {
        var c = K13MemoryCodec.Decode(Raw());
        foreach (var (col, code) in keys) c.SetKey(col, code);
        return c;
    }

    [Fact]
    public void Host_trigger_cells_become_chord_macros_in_free_slots_and_come_back()
    {
        var prof = WithHostKeys((3, 0x69), (5, 0x73));
        var target = PcLayerPlanner.ApplyHostChords(prof, out var noSlot);
        Assert.Empty(noSlot);
        var kp = K13MemoryCodec.Decode(K13MemoryCodec.Encode(target, Raw()));
        Assert.True(KeyCatalog.IsMacro(kp.GetKey(3)));
        Assert.True(KeyCatalog.IsMacro(kp.GetKey(5)));
        int s3 = KeyCatalog.MacroSlotOf(kp.GetKey(3)), s5 = KeyCatalog.MacroSlotOf(kp.GetKey(5));
        Assert.NotEqual(s3, s5);
        Assert.False(prof.Macros[s3].IsUsed);                                    // slot was free in the profile
        Assert.Contains(kp.Macros[s3].Events, e => e.Code == 0x3A);             // F14 → F1
        Assert.Contains(kp.Macros[s5].Events, e => e.Code == 0x44);             // F24 → F11
        Assert.Contains(kp.Macros[s3].Events, e => e.Code == 0x47);             // Scroll Lock
        Assert.DoesNotContain(kp.Macros, m => m.Mode1Bit);
        // the keypad never gets F14–F24 any more
        for (int col = 0; col < K13Layout.KeymapColumns; col++) Assert.False(PcLayerPlanner.IsHostTrigger(kp.GetKey(col)));
        // back to the profile: same trigger codes, slots freed, other macros untouched
        var back = PcLayerPlanner.RevertHostChords(kp);
        Assert.Equal(0x69, back.GetKey(3));
        Assert.Equal(0x73, back.GetKey(5));
        Assert.False(back.Macros[s3].IsUsed);
        for (int i = 0; i < prof.Macros.Count; i++) Assert.Equal(prof.Macros[i].IsUsed, back.Macros[i].IsUsed);
        // writing the reverted profile again gives the same keypad bytes (no endless «Записать»)
        Assert.Equal(K13MemoryCodec.Encode(target, Raw()), K13MemoryCodec.Encode(PcLayerPlanner.ApplyHostChords(back), Raw()));
    }

    [Fact]
    public void Old_profile_with_F14_needs_only_a_write()
    {
        var prof = WithHostKeys((2, 0x6A));
        var oldKeypad = K13MemoryCodec.Encode(prof, Raw());                    // what v5.9 wrote: the F15 code itself
        var target = PcLayerPlanner.ApplyHostChords(prof);
        Assert.NotEqual(oldKeypad, K13MemoryCodec.Encode(target, Raw()));        // → «Записать» is offered
        Assert.Equal(prof.GetKey(2), PcLayerPlanner.RevertHostChords(K13MemoryCodec.Decode(oldKeypad)).GetKey(2)); // reading the old keypad keeps the action
    }

    [Fact]
    public void No_free_slot_is_reported_not_silently_dropped()
    {
        var prof = WithHostKeys((1, 0x69));
        for (int i = 0; i < prof.Macros.Count; i++)
            prof.Macros[i] = new MacroSlot { Events = { new() { Down = true, Delay = 1, Code = 0x04 }, new() { Down = false, Delay = 1, Code = 0x04 } } };
        var t = PcLayerPlanner.ApplyHostChords(prof, out var noSlot);
        Assert.Equal(new[] { 1 }, noSlot);
        Assert.Equal(0x69, t.GetKey(1));
    }

    [Fact]
    public void Host_only_chord_routes_to_the_PC_action_and_real_F_keys_pass()
    {
        var prof = WithHostKeys((4, 0x6B));                                       // F16
        var st = PcLayerPlanner.HostChordState(prof);
        Assert.Single(st.Keys);
        Assert.Equal(0x3C, st.Keys[0].Code);                                     // chord F3
        var r = new PcLayerRouter(); r.SetLayer(st);
        Func<ushort, bool> host = vk => vk == 0x7F;                              // VK_F16 is a PC-action trigger
        var o = new List<PcOut>();
        foreach (var (vk, down, t) in new (ushort, bool, long)[] { (0x91, true, 0), (0x72, true, 8), (0x72, false, 16), (0x91, false, 24) })
        {
            o.AddRange(r.Feed(vk, down, prof, host, t, out bool sw));
            Assert.True(sw);
        }
        Assert.Contains(o, x => x.Kind == PcOutKind.HostDown && x.Vk == 0x7F);
        Assert.Contains(o, x => x.Kind == PcOutKind.HostUp && x.Vk == 0x7F);
        // a real F3 without Scroll Lock is the user's own key
        Assert.Empty(r.Feed(0x72, true, prof, host, 5000, out bool sw2));
        Assert.False(sw2);
    }

    [Fact]
    public void Lost_Scroll_Lock_release_stops_eating_F_keys()
    {
        var prof = WithHostKeys((4, 0x6B));
        var r = new PcLayerRouter(); r.SetLayer(PcLayerPlanner.HostChordState(prof));
        r.Feed(0x91, true, prof, _ => true, 0, out _);                            // keypad unplugged: its release never comes
        r.Feed(0x72, true, prof, _ => true, 300, out bool early);
        Assert.True(early);                                                      // still inside a real chord
        r.Feed(0x72, false, prof, _ => true, 310, out _);
        r.Feed(0x72, true, prof, _ => true, 5000, out bool late);
        Assert.False(late);                                                      // seconds later — the real keyboard's F3
    }

    [Fact]
    public void ReleaseAll_after_unplug_releases_held_keys_and_chord()
    {
        var prof = WithHostKeys((4, 0x6B));
        var r = new PcLayerRouter(); r.SetLayer(PcLayerPlanner.HostChordState(prof));
        r.Feed(0x91, true, prof, _ => true, 0, out _);
        r.Feed(0x72, true, prof, _ => true, 8, out _);
        var outs = r.ReleaseAll();
        Assert.Contains(outs, x => x.Kind == PcOutKind.HostUp);
        Assert.Equal(0, r.HeldCount);
        r.Feed(0x72, true, prof, _ => true, 20, out bool sw);
        Assert.False(sw);
    }

    [Fact]
    public void Hold_chooser_moves_the_single_Menu_code()
    {
        var main = K13MemoryCodec.Decode(Raw());
        for (int s = 1; s <= 2; s++)
        {
            main.Macros[s] = new MacroSlot { Events = { new() { Down = true, Delay = 1, Code = MouseCodes.Left }, new() { Down = false, Delay = 1, Code = MouseCodes.Left } }, RepeatMode = MacroRepeatMode.WhileHeld };
        }
        main.SetKey(9, (byte)(KeyCatalog.FirstMacro + 1));
        main.SetKey(8, (byte)(KeyCatalog.FirstMacro + 2));
        var st = PcLayerPlanner.NewState(new[] { main });
        var want = PcLayerPlanner.HoldColumns(new[] { main }, st);
        Assert.Contains(8, want); Assert.Contains(9, want);
        Assert.Single(PcLayerPlanner.HoldCodes(st, want));                       // only one hold code exists
        foreach (int pick in new[] { 8, 9 })
        {
            var order = want.OrderBy(c => c == pick ? -1 : 0).ToList();
            var codes = PcLayerPlanner.HoldCodes(st, order);
            Assert.Equal(new[] { pick }, codes.Keys.ToArray());
        }
        var c2 = st.Clone(); c2.HoldColumn = 8;
        Assert.Equal(8, c2.HoldColumn);
    }

    [Fact]
    public void Decoder_never_crashes_on_garbage()
    {
        var rnd = new Random(510);
        var good = Raw();
        for (int n = 0; n < 3000; n++)
        {
            byte[] img;
            switch (n % 4)
            {
                case 0: img = new byte[rnd.Next(0, 0x1100)]; rnd.NextBytes(img); break;
                case 1: img = (byte[])good.Clone(); for (int k = 0; k < 1 + rnd.Next(40); k++) img[rnd.Next(img.Length)] = (byte)rnd.Next(256); break;
                case 2: img = good.Take(rnd.Next(good.Length)).ToArray(); break;
                default: img = (byte[])good.Clone(); int at = 0xD8 + rnd.Next(0x200); for (int k = 0; k < 16 && at + k < img.Length; k++) img[at + k] = 0xFF; break;
            }
            try
            {
                var cfg = K13MemoryCodec.Decode(img);
                PcLayerPlanner.RevertHostChords(cfg);
                PcLayerPlanner.HostChordState(cfg);
            }
            catch (K13FormatException) { }
        }
    }

    [Fact]
    public void Errors_are_Russian_and_friendly()
    {
        Assert.Contains("Клавиатура не отвечает", FriendlyError.Text(new IOException("HidD_SetFeature не удалось (устройство отключено?).")));
        Assert.Contains("Внутренняя ошибка", FriendlyError.Text(new IndexOutOfRangeException("Index was outside the bounds of the array.")));
        Assert.Contains("Нет доступа", FriendlyError.Text(new UnauthorizedAccessException("Access to the path denied")));
        Assert.Contains("повреждён", FriendlyError.Text(new System.Text.Json.JsonException("bad")));
        Assert.Contains("не похожи", FriendlyError.Text(new K13FormatException("Образ не покрывает таблицу макросов.")));
        Assert.Equal("Чтение не удалось по адресу 0x0100.", FriendlyError.Text(new IOException("Чтение не удалось по адресу 0x0100.")));
        Assert.Contains("0x0000–0x0007", FriendlyError.Text(new WriteBlockedException("заблокирована: адреса 0x0000–0x0007")));
        Assert.Contains("не ответила", FriendlyError.Text(new AggregateException(new TimeoutException())));
        foreach (var ex in new Exception[] { new NullReferenceException(), new InvalidOperationException("Sequence contains no elements"), new FormatException("x") })
            Assert.Matches("[А-Яа-я]", FriendlyError.Text(ex));
    }

    [Fact]
    public void Update_check_compares_tags_and_only_opens_this_repo()
    {
        Assert.Equal(new Version(0, 5, 9, 8), UpdateCheck.ParseTag("v5.9.8"));
        Assert.Equal(new Version(0, 5, 10, 0), UpdateCheck.ParseTag("v5.10.0"));
        Assert.Equal(new Version(1, 0, 0, 0), UpdateCheck.ParseTag("v1.0.0"));
        Assert.Null(UpdateCheck.ParseTag("latest"));
        Assert.Null(UpdateCheck.ParseTag(null));
        string J(string tag, string url = "https://github.com/lle1ya/vorotex-k13-studio/releases/tag/X", bool pre = false) =>
            $"{{\"tag_name\":\"{tag}\",\"html_url\":\"{url.Replace("X", tag)}\",\"name\":\"{tag}\",\"draft\":false,\"prerelease\":{(pre ? "true" : "false")}}}";
        var cur = new Version(0, 5, 10, 0);
        Assert.False(UpdateCheck.IsNewer(UpdateCheck.Parse(J("v5.10.0"))!, cur));
        Assert.False(UpdateCheck.IsNewer(UpdateCheck.Parse(J("v5.9.8"))!, cur));
        Assert.True(UpdateCheck.IsNewer(UpdateCheck.Parse(J("v5.10.1"))!, cur));
        Assert.True(UpdateCheck.IsNewer(UpdateCheck.Parse(J("v1.0.0"))!, cur));
        Assert.Null(UpdateCheck.Parse(J("v9.0.0", pre: true)));
        Assert.Null(UpdateCheck.Parse("<html>rate limited</html>"));
        Assert.Null(UpdateCheck.Parse("[]"));
        Assert.Equal(UpdateCheck.ReleasesPage, UpdateCheck.Parse(J("v1.0.1", "https://evil.example/X"))!.Url);
        Assert.Equal("v5.10.0", UpdateCheck.Display(cur));
        Assert.Equal("v1.0.0", UpdateCheck.Display(new Version(1, 0, 0, 0)));
        var now = new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(UpdateCheck.Due(default, now));
        Assert.False(UpdateCheck.Due(now.AddHours(-3), now));
        Assert.True(UpdateCheck.Due(now.AddDays(2), now));                        // clock went back
    }
}
