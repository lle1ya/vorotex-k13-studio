using System.Drawing;
using System.Windows.Forms;

namespace Vorotex.Studio;

/// <summary>System tray icon with a quick profile switcher.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _profiles;
    public event Action? ShowRequested;
    public event Action? ExitRequested;
    public event Action<string>? ProfileChosen;

    public TrayIcon()
    {
        _profiles = new ToolStripMenuItem("Профиль");
        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть VOROTEX K13 Studio", null, (_, _) => ShowRequested?.Invoke());
        menu.Items.Add(_profiles);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitRequested?.Invoke());
        _icon = new NotifyIcon { Icon = MakeIcon(), Text = "VOROTEX K13 Studio", Visible = true, ContextMenuStrip = menu };
        _icon.DoubleClick += (_, _) => ShowRequested?.Invoke();
    }

    public void SetProfiles(IEnumerable<string> names, string active)
    {
        _profiles.DropDownItems.Clear();
        foreach (var n in names)
        {
            var item = new ToolStripMenuItem(n) { Checked = n == active };
            item.Click += (_, _) => ProfileChosen?.Invoke(n);
            _profiles.DropDownItems.Add(item);
        }
    }

    public void SetTooltip(string text) => _icon.Text = text.Length > 63 ? text[..63] : text;

    public void Balloon(string title, string text) => _icon.ShowBalloonTip(2500, title, text, ToolTipIcon.Info);

    private static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(Color.FromArgb(61, 139, 253));
            g.FillEllipse(bg, 1, 1, 30, 30);
            using var f = new Font("Segoe UI", 13, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            using var fg = new SolidBrush(Color.White);
            var sz = g.MeasureString("K", f);
            g.DrawString("K", f, fg, (32 - sz.Width) / 2, (32 - sz.Height) / 2);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose() { _icon.Visible = false; _icon.Dispose(); }
}
