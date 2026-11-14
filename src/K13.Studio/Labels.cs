using Vorotex.K13;

namespace Vorotex.Studio;

/// <summary>Short, human-friendly key legends (icon + short text) for the keypad picture and chips.</summary>
public static class Labels
{
    private static readonly Dictionary<byte, (string Icon, string Text)> Map = new()
    {
        [0xC9] = ("⏯", "Пауза"), [0xCA] = ("🔉", "Тише"), [0xCB] = ("🔊", "Громче"), [0xCC] = ("⏭", "Далее"), [0xCD] = ("⏮", "Назад"),
        [0xCE] = ("♫", "Плеер"), [0xCF] = ("✉", "Почта"), [0xD0] = ("→", "Вперёд"), [0xD1] = ("✕", "Стоп"), [0xD2] = ("←", "Назад"),
        [0xD3] = ("⟳", "Обновить"), [0xD4] = ("⌕", "Поиск"), [0xD5] = ("☆", "Избранное"), [0xD6] = ("🔇", "Без звука"),
        [0xD7] = ("🖥", "Мой ПК"), [0xD8] = ("⏹", "Стоп"), [0xD9] = ("🖩", "Кальк."), [0xDA] = ("⌂", "Домой"),
        [0xDC] = ("⏩", "Перемотка"), [0xDD] = ("⏏", "Извлечь"), [0xDE] = ("🗕", "Свернуть"), [0xDF] = ("✂", "Скриншот"),
        [0xE8] = ("💡", "Цвет"), [0xE9] = ("💡", "Режим"), [0xEA] = ("☀", "Ярче"), [0xEB] = ("☀", "Тусклее"),
        [0xEC] = ("💡", "Быстрее"), [0xED] = ("💡", "Медленнее"), [0xF0] = ("💡", "Вкл/выкл"),
        [0xF3] = ("🔒", "Блок."), [0xF4] = ("★", "KBD Mode"),
        [0x28] = ("", "Enter"), [0x29] = ("", "Esc"), [0x2A] = ("⌫", "Bksp"), [0x2B] = ("⇥", "Tab"), [0x2C] = ("", "Пробел"),
        [0x39] = ("", "Caps"), [0x46] = ("", "PrtSc"), [0x47] = ("", "ScrLk"), [0x48] = ("", "Pause"), [0x49] = ("", "Ins"),
        [0x4A] = ("", "Home"), [0x4B] = ("", "PgUp"), [0x4C] = ("", "Del"), [0x4D] = ("", "End"), [0x4E] = ("", "PgDn"),
        [0x53] = ("", "NumLk"), [0x58] = ("", "Num ⏎"), [0x65] = ("☰", "Меню"),
        [0xE0] = ("", "Ctrl"), [0xE1] = ("", "Shift"), [0xE2] = ("", "Alt"), [0xE3] = ("⊞", "Win"),
        [0xE4] = ("", "RCtrl"), [0xE5] = ("", "RShift"), [0xE6] = ("", "AltGr"), [0xE7] = ("⊞", "RWin"),
        [0xFF] = ("", "—"),
    };

    /// <summary>User macro names (set by the main window from the active profile).</summary>
    public static Func<int, string>? MacroNamer { get; set; }

    public static string KeyText(byte code) => Map.TryGetValue(code, out var v) ? v.Text : KeyCatalog.Name(code);

    /// <summary>v5.2: the PC-side action that already listens to this key code (F13–F24 triggers, diagonals), or null.</summary>
    public static string? HostUse(byte code, HostSettings host)
    {
        if (code is < 0x68 or > 0x73) return null;
        ushort vk = (ushort)(0x7C + (code - 0x68));
        var b = host.Keys.FirstOrDefault(k => k.TriggerVk == vk);
        if (b != null && (b.Tap.Kind != HostActionKind.None || b.Hold.Kind != HostActionKind.None))
        {
            var a = b.Tap.Kind != HostActionKind.None ? b.Tap : b.Hold;
            string what = a.Kind switch
            {
                HostActionKind.Ring => "Кольцо действий",
                HostActionKind.LaunchProgram => $"запуск «{System.IO.Path.GetFileNameWithoutExtension(a.Value)}»",
                HostActionKind.OpenUrl => $"сайт {Host(a.Value)}",
                HostActionKind.Text => "вставка текста",
                HostActionKind.KeyCombo => $"сочетание {a.Value}",
                HostActionKind.SwitchProfile => $"профиль «{a.Value}»",
                _ => a.ToString(),
            };
            return b.Tap.Kind == HostActionKind.None ? "удержание: " + what : what;
        }
        var c = host.Chords.FirstOrDefault(ch => ch.VkA == vk || ch.VkB == vk);
        return c != null ? $"диагональ «{c.Title}»" : null;
    }

