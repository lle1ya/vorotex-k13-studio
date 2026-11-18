using Xunit;

namespace Vorotex.K13.Tests;

/// <summary>1.0.4: user log 18.11.2026 11:01:32–11:01:52 — «1С» was written while the user already switched to «Основной» (11:01:40,
/// during the write); the switch was lost and the keypad kept the 1С colour. Now a write ends with a re-check and the gate
/// accepts a target while the cached image is stale; the active profile always wins.</summary>
public class V104Tests
{
    static readonly DateTime T0 = new(2026, 11, 18, 11, 1, 32);
    static DateTime At(double s) => T0.AddSeconds(s);

    [Fact]
    public void Log_sequence_ends_with_the_active_profile_written()
    {
        var g = new AutoLightingGate();
        var written = new List<string>();
        g.Switched("1С", At(0.04));                                             // 11:01:32.97
        Assert.Equal(AutoLightingGate.Verdict.Write, g.Decide(At(2.6), "1С", true));
        g.Wrote(At(2.6)); written.Add("1С");
        // 11:01:40.1: switch to «Основной» while that write runs — queued even though the stale image says «same»
        g.Switched("Основной", At(8.1));
        // write finished; tick after busy: keypad now 1С → lighting differs from «Основной»
        var due = g.DueAt(At(8.3))!.Value;
        Assert.True(due >= At(8.1) + AutoLightingGate.Stable);
        Assert.Equal(AutoLightingGate.Verdict.Write, g.Decide(due, "Основной", true));
        g.Wrote(due); written.Add("Основной");
        Assert.Equal(new[] { "1С", "Основной" }, written);
    }

    [Fact]
    public void Recheck_after_a_write_keeps_the_switch_time()
    {
        var g = new AutoLightingGate();
        g.Switched("A", At(0)); g.Wrote(At(2.5));
        g.Recheck("B", At(1));                                                  // B became active at 1 s, during A's write
        Assert.Equal(At(2.5) + AutoLightingGate.MinInterval, g.DueAt(At(3)));   // min interval, not another 2.5 s from now
        g.Recheck("B", At(5));                                                  // same target: the original time stays
        Assert.Equal(At(1), g.SwitchedAt);
    }

    [Fact]
    public void Rate_limited_switch_is_deferred_not_dropped()
    {
        var g = new AutoLightingGate();
        g.Switched("A", At(0)); g.Wrote(At(2.5));
        g.Switched("B", At(3));
        Assert.Equal(AutoLightingGate.Verdict.Wait, g.Decide(At(5.5), "B", true));
        Assert.Equal("B", g.PendingProfile);
        Assert.Equal(AutoLightingGate.Verdict.Write, g.Decide(g.DueAt(At(5.5))!.Value, "B", true));
    }

    [Fact]
    public void Back_and_forth_ends_on_the_last_profile()
    {
        var g = new AutoLightingGate();
        string keypad = "A";
        string active = "A";
        double t = 0;
        foreach (var p in new[] { "B", "A", "B", "A", "B" })
        {
            active = p; g.Switched(p, At(t));
            t += 1.5;                                                           // faster than Stable — nothing written meanwhile
        }
        var due = g.DueAt(At(t))!.Value;
        Assert.Equal(AutoLightingGate.Verdict.Write, g.Decide(due, active, keypad != active));
        g.Wrote(due); keypad = active;
        Assert.Equal("B", keypad);
    }
}
