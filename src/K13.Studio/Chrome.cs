using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Vorotex.Studio;

/// <summary>v5.0: wires the themed title bar of every window that uses the "DarkWindow" style
/// (caption buttons, dialogs = close only) and asks DWM for Win11 rounded corners + dark mode.</summary>
internal static class Chrome
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;

    private static bool _registered;
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, object> _wired = new();

    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => { if (s is Window w) Attach(w); }));
    }

    /// <summary>A dialog: owned or not in the taskbar. Gets a close button only and is never maximised.</summary>
    private static bool IsDialog(Window w) => w is not MainWindow && (w.Owner != null || !w.ShowInTaskbar);

    public static void Attach(Window w)
    {
        if (w.Template?.FindName("PART_Close", w) is not Button close) return;
        if (_wired.TryGetValue(w, out _)) { ApplyDwm(w); return; }
        _wired.Add(w, new object());
        var min = (Button)w.Template.FindName("PART_Min", w);
        var max = (Button)w.Template.FindName("PART_Max", w);
        close.Click += (_, _) => SystemCommands.CloseWindow(w);
        min.Click += (_, _) => SystemCommands.MinimizeWindow(w);
        max.Click += (_, _) => { if (w.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(w); else SystemCommands.MaximizeWindow(w); };
        if (IsDialog(w))
        {
            min.Visibility = Visibility.Collapsed; max.Visibility = Visibility.Collapsed;
            w.StateChanged += (_, _) => { if (w.WindowState == WindowState.Maximized) w.WindowState = WindowState.Normal; }; // no double-click maximise
        }
        ApplyDwm(w);
    }

    public static void ApplyDwm(Window w)
    {
        try
        {
            var h = new WindowInteropHelper(w).Handle;
            if (h == IntPtr.Zero) return;
            int round = DWMWCP_ROUND; DwmSetWindowAttribute(h, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            int dark = App.IsDark ? 1 : 0; DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        }
        catch { /* Win10 / Wine: attributes unsupported – the custom title bar still works */ }
    }

    public static void RefreshAll()
    {
        if (Application.Current == null) return;
        foreach (Window w in Application.Current.Windows) ApplyDwm(w);
    }
}

/// <summary>Themed replacement for System.Windows.MessageBox (same call shapes, so existing code compiles unchanged).</summary>
internal static class MessageBox
{
    public static MessageBoxResult Show(string text) => Show(null, text, "VOROTEX K13 Studio");
    public static MessageBoxResult Show(string text, string caption) => Show(null, text, caption);
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton b) => Show(null, text, caption, b);
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton b, MessageBoxImage i) => Show(null, text, caption, b, i);
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton b, MessageBoxImage i, MessageBoxResult d) => Show(null, text, caption, b, i, d);
    public static MessageBoxResult Show(Window? owner, string text) => Show(owner, text, "VOROTEX K13 Studio");
    public static MessageBoxResult Show(Window? owner, string text, string caption) => Show(owner, text, caption, MessageBoxButton.OK);
    public static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton b) => Show(owner, text, caption, b, MessageBoxImage.None);
    public static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton b, MessageBoxImage i) =>
        Show(owner, text, caption, b, i, b is MessageBoxButton.OK or MessageBoxButton.OKCancel ? MessageBoxResult.OK : MessageBoxResult.Yes);

    public static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult def)
    {
        owner ??= Application.Current?.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive) ?? Application.Current?.MainWindow;
        if (owner is { IsLoaded: false } or { IsVisible: false }) owner = null;
        var win = Build(owner, text, caption, buttons, image, def, out var result);
        win.ShowDialog();
        return result();
    }

    /// <summary>Builds the dialog without showing it (also used for screenshots).</summary>
    internal static Window Build(Window? owner, string text, string caption, MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult def, out Func<MessageBoxResult> result)
    {
        var win = Ui.MakeWindow(owner, caption, 460, 200);
        win.ResizeMode = ResizeMode.NoResize;
        win.SizeToContent = SizeToContent.Height;
        win.ShowInTaskbar = owner == null;
        var res = buttons is MessageBoxButton.YesNo ? MessageBoxResult.No : MessageBoxButton.YesNoCancel == buttons || buttons == MessageBoxButton.OKCancel ? MessageBoxResult.Cancel : MessageBoxResult.OK;
        result = () => res;

        (string glyph, string brush) = image switch
        {
            MessageBoxImage.Error => ("\uE711", "ErrBrush"),
            MessageBoxImage.Warning => ("\uE7BA", "WarnBrush"),
            MessageBoxImage.Question => ("\uE897", "AccentBrush"),
            MessageBoxImage.Information => ("\uE946", "AccentBrush"),
            _ => ("", ""),
        };
        var root = new DockPanel { Margin = new Thickness(24, 12, 24, 20) };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        if (glyph.Length > 0)
        {
            var fg = (Brush)Application.Current.FindResource(brush);
            var circle = new Border
            {
                Width = 36, Height = 36, CornerRadius = new CornerRadius(18), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 16, 0),
                Background = new SolidColorBrush(fg is SolidColorBrush sb ? Color.FromArgb(0x26, sb.Color.R, sb.Color.G, sb.Color.B) : Colors.Transparent),
                Child = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16, Foreground = fg, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            DockPanel.SetDock(circle, Dock.Left);
            root.Children.Add(circle);
        }
        var body = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 21, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        root.Children.Add(new ScrollViewer { Content = body, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        void Btn(string label, MessageBoxResult r, bool cancel = false)
        {
            bool primary = r == def;
            var b = new Button { Content = label, MinWidth = 96, Margin = new Thickness(8, 0, 0, 0), IsDefault = primary, IsCancel = cancel, Padding = new Thickness(16, 7, 16, 7) };
            if (primary) b.Style = (Style)Application.Current.FindResource("AccentButton");
            b.Click += (_, _) => { res = r; win.DialogResult = r is MessageBoxResult.OK or MessageBoxResult.Yes; win.Close(); };
            bar.Children.Add(b);
            if (primary) win.Loaded += (_, _) => b.Focus();
        }
        switch (buttons)
        {
            case MessageBoxButton.OK: Btn("OK", MessageBoxResult.OK, true); break;
            case MessageBoxButton.OKCancel: Btn("OK", MessageBoxResult.OK); Btn("Отмена", MessageBoxResult.Cancel, true); break;
            case MessageBoxButton.YesNo: Btn("Да", MessageBoxResult.Yes); Btn("Нет", MessageBoxResult.No, true); break;
            default: Btn("Да", MessageBoxResult.Yes); Btn("Нет", MessageBoxResult.No); Btn("Отмена", MessageBoxResult.Cancel, true); break;
        }
        win.Content = root;
        return win;
    }
}
