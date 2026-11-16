namespace Vorotex.K13;

/// <summary>Hardware keyboard mode of the K13 (PROTOCOL.md §30).</summary>
public enum HwMode { Unknown, Normal, Game }

/// <summary>v5.8: Normal / Game. Game = the user keymap at BASE+0x48 (what Studio and the vendor app write);
/// Normal = the built-in factory table (0x0020). The factory stick code 0xF4 («KBD Mode») toggles them; the blue LED between
/// Tab and 9 shows Game (VOROTEX, 16.11.2026). The mode is bit 7 of the flags byte BASE+0 (vendor ini key KEYSMODE,
/// FUN_0041bb30 → DAT_00622104 → bit 7 in FUN_00410042); GetFeature status byte 3 is the live copy of that byte.</summary>
public static class K13Mode
{
    public const byte KeysModeBit = 0x80;
    /// <summary>Bits the keypad owns (vendor Apply re-reads BASE+0 and keeps bits 5 and 4).</summary>
    public const byte DeviceOwnedBits = 0x30;
    public const byte KbdModeCode = 0xF4;

    /// <summary>Live mode from the status block (GetFeature RID7): byte 3 bit 7. Unknown if the block is too short.</summary>
    public static HwMode FromStatus(K13Status? s) =>
        s == null || s.Raw.Length < 4 || s.Raw[0] != K13Layout.ReportId ? HwMode.Unknown
        : (s.Raw[3] & KeysModeBit) != 0 ? HwMode.Game : HwMode.Normal;

    public static HwMode FromFlags(byte flags) => (flags & KeysModeBit) != 0 ? HwMode.Game : HwMode.Normal;

    public static string Name(HwMode m) => m switch { HwMode.Game => "Game", HwMode.Normal => "Normal", _ => "неизвестен" };

    /// <summary>What gets written (vendor-identical): tilts never FF (§29); flags bit 7 (KEYSMODE) = 1 as the vendor app writes
    /// by default, so «reload» (07 18 11) and the stick bring the user keymap — only when the flags byte is written anyway; bits 5–4 taken from the keypad as the vendor does.
    /// Returns a copy when anything changes.</summary>
    public static K13DeviceConfig PrepareTarget(K13DeviceConfig cfg, byte[]? deviceImage, Action<string>? log = null)
    {
        var c = cfg;
        K13DeviceConfig Copy() => ReferenceEquals(c, cfg) ? (c = cfg.Clone()) : c;
        if (StickTilts.Unassigned(cfg).Count > 0)
            log?.Invoke("Наклоны стика без назначения → стрелки: колонки " + string.Join(", ", StickTilts.Fill(Copy())) + ".");
        byte flags = cfg.Led.Flags;
        int? devFlags = deviceImage != null && cfg.Base + K13Layout.OffFlags < deviceImage.Length ? deviceImage[cfg.Base + K13Layout.OffFlags] : null;
        if (devFlags is int df) flags = (byte)(flags & ~DeviceOwnedBits | df & DeviceOwnedBits);
        // bit 7 only when the flags byte is written anyway — normalising alone never triggers a write
        if (devFlags is not int d || flags != d) flags |= KeysModeBit;
        if (flags != cfg.Led.Flags)
        {
            log?.Invoke($"Флаги BASE+0: {cfg.Led.Flags:X2} → {flags:X2} (бит 7 KEYSMODE = 1 — режим Game с вашими назначениями, как пишет программа производителя; биты 5–4 — как в клавиатуре).");
            Copy().Led.Flags = flags;
        }
        return c;
    }
}
