using System.Text.Json.Serialization;

namespace Vorotex.K13;

/// <summary>One of the 7 colour slots. Channels are 4-bit (0..15). X is an opaque vendor nibble (preserved).</summary>
/// <summary>
/// How logical colours (what the user picks) map to the 4-bit channel values stored on the K13.
/// Raw layout (vendor): byte A = rawR&lt;&lt;4 | rawB, byte B = X&lt;&lt;4 | rawG.
/// Evidence (13.11.2026): raw (R0,G0,B15) lit YELLOW; vendor default palette ends with raw (0,0,0); brightness is inverted too
/// (0 = max) → channels are inverted (15 = off). Perm[k] = raw channel (0=R,1=G,2=B) that drives logical channel k.
/// The «Калибровка подсветки» wizard can confirm/correct this and saves it in the store.
/// </summary>
public sealed class LedColorMap
{
    public bool Invert { get; set; } = true;
    public int[] Perm { get; set; } = { 0, 1, 2 };
    /// <summary>True once the user confirmed it with the calibration wizard.</summary>
    public bool Calibrated { get; set; }

    public static LedColorMap Identity => new() { Invert = false, Perm = new[] { 0, 1, 2 } };

    public bool IsValid => Perm is { Length: 3 } && Perm.OrderBy(x => x).SequenceEqual(new[] { 0, 1, 2 });

    /// <summary>Logical (R,G,B) → raw (R,G,B).</summary>
    public (int R, int G, int B) ToRaw(int r, int g, int b)
    {
        var p = IsValid ? Perm : new[] { 0, 1, 2 };
        var raw = new int[3]; var v = new[] { r, g, b };
        for (int k = 0; k < 3; k++) raw[p[k]] = Invert ? 15 - (v[k] & 15) : v[k] & 15;
        return (raw[0], raw[1], raw[2]);
    }

    /// <summary>Raw (R,G,B) → logical (R,G,B).</summary>
    public (int R, int G, int B) ToLogical(int r, int g, int b)
    {
        var p = IsValid ? Perm : new[] { 0, 1, 2 };
        var raw = new[] { r & 15, g & 15, b & 15 }; var v = new int[3];
        for (int k = 0; k < 3; k++) v[k] = Invert ? 15 - raw[p[k]] : raw[p[k]];
        return (v[0], v[1], v[2]);
    }

    public override string ToString() => $"{(Invert ? "инверсия" : "прямо")}, каналы R→{"RGB"[Perm[0]]} G→{"RGB"[Perm[1]]} B→{"RGB"[Perm[2]]}{(Calibrated ? " (откалибровано)" : "")}";
}

public sealed class ColorSlot
{
    public int R { get; set; }
    public int G { get; set; }
    public int B { get; set; }
    /// <summary>Upper nibble of the second byte (vendor internal UI state, see PROTOCOL.md §18/§20). Preserved as-is.</summary>
    public int X { get; set; }

    public ColorSlot Clone() => (ColorSlot)MemberwiseClone();
    public override string ToString() => $"R{R} G{G} B{B}";
}

public sealed class MacroEvent
{
    /// <summary>true = key press, false = key release.</summary>
    public bool Down { get; set; }
    /// <summary>Delay in 8 ms units, 0..127.</summary>
    public int Delay { get; set; }
    /// <summary>Key code (same code space as the keymap).</summary>
    public byte Code { get; set; }

    /// <summary>Extra payload bytes that follow the 2-byte head for extended events
    /// (0xF7 wheel: [amount, 0]; 0xF6 mouse move: [dx lo, dx hi, dy lo, dy hi]; 0xFA: 2 bytes). Null for plain key events.</summary>
    public byte[]? Extra { get; set; }

    [JsonIgnore] public int DelayMs => Delay * 8;
    [JsonIgnore] public bool IsWheel => Code == MouseCodes.Wheel;
    [JsonIgnore] public bool IsMouseButton => MouseCodes.IsButton(Code);
    /// <summary>Wheel detents: positive = up, negative = down (signed byte).</summary>
    [JsonIgnore] public int WheelAmount => IsWheel && Extra is { Length: > 0 } ? (sbyte)Extra[0] : 0;
    public MacroEvent Clone() { var c = (MacroEvent)MemberwiseClone(); c.Extra = Extra?.ToArray(); return c; }

