using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Vorotex.K13;

namespace Vorotex.Studio;

/// <summary>
/// v5.5: the «Профиль: 1С · по программе 1cv8c.exe» notice. A transparent, topmost, non-activating tool window
/// (never takes focus from the program you are working in, not in Alt+Tab / taskbar). One instance: a new switch
/// replaces the text and restarts the timer instead of stacking. Fades in, stays ≈2 s, fades out; a click hides it.
/// </summary>
public sealed class SwitchToastWindow : Window
{
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    private const int GWL_EXSTYLE = -20, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8;
    private const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
    private const int MarginPx = 20;

    private readonly DispatcherTimer _hold = new();
    private readonly TranslateTransform _slide = new();
    private ToastPlacement _placement;
    private int _generation;
    private bool _active; // on screen (also while fading out) — a new switch replaces it

    /// <summary>How many notices were shown / replaced (smoke test and log).</summary>
    public int Shown { get; private set; }
    public int Replaced { get; private set; }
    public ToastContent? Current { get; private set; }

    public SwitchToastWindow()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; Focusable = false; ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Left = -10000; Top = -10000; Opacity = 0;
        Title = "VOROTEX K13 — профиль";
        _hold.Tick += (_, _) => { _hold.Stop(); FadeOut(SwitchToastLogic.FadeOutMs); };
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST);
            HwndSource.FromHwnd(h)?.AddHook((IntPtr hw, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
            {
                if (msg == WM_MOUSEACTIVATE) { handled = true; return new IntPtr(MA_NOACTIVATE); }
                return IntPtr.Zero;
            });
        };
        MouseLeftButtonUp += (_, _) => { _hold.Stop(); FadeOut(160); };
    }

    /// <summary>Shows (or replaces) the notice.</summary>
    public void Show(ToastContent content, Brush accent, string glyph, ToastPlacement placement)
    {
        bool replacing = _active && IsVisible;
        _active = true;
        Current = content;
        _placement = placement;
        _generation++;
        Content = BuildCard(content, accent, glyph, _slide);
        if (replacing) Replaced++; else Shown++;
        if (!IsVisible) base.Show();
        UpdateLayout();
        Place();
        Dispatcher.BeginInvoke(Place, DispatcherPriority.Loaded); // after a possible DPI change / final size
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(replacing ? 90 : SwitchToastLogic.FadeInMs)));
        double from = placement == ToastPlacement.TopCenter ? -10 : 10;
        if (!replacing) _slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        _hold.Stop();
        _hold.Interval = TimeSpan.FromMilliseconds(SwitchToastLogic.ShowMs);
        _hold.Start();
    }

    public void HideNow() { _active = false; _hold.Stop(); BeginAnimation(OpacityProperty, null); Opacity = 0; if (IsVisible) Hide(); }

    private void FadeOut(int ms)
    {
        int gen = _generation;
        var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(ms));
        a.Completed += (_, _) => { if (gen == _generation) { _active = false; if (IsVisible) Hide(); } };
        BeginAnimation(OpacityProperty, a);
    }

    /// <summary>Physical-pixel placement on the monitor of the foreground window (or the cursor), above fullscreen windows where Windows allows it.</summary>
    private void Place()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return;
        try
        {
            GetCursorPos(out var cur);
            IntPtr mon = _placement == ToastPlacement.NearCursor ? MonitorFromPoint(cur, 2) : MonitorFromWindow(GetForegroundWindow(), 2);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(mon, ref mi)) return;
            // a fullscreen program covers the taskbar — then use the whole monitor, otherwise the work area
            var fg = GetForegroundWindow();
            bool fullscreen = fg != IntPtr.Zero && GetWindowRect(fg, out var fr) && fr.L <= mi.rcMonitor.L && fr.T <= mi.rcMonitor.T && fr.R >= mi.rcMonitor.R && fr.B >= mi.rcMonitor.B;
            var r = fullscreen ? mi.rcMonitor : mi.rcWork;
            GetWindowRect(h, out var wr);
            int w = wr.R - wr.L, hh = wr.B - wr.T;
            if (w <= 0) { w = (int)ActualWidth; hh = (int)ActualHeight; }
            var (x, y) = SwitchToastLogic.Place(_placement, new PixelRect(r.L, r.T, r.R, r.B), cur.X, cur.Y, w, hh, MarginPx);
            SetWindowPos(h, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }

    /// <summary>The card itself (also used for screenshots): profile tile in its colour, «Профиль: name», reason.</summary>
    public static FrameworkElement BuildCard(ToastContent c, Brush accent, string glyph, TranslateTransform? slide = null)
    {
        var icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        var row = new DockPanel();
        var tile = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(12), Background = accent, Margin = new Thickness(0, 0, 13, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = glyph, FontFamily = icons, FontSize = 17, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        row.Children.Add(tile);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 300 };
        var lead = new System.Windows.Documents.Run("Профиль: "); lead.SetResourceReference(TextElement.ForegroundProperty, "MutedBrush");
        title.Inlines.Add(lead);
        title.Inlines.Add(new System.Windows.Documents.Run(c.Profile) { FontWeight = FontWeights.SemiBold });
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        texts.Children.Add(title);
        if (c.Detail.Length > 0)
        {
            var d = new TextBlock { Text = c.Detail, FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 300 };
            d.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            texts.Children.Add(d);
        }
        row.Children.Add(texts);
        var card = new Border { Child = row, CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), Padding = new Thickness(12, 12, 22, 12), MinWidth = 240,
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Direction = 270, Opacity = 0.28, Color = Colors.Black } };
        card.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        card.SetResourceReference(TextElement.FontFamilyProperty, "UiFont");
        TextOptions.SetTextFormattingMode(card, TextFormattingMode.Display);
        // room for the shadow inside the transparent window
        var host = new Border { Child = card, Padding = new Thickness(18, 14, 18, 22), Background = Brushes.Transparent, Cursor = Cursors.Hand,
            ToolTip = null, RenderTransform = slide ?? new TranslateTransform() };
        return host;
    }
}
