using System.Diagnostics;

namespace Vorotex.K13;

public sealed record K13WriteResult(bool Ok, bool NothingToDo, bool Restored, int Blocks, string? BackupBefore, string? BackupAfter,
    bool KeysChanged, bool LightingChanged, byte[]? After, string Message, string Timing = "")
{
    /// <summary>v5.2: the header/layout of this unit is not certain — nothing was written; ask the user and call again
    /// with <c>acceptedHeader</c> = <see cref="K13HeaderInfo.RawHex"/> of <see cref="Header"/>.</summary>
    public bool NeedsConfirmation { get; init; }
    public K13HeaderInfo? Header { get; init; }
    /// <summary>v5.7.3: the blocks that were written (for the replug check).</summary>
    public IReadOnlyList<WriteBlock>? Written { get; init; }
    /// <summary>1.2.0: set when the write was refused because the keypad is not a K13 (After = its image as read).</summary>
    public KeypadModel? Model { get; init; }
}

/// <summary>
/// Safe and fast write (v0.4.4):
/// 1. «before» = the image read earlier in this session (cached) if the touched blocks still match it on the device
///    (one quick read of just those blocks + header), otherwise a full verified read.
/// 2. backup of «before» → encode the profile on top → write only the changed blocks (proven 03…/09/01, then 11/00).
/// 3. read back only the written blocks; on a mismatch read them again (up to 3 passes, slower) before deciding.
/// 4. a confirmed mismatch → restore those blocks from «before» and verify.
/// </summary>
public static class K13Writer
{
    public const int TrailerFrom = 0x023C, TrailerTo = 0x023F;
    public const int KeymapFrom = 0x0128, KeymapTo = 0x01B8, MacrosFrom = 0x01D8, LedFrom = 0x00F8, LedTo = 0x0100;
    static bool Trailer(int i) => i is >= TrailerFrom and <= TrailerTo;

    /// <summary>8-aligned blocks (addr, len) covering the given write blocks.</summary>
    public static List<(int Addr, int Len)> Cover(IEnumerable<WriteBlock> blocks, int limit)
    {
        var set = new SortedSet<int>();
        foreach (var b in blocks)
            for (int a = b.Address & ~7; a < b.Address + b.Data.Length; a += 8) set.Add(a);
        return set.Where(a => a < limit).Select(a => (a, Math.Min(8, limit - a))).ToList();
    }

