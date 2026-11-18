namespace Vorotex.K13;

/// <summary>1.0.3: lighting follows an auto-switch (profile by the active program) — with wear protection.
/// The keypad stores lighting in EEPROM, so an auto write happens only when the target profile has been stable for
/// <see cref="Stable"/>, at most once per <see cref="MinInterval"/> and at most <see cref="MaxPerHour"/> times per rolling hour.
/// A newer switch replaces the pending target (so quick Alt+Tab never writes). Pure logic; the window owns the timer.</summary>
public sealed class AutoLightingGate
{
    public static readonly TimeSpan Stable = TimeSpan.FromSeconds(2.5);
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(10);
    public const int MaxPerHour = 30;

    private readonly List<DateTime> _writes = new();
    public string? PendingProfile { get; private set; }
    public DateTime SwitchedAt { get; private set; }

    /// <summary>An auto-switch to <paramref name="profile"/> happened: it becomes the only pending target.</summary>
    public void Switched(string profile, DateTime now) { PendingProfile = profile; SwitchedAt = now; }

    /// <summary>A manual switch / write / disconnect: drop the pending target.</summary>
    public void Cancel() => PendingProfile = null;

    public int WritesLastHour(DateTime now) { Trim(now); return _writes.Count; }

    /// <summary>When the pending target may be written (null = nothing pending or the hourly cap is reached).</summary>
    public DateTime? DueAt(DateTime now)
    {
        if (PendingProfile == null) return null;
        Trim(now);
        if (_writes.Count >= MaxPerHour) return null;
        var due = SwitchedAt + Stable;
        if (_writes.Count > 0 && _writes[^1] + MinInterval > due) due = _writes[^1] + MinInterval;
        return due < now ? now : due;
    }

    public enum Verdict { Write, Wait, NothingPending, NotActiveAnymore, SameLighting, HourlyCap }

    /// <summary>Decision at a timer tick. <paramref name="activeProfile"/>: the profile active now;
    /// <paramref name="lightingOnlyPending"/>: the keypad differs from it only in lighting.</summary>
    public Verdict Decide(DateTime now, string activeProfile, bool lightingOnlyPending)
    {
        if (PendingProfile == null) return Verdict.NothingPending;
        if (!string.Equals(PendingProfile, activeProfile, StringComparison.Ordinal)) { PendingProfile = null; return Verdict.NotActiveAnymore; }
        Trim(now);
        if (_writes.Count >= MaxPerHour) { PendingProfile = null; return Verdict.HourlyCap; }
        if (DueAt(now) is DateTime d && d > now) return Verdict.Wait;
        if (!lightingOnlyPending) { PendingProfile = null; return Verdict.SameLighting; }
        return Verdict.Write;
    }

    /// <summary>The write was started for the pending target.</summary>
    public void Wrote(DateTime now) { _writes.Add(now); PendingProfile = null; }

    private void Trim(DateTime now) => _writes.RemoveAll(t => now - t >= TimeSpan.FromHours(1));
}
