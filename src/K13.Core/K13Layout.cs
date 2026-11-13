namespace Vorotex.K13;

/// <summary>Kind of physical control on the K13.</summary>
public enum ControlKind { Key, JoystickUp, JoystickDown, JoystickLeft, JoystickRight, Hidden }

/// <summary>One physical control mapped to a keymap column (row 0).</summary>
public sealed record K13Control(string Id, string Title, int Column, int? KuIndex, byte FactoryCode, ControlKind Kind,
                                double X, double Y, double W, double H);

/// <summary>Memory layout constants (see PROTOCOL.md §3, §11, §13). All offsets are relative to BASE unless noted.</summary>
public static class K13Layout
{
    public const ushort Vid = 0x30FA, Pid = 0x2350;
    public const ushort VendorUsagePage = 0xFF01, VendorUsage = 0x0001;
    public const byte ReportId = 7;
    public const int ReportLength = 8;

    public const int HeaderAddress = 0x00D0;           // absolute: E1 1E BASE_lo BASE_hi F0 0F 00 00
    public const int DefaultBase = 0x00E0;

    public const int OffFlags = 0x00;                  // BASE+0  global flags
    public const int OffColors = 0x01;                 // BASE+1..0x0E  7 colours x 2 bytes: (R<<4|B), (X<<4|G)
    public const int OffLedExtra = 0x0F;               // BASE+0x0F..0x17 raw (preserved)
    public const int LedExtraLength = 9;
    public const int OffLedA = 0x18;                   // dir<<6 | bright<<3 | speed
    public const int OffLedB = 0x19;                   // sym<<7 | colorIdx<<4 | mode
    public const int OffCycleMask = 0x1A;              // 0x80 | colour-cycle mask
    public const int OffModeEnable = 0x1B;             // mode enable mask
    public const int OffKeymapPtr = 0x1C;              // u16 = BASE+0x48
    public const int OffMacroPtr = 0x1E;               // u16 = BASE+0xF8
    public const int OffWheel = 0x20;                  // first<<4 | second
    public const int OffKeymap = 0x48;                 // 20 cols x 8 rows, column-major
    public const int KeymapColumns = 20, KeymapRows = 8, KeymapLength = 160;
    public const int OffFn = 0xE8;                     // 12 bytes Fn+F1..F12 + 4 bytes zero
    public const int OffMacroTable = 0xF8;             // 20 entries x 5 bytes
    public const int MacroSlots = 20, MacroEntrySize = 5, MacroTableLength = 100;
    public const int OffTrailer = 0x15C;               // 4 bytes: vendor stack garbage (never meaningful)
    public const int MacroAreaLimit = 0x4000;

    /// <summary>Minimum image size the codec needs (covers header, config, table, typical macros).</summary>
    public const int MinImageLength = 0x300;

    public static readonly byte[] FactoryColumnCodes =
        { 0x59, 0x5A, 0x5B, 0x5C, 0x5D, 0x5E, 0x5F, 0x60, 0x28, 0x2B, 0x61, 0x62, 0x50, 0x4F, 0x51, 0x52, 0xF4, 0xE9 };

    /// <summary>Physical controls. Coordinates follow the vendor KB.ini (pixels), joystick confirmed by experiments 7/8.</summary>
    public static readonly IReadOnlyList<K13Control> Controls = new List<K13Control>
    {
        new("k1",  "Клавиша 1", 0, 87, 0x59, ControlKind.Key, 367, 311, 58, 58),
        new("k2",  "Клавиша 2", 1, 88, 0x5A, ControlKind.Key, 430, 311, 58, 58),
        new("k3",  "Клавиша 3", 2, 89, 0x5B, ControlKind.Key, 494, 311, 58, 58),
        new("k4",  "Клавиша 4", 3, 71, 0x5C, ControlKind.Key, 557, 311, 58, 58),
        new("k5",  "Клавиша 5", 4, 72, 0x5D, ControlKind.Key, 621, 311, 58, 58),
        new("k6",  "Клавиша 6", 5, 73, 0x5E, ControlKind.Key, 367, 375, 86, 58),
        new("k7",  "Клавиша 7", 6, 54, 0x5F, ControlKind.Key, 461, 375, 58, 58),
        new("k8",  "Клавиша 8", 7, 55, 0x60, ControlKind.Key, 526, 375, 58, 58),
        new("ent", "Enter (высокая)", 8, 50, 0x28, ControlKind.Key, 600, 386, 70, 104),
        new("tab", "Tab (широкая)", 9, 37, 0x2B, ControlKind.Key, 367, 439, 105, 58),
        new("k9",  "Клавиша 9", 10, 56, 0x61, ControlKind.Key, 478, 439, 58, 58),
        new("k0",  "Клавиша 0", 11, 102, 0x62, ControlKind.Key, 541, 439, 58, 58),
        new("jl",  "Джойстик ←", 12, 99, 0x50, ControlKind.JoystickLeft, 760, 345, 40, 40),
        new("jr",  "Джойстик →", 13, 101, 0x4F, ControlKind.JoystickRight, 850, 345, 40, 40),
        new("jd",  "Джойстик ↓", 14, 100, 0x51, ControlKind.JoystickDown, 805, 390, 40, 40),
        new("ju",  "Джойстик ↑", 15, 86, 0x52, ControlKind.JoystickUp, 805, 300, 40, 40),
        new("h16", "Нажатие стика (колонка 16)", 16, null, 0xF4, ControlKind.Hidden, 0, 0, 0, 0),
        new("h17", "Скрытая кнопка 17 (смена режима LED)", 17, null, 0xE9, ControlKind.Hidden, 0, 0, 0, 0),
    };

    public static K13Control ByColumn(int col) => Controls.First(c => c.Column == col);

    /// <summary>Wheel functions (vendor "Roll" list, USCROLL0/1).</summary>
    public static readonly string[] WheelFunctions =
        { "Громкость −", "Громкость +", "Прокрутка вверх", "Прокрутка вниз", "Скорость эффекта +", "Скорость эффекта −", "Яркость +", "Яркость −" };

    /// <summary>LED effect names (vendor KbIndexText 30..42; index = mode, hypothesis — see PROTOCOL.md §16).</summary>
    public static readonly string[] LedModeNames =
    {
        "Подсветка выключена", "Дыхание", "Подсветка включена", "Отклик", "Неон", "Ambilight", "Хвост кометы",
        "Одиночная вода", "Проточная вода", "Мигающий", "Монохроматическая волна", "Перегородка", "Комбинация",
    };

    public static string LedModeName(int mode) => mode >= 0 && mode < LedModeNames.Length ? LedModeNames[mode] : $"Режим {mode}";

    public static int Align8(int v) => (v + 7) & ~7;
}
