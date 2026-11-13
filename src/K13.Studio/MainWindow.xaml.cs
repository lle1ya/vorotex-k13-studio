using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Vorotex.K13;

namespace Vorotex.Studio;

public partial class MainWindow : Window
{
    private readonly ProfileStore _store;
    private AppProfile _profile = null!;
    internal K13DeviceConfig Cfg => _profile.Device!;
    internal static readonly byte[] DefaultImage = LoadDefaultImage();

    private byte[]? _deviceImage;
    private K13Status? _status;
    private string? _devicePath;
    private bool _connected, _busy, _sessionBackupDone, _freshStore, _loading, _exiting, _trayHintShown;
    private readonly bool _offline;
    private int _selCol = -1, _selMacro;
    internal readonly StringBuilder LogText = new();

    private readonly TrayIcon? _tray;
    private readonly HostMapper? _mapper;
    private readonly RingOverlay? _ringOverlay;

    private (ushort, ushort, ushort, ushort) StickVks()
    {
        // what the joystick really sends (last read from the keypad, e.g. W/A/S/D), else the profile
        ushort V(int col) { var vk = KeyCatalog.ToVirtualKey(DeviceKey(col) ?? Cfg.GetKey(col)); return vk == 0 ? (ushort)0xFFFF : vk; }
        return (V(15), V(14), V(12), V(13));
    }
    private readonly ForegroundWatcher _fg = new();
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _anim = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DeviceView _kbLayout = new(), _kbLight = new(interactive: false, showLed: true);

    private bool _recording;
    private readonly Stopwatch _recClock = new();
    private readonly HashSet<byte> _recDown = new();

    /// <param name="offline">Screenshot/preview mode: no tray, no keyboard hook, no device access, profiles not saved.</param>
    public MainWindow(bool startMinimized = false, bool offline = false, string? storeFolder = null, byte[]? previewImage = null)
    {
        _offline = offline;
        _store = new ProfileStore(storeFolder);
        InitializeComponent();
        LayoutKbHost.Content = _kbLayout;
        LightKbHost.Content = _kbLight;
        _kbLayout.Clicked += OnDeviceClicked;
        _kbLayout.Hovered += id => HoverCaption.Text = id is int i && i is not DeviceView.JoystickKnob and not DeviceView.WheelBody
            ? $"{char.ToUpper(DeviceView.ControlName(i)[0])}{DeviceView.ControlName(i)[1..]}: {DeviceView.ActionOf(Cfg, _profile.Host, i)}"
            : id is DeviceView.JoystickKnob ? "Джойстик: 4 направления (центр — нажатие стика)" : id is DeviceView.WheelBody ? "Колесо: поворот вверх и вниз" : "";

        if (!offline)
        {
            _tray = new TrayIcon();
            _mapper = new HostMapper(Dispatcher);
            _mapper.Log += Log;
            _mapper.UiLog += m => LogText.AppendLine($"{DateTime.Now:HH:mm:ss} {m}");
            _mapper.SwitchProfileRequested += n => ActivateProfile(n, "действие клавиши");
            _mapper.MacroProvider = i => i >= 0 && i < Cfg.Macros.Count ? Cfg.Macros[i] : null;
            _ringOverlay = new RingOverlay(_mapper) { StickKeys = StickVks };
            _mapper.Ring = _ringOverlay;
        }

        LoadStore(previewImage);
        if (!offline) App.SetTheme(_store.Data.DarkTheme);
        BuildStaticUi();
        ActivateProfile(_store.Data.ActiveProfile, null);

        if (_tray != null)
        {
            _tray.ShowRequested += ShowFromTray;
            _tray.ExitRequested += ExitApp;
            _tray.ProfileChosen += n => ActivateProfile(n, "трей");
        }

        _anim.Tick += (_, _) => AnimateTick(_clock.Elapsed.TotalSeconds);
        _anim.Start();
        PreviewKeyDown += Window_PreviewKeyDown;
        SourceInitialized += (_, _) => ApplyTitleBar();

        if (!offline)
        {
            _fg.Rules = () => _store.Data.AutoSwitch ? _store.Data.AppRules : new();
            _fg.Matched += (p, proc, title) => { if (p != _profile.Name) ActivateProfile(p, "окно " + proc); };
            _fg.Start();
            _mapper!.Start();
            _poll.Tick += async (_, _) => await PollDevice();
            _poll.Start();
            Loaded += async (_, _) => await PollDevice();
            var dt = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            dt.Tick += (_, _) => { var (p, t) = _fg.Current; ForegroundText.Text = p.Length > 0 ? $"Сейчас: {p}.exe" : ""; };
            dt.Start();
        }
        else
        {
            _deviceImage = previewImage;
            _connected = previewImage != null;
            UpdateConn();
            RefreshDirty();
        }
        Closing += OnClosing;
        if (!offline && !_store.Data.WelcomeShown) Loaded += (_, _) => ShowWelcome(true);
        if (startMinimized) { WindowState = WindowState.Minimized; Loaded += (_, _) => Hide(); }
        Log($"Запуск v{typeof(App).Assembly.GetName().Version} (PID {Environment.ProcessId}, {Environment.ProcessPath}). Запись в клавиатуру: включена (с подтверждением, бэкапом и проверкой).");
    }

