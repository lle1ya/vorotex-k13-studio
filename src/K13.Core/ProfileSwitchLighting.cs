namespace Vorotex.K13;

/// <summary>1.0.2: lighting follows a profile switch.
/// Before: editing the lighting applied it to the keypad live (LED-only write, after the one-time write confirmation), but switching
/// to a profile with other lighting did not — the app showed the new colours, the keypad kept the old ones; «Записать» was the only
/// way and nothing said so for a manual switch. Now a switch the user makes (profile card / list, tray, keypad key) uses the same
/// live LED-only path; a switch by the active window (auto) never writes (EEPROM wear) and says «Записать».</summary>
public static class ProfileSwitchLighting
{
    /// <summary>Lighting area: BASE … BASE+0x1F (flags, colours, effect/brightness/speed).</summary>
    public const int LightingSpan = 0x20;

    /// <summary>Only the lighting area differs (the case the live path may write).</summary>
    public static bool IsLightingOnly(IReadOnlyCollection<WriteBlock> diff, int baseAddress) =>
        diff.Count > 0 && diff.All(b => b.Address >= baseAddress && b.Address + b.Data.Length <= baseAddress + LightingSpan);

    /// <summary>Lighting-only diff between the profile and the keypad image (false if the image is unknown).</summary>
    public static bool LightingOnlyPending(K13DeviceConfig target, byte[]? keypadImage)
    {
        if (keypadImage == null) return false;
        try { return IsLightingOnly(PcLayerHealth.PendingWrite(target, keypadImage), K13MemoryCodec.ReadBase(keypadImage)); }
        catch { return false; }
    }

    /// <summary>Does the lighting area differ at all (maybe together with other blocks)?</summary>
    public static bool LightingDiffers(K13DeviceConfig target, byte[]? keypadImage)
    {
        if (keypadImage == null) return false;
        try
        {
            int bs = K13MemoryCodec.ReadBase(keypadImage);
            return PcLayerHealth.PendingWrite(target, keypadImage).Any(b => b.Address < bs + LightingSpan && b.Address + b.Data.Length > bs);
        }
        catch { return false; }
    }

    /// <summary>Switches made by the user apply the lighting live; auto-switch by window never writes.</summary>
    public static bool AppliesLive(SwitchSource source) => source is SwitchSource.Manual or SwitchSource.Tray or SwitchSource.KeypadKey;

    /// <summary>Status note after a switch. <paramref name="willApply"/>: the live LED write was scheduled.</summary>
    public static string Note(bool lightingOnly, bool lightingDiffers, bool willApply, bool writeConfirmed, SwitchSource source, bool layerActive)
    {
        if (!lightingDiffers && !lightingOnly) return "";
        if (willApply) return "Подсветка профиля применяется к клавиатуре.";
        if (!lightingOnly)
            return layerActive ? "Кнопки уже переключены (через ПК); подсветка в клавиатуре прежняя — нажмите «Записать»."
                               : "Кнопки и подсветка в клавиатуре прежние — нажмите «Записать».";
        if (source == SwitchSource.Auto) return "Подсветка в клавиатуре прежняя: при автопереключении программа в клавиатуру не пишет — нажмите «Записать».";
        if (!writeConfirmed) return "Подсветка в клавиатуре прежняя — нажмите «Записать» (после первого подтверждения подсветка будет меняться вместе с профилем).";
        return "Подсветка в клавиатуре прежняя — нажмите «Записать».";
    }
}
