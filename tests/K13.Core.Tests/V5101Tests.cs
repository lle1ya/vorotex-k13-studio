using Xunit;
using Xunit.Abstractions;

namespace Vorotex.K13.Tests;

/// <summary>
/// v5.10.1 — stale / false warnings from the makers' test (17.11.2026 14:21–14:45, diagnostics 1447):
/// the expected keypad content is what «Записать» writes (Scroll Lock + F chord macros, the hold code «Меню»), not the bare profile cell.
/// </summary>
public class V5101Tests(ITestOutputHelper output)
{
    static byte[] Dump(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", name));
    static byte[] Raw() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data", "exp15-postwrite-20261113-133715.bin")).Take(0x1000).ToArray();

    [Fact]
    public void Dumps_look_as_the_log_says()
    {
        foreach (var n in new[] { "k13-postwrite-20261117-142145.bin", "k13-postwrite-20261117-143056.bin", "k13-postwrite-20261117-144428.bin", "k13-afterreplug-20261117-143556.bin" })
        {
            var kp = K13MemoryCodec.Decode(Dump(n));
            output.WriteLine(n + ": " + string.Join(" ", Enumerable.Range(0, 17).Select(c => kp.GetKey(c).ToString("X2"))) +
                             " | macros " + string.Join(" ", kp.Macros.Select((m, i) => m.IsUsed ? $"{i}:{string.Join(",", m.Events.Select(e => (e.Down ? "+" : "-") + e.Code.ToString("X2")))}" : "").Where(x => x != "")));
        }
    }

    /// <summary>Notepad on key 1 with «через ПК» off (14:21): the keypad holds the chord macro in slot 0 — that IS correct.</summary>
    [Fact]
    public void PC_action_chord_macro_is_the_expected_content()
    {
        var kp = K13MemoryCodec.Decode(Dump("k13-postwrite-20261117-142145.bin"));
        Assert.True(KeyCatalog.IsMacro(kp.GetKey(0)));
        var profile = PcLayerPlanner.RevertHostChords(kp);                       // what the profile holds: the F14–F24 trigger
        Assert.True(PcLayerPlanner.IsHostTrigger(profile.GetKey(0)));
        var off = new PcLayerState();
        Assert.Empty(PcLayerPlanner.Compare(profile, off, kp));                  // v5.10.0 reported «макрос из ячейки 1»
        Assert.True(PcLayerPlanner.SameCell(PcLayerPlanner.Target(profile, off), kp, 0));
        // the chord may sit in another free slot (e.g. after a macro was deleted) — still the same content
        var moved = PcLayerPlanner.Target(profile, off);
        int slot = KeyCatalog.MacroSlotOf(moved.GetKey(0)), other = Enumerable.Range(0, 16).First(i => i != slot && !moved.Macros[i].IsUsed && !kp.Macros[i].IsUsed);
        moved.Macros[other] = moved.Macros[slot]; moved.Macros[slot] = new MacroSlot(); moved.SetKey(0, (byte)(KeyCatalog.FirstMacro + other));
        Assert.True(PcLayerPlanner.SameCell(moved, kp, 0));
        // a real difference is still reported: another chord, or a plain key
        var wrong = kp.Clone(); wrong.Macros[KeyCatalog.MacroSlotOf(kp.GetKey(0))] = PcLayerPlanner.ChordMacro(0x44);
        Assert.Single(PcLayerPlanner.Compare(profile, off, wrong));
        var plain = kp.Clone(); plain.SetKey(0, 0x59);
        Assert.Single(PcLayerPlanner.Compare(profile, off, plain));
    }

    /// <summary>Autoclick «пока зажата» through the PC (14:30 / 14:44): the hold key sends «Меню» (App) — that IS correct.</summary>
    [Fact]
    public void Hold_key_Menu_code_is_the_expected_content()
    {
        var main = K13MemoryCodec.Decode(Raw());
        for (int s = 1; s <= 2; s++)
            main.Macros[s] = new MacroSlot { Events = { new() { Down = true, Delay = 1, Code = MouseCodes.Left }, new() { Down = false, Delay = 1, Code = MouseCodes.Left } }, RepeatMode = MacroRepeatMode.WhileHeld };
        main.SetKey(1, (byte)(KeyCatalog.FirstMacro + 1));                      // key 2
        main.SetKey(2, (byte)(KeyCatalog.FirstMacro + 2));                      // key 3
        var st = PcLayerPlanner.NewState(new[] { main });
        foreach (int holder in new[] { 1, 2 })
        {
            var hold = PcLayerPlanner.HoldColumns(new[] { main }, st).OrderBy(c => c == holder ? -1 : 0).ToList();
            var kp = K13MemoryCodec.Decode(K13MemoryCodec.Encode(PcLayerPlanner.Apply(main, st, hold), Raw()));
            Assert.Equal(PcLayerPlanner.HoldPool[0], kp.GetKey(holder));
            var target = PcLayerPlanner.Target(main, st, hold);
            foreach (var k in st.Keys) Assert.True(PcLayerPlanner.SameCell(target, kp, k.Column), $"кнопка {k.Column}");
            Assert.Empty(PcLayerPlanner.Compare(main, st, kp, hold));
            Assert.True(PcLayerPlanner.LooksPrepared(st, kp));
            // v5.10.0's per-key check compared with the chord cell only → false «не подготовлена: … Меню (App)»
            Assert.NotEqual(PcLayerPlanner.CellFor(st, st.Keys.First(k => k.Column == holder)), kp.GetKey(holder));
            // after «Отдать удержание» to the other key, the old image really differs until «Записать»
            var hold2 = hold.OrderBy(c => c == holder ? 1 : 0).ToList();
            Assert.NotEmpty(PcLayerPlanner.Compare(main, st, kp, hold2));
        }
    }

    [Fact]
    public void Real_hold_dumps_send_Menu_on_one_key()
    {
        var a = K13MemoryCodec.Decode(Dump("k13-postwrite-20261117-143056.bin"));
        var b = K13MemoryCodec.Decode(Dump("k13-postwrite-20261117-144428.bin"));
        int ca = Enumerable.Range(0, 16).Single(c => a.GetKey(c) == PcLayerPlanner.HoldPool[0]);
        int cb = Enumerable.Range(0, 16).Single(c => b.GetKey(c) == PcLayerPlanner.HoldPool[0]);
        Assert.Equal(1, ca);                                                     // key 2 at 14:30
        Assert.Equal(2, cb);                                                     // key 3 after «Отдать удержание» at 14:44
        Assert.True(KeyCatalog.IsMacro(b.GetKey(1)));                            // key 2 back to its chord (start / stop)
        // replug check at 14:35 matched the 14:30 write
        Assert.Equal(Dump("k13-postwrite-20261117-143056.bin"), Dump("k13-afterreplug-20261117-143556.bin"));
    }
}
