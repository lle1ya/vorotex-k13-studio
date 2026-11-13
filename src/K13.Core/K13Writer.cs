using System.Diagnostics;

namespace Vorotex.K13;

public sealed record K13WriteResult(bool Ok, bool NothingToDo, bool Restored, int Blocks, string? BackupBefore, string? BackupAfter,
    bool KeysChanged, bool LightingChanged, byte[]? After, string Message, string Timing = "");

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
        byte[]? cachedImage = null)
    {
        var sw = Stopwatch.StartNew();
        var times = new List<string>();
        void Mark(string what) { times.Add($"{what} {sw.ElapsedMilliseconds} мс"); sw.Restart(); }
        void P(string s, double f) => progress?.Report((s, f));
        var st = dev.GetStatus();
        if (st.Base != 0xE0) return Fail($"Неожиданный BASE 0x{st.Base:X} — запись отменена.");

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
            if (same) { before = ext; source = $"образ этого сеанса, сверено {check.Count} блок(ов)"; }
            else log("Клавиатура отличается от прочитанного ранее образа — читаю полностью.");
            Mark("проверка");
        }
        if (before == null)
        {
            before = dev.ReadImageVerified(new Progress<(string, double)>(x => P("Чтение клавиатуры…", 0.05 + x.Item2 * 0.6)));
            Mark("полное чтение");
        }
        if (!HeaderOk(before)) return Fail("Заголовок памяти не E1 1E E0 00 — запись отменена.");
        var bfile = BackupStore.Save(before, backupFolder, "k13-prewrite", st);
        log($"Резервная копия перед записью ({source}): {bfile}");

        // ---- 2. plan and write
        var target = K13MemoryCodec.Encode(cfg, before, out var plan);
        var diff = K13Protocol.DiffPlan(plan, before);
        if (diff.Count == 0) return new(true, true, false, 0, bfile, null, false, false, before, "Клавиатура уже совпадает с профилем — записывать нечего.", string.Join(", ", times));
        bool keys = diff.Any(b => b.Address < KeymapTo && b.Address + b.Data.Length > KeymapFrom || b.Address >= MacrosFrom);
        bool led = diff.Any(b => b.Address < LedTo && b.Address + b.Data.Length > 0x00E0);
        log($"Запись: {diff.Count} блок(ов): " + string.Join(", ", diff.Select(b => $"0x{b.Address:X4}[{b.Data.Length}] {b.Purpose}")));
        P("Запись…", 0.7);
        dev.Apply(diff, new Progress<double>(f => P("Запись…", 0.7 + f * 0.15)), programmingMode);
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
            return new(true, false, false, diff.Count, bfile, afile, keys, led, after, $"Записано и проверено: {diff.Count} блок(ов).", string.Join(", ", times));

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

    static bool HeaderOk(byte[] img) => img.Length > 0xD3 && img[0xD0] == 0xE1 && img[0xD1] == 0x1E && img[0xD2] == 0xE0 && img[0xD3] == 0x00;

    /// <summary>Read back and compare with the expected image. One pass when it matches; otherwise up to 3 more (slower) passes.
    /// ok=false only when two consecutive passes agree on a mismatch.</summary>
    static (bool Ok, List<byte[]> Data, int Passes) ReadBack(K13Device dev, List<(int Addr, int Len)> blocks, byte[] expect)
    {
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
