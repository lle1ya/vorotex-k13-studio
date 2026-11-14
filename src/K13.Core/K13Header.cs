namespace Vorotex.K13;

/// <summary>How far we trust the memory layout of a particular keypad before writing (v5.2).</summary>
public enum HeaderVerdict
{
    /// <summary>E1 1E BASE F0 0F 00 00 and the config block at BASE looks right — the layout our K13 uses.</summary>
    Exact,
    /// <summary>Marker differs (e.g. DA 1E – another unit/firmware) but BASE, both pointers, keymap and macro table check out.</summary>
    Compatible,
    /// <summary>Something does not match: write only after the user saw the bytes and confirmed.</summary>
    Uncertain,
    /// <summary>No usable BASE at all — nothing sensible can be encoded.</summary>
    Unusable,
}

public sealed record K13HeaderInfo(HeaderVerdict Verdict, int Base, byte[] Raw, IReadOnlyList<string> Problems, IReadOnlyList<string> Notes)
{
    public string RawHex => Raw.Length == 0 ? "—" : string.Join(" ", Raw.Select(x => x.ToString("X2")));
    public string Marker => Raw.Length >= 2 ? $"{Raw[0]:X2} {Raw[1]:X2}" : "—";
    public bool CanWrite => Verdict is HeaderVerdict.Exact or HeaderVerdict.Compatible;
    public string Summary => Verdict switch
    {
        HeaderVerdict.Exact => $"Заголовок 0xD0: {RawHex} — как у проверенной K13.",
        HeaderVerdict.Compatible => $"Заголовок 0xD0: {RawHex} — метка {Marker} отличается от E1 1E, но структура памяти совпадает (BASE 0x{Base:X}, указатели, раскладка, таблица макросов).",
        _ => $"Заголовок 0xD0: {RawHex}. " + string.Join(" ", Problems),
    };
}

/// <summary>
/// Structural check of the config header instead of an exact byte match (v5.2).
/// The vendor app never READS 0xD0 — on every Apply it simply writes the constant <c>E1 1E BASE F0 0F 00 00</c>
/// (FUN_0040fe10 @0x4103eb: <c>mov word [ebp-0x18], 0x1EE1</c>). So the first byte is a marker/revision value of
/// whoever wrote the block last; what has to be right for us is BASE and the block it points to.
/// </summary>
public static class K13Header
{
    public const byte KnownMarkerLo = 0xE1, MarkerHi = 0x1E;

    public static K13HeaderInfo Inspect(byte[] img, int? deviceBase = null)
    {
        var problems = new List<string>();
        var notes = new List<string>();
        int h = K13Layout.HeaderAddress;
        if (img.Length < h + 8)
            return new(HeaderVerdict.Unusable, -1, Array.Empty<byte>(), new[] { "Образ памяти слишком короткий — заголовка 0xD0 нет." }, notes);
        var raw = img.AsSpan(h, 8).ToArray();
        bool markerExact = raw[0] == KnownMarkerLo && raw[1] == MarkerHi;
        bool markerFamily = raw[1] == MarkerHi;
        bool tailOk = raw[4] == 0xF0 && raw[5] == 0x0F && raw[6] == 0x00 && raw[7] == 0x00;
        int headerBase = raw[2] | raw[3] << 8;

        int b;
        if (InRange(headerBase, img)) b = headerBase;
        else if (deviceBase is int db && InRange(db, img)) { b = db; problems.Add($"BASE в заголовке (0x{headerBase:X4}) вне памяти, взят BASE из ответа клавиатуры 0x{db:X}."); }
        else return new(HeaderVerdict.Unusable, -1, raw, new[] { $"BASE 0x{headerBase:X4} в заголовке недопустим, а клавиатура свой BASE не сообщила." }, notes);

        if (!markerExact)
        {
            if (markerFamily) notes.Add($"Метка {raw[0]:X2} 1E вместо E1 1E: второй байт (0x1E) тот же, меняется только первый — похоже на ревизию прошивки/программы, записавшей блок.");
            else problems.Add($"Метка {raw[0]:X2} {raw[1]:X2} не похожа на E1 1E (второй байт не 0x1E).");
        }
        if (!tailOk) problems.Add($"Хвост заголовка {raw[4]:X2} {raw[5]:X2} {raw[6]:X2} {raw[7]:X2} вместо F0 0F 00 00.");
        if (deviceBase is int d && d >= 0 && d != b) problems.Add($"Клавиатура сообщает BASE 0x{d:X}, а в заголовке 0x{b:X}.");

        int kp = U16(img, b + K13Layout.OffKeymapPtr), mp = U16(img, b + K13Layout.OffMacroPtr);
        if (kp != b + K13Layout.OffKeymap || mp != b + K13Layout.OffMacroTable)
            problems.Add($"Указатели keymap=0x{kp:X4} / макросы=0x{mp:X4}, ожидались 0x{b + K13Layout.OffKeymap:X4} / 0x{b + K13Layout.OffMacroTable:X4}.");

        int sane = 0;
        for (int i = 0; i < K13Layout.KeymapLength; i++)
        {
            byte c = img[b + K13Layout.OffKeymap + i];
            if (c is 0x00 or 0xFF || KeyCatalog.Get(c).Category != KeyCategory.Unknown) sane++;
        }
        if (sane * 100 < K13Layout.KeymapLength * 90) problems.Add($"Раскладка не похожа на раскладку K13: понятных кодов {sane} из {K13Layout.KeymapLength}.");

        int entries = 0;
        for (int i = 0; i < K13Layout.MacroSlots; i++)
            if (img[b + K13Layout.OffMacroTable + i * K13Layout.MacroEntrySize] == 0x07) entries++;
        if (entries < K13Layout.MacroSlots) problems.Add($"Таблица макросов: {entries} из {K13Layout.MacroSlots} записей начинаются с 0x07.");

        var verdict = problems.Count == 0 ? (markerExact ? HeaderVerdict.Exact : HeaderVerdict.Compatible) : HeaderVerdict.Uncertain;
        return new(verdict, b, raw, problems, notes);
    }

    static bool InRange(int b, byte[] img) => b is >= 0xD8 and <= 0x1000 && b + K13Layout.OffTrailer + 4 <= img.Length;
    static int U16(byte[] a, int i) => a[i] | a[i + 1] << 8;
}
