namespace Vorotex.K13;

/// <summary>1.2.0: which keypad is connected. The K13 and (very likely) the K12 report the SAME USB ids (VID 30FA, PID 2350),
/// the same header at 0x00D0 and the same BASE, so the PID cannot tell them apart. Their built-in factory tables (0x0020,
/// row 0) differ: the K13 has 18 columns (Num 1–8, Enter, Tab, Num 9/0, arrows, F4, E9); the second unit the makers plugged in
/// on 17.11.2026 11:32 (factory block 0x0008 = 82 9B 85 10 87 07 10 1B) has 14 — Num 1–5, Num Enter, Num 9–6, Num 0, Num .,
/// F4, E9 — twelve keys + the knob, matching the K12 description (PROTOCOL §41).</summary>
public enum KeypadModel { NotRead, K13, K12Likely, Unknown }

public static class K13Model
{
    /// <summary>Row 0 of the built-in table at 0x0020 of the 17.11 11:32 unit (columns 0–13, the rest FF).</summary>
    public static readonly byte[] K12FactoryRow = { 0x59, 0x5A, 0x5B, 0x5C, 0x5D, 0x58, 0x61, 0x60, 0x5F, 0x5E, 0x62, 0x63, 0xF4, 0xE9 };
    public const int FactoryTable = 0x0020, Columns = 18;
    /// <summary>Blocks a quick (cached) write re-reads so the model is checked on the keypad itself: the factory columns 5, 12
    /// and 16, where the K13 and the K12 differ (5E/58, 50/F4, F4/FF) — 3 blocks instead of the whole table (live lighting stays fast).</summary>
    public static readonly (int Addr, int Len)[] SignatureBlocks =
        { (FactoryTable + 5 * K13Layout.KeymapRows, 8), (FactoryTable + 12 * K13Layout.KeymapRows, 8), (FactoryTable + 16 * K13Layout.KeymapRows, 8) };

    /// <summary>1.3.4: the connect-time model check on an open device. Reads 24 bytes with 07 18 00 / 07 18 05 + GetFeature /
    /// 07 18 01 only and switches OFF the session end on Dispose — no 07 18 11 (settings reload = backlight blink, mode back to
    /// Normal, §35), no 07 18 00 zero, no 07 20. Throws IOException when the keypad does not answer.</summary>
    public static (KeypadModel Model, List<byte[]> Blocks) ReadSignature(K13Device dev)
    {
        dev.EndSessionOnDispose = false;
        var blocks = dev.ReadBlocks(SignatureBlocks);
        return (DetectSignature(blocks), blocks);
    }

    /// <summary>1.3.4: newest saved full image in <paramref name="folder"/> that is a K13 (K12 / unknown / damaged / prewrite / prerestore skipped).</summary>
    public static (byte[] Image, string File)? LastKnownK13(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return null;
            foreach (var f in new DirectoryInfo(folder).GetFiles("*.bin")
                .Where(x => x.Length >= K13Layout.MinImageLength && !x.Name.Contains("prewrite") && !x.Name.Contains("prerestore"))
                .OrderByDescending(x => x.LastWriteTimeUtc))
            {
                try
                {
                    var img = File.ReadAllBytes(f.FullName);
                    K13MemoryCodec.Decode(img); // sanity
                    if (Detect(img) == KeypadModel.K13) return (img, f.FullName);
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    /// <summary>1.3.4: the model from the three signature blocks alone (read-only check on connect, 24 bytes).
    /// Row 0 of columns 5/12/16 must match one model exactly and rows 1–7 must be FF; anything else (or a short answer) = Unknown.</summary>
    public static KeypadModel DetectSignature(IReadOnlyList<byte[]>? blocks)
    {
        if (blocks == null || blocks.Count != SignatureBlocks.Length || blocks.Any(b => b == null || b.Length != K13Layout.KeymapRows)) return KeypadModel.Unknown;
        if (blocks.Any(b => b.Skip(1).Any(x => x != 0xFF))) return KeypadModel.Unknown;
        var row = blocks.Select(b => b[0]).ToArray();
        int[] cols = { 5, 12, 16 };
        byte[] k13 = cols.Select(c => K13Layout.FactoryColumnCodes[c]).ToArray();
        byte[] k12 = cols.Select(c => c < K12FactoryRow.Length ? K12FactoryRow[c] : (byte)0xFF).ToArray();
        if (row.SequenceEqual(k13)) return KeypadModel.K13;
        if (row.SequenceEqual(k12)) return KeypadModel.K12Likely;
        return KeypadModel.Unknown;
    }

    public static byte[]? FactoryRow(byte[]? image)
    {
        if (image == null || image.Length < FactoryTable + Columns * K13Layout.KeymapRows) return null;
        return Enumerable.Range(0, Columns).Select(c => image[FactoryTable + c * K13Layout.KeymapRows]).ToArray();
    }

    public static KeypadModel Detect(byte[]? image)
    {
        var row = FactoryRow(image);
        if (row == null) return KeypadModel.NotRead;
        if (row.SequenceEqual(K13Layout.FactoryColumnCodes)) return KeypadModel.K13;
        if (row.Take(K12FactoryRow.Length).SequenceEqual(K12FactoryRow) && row.Skip(K12FactoryRow.Length).All(b => b == 0xFF)) return KeypadModel.K12Likely;
        return KeypadModel.Unknown;
    }

    /// <summary>Writes are allowed only to a keypad whose built-in table is the K13 one.</summary>
    public static bool WriteAllowed(KeypadModel m) => m == KeypadModel.K13;

    public static string Name(KeypadModel m) => m switch
    {
        KeypadModel.K13 => "VOROTEX K13",
        KeypadModel.K12Likely => "не K13 — похоже, VOROTEX K12",
        KeypadModel.Unknown => "не K13 — неизвестная модель",
        _ => "модель ещё не проверена (память не читалась)",
    };

    public static string FactoryRowHex(byte[]? image) => FactoryRow(image) is { } r ? Convert.ToHexString(r) : "—";

    /// <summary>The write refusal (writer and «Запоминать режим»).</summary>
    public static string Refusal(KeypadModel m) =>
        $"Запись заблокирована: подключена {Name(m)}. VOROTEX K13 Studio умеет записывать только K13 — у этой клавиатуры другая заводская таблица кнопок, " +
        "и запись раскладки K13 сделает её кнопки неправильными. Клавиатура не изменялась. Резервная копия её памяти сохранена — пришлите её разработчику " +
        "(«⋯ → Сохранить диагностику»), чтобы добавить поддержку.";

    /// <summary>Banner / status text when a non-K13 is connected.</summary>
    public static string Banner(KeypadModel m) => m switch
    {
        KeypadModel.K12Likely => "Подключена не K13 (похоже, K12). Её Studio пока не поддерживает: схема и назначения — от K13, запись в клавиатуру заблокирована. " +
                                 "Проверка кнопок работает. Чтобы вернуть кнопки, используйте программу производителя для K12.",
        KeypadModel.Unknown => "Подключена клавиатура с незнакомой заводской таблицей (не K13). Запись заблокирована; проверка кнопок работает.",
        _ => "",
    };
}
