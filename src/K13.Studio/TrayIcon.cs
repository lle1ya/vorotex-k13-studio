using System.Drawing;
using System.Windows.Forms;

namespace Vorotex.Studio;

/// <summary>System tray icon with a quick profile switcher.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _profiles, _auto;
    public event Action? ShowRequested;
    /// <summary>v5.4: «Приостановить автопереключение» clicked.</summary>
    public event Action? AutoPauseToggled;
    public event Action? ExitRequested;
    public event Action<string>? ProfileChosen;

    public TrayIcon()
    {
        _profiles = new ToolStripMenuItem("Профиль");
        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть VOROTEX K13 Studio", null, (_, _) => ShowRequested?.Invoke());
        menu.Items.Add(_profiles);
        _auto = new ToolStripMenuItem("Приостановить автопереключение");
        _auto.Click += (_, _) => AutoPauseToggled?.Invoke();
        menu.Items.Add(_auto);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitRequested?.Invoke());
        _icon = new NotifyIcon { Icon = MakeIcon(), Text = "VOROTEX K13 Studio", Visible = true, ContextMenuStrip = menu };
        _icon.DoubleClick += (_, _) => ShowRequested?.Invoke();
    }

    public void SetProfiles(IEnumerable<string> names, string active)
    {
        // v5.3: Clear() does not dispose the old items (a leak on every profile switch)
        var old = _profiles.DropDownItems.Cast<ToolStripItem>().ToList();
        _profiles.DropDownItems.Clear();
        foreach (var o in old) o.Dispose();
        foreach (var n in names)
        {
            var item = new ToolStripMenuItem(n.Replace("&", "&&", StringComparison.Ordinal)) { Checked = n == active }; // «&» is a mnemonic in menus
            item.Click += (_, _) => ProfileChosen?.Invoke(n);
            _profiles.DropDownItems.Add(item);
        }
    }

    /// <summary>v5.4: state of the auto-switch item (checked = paused; disabled when auto-switch is off in settings).</summary>
    public void SetAutoSwitch(bool enabled, bool paused)
    {
        _auto.Enabled = enabled;
        _auto.Checked = enabled && paused;
        _auto.Text = !enabled ? "Автопереключение выключено (в настройках)" : paused ? "Автопереключение на паузе — продолжить" : "Приостановить автопереключение";
    }

    public void SetTooltip(string text) => _icon.Text = text.Length > 63 ? text[..63] : text;

    public void Balloon(string title, string text) => _icon.ShowBalloonTip(2500, title, text, ToolTipIcon.Info);

    /// <summary>Exact tray size for the system DPI (16/20/24/32 px frame of the .ico – never a downscaled 256 px).</summary>
    private static Icon MakeIcon() => CrispIcons.Load(CrispIcons.SmallIconPx()) ?? SystemIcons.Application;

    public void Dispose() { _icon.Visible = false; var menu = _icon.ContextMenuStrip; _icon.Dispose(); menu?.Dispose(); }
}