    // ───────────────────────────── store / profiles
    private static byte[] LoadDefaultImage()
    {
        using var s = typeof(MainWindow).Assembly.GetManifestResourceStream("DefaultImage.bin");
        if (s == null) return new byte[K13Layout.MinImageLength];
        using var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray();
    }

    private void LoadStore(byte[]? previewImage)
    {
        if (!_offline) { try { _store.Load(); } catch (Exception ex) { Log("Не удалось прочитать профили: " + ex.Message); } }
        if (_store.Data.Profiles.Count == 0)
        {
            _freshStore = true;
            _store.Data.Profiles.Add(new AppProfile { Name = "Основной", Device = K13MemoryCodec.Decode(previewImage ?? DefaultImage) });
            _store.Data.ActiveProfile = "Основной";
        }
        foreach (var p in _store.Data.Profiles) p.Device ??= K13MemoryCodec.Decode(DefaultImage);
        foreach (var note in ProfileMigrations.Migrate(_store.Data)) Log("Обновление профилей: " + note);
        var unsafeNotes = TriggerKeyPolicy.DropUnsafe(_store.Data, KeyCombo.VkName);
        if (unsafeNotes.Count > 0)
        {
            foreach (var n in unsafeNotes) Log("Внимание: " + n);
            if (!_offline)
            {
                try { if (File.Exists(_store.FilePath)) File.Copy(_store.FilePath, _store.FilePath + $".before-unsafe-fix-{DateTime.Now:yyyyMMdd-HHmmss}.json"); } catch { }
                Dispatcher.BeginInvoke(() => MessageBox.Show(this, string.Join("\n\n", unsafeNotes) +
                    "\n\nПрограмма больше не перехватывает обычные клавиши основной клавиатуры. Для кольца и действий используются F13–F24, которые посылает только VOROTEX.",
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning), DispatcherPriority.ApplicationIdle);
            }
        }
        if (!_offline && _store.Data.StartWithWindows) Autostart.Set(true); // keep the Run entry pointing at this exe
        SaveStore();
    }

    internal void SaveStore()
    {
        if (_offline) return;
        try { _store.Save(); } catch (Exception ex) { Log("Не удалось сохранить профили: " + ex.Message); }
    }

    private void ActivateProfile(string name, string? reason)
    {
        var p = _store.Find(name) ?? _store.Data.Profiles[0];
        _profile = p;
        _store.Data.ActiveProfile = p.Name;
        SaveStore();
        _mapper?.Update(p.Host, p.Name);
        RefreshAll();
        if (reason != null)
        {
            Log($"Профиль «{p.Name}» ({reason}).");
            StatusText.Text = $"Профиль «{p.Name}» ({reason})";
            if (!IsVisible) _tray?.Balloon("Профиль: " + p.Name, "Действия на ПК переключены.");
        }
    }

    internal void Changed(bool refreshLayout = true)
    {
        SaveStore();
        if (refreshLayout) RefreshLayout();
        RefreshDirty();
        _mapper?.Update(_profile.Host, _profile.Name);
        ScheduleLiveLighting();
    }

    // ───────────────────────────── lighting applies live (≈0.2 s write, LED-only changes, debounced)
    private System.Windows.Threading.DispatcherTimer? _ledTimer;

    private bool LightingOnlyPending()
    {
        if (_deviceImage == null) return false;
        try
        {
            K13MemoryCodec.Encode(Cfg, _deviceImage, out var plan);
            var diff = K13Protocol.DiffPlan(plan, _deviceImage);
            return diff.Count > 0 && diff.All(b => b.Address >= 0x00E0 && b.Address + b.Data.Length <= 0x0100);
        }
        catch { return false; }
    }

    private void ScheduleLiveLighting()
    {
        if (_offline || !_connected || !_store.Data.WriteConfirmed || !LightingOnlyPending()) return;
        _ledTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _ledTimer.Tick -= LedTimer_Tick; _ledTimer.Tick += LedTimer_Tick;
        _ledTimer.Stop(); _ledTimer.Start();
    }

