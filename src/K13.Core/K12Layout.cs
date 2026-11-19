namespace Vorotex.K13;

/// <summary>1.3.3, READ-ONLY: what the VOROTEX K12 holds, from the makers' data of 19.11.2026 (two dumps 0x880, the vendor config.ini,
/// a layout photo and Key-Test screenshots; ANALYSIS in k12/ANALYSIS.md, PROTOCOL §45). Used only to name the K12's keys in the
/// key tester and diagnostics — Studio never writes a K12 (K13Model.WriteAllowed).
/// Built-in Normal table at 0x0020 and the stored Game table at BASE+0x48 (0x0128), 8 bytes per column, row 0, columns 0–13:
/// <code>
///  col  key (photo)          Normal (0x0020)   Game after a vendor reset (0x0128)
///  0–4  1…5                  Num 1…Num 5       1…5 (top row)
///  5    ENTER (tall, right)  Num Enter         Enter
///  6    9                    Num 9             9
///  7    8                    Num 8             8
///  8    7                    Num 7             7
///  9    6                    Num 6             6
///  10   VOROTEX (long)       Num 0             0
///  11   «•» (dot)            Num .             ` ~
///  12   knob press           F4 = Normal/Game  F4
///  13   — (not a key)        E9                E9
/// </code>
/// The knob turn is not a column: in Normal it is volume (firmware), in Game the wheel byte BASE+0x20 (0x32 after a reset:
/// right = scroll up, left = scroll down; same nibble order as the K13: low = right/«Roll+», high = left).</summary>
public static class K12Layout
{
    public const int Columns = 14;
    /// <summary>Tester ids for the knob turn (not memory columns).</summary>
    public const int KnobRight = 120, KnobLeft = 121, KnobPress = 12;

    public static readonly byte[] GameAfterReset = { 0x1E, 0x1F, 0x20, 0x21, 0x22, 0x28, 0x26, 0x25, 0x24, 0x23, 0x27, 0x35, 0xF4, 0xE9 };

    public static string ControlName(int id) => id switch
    {
        >= 0 and <= 4 => $"клавиша {id + 1}",
        5 => "клавиша Enter",
        6 => "клавиша 9", 7 => "клавиша 8", 8 => "клавиша 7", 9 => "клавиша 6",
        10 => "клавиша VOROTEX",
        11 => "клавиша с точкой",
        12 => "нажатие регулятора",
        13 => "колонка 13 (не клавиша)",
        KnobRight => "регулятор вправо",
        KnobLeft => "регулятор влево",
        _ => $"колонка {id}",
    };

    /// <summary>Short legend for the K12 schematic.</summary>
    public static string Legend(int id) => id switch
    {
        >= 0 and <= 4 => (id + 1).ToString(),
        5 => "ENTER", 6 => "9", 7 => "8", 8 => "7", 9 => "6", 10 => "VOROTEX", 11 => "•",
        KnobPress => "●", KnobRight => "↻", KnobLeft => "↺",
        _ => "?",
    };

    public static bool IsK12(byte[]? image) => K13Model.Detect(image) == KeypadModel.K12Likely;

    private static int Base(byte[] img) => img.Length > 0xD3 && img[0xD0] == 0xE1 && img[0xD1] == 0x1E ? img[0xD2] | img[0xD3] << 8 : K13Layout.DefaultBase;

    /// <summary>Row 0 of the built-in Normal table (columns 0–13).</summary>
    public static byte[]? NormalRow(byte[]? img) => K13Model.FactoryRow(img)?.Take(Columns).ToArray();

    /// <summary>Row 0 of the stored Game table (keymap pointer at BASE+0x1C, = 0x0128 on both units seen).</summary>
    public static byte[]? GameRow(byte[]? img)
    {
        if (img == null) return null;
        int b = Base(img);
        if (b + 0x20 > img.Length) return null;
        int km = img[b + K13Layout.OffKeymapPtr] | img[b + K13Layout.OffKeymapPtr + 1] << 8;
        if (km != b + K13Layout.OffKeymap || km + Columns * K13Layout.KeymapRows > img.Length) return null;
        return Enumerable.Range(0, Columns).Select(c => img[km + c * K13Layout.KeymapRows]).ToArray();
    }

    /// <summary>Game knob functions (wheel byte BASE+0x20): (left = high nibble, right = low nibble), or null.</summary>
    public static (int Left, int Right)? GameWheel(byte[]? img)
    {
        if (img == null) return null;
        int b = Base(img);
        if (b + K13Layout.OffWheel >= img.Length) return null;
        byte w = img[b + K13Layout.OffWheel];
        return (w >> 4, w & 15);
    }

    /// <summary>Controls that send <paramref name="vk"/> (unknown mode: both tables).</summary>
    public static List<int> ControlsFor(ushort vk, HwMode mode, byte[]? img)
    {
        var l = new List<int>();
        if (vk == 0) return l;
        void Scan(byte[]? row)
        {
            if (row == null) return;
            for (int c = 0; c < row.Length; c++)
                if (row[c] != KeyCatalog.Disabled && !l.Contains(c) && KeyCatalog.ToVirtualKey(row[c]) == vk) l.Add(c);
        }
        if (mode != HwMode.Game) Scan(NormalRow(img) ?? K13Model.K12FactoryRow);   // 1.3.4: model known from the connect check, memory not read
        if (mode != HwMode.Normal) Scan(GameRow(img) ?? GameAfterReset);
        // knob turn: Normal = volume (firmware); Game = the wheel byte
        if (mode != HwMode.Game) { if (vk == 0xAF) l.Add(KnobRight); if (vk == 0xAE) l.Add(KnobLeft); }
        if (mode != HwMode.Normal && GameWheel(img) is { } w)
        {
            if (KeyTester.WheelVk(w.Right) == vk && !l.Contains(KnobRight)) l.Add(KnobRight);
            if (KeyTester.WheelVk(w.Left) == vk && !l.Contains(KnobLeft)) l.Add(KnobLeft);
        }
        return l;
    }

    /// <summary>Knob side for a mouse-wheel event (Game: functions 2 = scroll up, 3 = scroll down).</summary>
    public static List<int> ControlsForScroll(bool up, HwMode mode, byte[]? img)
    {
        var l = new List<int>();
        if (mode == HwMode.Normal) return l;
        var (left, right) = GameWheel(img) ?? (3, 2);
        int want = up ? 2 : 3;
        if ((right & 7) == want) l.Add(KnobRight);
        if ((left & 7) == want) l.Add(KnobLeft);
        return l;
    }

    /// <summary>Diagnostics: both rows by key name.</summary>
    public static string Describe(byte[]? img)
    {
        var n = NormalRow(img); var g = GameRow(img);
        if (n == null) return "K12: память не прочитана.";
        var parts = Enumerable.Range(0, Columns).Select(c => $"{ControlName(c)}: {KeyCatalog.Name(n[c])} / {(g == null ? "?" : KeyCatalog.Name(g[c]))}");
        var w = GameWheel(img);
        return "K12 (Normal / Game): " + string.Join("; ", parts) +
               (w is { } x ? $"; регулятор в Game: вправо — {K13Layout.WheelFunctions[x.Right & 7]}, влево — {K13Layout.WheelFunctions[x.Left & 7]}" : "");
    }
}
