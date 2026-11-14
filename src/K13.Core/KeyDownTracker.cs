namespace Vorotex.K13;

/// <summary>
/// v5.3: press/auto-repeat bookkeeping for trigger keys (hook thread). Windows repeats only a key that is still held,
/// so a key-down that comes after a long silence is a NEW press whose previous key-up was lost (secure desktop, UAC,
/// Win+L, another hook). Before, such a lost key-up made every later press of that trigger look like auto-repeat:
/// the key silently stopped working (or the next tap was taken for a very long hold).
/// </summary>
public sealed class KeyDownTracker(int staleMs = KeyDownTracker.DefaultStaleMs)
{
    /// <summary>Longer than the slowest Windows typematic delay (1 s) + one repeat period.</summary>
    public const int DefaultStaleMs = 1500;

    private readonly Dictionary<ushort, long> _downAt = new(), _lastSeen = new();

    public int StaleMs { get; } = staleMs;

    /// <summary>Registers a key-down at <paramref name="nowMs"/>. Returns true for a real press, false for auto-repeat.</summary>
    public bool Down(ushort vk, long nowMs)
    {
        bool repeat = _downAt.ContainsKey(vk) && _lastSeen.TryGetValue(vk, out var last) && nowMs - last < StaleMs;
        _lastSeen[vk] = nowMs;
        if (repeat) return false;
        _downAt[vk] = nowMs;
        return true;
    }

    /// <summary>Registers a key-up. Returns how long the key was held, or null if no press was known.</summary>
    public double? Up(ushort vk, long nowMs)
    {
        _lastSeen.Remove(vk);
        return _downAt.Remove(vk, out var t) ? nowMs - t : null;
    }

    public bool IsDown(ushort vk) => _downAt.ContainsKey(vk);

    public void Clear() { _downAt.Clear(); _lastSeen.Clear(); }
}
