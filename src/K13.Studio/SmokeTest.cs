using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Vorotex.K13;

namespace Vorotex.Studio;

/// <summary>
/// v5.3: headless UI smoke test — <c>VorotexK13Studio.exe --smoke-test &lt;folder&gt;</c>.
/// Offline window (no keyboard hook, no tray, no HID access, profiles in a temp folder): visits every page, every control,
/// every editor tile, every macro slot, the welcome slides and the non-modal dialogs in light and dark theme, rendering each.
/// Writes smoke.txt (one line per step) and returns the number of failed steps.
/// </summary>
public static class SmokeTest
{
    public static int Unhandled;

    public static int Run(string folder)
    {
        Directory.CreateDirectory(folder);
        var lines = new List<string>();
        int fails = 0, steps = 0;
        void Step(string name, Action a)
        {
            steps++;
            int before = Unhandled;
            try { a(); Pump(); if (Unhandled != before) throw new InvalidOperationException("необработанное исключение в диспетчере (см. studio.log)"); lines.Add("OK   " + name); }
            catch (Exception ex) { fails++; lines.Add("FAIL " + name + " — " + ex.GetType().Name + ": " + ex.Message); App.Log("[smoke] " + name + ": " + ex); }
        }
        var tmp = Path.Combine(Path.GetTempPath(), "vorotex-smoke-" + Environment.ProcessId);
        foreach (var dark in new[] { false, true })
        {
            string th = dark ? "dark" : "light";
            MainWindow? w = null;
            Step($"{th}: открыть окно", () =>
            {
                App.SetTheme(dark);
                w = new MainWindow(offline: true, storeFolder: tmp + th, previewImage: null)
                { Width = 1280, Height = 800, Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
                w.Show();
            });
            if (w == null) continue;
            int demo = 0;
            Step($"{th}: демо-данные", () => { demo = w.DemoMouseMacro(); w.DemoV47(demo); });
            for (int p = 0; p < 4; p++) { int pg = p; Step($"{th}: вкладка {pg}", () => Render(w, pg)); }
            var ids = K13Layout.Controls.Select(c => c.Column).Concat(new[] { DeviceView.StickPress, DeviceView.RollUp, DeviceView.RollDown, -1 }).Distinct().ToList();
            foreach (var id in ids)
            {
                Step($"{th}: кнопка {id}", () => { w.SelectControl(id); Render(w, 0); });
                if (dark) continue;
                foreach (var tile in new[] { "key", "combo", "media", "app", "macro", "mouse", "light", "ring", "diag", "off" })
                    Step($"{th}: кнопка {id} → плитка {tile}", () => { w.SelectControl(id); w.OpenTile(tile); w.ScrollEditor(400); Render(w, 0); w.OpenTile(null); });
            }
            for (int m = 0; m < K13Layout.MacroSlots; m++) { int mm = m; Step($"{th}: макрос {mm + 1}", () => { w.SelectMacro(mm); Render(w, 2); }); }
            Step($"{th}: макрос: окно проверки", () => Dialog(w.BuildTestWindow(demo)));
            Step($"{th}: повтор N раз / по кругу / пока зажата", () =>
            {
                int t = w.FirstNamedMacroOtherThan(demo);
                w.DemoRepeat(t, MacroRepeatMode.Count, 65535); Render(w, 2);
                w.DemoRepeat(t, MacroRepeatMode.Toggle, 1); Render(w, 2);
                w.DemoRepeat(t, MacroRepeatMode.WhileHeld, 1); Render(w, 2);
            });
            for (int s = 0; s < MainWindow.WelcomeSlideCount; s++) { int ss = s; Step($"{th}: приветствие {ss + 1}", () => { w.ShowWelcome(true, ss); Render(w, 0); }); }
            Step($"{th}: закрыть приветствие", () => w.ShowWelcome(false));
            Step($"{th}: диалог «клавиша занята»", () => Dialog(w.BuildBusyKeyDialog(0x68, w.HostUseOf(0x68) ?? "Кольцо действий", out _)));
            Step($"{th}: диалог «проверка памяти»", () =>
            {
                var img = (byte[])MainWindow.DefaultImage.Clone(); img[0xD0] = 0xDA; img[0xD4] = 0xFF;
                Dialog(w.BuildLayoutDialog(K13Header.Inspect(img, 0xE0), @"C:\backups\k13-prewrite.bin", out _));
            });
            Step($"{th}: маленькое окно 960×640", () => { w.Width = 960; w.Height = 640; for (int p = 0; p < 4; p++) Render(w, p); });
            Step($"{th}: выход", () => w.ExitApp());
        }
        Step("кольцо на экране (отрисовка)", () =>
        {
            var ring = new RingView(true); ring.SetSlots(RingSettings.CreateDefault().Slots); ring.SetHighlightNow(2);
            ring.Measure(new Size(600, 600)); ring.Arrange(new Rect(0, 0, 600, 600));
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(600, 600, 96, 96, PixelFormats.Pbgra32); rtb.Render(ring);
        });
        lines.Insert(0, $"Шагов: {steps}, ошибок: {fails}, необработанных исключений: {Unhandled}");
        File.WriteAllLines(Path.Combine(folder, "smoke.txt"), lines);
        try { Directory.Delete(tmp + "light", true); Directory.Delete(tmp + "dark", true); } catch { }
        return fails;
    }

    private static void Render(MainWindow w, int page)
    {
        w.ShowPage(page);
        Pump();
        w.RenderFrame(1.3);
        w.UpdateLayout();
        var root = (FrameworkElement)VisualTreeHelper.GetChild(w, 0);
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(Math.Max(1, (int)root.ActualWidth), Math.Max(1, (int)root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
    }

    private static void Dialog(Window d)
    {
        d.Owner = null; d.Left = -32000; d.Top = -32000; d.ShowActivated = false; d.WindowStartupLocation = WindowStartupLocation.Manual;
        d.Show(); Pump(); d.UpdateLayout();
        var root = (FrameworkElement)VisualTreeHelper.GetChild(d, 0);
        if (root.ActualWidth < 50 || root.ActualHeight < 50) throw new InvalidOperationException($"окно слишком маленькое: {root.ActualWidth}×{root.ActualHeight}");
        d.Close();
    }

    private static void Pump()
    {
        for (int i = 0; i < 3; i++)
            Application.Current.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
    }
}