    private async void LedTimer_Tick(object? sender, EventArgs e)
    {
        if (Mouse.LeftButton == MouseButtonState.Pressed) return;     // still dragging a slider – wait
        _ledTimer!.Stop();
        if (_busy) { _ledTimer.Start(); return; }
        if (!LightingOnlyPending()) return;
        App.Log("[led] подсветка изменена — применяю сразу");
        await WriteDevice(quiet: true);
    }

    private void BuildStaticUi()
    {
        BuildModeCards();
    }

    private void RefreshAll()
    {
        _loading = true;
        try
        {
            ProfileBox.ItemsSource = _store.Data.Profiles.Select(p => p.Name).ToList();
            ProfileBox.SelectedItem = _profile.Name;
            RefreshProfilesList();
            AutoSwitchBox.IsChecked = _store.Data.AutoSwitch;
            TrayBox.IsChecked = _store.Data.MinimizeToTray;
            AutostartBox.IsChecked = _store.Data.StartWithWindows;
            DarkBox.IsChecked = _store.Data.DarkTheme;
            HostEnabledBox.IsChecked = _profile.Host.Enabled;
            _tray?.SetProfiles(_store.Data.Profiles.Select(p => p.Name), _profile.Name);
            _tray?.SetTooltip("VOROTEX K13 — " + _profile.Name);
            RefreshLayout();
            RefreshLed();
            RefreshMacros();
            RefreshHostSummary();
            RefreshDirty();
        }
        finally { _loading = false; }
    }

    // ───────────────────────────── tabs / header
    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (PageLayout == null) return;
        PageLayout.Visibility = TabLayout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageLight.Visibility = TabLight.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageMacro.Visibility = TabMacro.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageProfiles.Visibility = TabProfiles.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void ShowPage(int i)
    {
        (i switch { 1 => TabLight, 2 => TabMacro, 3 => TabProfiles, _ => TabLayout }).IsChecked = true;
    }

