using System.Text.Json;
using Xunit;

namespace Vorotex.K13.Tests;

/// <summary>1.0.3: lighting follows an auto-switch — debounced, rate-limited, skipped when the keypad already has it.</summary>
public class V103Tests
{
    static readonly DateTime T0 = new(2026, 11, 18, 10, 0, 0);
    static DateTime At(double s) => T0.AddSeconds(s);
    const AutoLightingGate.Verdict W = AutoLightingGate.Verdict.Write, Wait = AutoLightingGate.Verdict.Wait;

    [Fact]
    public void Waits_until_the_profile_is_stable()
    {
        var g = new AutoLightingGate();
        g.Switched("1С", At(0));
        Assert.Equal(At(AutoLightingGate.Stable.TotalSeconds), g.DueAt(At(0)));
        Assert.Equal(Wait, g.Decide(At(1), "1С", true));
        Assert.Equal(W, g.Decide(At(2.6), "1С", true));
    }

    [Fact]
    public void Quick_switching_never_writes_the_intermediate_profiles()
    {
        var g = new AutoLightingGate();
        g.Switched("A", At(0)); g.Switched("B", At(1)); g.Switched("C", At(2));
        Assert.Equal("C", g.PendingProfile);
        Assert.Equal(Wait, g.Decide(At(3), "C", true));                      // C switched at 2 s → due 4.5 s
        Assert.Equal(W, g.Decide(At(4.6), "C", true));
    }

    [Fact]
    public void Profile_changed_away_cancels()
    {
        var g = new AutoLightingGate();
        g.Switched("A", At(0));
        Assert.Equal(AutoLightingGate.Verdict.NotActiveAnymore, g.Decide(At(3), "B", true));
        Assert.Null(g.PendingProfile);
        Assert.Equal(AutoLightingGate.Verdict.NothingPending, g.Decide(At(4), "A", true));
    }

    [Fact]
    public void Same_lighting_is_not_written()
    {
        var g = new AutoLightingGate();
        g.Switched("A", At(0));
        Assert.Equal(AutoLightingGate.Verdict.SameLighting, g.Decide(At(3), "A", false));
        Assert.Equal(0, g.WritesLastHour(At(3)));
    }

    [Fact]
    public void Cancel_drops_the_pending_target()
    {
        var g = new AutoLightingGate();
        g.Switched("A", At(0)); g.Cancel();
        Assert.Null(g.DueAt(At(1)));
        Assert.Equal(AutoLightingGate.Verdict.NothingPending, g.Decide(At(3), "A", true));
    }

    [Fact]
    public void Min_interval_between_writes()
    {
        var g = new AutoLightingGate();
        g.Switched("A", At(0)); Assert.Equal(W, g.Decide(At(3), "A", true)); g.Wrote(At(3));
        g.Switched("B", At(3.5));
        Assert.Equal(At(3) + AutoLightingGate.MinInterval, g.DueAt(At(3.5)));   // 7 s, not 6 s
        Assert.Equal(Wait, g.Decide(At(6.5), "B", true));
        Assert.Equal(W, g.Decide(At(7), "B", true));
    }

    /// <summary>1.0.4: the hourly cap defers the write (never drops it); it runs when the oldest write leaves the window.</summary>
    [Fact]
    public void Hourly_cap_defers_not_drops()
    {
        var g = new AutoLightingGate();
        double t = 0;
        for (int i = 0; i < AutoLightingGate.MaxPerHour; i++)
        {
            g.Switched("P" + i, At(t));
            var due = g.DueAt(At(t))!.Value;
            Assert.Equal(W, g.Decide(due, "P" + i, true));
            g.Wrote(due);
            if (i == 0) Assert.Equal(At(2.5), due);
            t = (due - T0).TotalSeconds + 1;
        }
        Assert.True(g.CapReached(At(t)));
        g.Switched("X", At(t));
        var deferred = g.DueAt(At(t));
        Assert.Equal(At(2.5) + TimeSpan.FromHours(1), deferred);               // first write + 1 h
        Assert.Equal(Wait, g.Decide(At(t + 20), "X", true));
        Assert.Equal("X", g.PendingProfile);                                   // still pending
        Assert.Equal(W, g.Decide(deferred!.Value, "X", true));
    }

    [Fact]
    public void Limits_are_sane()
    {
        Assert.InRange(AutoLightingGate.Stable.TotalSeconds, 2, 3);
        Assert.InRange(AutoLightingGate.MinInterval.TotalSeconds, 3, 5);
        Assert.InRange(AutoLightingGate.MaxPerHour, 10, 60);
    }

    [Fact]
    public void Setting_defaults_on_and_survives_json()
    {
        var old = JsonSerializer.Deserialize<ProfileStoreData>("{}")!;
        Assert.True(old.AutoSwitchLighting);
        var back = JsonSerializer.Deserialize<ProfileStoreData>(JsonSerializer.Serialize(new ProfileStoreData { AutoSwitchLighting = false }))!;
        Assert.False(back.AutoSwitchLighting);
    }

    [Fact]
    public void Auto_notes()
    {
        Assert.Contains("через пару секунд", ProfileSwitchLighting.Note(true, true, true, true, SwitchSource.Auto, true, AutoLed.On));
        Assert.Contains("выключена", ProfileSwitchLighting.Note(true, true, false, true, SwitchSource.Auto, true, AutoLed.Off));
        Assert.Contains("за час", ProfileSwitchLighting.Note(true, true, false, true, SwitchSource.Auto, true, AutoLed.Cap));
        Assert.Contains("применится позже", ProfileSwitchLighting.Note(true, true, true, true, SwitchSource.Auto, true, AutoLed.Cap));
        Assert.Equal("Подсветка в клавиатуре прежняя — нажмите «Записать».", ProfileSwitchLighting.Note(true, true, false, true, SwitchSource.Auto, true, AutoLed.On));
        Assert.Contains("первого подтверждения", ProfileSwitchLighting.Note(true, true, false, false, SwitchSource.Auto, true, AutoLed.On));
        Assert.Equal("", ProfileSwitchLighting.Note(false, false, false, true, SwitchSource.Auto, true, AutoLed.On));
    }
}