    public static MacroEvent Wheel(int detents, int delay = 0) =>
        new() { Down = false, Delay = delay, Code = MouseCodes.Wheel, Extra = new[] { (byte)(sbyte)Math.Clamp(detents, -127, 127), (byte)0 } };
}

/// <summary>Mouse codes inside MACRO bodies (not the keymap!). See PROTOCOL.md §mouse.
/// 0xF1 = left button — confirmed (vendor macro «ЛКМ» stored as 81 F1 / 01 F1).
/// 0xF3 = right, 0xF2 = middle — derived from the vendor VK→HID converter (VK 0xF0→F1, 0xF1→F3, 0xF2→F2) and its name table, medium confidence.
/// 0xF7 = wheel event with 2 extra bytes [signed amount, 0]; 0xF6 = relative move with 4 extra bytes.</summary>
public static class MouseCodes
{
    public const byte Left = 0xF1, Middle = 0xF2, Right = 0xF3, Move = 0xF6, Wheel = 0xF7;
    public static bool IsButton(byte c) => c is Left or Middle or Right;
    public static bool IsConfirmed(byte c) => c == Left;
    public static string Name(byte c) => c switch
    {
        Left => "ЛКМ (левая кнопка мыши)",
        Right => "ПКМ (правая кнопка мыши)",
        Middle => "СКМ (нажатие колеса мыши)",
        Wheel => "Колесо мыши",
        Move => "Движение мыши",
        _ => $"Мышь 0x{c:X2}",
    };
    public static string Short(byte c) => c switch { Left => "ЛКМ", Right => "ПКМ", Middle => "СКМ", Wheel => "Колесо", Move => "Курсор", _ => $"0x{c:X2}" };
    /// <summary>Number of payload bytes after the 2-byte head; -1 = variable/unknown (text or LED blocks).</summary>
    public static int ExtraLength(byte code) => code switch { Move => 4, Wheel => 2, 0xFA => 2, 0xF9 or 0xFB => -1, _ => 0 };
}

/// <summary>Macro repeat mode as stored in the table entry bits.</summary>
public enum MacroRepeatMode { Count = 0, WhileHeld = 1, Toggle = 2 }

public sealed class MacroSlot
{
    /// <summary>Events; empty list = slot unused (len 0 in the table).</summary>
    public List<MacroEvent> Events { get; set; } = new();
    /// <summary>Repeat count (body header, little-endian u16).</summary>
    public int Repeat { get; set; } = 1;
    /// <summary>Table entry byte1 bit7.</summary>
    public bool Mode2Bit { get; set; }
    /// <summary>Table entry byte3 bit7.</summary>
    public bool Mode1Bit { get; set; }
    /// <summary>Table entry byte3 bit6.</summary>
    public bool FlagBit { get; set; }
    /// <summary>Set when the body contains an event we cannot size (vendor text/LED blocks 0xF9/0xFB). Such slots are read-only.</summary>
    [JsonIgnore] public bool Unsupported { get; set; }

    /// <summary>Body size in bytes (repeat header + events incl. extended payloads).</summary>
    [JsonIgnore] public int BodyLength => 2 + Events.Sum(e => 2 + Math.Max(0, MouseCodes.ExtraLength(e.Code)));

    [JsonIgnore] public bool IsUsed => Events.Count > 0;

    [JsonIgnore]
    public MacroRepeatMode RepeatMode
    {
        get => Mode1Bit ? MacroRepeatMode.WhileHeld : Mode2Bit ? MacroRepeatMode.Toggle : MacroRepeatMode.Count;
        set { Mode1Bit = value == MacroRepeatMode.WhileHeld; Mode2Bit = value == MacroRepeatMode.Toggle; }
    }

    public MacroSlot Clone() => new()
    {
        Events = Events.Select(e => e.Clone()).ToList(), Repeat = Repeat, Mode2Bit = Mode2Bit, Mode1Bit = Mode1Bit, FlagBit = FlagBit, Unsupported = Unsupported
    };
}

