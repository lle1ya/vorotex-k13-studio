using Microsoft.Win32;

namespace Vorotex.Studio;

/// <summary>«Запускать вместе с Windows»: HKCU\...\Run value (per user, no admin rights), starts minimized to the tray.</summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VorotexK13Studio";

    public static bool IsOn()
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(ValueName) is string; } catch { return false; }
    }

    /// <summary>Returns null on success or an error text.</summary>
    public static string? Set(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (on)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return "не удалось определить путь к программе";
                k.SetValue(ValueName, $"\"{exe}\" --minimized");
            }
            else if (k.GetValue(ValueName) != null) k.DeleteValue(ValueName, false);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }
}