    /// <summary>«F13 · занята: Кольцо действий» for a code taken by a PC action, otherwise just the key name.</summary>
    public static string BusyName(byte code, HostSettings host) =>
        HostUse(code, host) is { } use ? $"{KeyCatalog.Name(code)} · занята: {use}" : KeyCatalog.Name(code);

    /// <summary>Returns a short legend for a keymap code in the context of the profile.</summary>
    public static (string Icon, string Text) For(byte code, K13DeviceConfig cfg, HostSettings host)
    {
        if (KeyCatalog.IsMacro(code))
        {
            int slot = KeyCatalog.MacroSlotOf(code);
            var mouse = MouseOf(cfg.Macros[slot]);
            if (mouse != null) return ("🖱", mouse);
            var combo = ComboOf(cfg.Macros[slot]);
            return ("⏺", combo ?? MacroNamer?.Invoke(slot) ?? $"Макрос {slot + 1}");
        }
        if (code is >= 0x68 and <= 0x73)
        {
            var b = host.Keys.FirstOrDefault(k => k.TriggerVk == 0x7C + (code - 0x68));
            if (b != null && b.Tap.Kind != HostActionKind.None)
                return b.Tap.Kind switch
                {
                    HostActionKind.LaunchProgram => ("▶", System.IO.Path.GetFileNameWithoutExtension(b.Tap.Value)),
                    HostActionKind.OpenUrl => ("🔗", Host(b.Tap.Value)),
                    HostActionKind.Text => ("✎", "Текст"),
                    HostActionKind.KeyCombo => ("", b.Tap.Value),
                    HostActionKind.SwitchProfile => ("⇄", b.Tap.Value),
                    HostActionKind.Ring => ("◎", "Кольцо"),
                    HostActionKind.PlayMacro => ("⏺", b.Tap.ToString()),
                    _ => ("", KeyCatalog.Name(code)),
                };
        }
        if (Map.TryGetValue(code, out var v)) return v;
        var k = KeyCatalog.Get(code);
        if (k.Category == KeyCategory.Numpad && k.Name.StartsWith("Num ")) return ("", k.Name.Replace("Num ", "Num"));
        return ("", k.Name);
    }

    private static string Host(string url)
    {
        try { return new Uri(url.Contains("://") ? url : "https://" + url).Host.Replace("www.", ""); } catch { return "Ссылка"; }
    }

    /// <summary>If the macro is a plain key combination (all presses then reverse releases), returns e.g. "Ctrl+Alt+N".</summary>
    public static string? ComboOf(MacroSlot m)
    {
        var ev = m.Events;
        if (ev.Any(e => e.IsMouseButton || MouseCodes.ExtraLength(e.Code) != 0)) return null;
        if (ev.Count < 2 || ev.Count % 2 != 0 || m.RepeatMode != MacroRepeatMode.Count || m.Repeat > 1) return null;
        int n = ev.Count / 2;
        var downs = ev.Take(n).ToList();
        var ups = ev.Skip(n).ToList();
        if (downs.Any(e => !e.Down) || ups.Any(e => e.Down)) return null;
        if (!downs.Select(e => e.Code).OrderBy(c => c).SequenceEqual(ups.Select(e => e.Code).OrderBy(c => c))) return null;
        if (downs.Select(e => e.Code).Distinct().Count() != n) return null;
        return string.Join("+", ev.Take(n).Select(e => KeyText(e.Code)));
    }

    /// <summary>Text of one macro step in macro context (mouse codes differ from keymap codes!).</summary>
    public static string EventText(MacroEvent e)
    {
        if (e.IsWheel) return $"Колесо {(e.WheelAmount >= 0 ? "↑" : "↓")}" + (Math.Abs(e.WheelAmount) > 1 ? $"×{Math.Abs(e.WheelAmount)}" : "");
        if (e.IsMouseButton) return MouseCodes.Short(e.Code);
        if (e.Code == MouseCodes.Move) return "Курсор";
        return KeyText(e.Code);
    }

    /// <summary>"ЛКМ" / "Автоклик ПКМ" if the macro is a single mouse click, else null.</summary>
    public static string? MouseOf(MacroSlot m)
    {
        var ev = m.Events;
        if (ev.Count != 2 || !ev[0].IsMouseButton || !ev[0].Down || ev[1].Down || ev[1].Code != ev[0].Code) return null;
        if (m.RepeatMode == MacroRepeatMode.WhileHeld) return "Автоклик " + MouseCodes.Short(ev[0].Code);
        if (m.RepeatMode == MacroRepeatMode.Count && m.Repeat == 2) return "Двойной " + MouseCodes.Short(ev[0].Code);
        return m.RepeatMode == MacroRepeatMode.Count && m.Repeat <= 1 ? MouseCodes.Short(ev[0].Code) : null;
    }
}
