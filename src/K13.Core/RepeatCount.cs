namespace Vorotex.K13;

/// <summary>Repeat count of an "N раз" macro: one place for range, parsing and Russian wording,
/// so the editor field, the test-run, profiles.json and the keypad all use the same number.</summary>
public static class RepeatCount
{
    public const int Min = 2, Max = 0xFFFF;

    public static int Clamp(int n) => Math.Clamp(n, Min, Max);

    /// <summary>Parses what the user typed. Digits only; out-of-range values are clamped. Empty/garbage → false.</summary>
    public static bool TryParse(string? text, out int value)
    {
        value = 0;
        text = text?.Trim();
        // v5.3: ASCII digits only — char.IsDigit also accepts e.g. Arabic-Indic «٣», and int.Parse then threw (crash while typing)
        if (string.IsNullOrEmpty(text) || !text.All(c => c is >= '0' and <= '9')) return false;
        value = text.Length > 6 ? Max : Clamp(int.Parse(text));
        return true;
    }

    /// <summary>"1 раз", "2 раза", "5 раз", "11 раз", "21 раз", "22 раза".</summary>
    public static string Word(int n)
    {
        n = Math.Abs(n);
        if (n % 100 is >= 11 and <= 14) return "раз";
        return (n % 10) is 2 or 3 or 4 ? "раза" : "раз";
    }

    public static string Format(int n) => $"{n} {Word(n)}";
}

/// <summary>Russian plural forms: Of(1, "блок", "блока", "блоков") = «1 блок», 3 → «3 блока», 11 → «11 блоков».</summary>
public static class RuPlural
{
    public static string Word(int n, string one, string few, string many)
    {
        n = Math.Abs(n);
        if (n % 100 is >= 11 and <= 14) return many;
        return (n % 10) switch { 1 => one, 2 or 3 or 4 => few, _ => many };
    }

    public static string Of(int n, string one, string few, string many) => $"{n} {Word(n, one, few, many)}";

    public static string Blocks(int n) => Of(n, "блок", "блока", "блоков");
}