/// <summary>Lighting settings (BASE+0x00..0x1B).</summary>
public sealed class LedSettings
{
    public byte Flags { get; set; }                  // BASE+0 (bits 5,4 owned by the device)
    public List<ColorSlot> Colors { get; set; } = Enumerable.Range(0, 7).Select(_ => new ColorSlot()).ToList();
    public byte[] Extra { get; set; } = new byte[K13Layout.LedExtraLength]; // BASE+0x0F..0x17 raw
    public int Direction { get; set; }               // 0..3
    /// <summary>Raw brightness 0..7 as stored: 0 = MAX, 6 = MIN (vendor slider has 7 steps).</summary>
    public int Brightness { get; set; }
    public int Speed { get; set; }                   // 0..7 (vendor slider 0..6)
    public int Mode { get; set; }                    // 0..15
    /// <summary>Raw HR_COLOR_SEL (BASE+0x19 bits 6–4): 1..7 = one colour = slot ColorIndex−1 (vendor «цвет 1» = slot 0, exp12);
    /// 0 = colours in turn (cycle through CycleMask).</summary>
    public int ColorIndex { get; set; }
    /// <summary>Colour slot shown in single-colour modes, or null when colours cycle.</summary>
    [JsonIgnore] public int? SingleSlot => ColorIndex is >= 1 and <= 7 ? ColorIndex - 1 : null;
    public bool Symmetry { get; set; }
    public byte CycleMask { get; set; } = 0x81;      // BASE+0x1A raw (bit7 + cycle colours)
    public byte ModeEnableMask { get; set; } = 0xFF; // BASE+0x1B

    /// <summary>UI helper: 0 (min) .. 6 (max) – inverse of the stored value.</summary>
    [JsonIgnore]
    public int BrightnessLevel { get => 6 - Math.Clamp(Brightness, 0, 6); set => Brightness = 6 - Math.Clamp(value, 0, 6); }

    /// <summary>Whether reactive lighting is on – approximated from the colour-1 X nibble (PROTOCOL.md §15/§20). Informational only.</summary>
    [JsonIgnore] public bool ReactiveHint => Colors.Count > 0 && Colors[0].X == 2;

    public LedSettings Clone() => new()
    {
        Flags = Flags, Colors = Colors.Select(c => c.Clone()).ToList(), Extra = (byte[])Extra.Clone(),
        Direction = Direction, Brightness = Brightness, Speed = Speed, Mode = Mode, ColorIndex = ColorIndex,
        Symmetry = Symmetry, CycleMask = CycleMask, ModeEnableMask = ModeEnableMask
    };
}

/// <summary>Everything the device stores in its config memory, as decoded from an image.</summary>
public sealed class K13DeviceConfig
{
    public int Base { get; set; } = K13Layout.DefaultBase;
    public LedSettings Led { get; set; } = new();
    public int WheelFirst { get; set; }   // USCROLL0 (upper nibble)
    public int WheelSecond { get; set; } = 1; // USCROLL1 (lower nibble)
    /// <summary>Full keymap, 20 columns x 8 rows, column-major (index = col*8 + row).</summary>
    public byte[] Keymap { get; set; } = Enumerable.Repeat((byte)0xFF, K13Layout.KeymapLength).ToArray();
    public byte[] Fn { get; set; } = Enumerable.Repeat((byte)0xFF, 12).ToArray();
    public byte[] FnPad { get; set; } = new byte[4];
    public List<MacroSlot> Macros { get; set; } = Enumerable.Range(0, K13Layout.MacroSlots).Select(_ => new MacroSlot()).ToList();

    public byte GetKey(int column, int row = 0) => Keymap[column * K13Layout.KeymapRows + row];
    public void SetKey(int column, byte code, int row = 0) => Keymap[column * K13Layout.KeymapRows + row] = code;

    public K13DeviceConfig Clone() => new()
    {
        Base = Base, Led = Led.Clone(), WheelFirst = WheelFirst, WheelSecond = WheelSecond,
        Keymap = (byte[])Keymap.Clone(), Fn = (byte[])Fn.Clone(), FnPad = (byte[])FnPad.Clone(),
        Macros = Macros.Select(m => m.Clone()).ToList()
    };
}
