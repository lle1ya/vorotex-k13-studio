namespace Vorotex.K13;

/// <summary>v5.10: turns any exception into a short Russian message for the user (the technical text goes to the log).</summary>
public static class FriendlyError
{
    public static string Text(Exception ex)
    {
        if (ex is AggregateException { InnerException: { } inner }) ex = inner;
        string m = ex.Message ?? "";
        switch (ex)
        {
            case WriteBlockedException: return m;
            case K13FormatException: return "Данные клавиатуры не похожи на ожидаемые: " + m;
            case TimeoutException: return "Клавиатура не ответила вовремя. Переподключите USB и повторите.";
            case FileNotFoundException f: return $"Файл не найден: {Path.GetFileName(f.FileName ?? "")}".TrimEnd(' ', ':') + ".";
            case DirectoryNotFoundException: return "Папка не найдена.";
            case UnauthorizedAccessException: return "Нет доступа к файлу или папке (возможно, он открыт в другой программе или нужны права).";
            case System.Text.Json.JsonException: return "Файл повреждён или имеет другой формат.";
            case IOException when m.Contains("HidD_", StringComparison.Ordinal) || m.Contains("отключ", StringComparison.OrdinalIgnoreCase):
                return "Клавиатура не отвечает — возможно, её отключили. Переподключите USB и повторите.";
            case OperationCanceledException: return "Операция отменена.";
        }
        if (HasCyrillic(m)) return m;
        return $"Внутренняя ошибка программы ({ex.GetType().Name}). Подробности — в журнале (⋯ → «Устройство и журнал»).";
    }

    private static bool HasCyrillic(string s) => s.Any(c => c is >= 'А' and <= 'я' or 'ё' or 'Ё');
}