    public static K13WriteResult WriteProfile(K13Device dev, K13DeviceConfig cfg, Action<string> log,
        IProgress<(string Stage, double Fraction)>? progress = null, bool programmingMode = false, string? backupFolder = null,
        byte[]? cachedImage = null, string? acceptedHeader = null, bool keepModeFlags = false)
    {
        var sw = Stopwatch.StartNew();
        var times = new List<string>();
        void Mark(string what) { times.Add($"{what} {sw.ElapsedMilliseconds} мс"); sw.Restart(); }
        void P(string s, double f) => progress?.Report((s, f));
        var st = dev.GetStatus();
        int? devBase = st.Base is >= 0xD8 and <= 0x1000 ? st.Base : null;
        // v5.2: the exact «E1 1E E0 00» / «BASE == 0xE0» checks are replaced by a structural check (K13Header.Inspect).
        bool HeaderOk(byte[] img) { var h = K13Header.Inspect(img, devBase); return h.CanWrite || h.Verdict == HeaderVerdict.Uncertain && h.RawHex == acceptedHeader; }

        // ---- 1. image before the write
        byte[]? before = null;
        string source = "полное чтение";
        if (cachedImage != null && cachedImage.Length >= K13Layout.MinImageLength && HeaderOk(cachedImage))
        {
            P("Проверка клавиатуры…", 0.05);
            var t0 = K13MemoryCodec.Encode(cfg, cachedImage, out var plan0);
            var diff0 = K13Protocol.DiffPlan(plan0, cachedImage);
            int len0 = Math.Max(t0.Length, cachedImage.Length);
            var check = Cover(diff0, len0);
            if (!check.Any(c => c.Addr == 0xD0)) check.Insert(0, (0xD0, 8));
            // 1.2.0: the built-in factory table tells a K13 from a K12 (same VID/PID) — always checked on the keypad itself
            check.InsertRange(0, K13Model.SignatureBlocks);
            List<byte[]>? got = null;
            try { got = dev.ReadBlocks(check); } catch (IOException ex) { log("Быстрая проверка не удалась: " + ex.Message); }
            var ext = new byte[len0];
            Array.Copy(cachedImage, ext, cachedImage.Length);
            for (int i = cachedImage.Length; i < len0; i++) ext[i] = 0xFF;
            bool same = got != null;
            for (int k = 0; got != null && k < check.Count; k++)
            {
                var (a, n) = check[k];
                for (int i = 0; i < n; i++)
                {
                    if (a + i >= cachedImage.Length) ext[a + i] = got[k][i];
                    else if (got[k][i] != cachedImage[a + i] && !Trailer(a + i)) same = false;
                }
            }
            if (same) { before = ext; source = $"образ этого сеанса, сверено {RuPlural.Blocks(check.Count)}"; }
            else log("Клавиатура отличается от прочитанного ранее образа — читаю полностью.");
            Mark("проверка");
        }
        if (before == null)
        {
            try { before = dev.ReadImageVerified(new Progress<(string, double)>(x => P("Чтение клавиатуры…", 0.05 + x.Item2 * 0.6))); }
            catch (K13ReadMismatchException rm)
            {   // v5.6.4: nothing was written; keep both passes so the makers can send them
                var files = BackupStore.SaveReadPasses(rm, backupFolder);
                log(rm.Details + " Сохранено: " + string.Join(", ", files));
                throw;
            }
            if (dev.LastReadReport.Length > 0) log(dev.LastReadReport);
            Mark("полное чтение");
        }
        var header = K13Header.Inspect(before, devBase);
        log(header.Summary + (header.Notes.Count > 0 ? " " + string.Join(" ", header.Notes) : ""));
        if (header.Verdict == HeaderVerdict.Unusable)
            return Fail("Не удалось определить, где в памяти клавиатуры лежат настройки: " + string.Join(" ", header.Problems) + " Запись отменена, клавиатура не изменялась.") with { Header = header };
        var bfile = BackupStore.Save(before, backupFolder, "k13-prewrite", st);
        log($"Резервная копия перед записью ({source}): {bfile}");
        var model = K13Model.Detect(before);
        if (!K13Model.WriteAllowed(model))
        {   // 1.2.0: a K12 (same VID/PID, other key table) got the K13 layout on 17.11.2026 — never again
            log($"[model] {K13Model.Name(model)}: заводская таблица 0x0020 = {K13Model.FactoryRowHex(before)}, блок 0x0008 = {Convert.ToHexString(before, 8, 8)}. Запись не выполняется.");
            return Fail(K13Model.Refusal(model)) with { Header = header, BackupBefore = bfile, Model = model, After = before };
        }
        if (header.Verdict == HeaderVerdict.Uncertain && header.RawHex != acceptedHeader)
            return new K13WriteResult(false, false, false, 0, bfile, null, false, false, before,
                "Раскладка памяти этой клавиатуры не совпала с проверенной — нужно подтверждение. Ничего не записано.", string.Join(", ", times))
                { NeedsConfirmation = true, Header = header };
        if (cfg.Base != header.Base)
        {
            log($"Профиль был рассчитан на BASE 0x{cfg.Base:X}, у клавиатуры 0x{header.Base:X} — пересчитываю адреса.");
            cfg.Base = header.Base;
        }

        // ---- 2. plan and write
        if (keepModeFlags && cfg.Base < before.Length && cfg.Led.Flags != before[cfg.Base])
        {   // 1.3.0: lighting for Normal/Game — BASE+0 (bit 7 = mode) is written back as read, the write never asks for another mode
            log($"Флаги BASE+0 оставляю как в клавиатуре: {before[cfg.Base]:X2} (смена подсветки по режиму).");
            cfg = cfg.Clone(); cfg.Led.Flags = before[cfg.Base];
        }
        cfg = K13Mode.PrepareTarget(cfg, before, log); // v5.7.5 tilts never FF; v5.8 KEYSMODE bit = Game, device bits 5–4 kept
        var target = K13MemoryCodec.Encode(cfg, before, out var plan);
        var diff = K13Protocol.DiffPlan(plan, before);
        if (diff.Count == 0) return new(true, true, false, 0, bfile, null, false, false, before, "Клавиатура уже совпадает с профилем — записывать нечего.", string.Join(", ", times));
        int bb = cfg.Base; // v5.2: base-relative (KeymapFrom/MacrosFrom/LedTo are the BASE=0xE0 values)
        bool keys = diff.Any(b => b.Address < bb + K13Layout.OffFn && b.Address + b.Data.Length > bb + K13Layout.OffKeymap || b.Address >= bb + K13Layout.OffMacroTable);
        bool led = diff.Any(b => b.Address < bb + 0x20 && b.Address + b.Data.Length > bb);
        log($"Запись: {RuPlural.Blocks(diff.Count)}: " + string.Join(", ", diff.Select(b => $"0x{b.Address:X4}[{b.Data.Length}] {b.Purpose}")));
        if (diff.Any(b => b.Address == K13Layout.HeaderAddress) && header.Verdict == HeaderVerdict.Compatible)
            log($"Заголовок 0xD0: метка {header.Marker} → E1 1E, как записывает программа производителя при каждом «Применить» (иначе прошивка, вероятно, не берёт настройки после включения).");
        P("Запись…", 0.7);
        int hidFrom = dev.SentTimes.Count;
        void SaveHid(string what)
        {
            try
            {
                var f = BackupStore.SaveHidLog(dev.SentTimes.Skip(hidFrom), $"VOROTEX K13 — {what}, {DateTime.Now:yyyy-MM-dd HH:mm:ss}, BASE 0x{cfg.Base:X}, заголовок до записи {header.RawHex}", backupFolder);
                log("HID-кадры записи: " + f);
            }
            catch (Exception ex) { log("Журнал HID не сохранён: " + ex.Message); }
        }
        try { dev.Apply(diff, new Progress<double>(f => P("Запись…", 0.7 + f * 0.15)), programmingMode); SaveHid("запись " + RuPlural.Blocks(diff.Count)); }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            // v5.3: the write broke off half-way (cable pulled, HID error). Try to put back the touched blocks from «before»;
            // if the keypad is gone this fails too — then the backup file is the way back.
            SaveHid("запись прервана");
            log("Запись прервана: " + ex.Message + " — пробую вернуть затронутые блоки из резервной копии.");
            Mark("запись (прервана)");
            bool restored = false;
            try
            {
                var lenR = Math.Max(target.Length, before.Length);
                var touched = Cover(diff, lenR).Where(c => c.Addr + c.Len <= before.Length)
                    .Select(c => new WriteBlock(c.Addr, before.AsSpan(c.Addr, c.Len).ToArray(), "восстановление")).ToList();
                dev.Apply(touched, null, programmingMode);
                var (okR0, _, _) = ReadBack(dev, touched.Select(r => (r.Address, r.Data.Length)).ToList(), before);
                restored = okR0;
            }
            catch (Exception rex) { log("Восстановление не удалось: " + rex.Message); }
            Mark("восстановление");
            return new(false, false, restored, diff.Count, bfile, null, false, false, restored ? before : null,
                restored ? $"Запись прервалась ({ex.Message}), но затронутые блоки возвращены как было и проверены."
                         : $"Запись прервалась: {ex.Message}\nКлавиатура могла остаться частично записанной. Подключите её снова и нажмите «Записать» ещё раз " +
                           $"или верните резервную копию: {bfile}", string.Join(", ", times));
        }
        Mark("запись");

