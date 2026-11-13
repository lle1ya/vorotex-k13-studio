namespace Vorotex.K13;

public enum KeyCategory { Disabled, Letter, Digit, Function, Navigation, Editing, Numpad, Modifier, Symbol, Media, Lighting, Macro, Special, Unknown }

public sealed record KeyInfo(byte Code, string Name, KeyCategory Category, ushort Vk = 0);

/// <summary>Device key codes (one byte per keymap cell). HID keyboard usages + vendor specials (PROTOCOL.md §4).</summary>
public static class KeyCatalog
{
    public const byte Disabled = 0xFF;
    public const byte FirstMacro = 0xA5;
    public const int MacroCodeCount = 11; // A5..AF (vendor marks these as macros 0..10)

    private static readonly Dictionary<byte, KeyInfo> _byCode = new();
    private static readonly Dictionary<ushort, byte> _byVk = new();

    public static IReadOnlyCollection<KeyInfo> All => _byCode.Values;

    static KeyCatalog()
    {
        void Add(byte code, string name, KeyCategory cat, ushort vk = 0)
        {
            _byCode[code] = new KeyInfo(code, name, cat, vk);
            if (vk != 0 && !_byVk.ContainsKey(vk)) _byVk[vk] = code;
        }
        Add(0xFF, "Отключено", KeyCategory.Disabled);
        for (int i = 0; i < 26; i++) Add((byte)(0x04 + i), ((char)('A' + i)).ToString(), KeyCategory.Letter, (ushort)(0x41 + i));
        for (int i = 0; i < 9; i++) Add((byte)(0x1E + i), ((char)('1' + i)).ToString(), KeyCategory.Digit, (ushort)(0x31 + i));
        Add(0x27, "0", KeyCategory.Digit, 0x30);
        Add(0x28, "Enter", KeyCategory.Editing, 0x0D);
        Add(0x29, "Esc", KeyCategory.Editing, 0x1B);
        Add(0x2A, "Backspace", KeyCategory.Editing, 0x08);
        Add(0x2B, "Tab", KeyCategory.Editing, 0x09);
        Add(0x2C, "Пробел", KeyCategory.Editing, 0x20);
        Add(0x2D, "- _", KeyCategory.Symbol, 0xBD);
        Add(0x2E, "= +", KeyCategory.Symbol, 0xBB);
        Add(0x2F, "[ {", KeyCategory.Symbol, 0xDB);
        Add(0x30, "] }", KeyCategory.Symbol, 0xDD);
        Add(0x31, "\\ |", KeyCategory.Symbol, 0xDC);
        Add(0x33, "; :", KeyCategory.Symbol, 0xBA);
        Add(0x34, "' \"", KeyCategory.Symbol, 0xDE);
        Add(0x35, "` ~", KeyCategory.Symbol, 0xC0);
        Add(0x36, ", <", KeyCategory.Symbol, 0xBC);
        Add(0x37, ". >", KeyCategory.Symbol, 0xBE);
        Add(0x38, "/ ?", KeyCategory.Symbol, 0xBF);
        Add(0x39, "Caps Lock", KeyCategory.Editing, 0x14);
        for (int i = 0; i < 12; i++) Add((byte)(0x3A + i), "F" + (i + 1), KeyCategory.Function, (ushort)(0x70 + i));
        Add(0x46, "Print Screen", KeyCategory.Navigation, 0x2C);
        Add(0x47, "Scroll Lock", KeyCategory.Navigation, 0x91);
        Add(0x48, "Pause", KeyCategory.Navigation, 0x13);
        Add(0x49, "Insert", KeyCategory.Navigation, 0x2D);
        Add(0x4A, "Home", KeyCategory.Navigation, 0x24);
        Add(0x4B, "Page Up", KeyCategory.Navigation, 0x21);
        Add(0x4C, "Delete", KeyCategory.Navigation, 0x2E);
        Add(0x4D, "End", KeyCategory.Navigation, 0x23);
        Add(0x4E, "Page Down", KeyCategory.Navigation, 0x22);
        Add(0x4F, "→", KeyCategory.Navigation, 0x27);
        Add(0x50, "←", KeyCategory.Navigation, 0x25);
        Add(0x51, "↓", KeyCategory.Navigation, 0x28);
        Add(0x52, "↑", KeyCategory.Navigation, 0x26);
        Add(0x53, "Num Lock", KeyCategory.Numpad, 0x90);
        Add(0x54, "Num /", KeyCategory.Numpad, 0x6F);
        Add(0x55, "Num *", KeyCategory.Numpad, 0x6A);
        Add(0x56, "Num -", KeyCategory.Numpad, 0x6D);
        Add(0x57, "Num +", KeyCategory.Numpad, 0x6B);
        Add(0x58, "Num Enter", KeyCategory.Numpad);
        for (int i = 0; i < 9; i++) Add((byte)(0x59 + i), "Num " + (i + 1), KeyCategory.Numpad, (ushort)(0x61 + i));
        Add(0x62, "Num 0", KeyCategory.Numpad, 0x60);
        Add(0x63, "Num .", KeyCategory.Numpad, 0x6E);
        Add(0x65, "Меню (App)", KeyCategory.Navigation, 0x5D);
        for (int i = 0; i < 12; i++) Add((byte)(0x68 + i), "F" + (13 + i), KeyCategory.Function, (ushort)(0x7C + i));
        string[] mods = { "Left Ctrl", "Left Shift", "Left Alt", "Left Win", "Right Ctrl", "Right Shift", "Right Alt", "Right Win" };
        ushort[] modVk = { 0xA2, 0xA0, 0xA4, 0x5B, 0xA3, 0xA1, 0xA5, 0x5C };
        for (int i = 0; i < 8; i++) Add((byte)(0xE0 + i), mods[i], KeyCategory.Modifier, modVk[i]);
        for (int i = 0; i < MacroCodeCount; i++) Add((byte)(FirstMacro + i), "Макрос " + i, KeyCategory.Macro);
        Add(0xC9, "Воспроизведение/пауза", KeyCategory.Media, 0xB3);
        Add(0xCA, "Громкость −", KeyCategory.Media, 0xAE);
        Add(0xCB, "Громкость +", KeyCategory.Media, 0xAF);
        Add(0xCC, "Следующий трек", KeyCategory.Media, 0xB0);
        Add(0xCD, "Предыдущий трек", KeyCategory.Media, 0xB1);
        Add(0xCE, "Медиаплеер", KeyCategory.Media, 0xB5);
        Add(0xCF, "Почта", KeyCategory.Media, 0xB4);
        Add(0xD0, "Браузер: вперёд", KeyCategory.Media, 0xA7);
        Add(0xD1, "Браузер: стоп", KeyCategory.Media, 0xA9);
        Add(0xD2, "Браузер: назад", KeyCategory.Media, 0xA6);
        Add(0xD3, "Браузер: обновить", KeyCategory.Media, 0xA8);
        Add(0xD4, "Браузер: поиск", KeyCategory.Media, 0xAA);
        Add(0xD5, "Браузер: избранное", KeyCategory.Media, 0xAB);
        Add(0xD6, "Без звука", KeyCategory.Media, 0xAD);
        Add(0xD7, "Мой компьютер", KeyCategory.Media);
        Add(0xD8, "Стоп", KeyCategory.Media, 0xB2);
        Add(0xD9, "Калькулятор", KeyCategory.Media);
        Add(0xDA, "Браузер (домашняя)", KeyCategory.Media, 0xAC);
        Add(0xDC, "Перемотка", KeyCategory.Media);
        Add(0xDD, "Извлечь", KeyCategory.Media);
        Add(0xDE, "Свернуть окно", KeyCategory.Special);
        Add(0xDF, "Скриншот", KeyCategory.Special);
        Add(0xE8, "LED: смена цвета", KeyCategory.Lighting);
        Add(0xE9, "LED: смена режима", KeyCategory.Lighting);
        Add(0xEA, "LED: яркость +", KeyCategory.Lighting);
        Add(0xEB, "LED: яркость −", KeyCategory.Lighting);
        Add(0xEC, "LED: скорость +", KeyCategory.Lighting);
        Add(0xED, "LED: скорость −", KeyCategory.Lighting);
        Add(0xF0, "LED: вкл/выкл", KeyCategory.Lighting);
        Add(0xF3, "Блокировка всех клавиш", KeyCategory.Special);
        Add(0xF4, "Заводская функция стика (KBD Mode)", KeyCategory.Special);
    }