    private void MoreBtn_Click(object sender, RoutedEventArgs e)
    {
        var m = new ContextMenu { PlacementTarget = MoreBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        MenuItem Item(string glyph, string text, RoutedEventHandler click, bool enabled = true)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Width = 26, VerticalAlignment = VerticalAlignment.Center });
            header.Children.Add(new TextBlock { Text = text });
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            mi.Click += click;
            m.Items.Add(mi);
            return mi;
        }
        Item("\uE7C3", "Скрытые кнопки 16 и 17…", (_, _) => { ShowPage(0); SelectControl(16); });
        Item(_store.Data.DarkTheme ? "\uE706" : "\uE708", _store.Data.DarkTheme ? "Светлая тема" : "Тёмная тема", (_, _) => { DarkBox.IsChecked = !_store.Data.DarkTheme; });
        m.Items.Add(new Separator());
        Item("\uE896", "Прочитать с клавиатуры", ReadBtn_Click, _connected && !_busy && !_offline);
        Item("\uE74E", "Сохранить резервную копию", BackupBtn_Click, _connected && !_busy && !_offline);
        Item("\uE72C", "Кнопки не печатают? Вернуть обычный режим", Unstick_Click, _connected && !_busy && !_offline);
        Item("\uE9D9", "План записи (пробный прогон)", PlanBtn_Click);
        m.Items.Add(new Separator());
        Item("\uE8B5", "Импорт из config.ini…", ImportIni_Click);
        Item("\uE8E5", "Импорт резервной копии (.bin)…", ImportBin_Click);
        Item("\uE838", "Открыть папку резервных копий", (_, _) => { Directory.CreateDirectory(BackupStore.DefaultFolder); Process.Start(new ProcessStartInfo("explorer.exe", BackupStore.DefaultFolder) { UseShellExecute = true }); });
        m.Items.Add(new Separator());
        Item("\uE7F8", "Действия на компьютере…", OpenHost_Click);
        Item("\uE897", "Как пользоваться", (_, _) => ShowWelcome(true));
        Item("\uE950", "Устройство и журнал…", (_, _) => DeviceWindow.Show(this, DeviceInfo(), _deviceImage, LogText.ToString()));
        m.Items.Add(new Separator());
        Item("\uE7E8", "Выход", (_, _) => ExitApp());
        m.IsOpen = true;
    }

    private void ApplyTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int dark = _store.Data.DarkTheme ? 1 : 0;
            DwmSetWindowAttribute(hwnd, 20, ref dark, 4); // immersive dark title bar
            var bg = ((SolidColorBrush)FindResource("BgBrush")).Color;
            int colorRef = bg.R | bg.G << 8 | bg.B << 16;
            DwmSetWindowAttribute(hwnd, 35, ref colorRef, 4); // caption colour (Windows 11)
        }
        catch { }
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void Dark_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _store.Data.DarkTheme = DarkBox.IsChecked == true;
        SaveStore();
        App.SetTheme(_store.Data.DarkTheme);
        ApplyTitleBar();
        RefreshAll();
    }

    // ───────────────────────────── layout page
    internal void RefreshLayout()
    {
        _kbLayout.Rebuild(Cfg, _profile.Host, _selCol);
        _kbLight.Rebuild(Cfg, _profile.Host, -1, showDots: false);
        BuildEditor();
    }

    private void AnimateTick(double t)
    {
        if (PageLight.Visibility == Visibility.Visible) { _kbLight.Animate(Cfg.Led, t); AnimateModeCards(t); }
    }

    /// <summary>For screenshots: renders one deterministic animation frame.</summary>
    internal void RenderFrame(double t) => AnimateTick(t);

    // ───────────────────────────── host actions (dialog)
    internal IEnumerable<string> ProfileNames => _store.Data.Profiles.Select(p => p.Name);
    internal HostSettings Host => _profile.Host;

    private void RefreshHostSummary()
    {
        int n = _profile.Host.Keys.Count, c = _profile.Host.Chords.Count;
        HostSummary.Text = n + c == 0
            ? "Запуск программ, ссылки, текст, удержание и диагонали джойстика. Работает, пока программа запущена (можно в трее)."
            : $"Триггеров: {n}, аккордов (диагоналей): {c}. Работает, пока программа запущена (можно в трее).";
    }

    private void HostEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _profile.Host.Enabled = HostEnabledBox.IsChecked == true;
        Changed(false);
    }

    private void OpenHost_Click(object sender, RoutedEventArgs e)
    {
        HostActionsWindow.Show(this, _profile.Host, ProfileNames, SetupDiagonals, FreeTriggerVk);
        Changed();
        RefreshHostSummary();
    }

    internal ushort FreeTriggerVk() => (ushort)(0x7C + Math.Max(0, FreeTriggerCode() - 0x68));

    internal void SetupDiagonals()
    {
        if (MessageBox.Show(this,
                "Джойстик будет посылать F13 (←), F14 (→), F15 (↓), F16 (↑), а программа превратит их обратно в стрелки и добавит 4 диагонали (по умолчанию Home / PgUp / End / PgDn — можно изменить).\n\n" +
                "Раскладка джойстика попадёт в клавиатуру после нажатия «Записать». Продолжить?", Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        (int col, byte code, string arrow)[] map = { (12, 0x68, "Left"), (13, 0x69, "Right"), (14, 0x6A, "Down"), (15, 0x6B, "Up") };
        foreach (var (col, code, arrow) in map)
        {
            Cfg.SetKey(col, code);
            ushort vk = (ushort)(0x7C + (code - 0x68));
            var b = _profile.Host.Keys.FirstOrDefault(k => k.TriggerVk == vk);
            if (b == null) { b = new HostKeyBinding { TriggerVk = vk }; _profile.Host.Keys.Add(b); }
            if (b.Tap.Kind == HostActionKind.None) b.Tap = new HostAction { Kind = HostActionKind.KeyCombo, Value = arrow };
        }
        (string t, ushort a, ushort b2, string act)[] chords = { ("↖ вверх-влево", 0x7F, 0x7C, "Home"), ("↗ вверх-вправо", 0x7F, 0x7D, "PgUp"), ("↙ вниз-влево", 0x7E, 0x7C, "End"), ("↘ вниз-вправо", 0x7E, 0x7D, "PgDn") };
        foreach (var (t, a, b2, act) in chords)
            if (!_profile.Host.Chords.Any(c => (c.VkA == a && c.VkB == b2) || (c.VkA == b2 && c.VkB == a)))
                _profile.Host.Chords.Add(new HostChordBinding { Title = t, VkA = a, VkB = b2, Action = new HostAction { Kind = HostActionKind.KeyCombo, Value = act } });
        _profile.Host.Enabled = true;
        Changed(); RefreshAll();
    }

    private void ImportIni_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "config.ini родного приложения|*.ini|Все файлы|*.*", Title = "Импорт профилей из config.ini" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var list = VendorIniImporter.Import(dlg.FileName, K13MemoryCodec.Decode(_deviceImage ?? DefaultImage));
            if (list.Count == 0) { MessageBox.Show(this, "Профили в файле не найдены.", Title); return; }
            string? last = null;
            foreach (var vp in list)
            {
                var name = _store.UniqueName("Импорт: " + vp.Name + (vp.IsCurrent ? " (текущий)" : ""));
                _store.Data.Profiles.Add(new AppProfile { Name = name, Device = vp.Config, Comment = string.Join("; ", vp.Notes) });
                last = name;
            }
            var notes = list.SelectMany(v => v.Notes).Distinct().ToList();
            MessageBox.Show(this, $"Импортировано профилей: {list.Count}." + (notes.Count > 0 ? "\n\nЗамечания:\n• " + string.Join("\n• ", notes) : ""), Title);
            ActivateProfile(last!, "импорт");
        }
        catch (Exception ex) { MessageBox.Show(this, "Ошибка импорта: " + ex.Message, Title); }
    }

    private void ImportBin_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Резервная копия K13|*.bin|Все файлы|*.*", InitialDirectory = BackupStore.DefaultFolder };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var cfg = K13MemoryCodec.Decode(File.ReadAllBytes(dlg.FileName));
            var name = _store.UniqueName(System.IO.Path.GetFileNameWithoutExtension(dlg.FileName));
            _store.Data.Profiles.Add(new AppProfile { Name = name, Device = cfg });
            ActivateProfile(name, "из резервной копии");
        }
        catch (Exception ex) { MessageBox.Show(this, "Не удалось разобрать файл: " + ex.Message, Title); }
    }

    private void AutoSwitch_Changed(object sender, RoutedEventArgs e) { if (!_loading) { _store.Data.AutoSwitch = AutoSwitchBox.IsChecked == true; SaveStore(); } }
    private void Tray_Changed(object sender, RoutedEventArgs e) { if (!_loading) { _store.Data.MinimizeToTray = TrayBox.IsChecked == true; SaveStore(); } }

    private void Autostart_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _offline) return;
        bool on = AutostartBox.IsChecked == true;
        var err = Autostart.Set(on);
        if (err != null) { Log("Автозапуск: " + err); _loading = true; AutostartBox.IsChecked = !on; _loading = false; return; }
        _store.Data.StartWithWindows = on; SaveStore();
        Log(on ? "Автозапуск с Windows включён." : "Автозапуск с Windows выключен.");
    }

    // ───────────────────────────── device (read-only)
    private async Task PollDevice()
    {
        if (_busy || _offline) return;
        string? path;
        try { path = await Task.Run(() => WindowsHid.FindVendorInterfaces().FirstOrDefault()); }
        catch (Exception ex) { Log("Поиск устройства: " + ex.Message); return; }
        bool present = path != null;
        if (present && !_connected)
        {
            _connected = true; _devicePath = path;
            Log("Клавиатура подключена: " + path);
            try { _status = await Task.Run(() => { using var d = K13Device.OpenFirst() ?? throw new IOException("не открылась"); return d.GetStatus(); }); }
            catch (Exception ex) { Log("Статус: " + ex.Message); }
            UpdateConn();
            // v4.5: NO automatic memory read on connect. Our read session left the firmware in its transfer state and the
            // keypad stopped typing until replug (13.11.2026 15:50). Show the last known image instead; read on demand / before a write.
            if (_deviceImage == null && LoadLastKnownImage() is { } last)
            {
                _deviceImage = last.Image; _imageFresh = false;
                Log($"Память клавиатуры не читается автоматически. Для сравнения взят последний известный образ: {last.File}");
                RefreshDirty();
            }
        }
        else if (!present && _connected)
        {
            _connected = false; _devicePath = null; _imageFresh = false;
            Log("Клавиатура отключена.");
            UpdateConn();
        }
    }

    /// <summary>Newest backup image (auto/read/postwrite/restore) – the last known keypad memory, used until the user reads or writes.</summary>
    private static (byte[] Image, string File)? LoadLastKnownImage()
    {
        try
        {
            var folders = new[] { BackupStore.DefaultFolder };
            var f = folders.Where(Directory.Exists).SelectMany(d => new DirectoryInfo(d).GetFiles("*.bin"))
                .Where(x => x.Length >= K13Layout.MinImageLength && !x.Name.Contains("prewrite") && !x.Name.Contains("prerestore"))
                .OrderByDescending(x => x.LastWriteTimeUtc).FirstOrDefault();
            if (f == null) return null;
            var img = File.ReadAllBytes(f.FullName);
            K13MemoryCodec.Decode(img); // sanity
            return (img, f.FullName);
        }
        catch { return null; }
    }

    /// <summary>Sends only the session-end frames (07 18 11, 07 18 00, 07 20) – brings a "silent" keypad back without replugging.</summary>
    private async void Unstick_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !_connected) return;
        _busy = true; UpdateConn();
        try
        {
            await Task.Run(() => { using var d = K13Device.OpenFirst() ?? throw new IOException("Клавиатура не найдена."); d.EndSession(); });
            Log("Отправлено завершение сеанса (07 18 11 / 07 18 00 / 07 20) — память не изменялась.");
            StatusText.Text = "Клавиатура переведена в обычный режим. Проверьте кнопки.";
        }
        catch (Exception ex) { Log("Завершение сеанса: " + ex.Message); StatusText.Text = ex.Message; }
        finally { _busy = false; UpdateConn(); }
    }

    private void UpdateConn()
    {
        ConnDot.Fill = (Brush)FindResource(_connected ? "OkBrush" : "ErrBrush");
        ConnText.Text = _busy ? "Обмен с клавиатурой…" : _connected ? "Клавиатура подключена" : "Клавиатура не найдена";
    }

    internal string DeviceInfo() =>
        $"Интерфейс: {(_devicePath ?? "—")}\nVID:PID 30FA:2350, коллекция FF01/1, Report ID 7\n" +
        $"Статус: {(_status?.Hex ?? "—")}\n" +
        $"Образ памяти: {(_deviceImage != null ? $"0x{_deviceImage.Length:X} байт" : "не прочитан")}\n" +
        $"Запись: включена (бэкап перед записью, проверка после)\nРезервные копии: {BackupStore.DefaultFolder}";

    private async Task<byte[]?> ReadDevice(bool auto)
    {
        if (_busy) return null;
        _busy = true; UpdateConn();
        Progress.Visibility = Visibility.Visible; Progress.Value = 0;
        StatusText.Text = "Чтение памяти клавиатуры (только чтение)…";
        try
        {
            var progress = new Progress<(string Stage, double Fraction)>(p => { Progress.Value = p.Fraction; StatusText.Text = p.Stage; });
            var (img, st, sent) = await Task.Run(() =>
            {
                using var dev = K13Device.OpenFirst() ?? throw new IOException("Клавиатура не найдена.");
                var s = dev.GetStatus();
                var image = dev.ReadImageVerified(progress);
                return (image, s, dev.SentLog.ToList());
            });
            if (sent.Any(f => !K13Protocol.IsReadOnlyFrame(f) && !K13Protocol.IsSessionEndFrame(f))) throw new InvalidOperationException("Обнаружена не-читающая команда — прервано.");
            _deviceImage = img; _status = st; _imageFresh = true;
            var path = BackupStore.Save(img, null, auto ? "k13-auto" : "k13-read", st);
            _sessionBackupDone = true;
            Log($"Прочитано 0x{img.Length:X} байт ({sent.Count} кадров, только 00/05/01). Резервная копия: {path}");
            if (_freshStore && auto)
            {
                _profile.Device = K13MemoryCodec.Decode(img);
                _freshStore = false;
                Log("Профиль «" + _profile.Name + "» заполнен настройками с клавиатуры.");
                SaveStore(); RefreshAll();
            }
            StatusText.Text = $"Память прочитана, резервная копия сохранена ({DateTime.Now:HH:mm})";
            return img;
        }
        catch (Exception ex)
        {
            Log("Ошибка чтения: " + ex.Message);
            StatusText.Text = "Ошибка чтения: " + ex.Message;
            return null;
        }
        finally
        {
            _busy = false; Progress.Visibility = Visibility.Collapsed; UpdateConn(); RefreshDirty();
        }
    }

    private async void ReadBtn_Click(object sender, RoutedEventArgs e)
    {
        var img = await ReadDevice(auto: false);
        if (img == null) return;
        if (MessageBox.Show(this, $"Память прочитана и сохранена в резервную копию.\n\nЗаменить настройки профиля «{_profile.Name}» данными с клавиатуры?\n(Действия на ПК профиля сохранятся.)",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _profile.Device = K13MemoryCodec.Decode(img);
            SaveStore(); RefreshAll();
        }
    }

    private async void WriteBtn_Click(object sender, RoutedEventArgs e) => await WriteDevice();

    /// <summary>Writes the active profile to the keypad: backup → changed blocks only → read-back → auto-restore on mismatch.</summary>
    /// <summary>True while <see cref="_deviceImage"/> was read (or written+verified) since the keypad was last connected.</summary>
    private bool _imageFresh;

    internal async Task<bool> WriteDevice(bool quiet = false)
    {
        if (_busy || _offline || !_connected) return false;
        if (quiet && !_store.Data.WriteConfirmed) return false;
        try { K13MemoryCodec.Validate(Cfg); }
        catch (Exception ex) { MessageBox.Show(this, "Профиль содержит ошибку: " + ex.Message, Title); return false; }
        if (!_store.Data.WriteConfirmed)
        {
            if (MessageBox.Show(this,
                    "Записать настройки профиля «" + _profile.Name + "» в клавиатуру?\n\n" +
                    "• Перед записью программа сохранит резервную копию памяти.\n" +
                    "• После записи всё будет прочитано и сверено; если что-то не совпадёт — память вернётся как было.\n" +
                    "• Запись занимает несколько секунд, клавиатуру не отключайте.\n\nБольше этот вопрос задаваться не будет.",
                    Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
            _store.Data.WriteConfirmed = true; SaveStore();
        }
        _busy = true; UpdateConn(); WriteBtn.IsEnabled = false;
        Progress.Visibility = Visibility.Visible; Progress.Value = 0;
        var cfg = K13MemoryCodec.Decode(K13MemoryCodec.Encode(Cfg, _deviceImage ?? DefaultImage)); // snapshot of the profile
        // lighting-only live writes may start from the last known image: the writer re-reads the target blocks + header
        // and falls back to a full read if they differ. A normal «Записать» reads the whole memory once per connection.
        var cached = _imageFresh || quiet ? _deviceImage : null;
        int est = EstimateWriteSeconds(cfg, cached);
        StatusText.Text = $"Запись в клавиатуру… (≈ {est} с)";
        var swTotal = System.Diagnostics.Stopwatch.StartNew();
        var lines = new List<string>();
        try
        {
            var progress = new Progress<(string Stage, double Fraction)>(p =>
            {
                Progress.Value = p.Fraction;
                double left = Math.Max(0, est - swTotal.Elapsed.TotalSeconds);
                StatusText.Text = $"{p.Stage} {p.Fraction:P0}" + (left >= 1 ? $" · осталось ≈ {left:0} с" : "");
            });
            var r = await Task.Run(() =>
            {
                using var dev = K13Device.OpenFirst() ?? throw new IOException("Клавиатура не найдена.");
                K13WriteGate.Unlock(K13WriteGate.ConfirmationPhrase);
                try { return K13Writer.WriteProfile(dev, cfg, m => { lock (lines) lines.Add(m); App.Log("[write] " + m); }, progress, cachedImage: cached); }
                finally { K13WriteGate.Lock(); }
            });
            foreach (var l in lines) Log(l);
            double secs = swTotal.Elapsed.TotalSeconds;
            Log($"Запись: {r.Message} Время {secs:0.0} с [{r.Timing}]" + (r.BackupAfter != null ? " Проверка: " + r.BackupAfter : ""));
            if (r.After != null) { _deviceImage = r.After; _imageFresh = r.Ok; } else _imageFresh = false;
            StatusText.Text = r.Message + $" ({secs:0.0} с)";
            if (r.Ok && !r.NothingToDo)
            {
                if (r.KeysChanged)
                    MessageBox.Show(this, $"Готово за {secs:0.0} с: настройки записаны и проверены.\n\nНазначения кнопок/макросы включатся после переподключения: выньте USB-кабель клавиатуры и вставьте снова.",
                        Title, MessageBoxButton.OK, MessageBoxImage.Information);
                else if (!quiet) StatusText.Text = $"Готово за {secs:0.0} с — подсветка уже обновлена на клавиатуре.";
            }
            else if (!r.Ok) MessageBox.Show(this, r.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return r.Ok;
        }
        catch (Exception ex)
        {
            Log("Ошибка записи: " + ex.Message); App.Log("write: " + ex);
            StatusText.Text = "Ошибка записи: " + ex.Message;
            MessageBox.Show(this, "Ошибка записи: " + ex.Message + "\n\nРезервные копии: " + BackupStore.DefaultFolder, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            _busy = false; Progress.Visibility = Visibility.Collapsed; UpdateConn(); RefreshDirty();
        }
    }

    private int EstimateWriteSeconds(K13DeviceConfig cfg, byte[]? cached)
    {
        try
        {
            var img = cached ?? _deviceImage ?? DefaultImage;
            K13MemoryCodec.Encode(cfg, img, out var plan);
            int blocks = Math.Max(1, K13Protocol.DiffPlan(plan, img).Count);
            double s = (cached == null ? 10.5 : 0.15) + blocks * 0.17 + 0.3;
            return (int)Math.Ceiling(s);
        }
        catch { return 3; }
    }

    private async void BackupBtn_Click(object sender, RoutedEventArgs e)
    {
        var img = await ReadDevice(auto: false);
        if (img != null && MessageBox.Show(this, "Резервная копия сохранена в\n" + BackupStore.DefaultFolder + "\n\nОткрыть папку?", Title, MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            Process.Start(new ProcessStartInfo("explorer.exe", BackupStore.DefaultFolder) { UseShellExecute = true });
    }

    private void RefreshDirty()
    {
        string tip; Brush fill; bool dirty = false;
        try
        {
            K13MemoryCodec.Validate(Cfg);
            if (_deviceImage == null) { tip = "Клавиатура ещё не прочитана"; fill = (Brush)FindResource("MutedBrush"); }
            else
            {
                K13MemoryCodec.Encode(Cfg, _deviceImage, out var plan);
                var diff = K13Protocol.DiffPlan(plan, _deviceImage);
                tip = diff.Count == 0 ? "Профиль совпадает с клавиатурой" : $"Профиль отличается от клавиатуры: {diff.Count} блок(ов) по 8 байт — нажмите «Записать».";
                dirty = diff.Count > 0;
                fill = (Brush)FindResource(diff.Count == 0 ? "OkBrush" : "WarnBrush");
            }
        }
        catch (Exception ex) { tip = "Ошибка в профиле: " + ex.Message; fill = (Brush)FindResource("ErrBrush"); }
        SyncDot.Fill = fill;
        WriteBtn.ToolTip = "Записать настройки профиля в клавиатуру.\nПеред записью сохраняется резервная копия, после — проверка чтением; при ошибке всё возвращается как было.\n\n" + tip;
        WriteBtn.IsEnabled = !_offline && _connected && !_busy && (dirty || _deviceImage == null);
    }

    private void PlanBtn_Click(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ПРОБНЫЙ ПРОГОН — НИЧЕГО НЕ ОТПРАВЛЯЕТСЯ НА КЛАВИАТУРУ (записывает кнопка «Записать»).");
        sb.AppendLine();
        try
        {
            K13MemoryCodec.Validate(Cfg);
            var baseImg = _deviceImage ?? DefaultImage;
            if (_deviceImage == null) sb.AppendLine("⚠ Клавиатура не прочитана — сравнение со встроенным образом от 12.11.2026.\n");
            K13MemoryCodec.Encode(Cfg, baseImg, out var plan);
            var diff = K13Protocol.DiffPlan(plan, baseImg);
            sb.AppendLine($"Полный план родного приложения: {plan.Count} блоков по 8 байт. Отличающихся блоков: {diff.Count}.");
            sb.AppendLine();
            foreach (var b in diff)
            {
                var old = b.Address < baseImg.Length ? baseImg.Skip(b.Address).Take(b.Data.Length).ToArray() : Array.Empty<byte>();
                sb.AppendLine($"0x{b.Address:X4}  {b.Purpose}");
                sb.AppendLine($"   было:   {string.Join(" ", old.Select(x => x.ToString("X2")))}");
                sb.AppendLine($"   станет: {string.Join(" ", b.Data.Select(x => x.ToString("X2")))}");
            }
            sb.AppendLine();
            var frames = K13Protocol.BuildApplyFrames(diff);
            sb.AppendLine($"Кадры HID для минимальной записи ({frames.Count}):");
            foreach (var f in frames.Take(400)) sb.AppendLine("  " + f);
            if (frames.Count > 400) sb.AppendLine($"  … ещё {frames.Count - 400}");
        }
        catch (Exception ex) { sb.AppendLine("Ошибка: " + ex.Message); }
        DeviceWindow.ShowText(this, "План записи (пробный прогон)", sb.ToString());
    }

    // ───────────────────────────── misc
    internal void Log(string msg)
    {
        App.Log(msg);
        void Add() => LogText.AppendLine($"{DateTime.Now:HH:mm:ss} {msg}");
        if (Dispatcher.CheckAccess()) Add(); else Dispatcher.BeginInvoke(Add);
    }

    internal void ShowFromTrayPublic() => ShowFromTray();

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_exiting && !_offline && _store.Data.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown) { _tray?.Balloon("VOROTEX K13 Studio", "Программа работает в трее. Выход — через меню значка или «⋯ → Выход»."); _trayHintShown = true; }
            return;
        }
        if (!_exiting) ExitApp();
    }

    internal void ExitApp()
    {
        if (_recording) ToggleRecording();
        _exiting = true;
        _poll.Stop(); _anim.Stop(); _fg.Dispose(); _mapper?.Dispose(); _tray?.Dispose();
        SaveStore();
        if (!_offline) Application.Current.Shutdown();
        else Close();
    }
}