        // ---- 3. read back only the written blocks
        int len = Math.Max(target.Length, before.Length);
        var cover = Cover(diff, len);
        P("Проверка записи…", 0.88);
        var (okRead, blocksAfter, passes) = ReadBack(dev, cover, target);
        Mark($"проверка ({passes} проход.)");
        var after = (byte[])before.Clone();
        if (after.Length < len) { var e = new byte[len]; Array.Copy(after, e, after.Length); Array.Copy(target, after.Length, e, after.Length, len - after.Length); after = e; }
        for (int k = 0; k < cover.Count; k++) Array.Copy(blocksAfter[k], 0, after, cover[k].Addr, cover[k].Len);
        var afile = BackupStore.Save(after, backupFolder, "k13-postwrite", null);
        if (okRead)
            return new(true, false, false, diff.Count, bfile, afile, keys, led, after, $"Записано и проверено: {RuPlural.Blocks(diff.Count)}.", string.Join(", ", times)) { Written = diff };

        // ---- 4. confirmed mismatch → restore
        var bad = cover.SelectMany(c => Enumerable.Range(c.Addr, c.Len)).Where(i => after[i] != target[i] && !Trailer(i)).ToList();
        log("Расхождения после записи: " + string.Join(", ", bad.Take(30).Select(i => $"0x{i:X4} план {target[i]:X2} факт {after[i]:X2}")) + " — восстанавливаю.");
        var restore = cover.Where(c => c.Addr + c.Len <= before.Length)
            .Select(c => new WriteBlock(c.Addr, before.AsSpan(c.Addr, c.Len).ToArray(), "восстановление")).ToList();
        dev.Apply(restore, null, programmingMode);
        var (okR, rb, _) = ReadBack(dev, restore.Select(r => (r.Address, r.Data.Length)).ToList(), before);
        var check2 = (byte[])before.Clone();
        for (int k = 0; k < restore.Count; k++) Array.Copy(rb[k], 0, check2, restore[k].Address, restore[k].Data.Length);
        BackupStore.Save(check2, backupFolder, "k13-restored", null);
        Mark("восстановление");
        return new(false, false, true, diff.Count, bfile, afile, keys, led, okR ? before : null,
            okR ? "Запись не совпала с планом — всё восстановлено из резервной копии." : $"ВНИМАНИЕ: восстановление не подтвердилось чтением. Резервная копия: {bfile}", string.Join(", ", times));

