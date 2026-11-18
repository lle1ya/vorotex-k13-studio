namespace Vorotex.K13;

/// <summary>1.0.3/1.0.4: lighting follows an auto-switch (profile by the active program) — with wear protection.
/// The keypad stores lighting in EEPROM, so an auto write happens only when the target profile has been stable for
/// <see cref="Stable"/>, at most once per <see cref="MinInterval"/> and at most <see cref="MaxPerHour"/> times per rolling hour.
/// 1.0.4: nothing is dropped because of the limits — the write is deferred until they allow it; the window re-checks after every
/// write (a switch during a write used to be lost: user log 18.11.2026 11:01:40). Only the profile active at write time is written.
/// Pure logic; the window owns the timer.</summary>
public sealed class AutoLightingGate
{
    public static readonly TimeSpan Stable = TimeSpan.FromSeconds(2.5);
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(4);
    public const int MaxPerHour = 30;
    static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private readonly List<DateTime> _writes = new();
    public string? PendingProfile { get; private set; }
    public DateTime SwitchedAt { get; private set; }

    /// <summary>An auto-switch to <paramref name="profile"/> happened: it becomes the only pending target.</summary>
    public void Switched(string profile, DateTime now) { PendingProfile = profile; SwitchedAt = now; }

    /// <summary>Re-check after a write: keep the original switch time (the stable wait already ran).</summary>
    public void Recheck(string profile, DateTime switchedAt)
    {
        if (PendingProfile == profile) return;
        PendingProfile = profile; SwitchedAt = switchedAt;
    }

    /// <summary>A manual switch / disconnect / setting off: drop the pending target.</summary>
    public void Cancel() => PendingProfile = null;

    public int WritesLastHour(DateTime now) { Trim(now); return _writes.Count; }

    /// <summary>True when the hourly cap is what delays the pending write.</summary>
    public bool CapReached(DateTime now) { Trim(now); return _writes.Count >= MaxPerHour; }

    /// <summary>When the pending target may be written (null = nothing pending). Never null because of the limits.</summary>
    public DateTime? DueAt(DateTime now)
    {
        if (PendingProfile == null) return null;
        Trim(now);
        var due = SwitchedAt + Stable;
        if (_writes.Count > 0 && _writes[^1] + MinInterval > due) due = _writes[^1] + MinInterval;
        if (_writes.Count >= MaxPerHour && _writes[_writes.Count - MaxPerHour] + Hour > due) due = _writes[_writes.Count - MaxPerHour] + Hour;
        return due < now ? now : due;
    }

    public enum Verdict { Write, Wait, NothingPending, NotActiveAnymore, SameLighting }

    /// <summary>Decision at a timer tick. <paramref name="activeProfile"/>: the profile active now;
    /// <paramref name="lightingOnlyPending"/>: the keypad differs from it only in lighting.</summary>
    public Verdict Decide(DateTime now, string activeProfile, bool lightingOnlyPending)
    {
        if (PendingProfile == null) return Verdict.NothingPending;
        if (!string.Equals(PendingProfile, activeProfile, StringComparison.Ordinal)) { PendingProfile = null; return Verdict.NotActiveAnymore; }
        if (!lightingOnlyPending) { PendingProfile = null; return Verdict.SameLighting; }
        if (DueAt(now) is DateTime d && d > now) return Verdict.Wait;
        return Verdict.Write;
    }

    /// <summary>The write was started for the pending target.</summary>
    public void Wrote(DateTime now) { _writes.Add(now); PendingProfile = null; }

    private void Trim(DateTime now) => _writes.RemoveAll(t => now - t >= Hour);
}
