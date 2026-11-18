namespace Vorotex.K13;

/// <summary>1.3.0: different lighting for Normal and Game. The K13 keeps ONE lighting block (BASE…BASE+0x1F) for both modes —
/// the stick switches the keymap table only. So the app rewrites the lighting block when it sees the mode change (status byte 3,
/// GetFeature polling) — only while the app runs, through the same confirmed lighting-only write, and BASE+0 (the mode flags byte)
/// is written back exactly as read, so the write never changes the mode.</summary>
public static class ModeLighting
{
    /// <summary>Stored value: lighting off in Normal.</summary>
    public const string Off = "<off>";
    /// <summary>Wear limit: at most this many mode-lighting writes per hour (≈0.2 s each, one 32-byte area).</summary>
    public const int MaxPerHour = 40;
    /// <summary>Wait after the mode change before writing (quick double presses of the stick write once).</summary>
    public const int SettleMs = 1200;

    public static bool Enabled(string? choice) => !string.IsNullOrEmpty(choice);

    /// <summary>The lighting for Normal, or null when the profile lighting is used in both modes (or the chosen profile is gone).</summary>
    public static LedSettings? NormalLed(string? choice, IEnumerable<AppProfile> profiles, LedSettings current)
    {
        if (!Enabled(choice)) return null;
        if (choice == Off) { var o = current.Clone(); o.Mode = 0; return o; }
        var p = profiles.FirstOrDefault(x => x.Name == choice);
        return p?.Device?.Led.Clone();
    }

    public static string Describe(string? choice) => !Enabled(choice) ? "как в Game (одинаковая)" : choice == Off ? "выключена" : $"как в профиле «{choice}»";

    /// <summary>The profile target with <paramref name="led"/> as its lighting; BASE+0 (flags, bit 7 = mode) from the keypad image, so a
    /// lighting write in Normal does not ask the keypad for Game.</summary>
    public static K13DeviceConfig WithLed(K13DeviceConfig target, LedSettings led, byte[]? image)
    {
        var c = target.Clone();
        byte flags = target.Led.Flags;
        try { if (image != null) { int bs = K13MemoryCodec.ReadBase(image); if (bs < image.Length) flags = image[bs]; } } catch { }
        c.Led = led.Clone();
        c.Led.Flags = flags;
        return c;
    }

    /// <summary>The same with only BASE+0 taken from the image (Game side of the feature).</summary>
    public static K13DeviceConfig KeepFlags(K13DeviceConfig target, byte[]? image) => WithLed(target, target.Led, image);
}

/// <summary>Rate limit for mode-change lighting writes (pure, unit-tested).</summary>
public sealed class ModeLightingGate
{
    private readonly Queue<long> _writes = new();
    public int WritesLastHour(long nowMs) { Trim(nowMs); return _writes.Count; }
    public bool CanWrite(long nowMs) { Trim(nowMs); return _writes.Count < ModeLighting.MaxPerHour; }
    public void Wrote(long nowMs) { Trim(nowMs); _writes.Enqueue(nowMs); }
    private void Trim(long nowMs) { while (_writes.Count > 0 && nowMs - _writes.Peek() >= 3_600_000) _writes.Dequeue(); }
}
