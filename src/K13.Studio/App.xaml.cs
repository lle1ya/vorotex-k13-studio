using System.IO;
using System.Windows;
using System.Windows.Threading;
using Vorotex.K13;

namespace Vorotex.Studio;

public partial class App : Application
{
    public static string DataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VorotexK13Studio");
    public static string LogPath => Path.Combine(DataFolder, "studio.log");

    private static readonly System.Collections.Concurrent.BlockingCollection<string> _logQ = StartLogWriter();

    /// <summary>Thread-safe, non-blocking (the keyboard hook logs too): lines go to a background writer.</summary>
    public static void Log(string msg)
    {
        try { _logQ.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {msg}"); } catch { }
    }

    private static System.Collections.Concurrent.BlockingCollection<string> StartLogWriter()
    {
        var q = new System.Collections.Concurrent.BlockingCollection<string>();
        new Thread(() =>
        {
            foreach (var line in q.GetConsumingEnumerable())
            {
                var sb = new System.Text.StringBuilder(line).Append(Environment.NewLine);
                while (q.TryTake(out var more)) sb.Append(more).Append(Environment.NewLine);
                for (int i = 0; i < 3; i++)
                {
                    try { Directory.CreateDirectory(DataFolder); File.AppendAllText(LogPath, sb.ToString()); break; }
                    catch { Thread.Sleep(50); }
                }
            }
        }) { IsBackground = true, Name = "studio.log" }.Start();
        return q;
    }

    /// <summary>Waits (briefly) until queued log lines are written — on exit.</summary>
    public static void FlushLog() { for (int i = 0; i < 20 && _logQ.Count > 0; i++) Thread.Sleep(25); }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        Exit += (_, _) => FlushLog();
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log("FATAL " + a.ExceptionObject);

        // Headless read-only self-test: VorotexK13Studio.exe --read-backup [folder]
        int i = Array.IndexOf(e.Args, "--read-backup");
        if (i >= 0)
        {
            int code = RunHeadlessBackup(i + 1 < e.Args.Length ? e.Args[i + 1] : null);
            Shutdown(code);
            return;
        }

        // After a keypad reset: VorotexK13Studio.exe --reset-profiles-from <image.bin>
        // (backs up profiles.json, then every profile's device part = the given image; PC-side settings stay)
        int rp = Array.IndexOf(e.Args, "--reset-profiles-from");
        if (rp >= 0 && rp + 1 < e.Args.Length)
        {
            int code = 0;
            try
            {
                var store = new ProfileStore();
                if (File.Exists(store.FilePath)) File.Copy(store.FilePath, store.FilePath + $".before-wipe-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                store.Load();
                var img = File.ReadAllBytes(e.Args[rp + 1]);
                foreach (var p in store.Data.Profiles) p.Device = K13MemoryCodec.Decode(img);
                store.Data.SchemaVersion = Math.Max(store.Data.SchemaVersion, ProfileMigrations.CurrentVersion);
                store.Save();
                Log($"Профили ({store.Data.Profiles.Count}) приведены к образу {e.Args[rp + 1]} (очищенная клавиатура).");
            }
            catch (Exception ex) { Log("reset-profiles: " + ex); code = 1; }
            FlushLog();
            Shutdown(code);
            return;
        }

        // Offline screenshot mode: VorotexK13Studio.exe --render-screenshot <folder>  (no device access, no hook, no tray)
        int r = Array.IndexOf(e.Args, "--render-screenshot");
        if (r >= 0)
        {
            int code = Screenshots.Render(r + 1 < e.Args.Length ? e.Args[r + 1] : Path.Combine(DataFolder, "screens"), r + 2 < e.Args.Length ? e.Args[r + 2] : null);
            Shutdown(code);
            return;
        }

        // One running copy only: two keyboard hooks would open the ring twice. A second start just shows the first window.
        _single = new Mutex(true, @"Local\VorotexK13Studio.Single", out bool first);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\VorotexK13Studio.Show");
        if (!first)
        {
            if (!e.Args.Contains("--minimized")) _showEvent.Set();
            Shutdown(0);
            return;
        }

        var w = new MainWindow(e.Args.Contains("--minimized"));
        MainWindow = w;
        w.Show();
        var t = new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.BeginInvoke(() => w.ShowFromTrayPublic());
        }) { IsBackground = true, Name = "single-instance" };
        t.Start();
    }

    private static Mutex? _single;
    private static EventWaitHandle? _showEvent;

    public static bool IsDark { get; private set; }

    public static void SetTheme(bool dark)
    {
        IsDark = dark;
        var dicts = Current.Resources.MergedDictionaries;
        var uri = new Uri($"pack://application:,,,/VorotexK13Studio;component/Themes/{(dark ? "Dark" : "Light")}.xaml");
        if (dicts.Count > 0 && dicts[0].Source == uri) return;
        var d = new ResourceDictionary { Source = uri };
        if (dicts.Count > 0) dicts[0] = d; else dicts.Insert(0, d);
    }

    private static int RunHeadlessBackup(string? folder)
    {
        var report = Path.Combine(folder ?? BackupStore.DefaultFolder, "headless-read.txt");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            using var dev = K13Device.OpenFirst();
            if (dev == null) { File.WriteAllText(report, "Устройство 30FA:2350 (COL07 FF01) не найдено."); return 2; }
            var st = dev.GetStatus();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var img = dev.ReadImageVerified();
            var path = BackupStore.Save(img, folder, "k13-headless", st);
            var cfg = K13MemoryCodec.Decode(img);
            bool rt = K13MemoryCodec.Encode(cfg, img).AsSpan(0, img.Length).SequenceEqual(img);
            var st2 = dev.GetStatus();
            var sent = dev.SentLog.Select(f => f[2]).Distinct().Select(c => $"07 18 {c:X2}");
            File.WriteAllText(report,
                $"OK {DateTime.Now:s}\npath={dev.Path}\nstatus before={st.Hex}\nstatus after ={st2.Hex}\nBASE=0x{cfg.Base:X4} image=0x{img.Length:X} bytes, {sw.ElapsedMilliseconds} ms\n" +
                $"backup={path}\nroundtrip={(rt ? "OK" : "MISMATCH")}\ncommands sent: {string.Join(", ", sent)} (frames={dev.SentLog.Count})\n" +
                $"keys: {string.Join(" ", Enumerable.Range(0, 18).Select(c => cfg.GetKey(c).ToString("X2")))}\nwheel={cfg.WheelFirst}/{cfg.WheelSecond} mode={cfg.Led.Mode} bright={cfg.Led.Brightness} speed={cfg.Led.Speed}\n");
            return rt ? 0 : 3;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(report, "ERROR " + ex); } catch { }
            return 1;
        }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("UNHANDLED " + e.Exception);
        MessageBox.Show(e.Exception.Message, "VOROTEX K13 Studio — ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
