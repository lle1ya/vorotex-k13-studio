using System.Text.Json;
using Vorotex.K13;
using Xunit;

namespace K13.Core.Tests;

public class RingTests
{
    [Fact]
    public void Default_HasEightUsefulSlots()
    {
        var r = RingSettings.CreateDefault();
        Assert.Equal(8, r.Slots.Count);
        Assert.Equal(new[] { "Копировать", "Вставить", "Скриншот", "Диспетчер задач", "Рабочий стол", "Плеер", "Браузер", "Заблокировать" }, r.Slots.Select(s => s.Title));
        Assert.All(r.Slots, s => Assert.NotEqual(HostActionKind.None, s.Action.Kind));
        Assert.Equal("Win+Shift+S", r.Slots[2].Action.Value);
        Assert.Equal("Win+L", r.Slots[7].Action.Value);
    }

    [Theory]
    [InlineData(0, -50, 8, 0)]   // up
    [InlineData(50, -50, 8, 1)]  // up-right
    [InlineData(50, 0, 8, 2)]    // right
    [InlineData(50, 50, 8, 3)]
    [InlineData(0, 50, 8, 4)]    // down
    [InlineData(-50, 50, 8, 5)]
    [InlineData(-50, 0, 8, 6)]   // left
    [InlineData(-50, -50, 8, 7)]
    [InlineData(-5, -60, 8, 0)]  // slightly left of up still slot 0
    [InlineData(3, 3, 8, -1)]    // dead zone
    [InlineData(40, -20, 6, 1)]  // 6 slots: slot 1 is 60°±30
    public void SlotAt_MapsDirections(double dx, double dy, int count, int expected) =>
        Assert.Equal(expected, RingMath.SlotAt(dx, dy, count, 20));

    [Fact]
    public void Stick_EightDirections()
    {
        Assert.Equal(-1, RingMath.SlotFromStick(false, false, false, false, 8));
        Assert.Equal(0, RingMath.SlotFromStick(true, false, false, false, 8));
        Assert.Equal(1, RingMath.SlotFromStick(true, false, false, true, 8));
        Assert.Equal(2, RingMath.SlotFromStick(false, false, false, true, 8));
        Assert.Equal(4, RingMath.SlotFromStick(false, true, false, false, 8));
        Assert.Equal(5, RingMath.SlotFromStick(false, true, true, false, 8));
        Assert.Equal(7, RingMath.SlotFromStick(true, false, true, false, 8));
    }

    [Fact]
    public void Profile_WithRing_SurvivesJson_AndOldProfilesGetDefault()
    {
        var p = new AppProfile { Name = "x" };
        p.Host.Ring.Slots[0].Title = "Своё";
        p.Host.Keys.Add(new HostKeyBinding { TriggerVk = 0x7C, Tap = new HostAction { Kind = HostActionKind.Ring } });
        var back = p.Clone();
        Assert.Equal("Своё", back.Host.Ring.Slots[0].Title);
        Assert.Equal(8, back.Host.Ring.Slots.Count);
        Assert.Equal(HostActionKind.Ring, back.Host.Keys[0].Tap.Kind);

        var old = JsonSerializer.Deserialize<HostSettings>("{\"Enabled\":true,\"Keys\":[],\"Chords\":[]}", ProfileStore.JsonOptions)!;
        Assert.Equal(8, old.Ring.Slots.Count);
    }
}

public class StickNavTests
{
    [Theory]
    [InlineData(-1, 2, 8, 2)]  // nothing highlighted: → jumps right
    [InlineData(0, 2, 8, 1)]   // at top, → : one step to up-right
    [InlineData(1, 2, 8, 2)]   // again → : right
    [InlineData(2, 4, 8, 3)]   // at right, ↓ : down-right (Диспетчер задач)
    [InlineData(0, 4, 8, 4)]   // opposite: jump
    [InlineData(4, 6, 8, 5)]   // at bottom, ← : down-left
    [InlineData(0, 6, 8, 7)]   // at top, ← : up-left
    [InlineData(2, 2, 8, 2)]   // already there
    public void StepToward_Works(int cur, int target, int n, int expected) => Assert.Equal(expected, RingMath.StepToward(cur, target, n));

    [Fact]
    public void EverySlotReachable_WithFourDirections()
    {
        foreach (int n in new[] { 4, 5, 6, 7, 8 })
        {
            var seen = new HashSet<int>();
            var dirs = new[] { (true, false, false, false), (false, true, false, false), (false, false, true, false), (false, false, false, true) };
            var frontier = new Queue<int>(); frontier.Enqueue(-1);
            while (frontier.Count > 0)
            {
                int cur = frontier.Dequeue();
                foreach (var (u, d, l, r) in dirs)
                {
                    int nx = RingMath.StepToward(cur, RingMath.SlotFromStick(u, d, l, r, n), n);
                    if (seen.Add(nx)) frontier.Enqueue(nx);
                }
            }
            Assert.Equal(n, seen.Count);
        }
    }
}