    public static KeyInfo Get(byte code) =>
        _byCode.TryGetValue(code, out var k) ? k : new KeyInfo(code, $"Код 0x{code:X2}", KeyCategory.Unknown);

    public static string Name(byte code) => Get(code).Name;

    public static bool TryFromVirtualKey(ushort vk, out byte code) => _byVk.TryGetValue(vk, out code);

    public static ushort ToVirtualKey(byte code) => Get(code).Vk;

    public static bool IsMacro(byte code) => code >= FirstMacro && code < FirstMacro + MacroCodeCount;
    public static int MacroSlotOf(byte code) => IsMacro(code) ? code - FirstMacro : -1;

    public static string CategoryTitle(KeyCategory c) => c switch
    {
        KeyCategory.Disabled => "Отключено",
        KeyCategory.Letter => "Буквы",
        KeyCategory.Digit => "Цифры",
        KeyCategory.Function => "F-клавиши",
        KeyCategory.Navigation => "Навигация",
        KeyCategory.Editing => "Ввод/редактирование",
        KeyCategory.Numpad => "Цифровой блок",
        KeyCategory.Modifier => "Модификаторы",
        KeyCategory.Symbol => "Символы",
        KeyCategory.Media => "Медиа",
        KeyCategory.Lighting => "Подсветка",
        KeyCategory.Macro => "Макросы",
        KeyCategory.Special => "Спецфункции",
        _ => "Прочее",
    };
}
