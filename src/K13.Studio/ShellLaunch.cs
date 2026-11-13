using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Vorotex.Studio;

/// <summary>Opens the default browser / mail client / Task Manager reliably (media keys like VK_BROWSER_HOME are often ignored).</summary>
internal static class ShellLaunch
{
    public static void Open(string target, Action<string>? log)
    {
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        log?.Invoke("Открыто: " + target);
    }

    /// <summary>Default browser from the https UserChoice ProgId → shell\open\command; falls back to ShellExecute of a URL.</summary>
    public static void OpenBrowser(Action<string>? log)
    {
        string? exe = null, progId = null;
        try
        {
            using var uc = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            progId = uc?.GetValue("ProgId") as string;
            if (!string.IsNullOrEmpty(progId))
            {
                using var cmd = Registry.ClassesRoot.OpenSubKey(progId + @"\shell\open\command");
                var line = cmd?.GetValue(null) as string;
                exe = ExeFromCommand(line);
            }
        }
        catch (Exception ex) { App.Log("browser lookup: " + ex.Message); }
        App.Log($"[action] браузер: ProgId={progId ?? "?"}, exe={exe ?? "?"}");
        if (exe != null && File.Exists(exe))
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            log?.Invoke("Браузер: " + Path.GetFileName(exe));
            return;
        }
        Open("https://www.google.com/", log);
    }

    internal static string? ExeFromCommand(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        line = line.Trim();
        if (line.StartsWith('"')) { int e = line.IndexOf('"', 1); return e > 1 ? line[1..e] : null; }
        int i = line.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return i > 0 ? line[..(i + 4)] : null;
    }

    /// <summary>Ctrl+Shift+Esc is a system hotkey; launching taskmgr directly also works when synthetic input is ignored.</summary>
    public static void TaskManager(Action<string>? log)
    {
        try
        {
            Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true });
            log?.Invoke("Диспетчер задач запущен");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            log?.Invoke("Диспетчер задач: запуск отменён в окне UAC");
        }
    }
}
