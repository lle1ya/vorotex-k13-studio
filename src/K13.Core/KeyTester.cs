namespace Vorotex.K13;

/// <summary>1.2.0 «Проверка кнопок»: turns what the PC sees from the keypad (Raw Input of VID_30FA&amp;PID_2350, or a key the
/// «через ПК» hook took) into the physical control(s) on the schematic. The keypad does not report WHICH button was pressed —
/// only the code that button sends — so the control is found by the code: in Normal by the built-in factory table, in Game by
/// the keymap last read/written, through the PC by the Scroll Lock + F chord. Buttons sending the same code cannot be told
/// apart (all candidates are lit).</summary>
public static class KeyTester
{
    public const int RollUp = 100, RollDown = 101;   // = DeviceView.RollUp/RollDown (Studio)

    /// <summary>Consumer-page usages (HID page 0x0C) the K13 media codes send → the VK Windows gives the same key.</summary>
    public static readonly IReadOnlyDictionary<ushort, (ushort Vk, string Name)> Consumer = new Dictionary<ushort, (ushort, string)>
    {
        [0x00E9] = (0xAF, "Громкость +"), [0x00EA] = (0xAE, "Громкость −"), [0x00E2] = (0xAD, "Без звука"),
        [0x00CD] = (0xB3, "Воспроизведение/пауза"), [0x00B5] = (0xB0, "Следующий трек"), [0x00B6] = (0xB1, "Предыдущий трек"),
        [0x00B7] = (0xB2, "Стоп"), [0x0183] = (0xB5, "Медиаплеер"), [0x018A] = (0xB4, "Почта"), [0x0192] = (0, "Калькулятор"),
        [0x0194] = (0, "Мой компьютер"), [0x0223] = (0xAC, "Браузер: домой"), [0x0224] = (0xA6, "Браузер: назад"),
        [0x0225] = (0xA7, "Браузер: вперёд"), [0x0226] = (0xA9, "Браузер: стоп"), [0x0227] = (0xA8, "Браузер: обновить"),
        [0x022A] = (0xAB, "Браузер: избранное"), [0x0221] = (0xAA, "Браузер: поиск"),
    };

    /// <summary>Usage from a consumer-control input report: 16-bit little-endian after the report id (1 byte) when the report
    /// is 3 bytes, else the first non-zero 16-bit value after the id. 0 = all released.</summary>
    public static ushort ConsumerUsage(ReadOnlySpan<byte> report)
    {
        if (report.Length < 2) return 0;
        for (int i = 1; i + 1 < report.Length; i += 2)
        {
            ushort u = (ushort)(report[i] | report[i + 1] << 8);
            if (u != 0) return u;
        }
        return report.Length == 2 ? report[1] : (ushort)0;
    }

    public static string ConsumerName(ushort usage) => Consumer.TryGetValue(usage, out var c) ? c.Name : $"медиа-код 0x{usage:X3}";

    /// <summary>Code each control sends now: Normal = built-in factory table; Game/unknown = the keypad's keymap (if known).</summary>
    public static byte[] ActiveCodes(HwMode mode, K13DeviceConfig? keypad)
    {
        if (mode == HwMode.Normal || keypad == null) return (byte[])K13Layout.FactoryColumnCodes.Clone();
        return Enumerable.Range(0, K13Layout.FactoryColumnCodes.Length).Select(c => keypad.GetKey(c)).ToArray();
    }

    /// <summary>VK a code produces first (a macro: its first key press).</summary>
    public static ushort FirstVk(byte code, K13DeviceConfig? keypad)
    {
        if (KeyCatalog.IsMacro(code))
        {
            int s = KeyCatalog.MacroSlotOf(code);
            var e = keypad != null && s < keypad.Macros.Count ? keypad.Macros[s].Events.FirstOrDefault(x => x.Down && !x.IsMouseButton && MouseCodes.ExtraLength(x.Code) == 0) : null;
            return e == null ? (ushort)0 : KeyCatalog.ToVirtualKey(e.Code);
        }
        return KeyCatalog.ToVirtualKey(code);
    }

    /// <summary>Controls that send <paramref name="vk"/> (columns 0–17, plus the wheel for volume/scroll).</summary>
    public static List<int> ControlsFor(ushort vk, HwMode mode, K13DeviceConfig? keypad)
    {
        var list = new List<int>();
        if (vk == 0) return list;
        var codes = ActiveCodes(mode, keypad);
        for (int col = 0; col < codes.Length; col++)
            if (codes[col] != KeyCatalog.Disabled && FirstVk(codes[col], mode == HwMode.Normal ? null : keypad) == vk) list.Add(col);
        int first = keypad?.WheelFirst ?? 0, second = keypad?.WheelSecond ?? 1;
        if (WheelVk(second) == vk) list.Add(RollUp);
        if (WheelVk(first) == vk) list.Add(RollDown);
        return list;
    }

    /// <summary>VK a wheel function sends (volume only; scroll is a mouse wheel event, lighting is invisible to the PC).</summary>
    public static ushort WheelVk(int fn) => (fn & 7) switch { 0 => 0xAE, 1 => 0xAF, _ => 0 };

    /// <summary>Wheel side for a mouse-wheel event (functions 2 = scroll up, 3 = scroll down).</summary>
    public static List<int> ControlsForScroll(bool up, K13DeviceConfig? keypad)
    {
        int first = keypad?.WheelFirst ?? 0, second = keypad?.WheelSecond ?? 1, want = up ? 2 : 3;
        var l = new List<int>();
        if ((second & 7) == want) l.Add(RollUp);
        if ((first & 7) == want) l.Add(RollDown);
        return l;
    }

    /// <summary>Through the PC: the F-key of a «Scroll Lock + F» chord → the column that owns it.</summary>
    public static int ChordColumn(ushort fVk, PcLayerState? routed)
    {
        if (routed == null || !routed.Active) return -1;
        var k = routed.Keys.FirstOrDefault(x => KeyCatalog.ToVirtualKey(x.Code) == fVk);
        return k?.Column ?? -1;
    }

    /// <summary>The limits shown under the tester.</summary>
    public static IReadOnlyList<string> Limits(bool layerActive) => new List<string>
    {
        "Клавиатура сообщает не номер кнопки, а код, который кнопка посылает. Если две кнопки посылают одно и то же, подсветятся обе.",
        "В режиме Normal кнопки посылают заводские коды (Num 1…), в Game — назначенные. Макрос узнаётся по первой нажатой в нём клавише.",
        "Колесо видно, только если оно посылает громкость или прокрутку; функции подсветки (скорость, яркость) компьютеру не видны.",
        "Нажатие стика в заводском назначении (Normal/Game) и кнопки подсветки компьютеру ничего не посылают — увидеть их нельзя.",
        layerActive
            ? "«Кнопки по профилю (через ПК)» включены: такие кнопки видны по сигналу Scroll Lock + F; отпускание видно только у кнопки с удержанием."
            : "Действие кнопки при проверке выполняется как обычно (окно проверки его не отменяет).",
    };
}
