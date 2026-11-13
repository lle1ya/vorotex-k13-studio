using System.Windows.Input;
using Vorotex.K13;

namespace Vorotex.Studio;

/// <summary>Parses/sends key combinations like "Ctrl+Shift+Esc" (virtual-key based).</summary>
public static class KeyCombo
{
    private static readonly Dictionary<string, ushort> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = 0xA2, ["Control"] = 0xA2, ["Shift"] = 0xA0, ["Alt"] = 0xA4, ["Win"] = 0x5B,
        ["RCtrl"] = 0xA3, ["RShift"] = 0xA1, ["RAlt"] = 0xA5, ["RWin"] = 0x5C,
        ["Enter"] = 0x0D, ["Esc"] = 0x1B, ["Escape"] = 0x1B, ["Tab"] = 0x09, ["Space"] = 0x20, ["Backspace"] = 0x08,
        ["Delete"] = 0x2E, ["Del"] = 0x2E, ["Insert"] = 0x2D, ["Home"] = 0x24, ["End"] = 0x23, ["PgUp"] = 0x21, ["PgDn"] = 0x22,
        ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28, ["PrintScreen"] = 0x2C, ["Pause"] = 0x13,
        ["VolumeUp"] = 0xAF, ["VolumeDown"] = 0xAE, ["Mute"] = 0xAD, ["PlayPause"] = 0xB3, ["Next"] = 0xB0, ["Prev"] = 0xB1,
        ["Stop"] = 0xB2, ["Player"] = 0xB5, ["Mail"] = 0xB4, ["BrowserHome"] = 0xAC, ["BrowserBack"] = 0xA6, ["BrowserForward"] = 0xA7, ["BrowserRefresh"] = 0xA8,
    };

    public static List<ushort> Parse(string combo)
    {
        var list = new List<ushort>();
        foreach (var part in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Named.TryGetValue(part, out var vk)) { list.Add(vk); continue; }
            if (part.Length == 1 && char.IsLetterOrDigit(part[0])) { list.Add(char.ToUpperInvariant(part[0])); continue; }
            if (part.Length >= 2 && (part[0] == 'F' || part[0] == 'f') && int.TryParse(part[1..], out int f) && f is >= 1 and <= 24) { list.Add((ushort)(0x6F + f)); continue; }
            if (Enum.TryParse<Key>(part, true, out var key)) { list.Add((ushort)KeyInterop.VirtualKeyFromKey(key)); continue; }
            throw new FormatException($"Неизвестная клавиша «{part}».");
        }
        return list;
    }

    public static string VkName(ushort vk)
    {
        foreach (var kv in Named) if (kv.Value == vk) return kv.Key;
        if (vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x6F);
        return KeyInterop.KeyFromVirtualKey(vk).ToString();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool LockWorkStation();

    public static void Send(string combo)
    {
        var vks = Parse(combo);
        // Win+L cannot be injected (secure attention sequence) — use the API instead.
        if (vks.Count == 2 && vks.Contains(0x5B) && vks.Contains('L')) { LockWorkStation(); return; }
        var inputs = new List<Native.INPUT>();
        foreach (var vk in vks) inputs.Add(KeyInput(vk, false));
        for (int i = vks.Count - 1; i >= 0; i--) inputs.Add(KeyInput(vks[i], true));
        Native.SendInput((uint)inputs.Count, inputs.ToArray(), System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>());
    }

    public static void TypeText(string text)
    {
        var inputs = new List<Native.INPUT>();
        foreach (var ch in text)
        {
            inputs.Add(Unicode(ch, false)); inputs.Add(Unicode(ch, true));
        }
        Native.SendInput((uint)inputs.Count, inputs.ToArray(), System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>());
    }

    private static Native.INPUT KeyInput(ushort vk, bool up) => new()
    {
        type = Native.INPUT_KEYBOARD,
        U = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = vk, dwFlags = up ? Native.KEYEVENTF_KEYUP : 0, dwExtraInfo = Native.InjectedTag } }
    };

    private static Native.INPUT Unicode(char c, bool up) => new()
    {
        type = Native.INPUT_KEYBOARD,
        U = new Native.InputUnion { ki = new Native.KEYBDINPUT { wScan = c, dwFlags = Native.KEYEVENTF_UNICODE | (up ? Native.KEYEVENTF_KEYUP : 0), dwExtraInfo = Native.InjectedTag } }
    };

    /// <summary>Converts a WPF key to a device key code (HID usage), if known.</summary>
    public static bool TryDeviceCode(Key key, out byte code)
    {
        ushort vk = (ushort)KeyInterop.VirtualKeyFromKey(key);
        if (key == Key.LeftCtrl) vk = 0xA2; else if (key == Key.RightCtrl) vk = 0xA3;
        else if (key == Key.LeftShift) vk = 0xA0; else if (key == Key.RightShift) vk = 0xA1;
        else if (key == Key.LeftAlt) vk = 0xA4; else if (key == Key.RightAlt) vk = 0xA5;
        return KeyCatalog.TryFromVirtualKey(vk, out code);
    }
}
