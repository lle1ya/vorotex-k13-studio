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
    public const string RememberAction = "Запоминать режим…";
    /// <summary>The removed manual action. Kept only so tests can check it never comes back.</summary>
    public const string RemovedManualAction = "Включить Game сейчас";
    public const string ConnectSetting = "Пробовать включать Game при подключении";

    /// <summary>Should the Normal banner offer «Запоминать режим…»? Only when the flag is known off or unknown.</summary>
    public static bool OfferRemember(byte? rememberByte) => rememberByte is not byte b || !K13RememberMode.IsOn(b);

    /// <summary>Banner text in Normal. <paramref name="failedAfterTry"/>: the connect-time attempt ran and the keypad stayed Normal.</summary>
    public static string NormalBanner(string what, bool failedAfterTry, byte? rememberByte)
    {
        string s = what + (failedAfterTry ? "Программа не смогла включить Game — " : "Ваши назначения работают в режиме Game: ") + PressStick + ".";
        if (OfferRemember(rememberByte)) s += " Чтобы клавиатура сама оставалась в Game после переподключения, включите «Запоминать режим».";
        return s;
    }

    /// <summary>Status line after the connect-time attempt.</summary>
    public static string AfterConnectTry(HwMode after) => after switch
    {
        HwMode.Game => "Включён режим Game — работают ваши назначения (только чтение, без записи).",
        HwMode.Normal => "Клавиатура осталась в режиме Normal — " + PressStick + ".",
        _ => "Режим клавиатуры определить не удалось. Если кнопки делают заводское (Num 1…), " + PressStick + ".",
    };

    public const string StatusForeignKey = "Клавиатура прислала «{0}» — она в режиме Normal. Чтобы включить Game, " + PressStick + ".";
}