        K13WriteResult Fail(string m) => new(false, false, false, 0, null, null, false, false, null, m, string.Join(", ", times));
    }

    /// <summary>Read back and compare with the expected image. One pass when it matches; otherwise up to 3 more (slower) passes.
    /// ok=false only when two consecutive passes agree on a mismatch.</summary>
    static (bool Ok, List<byte[]> Data, int Passes) ReadBack(K13Device dev, List<(int Addr, int Len)> blocks, byte[] expect)
    {
        // v5.9.1: the first read right after a write may echo the last written byte (PROTOCOL §31.4) — settle and discard it
        if (blocks.Count > 0)
        {
            K13Device.PreciseDelay(dev.SettleAfterWriteMs);
            try { dev.ReadBlocks(new[] { blocks[0] }); } catch (IOException) { }
        }
        List<byte[]>? prev = null;
        for (int pass = 0; pass < 4; pass++)
        {
            if (pass > 0) K13Device.PreciseDelay(30 * pass);
            List<byte[]> cur;
            try { cur = WithSlowdown(dev, pass, () => dev.ReadBlocks(blocks)); }
            catch (IOException) when (pass < 3) { continue; }
            bool match = true;
            for (int k = 0; k < blocks.Count && match; k++)
                for (int i = 0; i < blocks[k].Len; i++)
                {
                    int a = blocks[k].Addr + i;
                    if (a < expect.Length && cur[k][i] != expect[a] && !Trailer(a)) { match = false; break; }
                }
            if (match) return (true, cur, pass + 1);
            if (prev != null && Same(prev, cur)) return (false, cur, pass + 1);
            prev = cur;
        }
        throw new IOException("Не удалось надёжно прочитать записанные блоки (проходы расходятся). Память не изменялась повторно; проверьте чтением.");
    }

    static T WithSlowdown<T>(K13Device dev, int pass, Func<T> f)
    {
        double d = dev.DelayMs, l = dev.LoadDelayMs;
        if (pass >= 2) { dev.DelayMs = Math.Max(d, 2); dev.LoadDelayMs = Math.Max(l, 5); }
        try { return f(); }
        finally { dev.DelayMs = d; dev.LoadDelayMs = l; }
    }

    static bool Same(List<byte[]> a, List<byte[]> b) => a.Count == b.Count && a.Zip(b).All(x => x.First.AsSpan().SequenceEqual(x.Second));
}
