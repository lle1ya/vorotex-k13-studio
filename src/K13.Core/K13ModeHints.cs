namespace Vorotex.K13;

/// <summary>1.0.1: honest texts about switching Normal → Game.
/// There is no documented «set mode» command (the vendor app has none, PROTOCOL §30.2). The read exchange ending in 07 18 11 that
/// Studio used for «Включить Game сейчас» switched to Game only right after USB connect on the user's K13 (8 of 8, 16–17.11.2026);
/// on the makers' K13 a manual run 17 of 17 times only blinked the backlight (07 18 11 re-applies settings) and left Normal
/// (diagnostics 17.11.2026 16:26–16:29). So the manual button is gone; the one reliable way is the stick (factory 0xF4),
/// and «Запоминать режим» (0x000F bit 1) keeps the last stick-chosen mode across replugs (confirmed by the makers).</summary>
public static class K13ModeHints
{
    public const string PressStick = "нажмите центр стика (загорится синий индикатор между Tab и 9)";
    /// <summary>1.3.2: where to see the mode when the blue indicator is switched off (0x000F bit 4 = 0).</summary>
    public const string StudioShowsMode = "режим видно в Studio — в строке под названием («режим Game»)";

    /// <summary>1.3.2: the indicator is known to be off — 0x000F read with a known pattern and bit 4 clear (0x81/0x83).</summary>
    public static bool IndicatorOff(byte? flagByte) => flagByte is byte b && K13RememberMode.IsKnownPattern(b) && !K13ModeIndicator.IsOn(b);

    /// <summary>«как увидеть Game»: the blue LED, or Studio's header when the LED is off.</summary>
    public static string GameShown(byte? flagByte) =>
        IndicatorOff(flagByte) ? "синий индикатор у вас выключен — " + StudioShowsMode : "горит синий индикатор между Tab и 9";

    /// <summary>«нажмите центр стика (…)» with the indicator state taken into account.</summary>
    public static string PressStickFor(byte? flagByte) =>
        IndicatorOff(flagByte) ? "нажмите центр стика (синий индикатор у вас выключен — " + StudioShowsMode + ")" : PressStick;
    public const string RememberAction = "Запоминать режим…";
    /// <summary>The removed manual action. Kept only so tests can check it never comes back.</summary>
    public const string RemovedManualAction = "Включить Game сейчас";
    /// <summary>1.0.5: the removed connect-time setting. Kept only so tests can check it never comes back.</summary>
    public const string RemovedConnectSetting = "Пробовать включать Game при подключении";

    /// <summary>Should the Normal banner offer «Запоминать режим…»? Only when the flag is known off or unknown.</summary>
    public static bool OfferRemember(byte? rememberByte) => rememberByte is not byte b || !K13RememberMode.IsOn(b);

    /// <summary>Banner text in Normal.</summary>
    public static string NormalBanner(string what, byte? rememberByte)
    {
        string s = what + "Ваши назначения работают в режиме Game: " + PressStickFor(rememberByte) + ".";
        if (OfferRemember(rememberByte)) s += " Чтобы клавиатура сама оставалась в Game после переподключения, включите «Запоминать режим».";
        return s;
    }

    public const string StatusForeignKey = "Клавиатура прислала «{0}» — она в режиме Normal. Чтобы включить Game, " + PressStick + ".";
    public static string StatusForeignKeyFor(string key, byte? flagByte) =>
        $"Клавиатура прислала «{key}» — она в режиме Normal. Чтобы включить Game, {PressStickFor(flagByte)}.";

    /// <summary>1.3.2: after «Вернуть заводские назначения» — not the usual «ваши назначения работают в Game» (makers 19.11.2026).</summary>
    public static string FactoryWritten(string profile, double secs) =>
        $"Заводские назначения записаны и проверены ({secs:0.0} с). Профиль «{profile}» сохранён на ПК.\n\n" +
        "В клавиатуру он попадёт только по кнопке «Применить профиль». До этого клавиатура работает как с завода — и в Normal, и в Game: " +
        "кнопки посылают заводские коды (Num 1…), колесо — громкость, макросов нет.";
}
