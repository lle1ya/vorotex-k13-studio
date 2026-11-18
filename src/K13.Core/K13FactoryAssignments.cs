namespace Vorotex.K13;

/// <summary>1.2.0 «Вернуть заводские назначения»: what can be put back RELIABLY. The K13 has no known «factory reset» command
/// (the vendor app has none; the burn tool rewrites the whole EEPROM — PROTOCOL §31). So the reset is an ordinary profile write
/// (backup → changed blocks → read-back) of values whose factory state is known:
/// • keys (keymap row 0, columns 0–17) — from the keypad's OWN built-in table at 0x0020 (the codes it sends in Normal mode);
/// • wheel — Vol−/Vol+ (0x01, confirmed on the makers' unit, exp16);
/// • Fn layer (BASE+0xE8) — FF (off, as on both known units);
/// • macros — all 20 slots empty.
/// Not touched (factory value unknown or not part of the assignments): lighting, BASE+0 flags (bit 7 must stay 1),
/// BASE+0x21..0x47, keymap rows 1–7, the factory block 0x0000–0x00DF (0x0000–0x0007 are refused by the protocol layer anyway),
/// «Запоминать режим» (0x000F). PROTOCOL §41.</summary>
public static class K13FactoryAssignments
{
    public sealed record Plan(K13DeviceConfig Target, bool KeysFromKeypad, List<string> Resets, List<string> Keeps, List<int> ChangedColumns);

    /// <summary>Factory codes per column (0–17). <paramref name="fromKeypad"/> = the image read from the keypad has exactly this
    /// built-in table (a K13); otherwise the documented K13 table is used and the writer refuses a non-K13 anyway.</summary>
    public static byte[] FactoryKeys(byte[]? image, out bool fromKeypad)
    {
        fromKeypad = K13Model.Detect(image) == KeypadModel.K13;
        return (byte[])K13Layout.FactoryColumnCodes.Clone();
    }

    public static Plan Build(K13DeviceConfig current, byte[]? image)
    {
        var t = current.Clone();
        var keys = FactoryKeys(image, out bool fromKeypad);
        var changed = new List<int>();
        for (int col = 0; col < keys.Length; col++)
        {
            if (t.GetKey(col) != keys[col]) changed.Add(col);
            t.SetKey(col, keys[col]);
        }
        t.WheelFirst = 0; t.WheelSecond = 1;
        for (int i = 0; i < t.Fn.Length; i++) t.Fn[i] = 0xFF;
        t.Macros = Enumerable.Range(0, K13Layout.MacroSlots).Select(_ => new MacroSlot()).ToList();
        var resets = new List<string>
        {
            "Кнопки 1–0, Enter, Tab, наклоны и нажатие стика — заводские коды (Num 1 … Num 0, Enter, Tab, стрелки, переключение Normal/Game)" +
                (fromKeypad ? ", взятые из встроенной таблицы самой клавиатуры" : ""),
            "Колесо — громкость − / +",
            "Слой Fn — выключен",
            "Макросы в памяти клавиатуры — очищены (все 20)",
        };
        var keeps = new List<string>
        {
            "Подсветка — заводские значения неизвестны, поэтому она остаётся как есть (её можно поменять на вкладке «Подсветка»)",
            "«Запоминать режим» и служебная область клавиатуры (тип памяти, VID/PID) — не трогаются",
            "Профили на компьютере (например «Основной») — остаются на ПК и в клавиатуру не пишутся, пока вы не нажмёте «Применить профиль»",
        };
        return new Plan(t, fromKeypad, resets, keeps, changed);
    }

    /// <summary>1.3.1: the assignments a factory reset writes are what the keypad holds: keys 0–17 = the factory table, wheel Vol−/Vol+,
    /// Fn layer off, no macros. Lighting and the other rows do not count (a reset does not touch them).</summary>
    public static bool IsFactory(K13DeviceConfig c)
    {
        var keys = K13Layout.FactoryColumnCodes;
        for (int col = 0; col < keys.Length; col++) if (c.GetKey(col) != keys[col]) return false;
        if (c.WheelFirst != 0 || c.WheelSecond != 1) return false;
        if (c.Fn.Any(b => b != 0xFF)) return false;
        return c.Macros.All(m => !m.IsUsed);
    }

    /// <summary>1.3.1: a K13 image (the model check is part of it) that holds the factory assignments.</summary>
    public static bool IsFactoryImage(byte[]? image)
    {
        if (image == null || K13Model.Detect(image) != KeypadModel.K13) return false;
        try { return IsFactory(K13MemoryCodec.Decode(image)); } catch { return false; }
    }

    /// <summary>True when nothing outside the known-factory parts differs between <paramref name="before"/> and the plan's target.</summary>
    public static bool OnlyKnownParts(K13DeviceConfig before, K13DeviceConfig target) =>
        before.Base == target.Base && before.Led.Flags == target.Led.Flags && LedSame(before.Led, target.Led) &&
        Enumerable.Range(0, K13Layout.KeymapColumns).All(c => Enumerable.Range(1, K13Layout.KeymapRows - 1).All(r => before.GetKey(c, r) == target.GetKey(c, r))) &&
        Enumerable.Range(18, K13Layout.KeymapColumns - 18).All(c => before.GetKey(c) == target.GetKey(c));

    private static bool LedSame(LedSettings a, LedSettings b) =>
        a.Mode == b.Mode && a.Brightness == b.Brightness && a.Speed == b.Speed && a.Direction == b.Direction && a.ColorIndex == b.ColorIndex &&
        a.Symmetry == b.Symmetry && a.CycleMask == b.CycleMask && a.ModeEnableMask == b.ModeEnableMask && a.Extra.SequenceEqual(b.Extra) &&
        a.Colors.Count == b.Colors.Count && a.Colors.Zip(b.Colors).All(p => p.First.R == p.Second.R && p.First.G == p.Second.G && p.First.B == p.Second.B && p.First.X == p.Second.X);
}
