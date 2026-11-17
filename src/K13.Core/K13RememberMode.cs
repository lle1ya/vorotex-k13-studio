namespace Vorotex.K13;

/// <summary>v5.9, EXPERIMENTAL: factory flag FN_M_MODE_SV_EN («remember mode») = EEPROM 0x000F bit 1 (docs/ANALYSIS-burn-tool.md §2.2,
/// PROTOCOL §31). Both known K13 hold 0x91 (bit 1 = 0). Setting it (0x93) should keep the last mode (Game) across power-up (≈60–65 %).
/// Only bit 1 of 0x000F is ever changed; 0x0000–0x0007 are refused by the protocol layer.</summary>
public static class K13RememberMode
{
    public const int Address = 0x000F;
    public const byte Bit = 0x02;
    /// <summary>The factory value on both known units (bit 1 cleared).</summary>
    public const byte Expected = 0x91;
    /// <summary>Factory flags block 0x0008–0x000F (read for checking that nothing else changes).</summary>
    public const int BlockAddress = 0x0008;

    public static bool IsOn(byte b) => (b & Bit) != 0;

    public static string Describe(byte? b) => b is not byte v ? "неизвестно" : $"{(IsOn(v) ? "включено" : "выключено")} (0x000F = {v:X2})";

    /// <summary>The value read is one this feature knows: 0x91 or 0x93 (everything except bit 1 as on the known units).</summary>
    public static bool IsKnownPattern(byte b) => (b & ~Bit & 0xFF) == Expected;

    /// <summary>Target byte for on/off: only bit 1 changes; null + reason when the read value is not the known pattern.</summary>
    public static (byte? Target, string? Error) Target(byte current, bool enable)
    {
        if (!IsKnownPattern(current))
            return (null, $"В ячейке 0x000F прочитано 0x{current:X2}, а ожидалось 0x91 или 0x93. Это незнакомая прошивка или ошибка чтения — программа ничего не записывает.");
        return ((byte)(enable ? current | Bit : current & ~Bit), null);
    }

    /// <summary>Target for «restore from backup»: the backup byte, only if it differs from the current one in bit 1 at most and is a known pattern.</summary>
    public static (byte? Target, string? Error) RestoreTarget(byte current, byte[] backup)
    {
        if (backup.Length <= Address) return (null, "В файле резервной копии нет адреса 0x000F.");
        byte b = backup[Address];
        if (!IsKnownPattern(b)) return (null, $"В резервной копии 0x000F = 0x{b:X2} — незнакомое значение, восстанавливать его программа не будет.");
        if (!IsKnownPattern(current)) return (null, $"В клавиатуре 0x000F = 0x{current:X2} — незнакомое значение, программа ничего не записывает.");
        return (b, null);
    }

    public static WriteBlock Plan(byte target) => new(Address, new[] { target }, "Запоминать режим (0x000F)");

    public sealed record Result(bool Ok, bool Written, byte Before, byte After, byte? BaseFlags, string BackupPath, string Message);

    /// <summary>Backup (full verified read) → read 0x0008–0x000F → check → write ONE byte at 0x000F (07 18 03 / 09 / 01, 07 18 11, 07 18 00)
    /// → read back and verify (0x0008–0x000E unchanged). The caller opens the write gate only after the user confirmed.</summary>
    /// <param name="decide">gets the current byte, returns the target (or an error).</param>
    public static Result Run(K13Device dev, Func<byte, (byte? Target, string? Error)> decide, Action<string> log, string? backupFolder = null)
    {
        var st = dev.GetStatus();
        log($"[remember] статус: {st.Hex}");
        var img = dev.ReadImageVerified();
        var backup = BackupStore.Save(img, backupFolder, "k13-remember-before", st);
        int baseAddr = st.Base > 0 ? st.Base : K13Layout.DefaultBase;
        byte? baseFlags = baseAddr < img.Length ? img[baseAddr] : null;
        log($"[remember] резервная копия перед изменением: {backup}; 0x0008–0x000F = {Convert.ToHexString(img, BlockAddress, 8)}; BASE+0 = {(baseFlags is byte bf ? bf.ToString("X2") : "—")}");
        var block = dev.ReadBlocks(new[] { (BlockAddress, 8) })[0];
        byte cur = block[Address - BlockAddress];
        if (cur != img[Address])
            return new(false, false, cur, cur, baseFlags, backup, $"Два чтения 0x000F разошлись (0x{img[Address]:X2} и 0x{cur:X2}). Ничего не записано — попробуйте ещё раз.");
        var (target, err) = decide(cur);
        if (target is not byte t) { log("[remember] отказ: " + err); return new(false, false, cur, cur, baseFlags, backup, err ?? "Отменено."); }
        if (t == cur) { log($"[remember] 0x000F уже 0x{cur:X2} — запись не нужна."); return new(true, false, cur, cur, baseFlags, backup, $"Ничего не нужно менять: 0x000F уже 0x{cur:X2}."); }
        if ((t ^ cur) != Bit) return new(false, false, cur, cur, baseFlags, backup, "Внутренняя проверка: меняться может только бит 1. Ничего не записано.");
        log($"[remember] запись 0x000F: {cur:X2} → {t:X2} (один байт, 07 18 03 / 09 / 01, затем 07 18 11, 07 18 00)");
        dev.Apply(new[] { Plan(t) });
        // v5.9.1: the first read after a write may echo the written byte into index 0 (0x0008 read as 93) — stable read, discard first
        var after = dev.ReadBlocksAfterWrite(new[] { (BlockAddress, 8) })[0];
        var diffs = Differences(block, after, t);
        if (diffs.Count > 0)
        {
            log("[remember] после записи расхождение: " + string.Join("; ", diffs) + " — перечитываю ещё раз.");
            K13Device.PreciseDelay(150);
            after = dev.ReadBlocksAfterWrite(new[] { (BlockAddress, 8) })[0];
            diffs = Differences(block, after, t);
        }
        byte got = after[Address - BlockAddress];
        byte? baseAfter = null;
        try { baseAfter = dev.ReadBlocksAfterWrite(new[] { (baseAddr, 1) })[0][0]; } catch (IOException) { }
        log($"[remember] после записи: 0x0008–0x000F = {Convert.ToHexString(after)}; BASE+0 = {(baseAfter is byte ba ? ba.ToString("X2") : "—")}");
        if (diffs.Count > 0)
            return new(false, true, cur, got, baseAfter, backup, "Проверка после записи не прошла: " + string.Join("; ", diffs) +
                $". Сделайте «Прочитать с клавиатуры», чтобы проверить ещё раз. Резервная копия: {backup}. Сохраните диагностику и пришлите нам.");
        return new(true, true, cur, got, baseAfter, backup, $"Записано и проверено: 0x000F = 0x{got:X2} — «запоминать режим» {(IsOn(got) ? "включено" : "выключено")}.");
    }

    /// <summary>Exact byte differences after the write: 0x000F vs the target, 0x0008–0x000E vs the bytes read before.</summary>
    public static List<string> Differences(byte[] before, byte[] after, byte target)
    {
        var d = new List<string>();
        for (int i = 0; i < 8; i++)
        {
            int a = BlockAddress + i;
            byte want = a == Address ? target : before[i];
            if (after[i] != want)
                d.Add(a == Address ? $"0x{a:X4}: должно быть {want:X2}, прочитано {after[i]:X2}" : $"0x{a:X4}: было {want:X2}, прочитано {after[i]:X2} (этот байт не записывался)");
        }
        return d;
    }
}
