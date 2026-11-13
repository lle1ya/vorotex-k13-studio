using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Vorotex.K13;

namespace Vorotex.Studio;

/// <summary>Renders the main window to PNG files without touching the device (preview of the UI).</summary>
internal static class Screenshots
{
    public static int Render(string folder, string? imagePath = null)
    {
        try
        {
            Directory.CreateDirectory(folder);
            byte[]? img = null;
            try
            {
                if (imagePath != null && File.Exists(imagePath)) img = File.ReadAllBytes(imagePath);
                else
                {
                var latest = new DirectoryInfo(BackupStore.DefaultFolder).Exists
                    ? new DirectoryInfo(BackupStore.DefaultFolder).GetFiles("*.bin").OrderByDescending(f => f.LastWriteTime).FirstOrDefault() : null;
                if (latest != null) img = File.ReadAllBytes(latest.FullName);
                }
            }
            catch { }
            var tmp = Path.Combine(Path.GetTempPath(), "vorotex-screens-store-" + Environment.ProcessId);
            var shots = new List<string>();
            foreach (var dark in new[] { false, true })
            {
                App.SetTheme(dark);
                var w = new MainWindow(offline: true, storeFolder: tmp, previewImage: img)
                {
                    Width = 1280, Height = 800, Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                };
                w.Show();
                int demo = w.DemoMouseMacro();
                if (!dark)
                {
                    w.SelectControl(-1);
                    shots.Add(Shot(w, 0, Path.Combine(folder, "01-keys.png")));
                    foreach (var sc in new[] { 1.0, 1.25, 1.5, 2.0 })
                        shots.Add(Shot(w, 0, Path.Combine(folder, $"01-keys-{(int)(sc * 100)}pct.png"), sc));
                    w.SelectControl(4);
                    shots.Add(Shot(w, 0, Path.Combine(folder, "02-key5-tiles.png")));
                    w.SelectControl(DeviceView.StickPress);
                    shots.Add(Shot(w, 0, Path.Combine(folder, "03-stick-press.png")));
                    w.OpenTile("ring");
                    w.ScrollEditor(60);
                    shots.Add(Shot(w, 0, Path.Combine(folder, "04-ring-editor.png")));
                    w.ScrollEditor(520);
                    shots.Add(Shot(w, 0, Path.Combine(folder, "04b-ring-editor-slot.png")));
                    var test = new List<string>();
                    int n = w.RingSlotCount();
                    for (int k = n - 1; k >= 0; k--)
                    {
                        test.Add(w.RingEditorClickTest(k));
                        w.ScrollEditor(60);
                        shots.Add(Shot(w, 0, Path.Combine(folder, $"04c-ring-click-{k + 1}.png")));
                    }
                    File.WriteAllLines(Path.Combine(folder, "ring-editor-test.txt"), test);
                    w.SelectControl(4);
                    w.OpenTile("mouse");
                    shots.Add(Shot(w, 0, Path.Combine(folder, "05-mouse-tile.png")));
                    w.SelectControl(15);
                    shots.Add(Shot(w, 0, Path.Combine(folder, "06-joystick.png")));
                    w.SelectControl(DeviceView.RollUp);
                    shots.Add(Shot(w, 0, Path.Combine(folder, "07-wheel.png")));
                    w.SelectControl(-1);
                    shots.Add(Shot(w, 1, Path.Combine(folder, "08-lighting.png")));
                    w.SelectMacro(demo);
                    shots.Add(Shot(w, 2, Path.Combine(folder, "09-macros-mouse.png")));
                    w.ShowWelcome(true);
                    shots.Add(Shot(w, 0, Path.Combine(folder, "10-welcome.png")));
                    w.ShowWelcome(false);
                    w.Width = 960; w.Height = 640;
                    w.SelectControl(4);
                    shots.Add(Shot(w, 0, Path.Combine(folder, "12-key5-960x640.png")));
                }
                else
                {
                    w.SelectControl(-1);
                    shots.Add(Shot(w, 0, Path.Combine(folder, "13-keys-dark.png")));
                    w.SelectControl(4);
                    shots.Add(Shot(w, 0, Path.Combine(folder, "14-key5-dark.png")));
                    w.SelectControl(DeviceView.StickPress);
                    shots.Add(Shot(w, 0, Path.Combine(folder, "15-stick-press-dark.png")));
                    w.OpenTile("ring");
                    w.ScrollEditor(60);
                    shots.Add(Shot(w, 0, Path.Combine(folder, "16-ring-editor-dark.png")));
                    w.SelectControl(-1);
                    shots.Add(Shot(w, 1, Path.Combine(folder, "17-lighting-dark.png")));
                    w.SelectMacro(demo);
                    shots.Add(Shot(w, 2, Path.Combine(folder, "18-macros-mouse-dark.png")));
                }
                w.ExitApp();
            }
            shots.Add(RingShot(Path.Combine(folder, "11-ring-overlay.png"), true, false));
            shots.Add(RingShot(Path.Combine(folder, "19-ring-overlay-light.png"), false, true));
            File.WriteAllText(Path.Combine(folder, "screens.txt"), string.Join(Environment.NewLine, shots) + Environment.NewLine + "source image: " + (img != null ? "latest backup" : "embedded default"));
            return 0;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(folder, "screens-error.txt"), ex.ToString()); } catch { }
            return 1;
        }
    }

    /// <summary>v4.7: only the «Макросы» and «Профили» tabs, light 100 % / 150 % and dark 150 %, with demo data (offline store).</summary>
    public static int RenderV47(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var tmp = Path.Combine(Path.GetTempPath(), "vorotex-screens47-" + Environment.ProcessId);
            var shots = new List<string>();
            foreach (var dark in new[] { false, true })
            {
                App.SetTheme(dark);
                var w = new MainWindow(offline: true, storeFolder: tmp + (dark ? "d" : "l"), previewImage: null)
                {
                    Width = 1280, Height = 800, Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
                };
                w.Show();
                int demo = w.DemoMouseMacro();
                w.DemoV47(demo);
                string sfx = dark ? "-dark" : "";
                if (!dark) shots.Add(Shot(w, 2, Path.Combine(folder, "01-macros-100pct.png"), 1.0));
                shots.Add(Shot(w, 2, Path.Combine(folder, $"02-macros-150pct{sfx}.png"), 1.5));
                if (!dark) shots.Add(Shot(w, 3, Path.Combine(folder, "03-profiles-100pct.png"), 1.0));
                shots.Add(Shot(w, 3, Path.Combine(folder, $"04-profiles-150pct{sfx}.png"), 1.5));
                if (!dark)
                {
                    w.SelectMacro(w.FirstNamedMacroOtherThan(demo));
                    shots.Add(Shot(w, 2, Path.Combine(folder, "05-macros-text-150pct.png"), 1.5));
                    w.Width = 960; w.Height = 640;
                    shots.Add(Shot(w, 2, Path.Combine(folder, "06-macros-960x640-150pct.png"), 1.5));
                    shots.Add(Shot(w, 3, Path.Combine(folder, "07-profiles-960x640-150pct.png"), 1.5));
                }
                w.ExitApp();
            }
            File.WriteAllText(Path.Combine(folder, "screens.txt"), string.Join(Environment.NewLine, shots));
            return 0;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(folder, "screens-error.txt"), ex.ToString()); } catch { }
            return 1;
        }
    }

    /// <summary>v4.8: the Macros tab with each repeat mode (1×, N× stepper, hold, toggle), light/dark, 100/150 %, narrow.</summary>
    public static int RenderV48(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var tmp = Path.Combine(Path.GetTempPath(), "vorotex-screens48-" + Environment.ProcessId);
            var shots = new List<string>();
            foreach (var dark in new[] { false, true })
            {
                App.SetTheme(dark);
                var w = new MainWindow(offline: true, storeFolder: tmp + (dark ? "d" : "l"), previewImage: null)
                {
                    Width = 1280, Height = 800, Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
                };
                w.Show();
                int demo = w.DemoMouseMacro();
                w.DemoV47(demo);
                string sfx = dark ? "-dark" : "";
                if (!dark)
                {
                    w.DemoRepeat(demo, MacroRepeatMode.Count, 1);
                    shots.Add(Shot(w, 2, Path.Combine(folder, "01-repeat-once-150pct.png"), 1.5));
                    w.DemoRepeat(demo, MacroRepeatMode.Count, 5);
                    shots.Add(Shot(w, 2, Path.Combine(folder, "02-repeat-times-150pct.png"), 1.5));
                    w.DemoRepeat(demo, MacroRepeatMode.WhileHeld, 1);
                    shots.Add(Shot(w, 2, Path.Combine(folder, "03-repeat-hold-150pct.png"), 1.5));
                    w.DemoRepeat(demo, MacroRepeatMode.Toggle, 1);
                    shots.Add(Shot(w, 2, Path.Combine(folder, "04-repeat-toggle-150pct.png"), 1.5));
                    shots.Add(Shot(w, 2, Path.Combine(folder, "05-repeat-toggle-100pct.png"), 1.0));
                    w.Width = 960; w.Height = 640;
                    w.DemoRepeat(demo, MacroRepeatMode.Count, 5);
                    shots.Add(Shot(w, 2, Path.Combine(folder, "06-repeat-times-960x640-150pct.png"), 1.5));
                    w.DemoRepeat(demo, MacroRepeatMode.Toggle, 1);
                    shots.Add(Shot(w, 2, Path.Combine(folder, "07-repeat-toggle-960x640-150pct.png"), 1.5));
                }
                else
                {
                    w.DemoRepeat(demo, MacroRepeatMode.Toggle, 1);
                    shots.Add(Shot(w, 2, Path.Combine(folder, "08-repeat-toggle-150pct-dark.png"), 1.5));
                    w.DemoRepeat(demo, MacroRepeatMode.Count, 12);
                    shots.Add(Shot(w, 2, Path.Combine(folder, "09-repeat-times-150pct-dark.png"), 1.5));
                }
                w.ExitApp();
            }
            File.WriteAllText(Path.Combine(folder, "screens.txt"), string.Join(Environment.NewLine, shots));
            return 0;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(folder, "screens-error.txt"), ex.ToString()); } catch { }
            return 1;
        }
    }

    /// <summary>v4.9: «N раз» = 10 (stepper / plural wording), light 150 %, narrow, dark.</summary>
    public static int RenderV49(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var tmp = Path.Combine(Path.GetTempPath(), "vorotex-screens49-" + Environment.ProcessId);
            var shots = new List<string>();
            foreach (var dark in new[] { false, true })
            {
                App.SetTheme(dark);
                var w = new MainWindow(offline: true, storeFolder: tmp + (dark ? "d" : "l"), previewImage: null)
                { Width = 1280, Height = 800, Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
                w.Show();
                int demo = w.DemoMouseMacro();
                w.DemoV47(demo);
                int text = w.FirstNamedMacroOtherThan(demo);
                w.DemoRepeat(text, MacroRepeatMode.Count, 10);
                if (!dark)
                {
                    // the user's real screen: 1920 px wide window at 100 % (1920 DIP) and at 150 % (1280 DIP)
                    w.Width = 1920; w.Height = 1040;
                    shots.Add(Shot(w, 2, Path.Combine(folder, "01-macros-1920px-100pct.png"), 1.0));
                    w.Width = 1280; w.Height = 693;
                    shots.Add(Shot(w, 2, Path.Combine(folder, "02-macros-1920px-150pct.png"), 1.5));
                    w.DemoRepeat(text, MacroRepeatMode.Count, 22);
                    w.Width = 1600; w.Height = 900;
                    shots.Add(Shot(w, 2, Path.Combine(folder, "03-macros-1600x900-100pct-22.png"), 1.0));
                    w.Width = 960; w.Height = 640;
                    w.DemoRepeat(text, MacroRepeatMode.Count, 10);
                    shots.Add(Shot(w, 2, Path.Combine(folder, "04-macros-960x640-150pct.png"), 1.5));
                    w.DemoRepeat(demo, MacroRepeatMode.Toggle, 1);
                    shots.Add(Shot(w, 2, Path.Combine(folder, "05-macros-toggle-960x640-150pct.png"), 1.5));
                }
                else { w.Width = 1280; w.Height = 693; shots.Add(Shot(w, 2, Path.Combine(folder, "06-macros-1920px-150pct-dark.png"), 1.5)); }
                w.ExitApp();
            }
            File.WriteAllText(Path.Combine(folder, "screens.txt"), string.Join(Environment.NewLine, shots));
            return 0;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(folder, "screens-error.txt"), ex.ToString()); } catch { }
            return 1;
        }
    }

    /// <summary>The actions ring as it appears over the desktop (cursor in the middle, «Скриншот» highlighted).</summary>
    private static string RingShot(string path, bool darkRing, bool lightDesk)
    {
        App.SetTheme(!lightDesk);
        var root = new System.Windows.Controls.Grid { Width = 960, Height = 600 };
        root.Background = lightDesk
            ? new LinearGradientBrush(Color.FromRgb(0xC9, 0xD8, 0xEE), Color.FromRgb(0xE9, 0xD6, 0xE4), 35)
            : new LinearGradientBrush(Color.FromRgb(0x1B, 0x2A, 0x45), Color.FromRgb(0x3A, 0x1F, 0x3D), 35);
        // a fake app window behind the ring
        var win = new System.Windows.Controls.Border
        {
            Width = 640, Height = 400, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(lightDesk ? Color.FromRgb(0xFB, 0xFB, 0xFC) : Color.FromRgb(0x22, 0x24, 0x29)),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 40, ShadowDepth = 8, Opacity = 0.35 }, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(70, 60, 0, 0),
        };
        var lines = new System.Windows.Controls.StackPanel { Margin = new Thickness(28, 46, 28, 0) };
        foreach (var wdt in new[] { 420.0, 520, 380, 470, 300, 500, 410 })
            lines.Children.Add(new System.Windows.Shapes.Rectangle { Width = wdt, Height = 10, RadiusX = 5, RadiusY = 5, Margin = new Thickness(0, 0, 0, 16), HorizontalAlignment = HorizontalAlignment.Left,
                Fill = new SolidColorBrush(lightDesk ? Color.FromRgb(0xE3, 0xE6, 0xEB) : Color.FromRgb(0x33, 0x36, 0x3D)) });
        win.Child = lines;
        root.Children.Add(win);
        var ring = new RingView(darkRing) { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(480 - RingView.Center, 300 - RingView.Center, 0, 0) };
        ring.SetSlots(RingSettings.CreateDefault().Slots);
        ring.SetHighlightNow(2);
        root.Children.Add(ring);
        // mouse pointer moved towards the highlighted slot
        var cursor = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 0,0 L 0,17 L 4.5,13 L 7.5,20 L 10,19 L 7,12 L 13,12 Z"), Fill = Brushes.White, Stroke = Brushes.Black, StrokeThickness = 1.1,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(480 + 62, 300 + 22, 0, 0),
        };
        root.Children.Add(cursor);
        root.Measure(new Size(960, 600)); root.Arrange(new Rect(0, 0, 960, 600)); root.UpdateLayout();
        Pump();
        double scale = 1.5;
        var rtb = new RenderTargetBitmap((int)(960 * scale), (int)(600 * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(path)) enc.Save(fs);
        return $"{Path.GetFileName(path)} {(int)(960 * scale)}x{(int)(600 * scale)}";
    }

    private static void Pump()
    {
        for (int i = 0; i < 4; i++)
            Application.Current.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
    }

    private static string Shot(MainWindow w, int page, string path, double scale = 1.5)
    {
        w.ShowPage(page);
        Pump();
        w.RenderFrame(1.3);
        w.UpdateLayout();
        Pump();
        var root = (FrameworkElement)w.Content;
        int pw = (int)Math.Ceiling(root.ActualWidth * scale), ph = (int)Math.Ceiling(root.ActualHeight * scale);
        var rtb = new RenderTargetBitmap(pw, ph, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(path)) enc.Save(fs);
        return $"{Path.GetFileName(path)} {pw}x{ph}";
    }
}
