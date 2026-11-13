namespace Vorotex.K13;

/// <summary>A single 8-byte feature report to send, with an optional pause afterwards.</summary>
public sealed record HidFrame(byte[] Data, int SleepAfterMs, string Note)
{
    public override string ToString() => string.Join(" ", Data.Select(x => x.ToString("X2"))) + (SleepAfterMs > 0 ? $"  ; sleep {SleepAfterMs}" : "") + (Note.Length > 0 ? "  ; " + Note : "");
}

/// <summary>Builds feature-report frames (Report ID 7, 8 bytes). Pure – sends nothing.</summary>
public static class K13Protocol
{
    // ---- read path (approved, read-only) ----
    public static byte[] LoadBlock(int addr) => new byte[] { 7, 0x18, 0x00, 0x00, (byte)addr, (byte)(addr >> 8), 0, 0 };
    public static byte[] ReadByte(int addr, int index, int count) => new byte[] { 7, 0x18, 0x05, (byte)index, (byte)addr, (byte)(addr >> 8), 0, (byte)(count - 1) };
    public static byte[] EndRead() => new byte[] { 7, 0x18, 0x01, 0, 0, 0, 0, 0 };

    /// <summary>
    /// v4.5: end of every HID session (after any read or write), exactly as the vendor app leaves the keypad:
    /// 07 18 11 (+10 ms) and 07 18 00 (all zero, +10 ms) – its connect/Apply tail (FUN_00414370 / FUN_0040fe10) –
    /// then 07 20 (+20 ms) – sent by the vendor app when its window closes. Without this the firmware stayed in
    /// "transfer" state after our reads and stopped sending key presses until the USB cable was replugged (15:50, 13.11.2026).
    /// No memory is written by these frames.
    /// </summary>
    public static IReadOnlyList<HidFrame> SessionEndFrames { get; } = new HidFrame[]
    {
        new(new byte[] { 7, 0x18, 0x11, 0, 0, 0, 0, 0 }, 10, "завершение сеанса (07 18 11)"),
        new(new byte[] { 7, 0x18, 0x00, 0, 0, 0, 0, 0 }, 10, "сброс (07 18 00)"),
        new(new byte[] { 7, 0x20, 0, 0, 0, 0, 0, 0 }, 20, "выход из режима настройки (07 20)"),
    };

    /// <summary>Session-end frames (07 18 11 / 07 20 with zero payload): allowed without the write gate, they write no memory.</summary>
    public static bool IsSessionEndFrame(ReadOnlySpan<byte> f) =>
        f.Length == 8 && f[0] == 7 && f[3] == 0 && f[4] == 0 && f[5] == 0 && f[6] == 0 && f[7] == 0 &&
        ((f[1] == 0x18 && f[2] == 0x11) || (f[1] == 0x20 && f[2] == 0));

    /// <summary>True only for the commands approved as read-only: 07 18 00 (load to buffer), 07 18 05 (read buffer), 07 18 01 (end).</summary>
    public static bool IsReadOnlyFrame(ReadOnlySpan<byte> f) =>
        f.Length == 8 && f[0] == 7 && f[1] == 0x18 &&
        ((f[2] == 0x00 && f[3] == 0 && f[6] == 0 && f[7] == 0) ||   // load block
         (f[2] == 0x05 && f[3] < 8 && f[6] == 0) ||                  // read buffer byte
         (f[2] == 0x01 && f[3] == 0 && f[6] == 0 && f[7] == 0));     // end

    // ---- write path (vendor Apply, PROTOCOL.md §13). NEVER sent unless the write gate is open. ----
    /// <param name="programmingMode">Send the vendor's leading 07 09 41 (+200 ms). exp15 proved writes work without it.</param>
    public static List<HidFrame> BuildApplyFrames(IReadOnlyList<WriteBlock> plan, bool programmingMode = false)
    {
        var frames = new List<HidFrame>();
        if (programmingMode) frames.Add(new(new byte[] { 7, 0x09, 0x41, 0, 0, 0, 0, 0 }, 200, "режим программирования (07 09 41)"));
        foreach (var blk in plan)
        {
            int n = blk.Data.Length, a = blk.Address;
            if (n is < 1 or > 8) throw new ArgumentException("Блок записи должен быть 1..8 байт.");
            for (int i = 0; i < n; i++)
                frames.Add(new(new byte[] { 7, 0x18, 0x03, (byte)i, (byte)a, (byte)(a >> 8), blk.Data[i], (byte)(n - 1) }, i == n - 1 ? 2 : 0, i == 0 ? $"{blk.Purpose} @0x{a:X4}" : ""));
            frames.Add(new(new byte[] { 7, 0x18, 0x09, 0, (byte)a, (byte)(a >> 8), 0, (byte)(n - 1) }, 10, "commit"));
            frames.Add(new(new byte[] { 7, 0x18, 0x01, 0, (byte)a, (byte)(a >> 8), 0, 0 }, 0, ""));
        }
        frames.Add(new(new byte[] { 7, 0x18, 0x11, 0, 0, 0, 0, 0 }, 10, "завершение записи"));
        frames.Add(new(new byte[] { 7, 0x18, 0x00, 0, 0, 0, 0, 0 }, 0, ""));
        return frames;
    }

    /// <summary>Only blocks whose bytes differ between the device image and the target image (for a minimal write).</summary>
    public static List<WriteBlock> DiffPlan(IReadOnlyList<WriteBlock> full, byte[] deviceImage) =>
        full.Where(b => b.Address + b.Data.Length > deviceImage.Length ||
                        !deviceImage.AsSpan(b.Address, b.Data.Length).SequenceEqual(b.Data)).ToList();
}
