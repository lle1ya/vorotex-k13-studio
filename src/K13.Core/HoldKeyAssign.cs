namespace Vorotex.K13;

/// <summary>1.2.0 «Удерживать до отпускания» in the key window: one step for what the makers built by hand (macro «Зажать Alt» →
/// «Пока зажата» → «Отдать удержание этой кнопке» → «Записать»). The key gets a «пока зажата» macro that only PRESSES the keys;
/// through the PC the macro plays while the key is held and the sequencer releases every pressed key when the key is let go
/// (MacroSequencer: held keys are released on stop). True hold needs «Кнопки по профилю (через ПК)» and works for ONE key at a
/// time (one free hold code, «Меню» — PcLayerPlanner.HoldPool, PROTOCOL §33).</summary>
public static class HoldKeyAssign
{
    /// <summary>The macro: keys pressed in order, nothing released (released on key-up by the player).</summary>
    public static MacroSlot Macro(IReadOnlyList<byte> codes)
    {
        if (codes.Count == 0) throw new ArgumentException("Нет клавиш для удержания.");
        var m = new MacroSlot { Repeat = 1, Events = codes.Select(c => new MacroEvent { Down = true, Delay = 1, Code = c }).ToList() };
        m.RepeatMode = MacroRepeatMode.WhileHeld;
        return m;
    }

    /// <summary>Keys a macro holds, when it is such a «hold» macro (only presses, «пока зажата», no mouse/wheel/move); else null.</summary>
    public static List<byte>? HeldKeys(MacroSlot m)
    {
        if (m.RepeatMode != MacroRepeatMode.WhileHeld || m.Events.Count is 0 or > 4) return null;
        if (m.Events.Any(e => !e.Down || e.IsMouseButton || MouseCodes.ExtraLength(e.Code) != 0 || e.Code == MouseCodes.Wheel)) return null;
        var codes = m.Events.Select(e => e.Code).ToList();
        return codes.Distinct().Count() == codes.Count ? codes : null;
    }

    /// <summary>Assigns «hold <paramref name="codes"/>» to <paramref name="column"/>: reuses the key's own macro slot when it already
    /// holds keys, otherwise <paramref name="freeSlot"/>. Returns the slot or an error text.</summary>
    public static (int Slot, string? Error) Assign(K13DeviceConfig cfg, int column, IReadOnlyList<byte> codes, int freeSlot)
    {
        if (codes.Count == 0) return (-1, "Выберите клавишу, которую держать.");
        byte cur = cfg.GetKey(column);
        int slot = KeyCatalog.IsMacro(cur) && HeldKeys(cfg.Macros[KeyCatalog.MacroSlotOf(cur)]) != null ? KeyCatalog.MacroSlotOf(cur) : freeSlot;
        if (slot < 0 || slot >= KeyCatalog.MacroCodeCount) return (-1, "Нет свободного места для макроса удержания (все 11 макросов заняты).");
        cfg.Macros[slot] = Macro(codes);
        cfg.SetKey(column, (byte)(KeyCatalog.FirstMacro + slot));
        return (slot, null);
    }

    /// <summary>Plain-language status for the key window.</summary>
    /// <param name="layerActive">«Кнопки по профилю (через ПК)» is on.</param>
    /// <param name="mapped">the key goes through the PC in that mode.</param>
    /// <param name="owner">the key that has (or gets after «Записать») the one hold code; -1 = none.</param>
    public static string Status(string keyName, bool layerActive, bool mapped, int owner, int column, Func<int, string> name)
    {
        if (!layerActive)
            return "Удерживать до отпускания умеет только режим «Кнопки по профилю (через ПК)»: кнопку держит программа. Сама клавиатура так не умеет " +
                   "(её макрос «пока зажата» нажимает и отпускает по кругу). Включите режим кнопкой ниже (или «Профили → Кнопки по профилю»).";
        if (!mapped)
            return $"{keyName} сейчас не проходит через ПК (в этом режиме 11 мест на 12 кнопок). Чтобы удержание работало, подготовьте клавиатуру заново.";
        if (owner < 0 || owner == column)
            return $"Удержание будет у этой кнопки. Через ПК держать до отпускания может только одна кнопка за раз. Нажмите «Записать».";
        return $"Сейчас удержание у кнопки «{name(owner)}». Через ПК держать до отпускания может только одна кнопка — после «Готово» оно перейдёт сюда, " +
               $"а у «{name(owner)}» «пока зажата» будет работать как «до повторного нажатия». Нажмите «Записать».";
    }
}
