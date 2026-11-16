namespace Vorotex.K13;

/// <summary>v5.5: where the «profile switched» notice appears.</summary>
public enum ToastPlacement { BottomRight = 0, TopCenter = 1, NearCursor = 2 }

/// <summary>Why the profile changed — decides whether the notice is shown.</summary>
public enum SwitchSource { Manual, Tray, Auto, KeypadKey }

/// <summary>Text of the notice: «Профиль: 1С» / «по программе 1cv8c.exe».</summary>
public sealed record ToastContent(string Profile, string Detail, bool IsFallback)
{
    public string Title => "Профиль: " + Profile;
    public string Text => Detail.Length > 0 ? $"{Title} · {Detail}" : Title;
}

/// <summary>A screen rectangle in physical pixels (may have negative coordinates on multi-monitor setups).</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

public static class SwitchToastLogic
{
    public const int ShowMs = 2000, FadeInMs = 140, FadeOutMs = 450;
    public const int MaxDetail = 80;

    public static string PlacementName(ToastPlacement p) => p switch
    {
        ToastPlacement.TopCenter => "Вверху по центру",
        ToastPlacement.NearCursor => "У курсора мыши",
        _ => "Внизу справа",
    };

    /// <summary>
    /// The notice is for switches you did not make in this window: by program (auto-switch, also back to the default
    /// profile) or by a keypad key. Picking a profile in the app or the tray menu shows nothing — you just did it.
    /// </summary>
    public static bool ShouldShow(bool enabled, SwitchSource source, bool profileChanged) =>
        enabled && profileChanged && source is SwitchSource.Auto or SwitchSource.KeypadKey;

    public static ToastContent? For(string profile, string reason, SwitchSource source, bool isFallback = false)
    {
        if (source is SwitchSource.Manual or SwitchSource.Tray) return null;
        var name = Clean(profile, 40);
        if (name.Length == 0) return null;
        string detail = source == SwitchSource.KeypadKey ? "кнопкой на клавиатуре" : Clean(reason, MaxDetail);
        return new ToastContent(name, detail, isFallback);
    }

    /// <summary>Top-left corner of a <paramref name="w"/>×<paramref name="h"/> notice, always fully inside <paramref name="work"/>.</summary>
    public static (int X, int Y) Place(ToastPlacement placement, PixelRect work, int cursorX, int cursorY, int w, int h, int margin)
    {
        w = Math.Max(1, w); h = Math.Max(1, h); margin = Math.Max(0, margin);
        int x, y;
        switch (placement)
        {
            case ToastPlacement.TopCenter:
                x = work.Left + (work.Width - w) / 2; y = work.Top + margin; break;
            case ToastPlacement.NearCursor:
                int off = Math.Max(12, margin);
                x = cursorX + off; y = cursorY + off;
                if (x + w > work.Right - margin) x = cursorX - off - w;   // no room on the right → left of the cursor
                if (y + h > work.Bottom - margin) y = cursorY - off - h;  // no room below → above it
                break;
            default:
                x = work.Right - margin - w; y = work.Bottom - margin - h; break;
        }
        x = Math.Clamp(x, work.Left + Math.Min(margin, Math.Max(0, (work.Width - w) / 2)), Math.Max(work.Left, work.Right - w - Math.Min(margin, Math.Max(0, (work.Width - w) / 2))));
        y = Math.Clamp(y, work.Top + Math.Min(margin, Math.Max(0, (work.Height - h) / 2)), Math.Max(work.Top, work.Bottom - h - Math.Min(margin, Math.Max(0, (work.Height - h) / 2))));
        return (x, y);
    }

    private static string Clean(string? s, int max)
    {
        var t = new string((s ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return t.Length > max ? t[..(max - 1)].TrimEnd() + "…" : t;
    }
}
