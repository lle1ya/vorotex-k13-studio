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
    private bool _connected, _busy, _polling, _exitPending, _freshStore, _loading, _exiting, _trayHintShown;
    private readonly bool _offline;
    private int _selCol = -1, _selMacro;
    internal readonly StringBuilder LogText = new();

    private readonly TrayIcon? _tray;
    private readonly HostMapper? _mapper;
    private readonly RingOverlay? _ringOverlay;

    /// <summary>v5.7.3: tilt keys for the ring — what the keypad memory says (e.g. W/A/S/D), the profile, and the arrows
    /// a keypad on its factory layout sends.</summary>
    private (ushort[], ushort[], ushort[], ushort[]) StickVks()
    {
        K13DeviceConfig? kp = null;
        try { if (_deviceImage != null) kp = K13MemoryCodec.Decode(_deviceImage); } catch { }
        return RingStick.Keys(kp, _profile.Device);
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
            ? $"{char.ToUpperInvariant(DeviceView.ControlName(i)[0])}{DeviceView.ControlName(i)[1..]}: {DeviceView.ActionOf(Cfg, _profile.Host, i)}"
            : id is DeviceView.JoystickKnob ? "Джойстик: 4 направления (центр — нажатие стика)" : id is DeviceView.WheelBody ? "Колесо: поворот вверх и вниз" : "";

        if (!offline)
        {
            _tray = new TrayIcon();
            _mapper = new HostMapper(Dispatcher);
            _mapper.Log += Log;
            _mapper.HookRouted += () => { if (_hookBlockedBy != null || _missedFg != null) { _hookBlockedBy = null; _missedFg = null; RefreshModeBanner(); } };
            _mapper.UiLog += m => LogText.AppendLine($"{DateTime.Now:HH:mm:ss} {m}");
            _mapper.SwitchProfileRequested += n => ActivateProfile(n, "действие клавиши");
            _mapper.MacroProvider = i => i >= 0 && i < Cfg.Macros.Count ? Cfg.Macros[i] : null;
            _ringOverlay = new RingOverlay(_mapper) { StickKeys = StickVks, Opened = OnRingOpened, Closed = OnRingClosed };
            _mapper.Ring = _ringOverlay;
            _mapper.LayerRouted += OnLayerRouted;
            _k13Keys = new K13KeyMonitor();
            _k13Keys.KeypadKey += OnKeypadRaw;
            if (_k13Keys.Start()) Log("[k13] слежу за кнопками самой K13 (Raw Input, только VID_30FA&PID_2350).");
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
            _tray.AutoPauseToggled += () => SetAutoPaused(!_fg.Engine.Paused);
        }

        _anim.Tick += (_, _) => AnimateTick(_clock.Elapsed.TotalSeconds);
        _anim.Start();
        PreviewKeyDown += Window_PreviewKeyDown;
        SourceInitialized += (_, _) => ApplyTitleBar();

        if (!offline)
        {
            _fg.Data = () => _store.Data;
            _fg.CurrentProfile = () => _profile.Name;
            _fg.Decided += OnAutoDecided;
            _fg.Start();
            _mapper!.Start();
            _poll.Tick += async (_, _) => { await PollDevice(); await PollHwMode(); };
            _poll.Start();
            Loaded += async (_, _) => await PollDevice();
            var dt = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            dt.Tick += (_, _) => RefreshForegroundLabel();
            dt.Start();
        }
        else
        {
            _deviceImage = previewImage;
            _connected = previewImage != null;
            UpdateConn();
            RefreshDirty();
        }
        Labels.MacroNamer = MacroName;
        Closing += OnClosing;
        WireDragDrop();
        if (!offline && !_store.Data.WelcomeShown) Loaded += (_, _) => ShowWelcome(true);
        if (!offline) Loaded += (_, _) => ScheduleUpdateCheck();
        if (!offline) Loaded += (_, _) => AfterStartUpdateTasks(App.StartArgs);
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
        if (!_offline)
        {
            // v5.3: Load never throws and keeps a damaged profiles.json aside (…corrupt-…) instead of it being overwritten below
            try { _store.Load(); } catch (Exception ex) { Log("Не удалось прочитать профили: " + ex.Message); }
            var problems = _store.LoadNotes.Where(n => n.Contains("повреждён", StringComparison.Ordinal) || n.Contains("восстановлены", StringComparison.Ordinal) || n.Contains("не читается", StringComparison.Ordinal)).ToList();
            foreach (var n in _store.LoadNotes) Log("Профили: " + n);
            if (problems.Count > 0)
                Dispatcher.BeginInvoke(() => MessageBox.Show(this, string.Join("\n\n", problems) + "\n\nПапка: " + _store.Folder,
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning), DispatcherPriority.ApplicationIdle);
        }
        else _store.Data.Normalize();
        if (_store.Data.Profiles.Count == 0)
        {
            _freshStore = true;
            _store.Data.Profiles.Add(new AppProfile { Name = "Основной", Device = K13MemoryCodec.Decode(previewImage ?? DefaultImage) });
            _store.Data.ActiveProfile = "Основной";
            // v5.8: new install — the stick keeps the factory Normal/Game toggle; the ring is one click away in the stick editor
            _store.Data.SchemaVersion = ProfileMigrations.CurrentVersion;
            ProfileMigrations.EnsureStickRing(_store.Data.Profiles[0], setStickKey: false);
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
                    "\n\nПрограмма больше не перехватывает обычные клавиши основной клавиатуры. Для кольца и действий на ПК кнопки VOROTEX посылают служебные сигналы (F13 или Scroll Lock + F).",
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

    /// <param name="why">v5.4: shown as «Активен: X — why» (default: derived from <paramref name="reason"/>).</param>
    private void ActivateProfile(string name, string? reason, string? why = null, bool auto = false)
    {
        var found = _store.Find(name);
        // v5.3: «Переключить профиль» on a deleted/renamed profile jumped to the first profile — now it is ignored
        if (found == null && reason != null) { Log($"Профиль «{name}» не найден ({reason}) — переключение пропущено."); return; }
        var p = found ?? _store.Data.Profiles[0];
        bool changed = p != _profile;
        // v5.3: a profile switch (e.g. auto-switch by the active window) during recording put the steps into the wrong profile
        if (_recording && p != _profile) ToggleRecording();
        _profile = p;
        _store.Data.ActiveProfile = p.Name;
        _activeWhy = why ?? reason switch
        {
            null => _activeWhy.Length > 0 ? _activeWhy : "последний использованный",
            "вручную" => "выбран вручную",
            "трей" => "выбран в меню трея",
            "действие клавиши" => "переключён кнопкой клавиатуры",
            _ => reason,
        };
        SaveStore();
        PushMapper();
        RefreshAll();
        RefreshAutoStatus();
        if (reason != null)
        {
            Log($"Профиль «{p.Name}» ({_activeWhy}).");
            var source = auto ? SwitchSource.Auto : reason == "действие клавиши" ? SwitchSource.KeypadKey : reason == "трей" ? SwitchSource.Tray : SwitchSource.Manual;
            // 1.0.2: lighting follows a switch made by the user (same live LED-only path as editing the lighting)
            bool userSwitch = reason is "вручную" or "трей" or "действие клавиши" && ProfileSwitchLighting.AppliesLive(source);
            bool ledOnly = LightingOnlyPending(), ledDiff = ledOnly || ProfileSwitchLighting.LightingDiffers(DeviceTarget, _deviceImage);
            bool ledPart = LightingPending();                                  // 1.1.1: lighting can be applied alone even if other blocks differ
            if (changed) _autoLed.Cancel();                                   // any newer switch replaces a pending auto write
            bool apply = changed && userSwitch && ledPart && ScheduleLiveLighting();
            // 1.0.3: auto-switch too — debounced and rate-limited (AutoLightingGate)
            bool autoOn = auto && _store.Data.AutoSwitchLighting;
            if (changed) { _lastSwitchAuto = auto; _lastSwitchAt = DateTime.Now; }
            // 1.0.4: during a write the cached image is about to change — queue anyway, the timer re-checks after the write
            if (changed && autoOn && (ledPart || _busy)) apply = ScheduleAutoLighting(p.Name);
            string others = ledOnly ? "" : $"; другие блоки тоже отличаются ({OtherPendingNames()}) — их пишет только «Записать»";
            if (apply) Log(auto ? $"[led] профиль «{p.Name}» (авто): {(ledPart ? "подсветка отличается" : "идёт запись")} — применю через {(_autoLed.DueAt(DateTime.Now) is DateTime du ? Math.Max(0, (du - DateTime.Now).TotalSeconds) : 0):0.#} с, если профиль не сменится (только подсветка{others})."
                                : $"[led] профиль «{p.Name}»: подсветка отличается от клавиатуры — применяю (только подсветка{others}).");
            else if (ledDiff && _deviceImage != null) Log($"[led] профиль «{p.Name}»: подсветка в клавиатуре прежняя ({(ledOnly ? "" : $"отличаются и другие блоки: {OtherPendingNames()}; ")}{(!_store.Data.WriteConfirmed ? "запись ещё не подтверждена" : !ledPart ? "подсветку отдельно не выделить — нужно «Записать»" : !_connected || _offline ? "клавиатура не подключена" : auto && !autoOn ? "смена подсветки при автопереключении выключена" : auto ? $"лимит {AutoLightingGate.MaxPerHour} автосмен за час" : "нужно «Записать»")}).");
            bool capHit = auto && apply && _autoLed.CapReached(DateTime.Now);
            string note = ProfileSwitchLighting.Note(ledOnly, ledDiff, apply, _store.Data.WriteConfirmed, source, Layer.Active, !autoOn ? AutoLed.Off : capHit ? AutoLed.Cap : AutoLed.On);
            StatusText.Text = (auto ? $"Профиль «{p.Name}» — {_activeWhy}." : $"Профиль «{p.Name}» ({reason}).") + (note.Length > 0 ? " " + note : "");
            bool toasted = MaybeToast(p, source, changed);
            if (!IsVisible && !auto && !toasted) _tray?.Balloon("Профиль: " + p.Name, "Действия на ПК переключены.");
        }
    }

    internal void Changed(bool refreshLayout = true)
    {
        if (StickTilts.Fill(Cfg) is { Count: > 0 } filled)
        {   // v5.7.5: an empty tilt sends nothing on the keypad — keep the arrow instead
            refreshLayout = true;
            StatusText.Text = "Наклон стика не может быть пустым: без назначения он ничего не посылает. Назначена стрелка.";
            Log("Наклоны стика без назначения → стрелки: " + string.Join(", ", filled.Select(DeviceView.ControlName)));
        }
        SaveStore();
        if (refreshLayout) RefreshLayout();
        RefreshDirty();
        PushMapper();
        ScheduleLiveLighting();
    }

    // ───────────────────────────── lighting applies live (≈0.2 s write, LED-only changes, debounced)
    private System.Windows.Threading.DispatcherTimer? _ledTimer;

    private bool LightingOnlyPending() => ProfileSwitchLighting.LightingOnlyPending(DeviceTarget, _deviceImage);
    /// <summary>1.1.1: the keypad image with only the profile's lighting (null = nothing to apply live).</summary>
    private K13DeviceConfig? LightingTarget() => ProfileSwitchLighting.LightingDiffers(DeviceTarget, _deviceImage) ? ProfileSwitchLighting.LightingPart(DeviceTarget, _deviceImage) : null;
    private bool LightingPending() => LightingTarget() != null;

    private string OtherPendingNames()
    {
        if (_deviceImage == null) return "";
        try
        {
            int bs = K13MemoryCodec.ReadBase(_deviceImage);
            return string.Join(", ", PcLayerHealth.PendingWrite(DeviceTarget, _deviceImage)
                .Where(b => !(b.Address >= bs && b.Address + b.Data.Length <= bs + ProfileSwitchLighting.LightingSpan))
                .Select(b => b.Purpose));
        }
        catch { return ""; }
    }

    /// <summary>Starts the debounced LED-only write; false when it will not run (not confirmed yet, offline, other blocks differ).</summary>
    private bool ScheduleLiveLighting()
    {
        if (_offline || !_connected || !_store.Data.WriteConfirmed || !LightingPending()) return false;
        _ledTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _ledTimer.Tick -= LedTimer_Tick; _ledTimer.Tick += LedTimer_Tick;
        _ledTimer.Stop(); _ledTimer.Start();
        return true;
    }

    // ───────────────────────────── 1.0.3: lighting after an auto-switch (wear-protected)
    private readonly AutoLightingGate _autoLed = new();
    private System.Windows.Threading.DispatcherTimer? _autoLedTimer;

    /// <summary>Queues the LED-only write for an auto-switch; false when it will not run (not confirmed, offline, hourly cap).</summary>
    private bool ScheduleAutoLighting(string profile)
    {
        if (_offline || !_connected || !_store.Data.WriteConfirmed) return false;
        var now = DateTime.Now;
        _autoLed.Switched(profile, now);
        if (_autoLed.DueAt(now) is not DateTime due) { _autoLed.Cancel(); return false; }
        StartAutoLedTimer(due - now);
        return true;
    }

    private void StartAutoLedTimer(TimeSpan wait)
    {
        _autoLedTimer ??= new System.Windows.Threading.DispatcherTimer();
        _autoLedTimer.Tick -= AutoLedTimer_Tick; _autoLedTimer.Tick += AutoLedTimer_Tick;
        _autoLedTimer.Stop();
        _autoLedTimer.Interval = wait < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : wait;
        _autoLedTimer.Start();
    }

    private async void AutoLedTimer_Tick(object? sender, EventArgs e)
    {
        _autoLedTimer!.Stop();
        if (_offline || !_connected || !_store.Data.AutoSwitchLighting || !_store.Data.WriteConfirmed) { _autoLed.Cancel(); return; }
        if (_busy) { StartAutoLedTimer(TimeSpan.FromMilliseconds(700)); return; }   // the cached image changes with the running write
        var now = DateTime.Now;
        string? target = _autoLed.PendingProfile;
        switch (_autoLed.Decide(now, _profile.Name, LightingPending()))
        {
            case AutoLightingGate.Verdict.Wait:
                if (_autoLed.DueAt(now) is DateTime due)
                {
                    if (_autoLed.CapReached(now) && !_capLogged)
                    {
                        _capLogged = true;
                        Log($"[led] авто: уже {AutoLightingGate.MaxPerHour} автосмен подсветки за час — «{target}» применю в {due:HH:mm:ss} (бережём память клавиатуры).");
                        StatusText.Text = ProfileSwitchLighting.Note(true, true, false, true, SwitchSource.Auto, Layer.Active, AutoLed.Cap);
                    }
                    StartAutoLedTimer(due - now);
                }
                return;
            case AutoLightingGate.Verdict.SameLighting:
                Log($"[led] авто: «{target}» — подсветка в клавиатуре уже такая (или отличаются и кнопки), не пишу.");
                return;
            case AutoLightingGate.Verdict.NotActiveAnymore:
                Log($"[led] авто: профиль «{target}» уже сменился — не пишу.");
                return;
            case AutoLightingGate.Verdict.Write:
                _autoLed.Wrote(now); _capLogged = false;
                Log($"[led] авто: профиль «{target}» — пишу подсветку (только блок подсветки; {_autoLed.WritesLastHour(now)}-я автосмена за час).");
                await WriteLightingOnly();
                return;
        }
    }

    private bool _lastWriteOk, _lastSwitchAuto, _capLogged;
    private DateTime _lastSwitchAt = DateTime.MinValue;

    /// <summary>1.0.4: after a successful write: if the keypad lighting differs from the active profile (it was switched while the
    /// write ran), queue the matching write — through the gate for an auto-switch, the live path otherwise.</summary>
    private void ReconcileLighting()
    {
        if (_offline || !_connected || _busy || !_store.Data.WriteConfirmed || !LightingPending()) return;
        var now = DateTime.Now;
        if (_lastSwitchAuto)
        {
            if (!_store.Data.AutoSwitchLighting) return;
            _autoLed.Recheck(_profile.Name, _lastSwitchAt);
            if (_autoLed.DueAt(now) is not DateTime due) return;
            Log($"[led] авто: после записи подсветка в клавиатуре не совпадает с профилем «{_profile.Name}» — применю через {Math.Max(0, (due - now).TotalSeconds):0.#} с.");
            StartAutoLedTimer(due - now);
        }
        else if (ScheduleLiveLighting()) Log($"[led] после записи подсветка в клавиатуре не совпадает с профилем «{_profile.Name}» — применяю.");
    }

    internal void SetAutoSwitchLighting(bool on)
    {
        _store.Data.AutoSwitchLighting = on; SaveStore();
        if (!on) { _autoLed.Cancel(); _autoLedTimer?.Stop(); }
        Log(on ? "Смена подсветки при автопереключении включена." : "Смена подсветки при автопереключении выключена.");
    }

    private async void LedTimer_Tick(object? sender, EventArgs e)
    {
        if (Mouse.LeftButton == MouseButtonState.Pressed) return;     // still dragging a slider – wait
        _ledTimer!.Stop();
        if (_busy) { _ledTimer.Start(); return; }
        if (!LightingPending()) return;
        App.Log("[led] подсветка изменена — применяю сразу");
        await WriteLightingOnly();
    }

    /// <summary>1.1.1: the live path writes the lighting area only — even when other blocks of the profile differ (they wait for «Записать»).</summary>
    private async Task<bool> WriteLightingOnly()
    {
        if (LightingTarget() is not { } t) return false;
        return await WriteDevice(quiet: true, target: t);
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
            TrayBox.IsChecked = _store.Data.MinimizeToTray;
            AutostartBox.IsChecked = _store.Data.StartWithWindows;
            DarkBox.IsChecked = _store.Data.DarkTheme;
            SafeText.Visibility = ShowTech ? Visibility.Visible : Visibility.Collapsed; // v5.7: the same text is in the «Записать» tooltip
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

    private void MoreBtn_Click(object sender, RoutedEventArgs e) => MainMenu().IsOpen = true;

    internal ContextMenu MainMenu()
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
        // v5.7.2: moved here from the «Настройки» card on the Profiles page
        Item(_store.Data.MinimizeToTray ? "\uE73E" : "", "Сворачивать в трей при закрытии", (_, _) => { TrayBox.IsChecked = !_store.Data.MinimizeToTray; });
        Item(_store.Data.StartWithWindows ? "\uE73E" : "", "Запускать вместе с Windows (в трей)", (_, _) => { AutostartBox.IsChecked = !_store.Data.StartWithWindows; });
        m.Items.Add(new Separator());
        Item("\uE896", "Прочитать с клавиатуры", ReadBtn_Click, _connected && !_busy && !_offline);
        Item("\uE74E", "Сохранить резервную копию", BackupBtn_Click, _connected && !_busy && !_offline);
        Item("\uE777", "Восстановить из резервной копии…", ImportBin_Click)
            .ToolTip = "Открыть сохранённую копию памяти как профиль. В клавиатуру она попадёт только после «Записать» и подтверждения.";
        Item("\uE72C", "Кнопки не печатают? Вернуть обычный режим", Unstick_Click, _connected && !_busy && !_offline);
        // 1.0.1: the one-shot «Включить Game сейчас» is removed (did not switch on the makers' K13); the stick does it
        m.Items.Add(new Separator());
        // 1.0.5: «Пробовать включать Game при подключении» is removed — it never switched Normal → Game (makers' diagnostics 18.11.2026)
        // v5.9: experimental factory flag «remember mode» (EEPROM 0x000F bit 1)
        Item("\uE7BA", RememberMenuText(), (_, _) => ShowRememberDialog())
            .ToolTip = "Заводской флаг клавиатуры: помнить последний режим (Normal или Game) после переподключения USB, даже без программы. Откроется окно с объяснением; запись — только после подтверждения.";
        m.Items.Add(new Separator());
        if (ShowTech)
        {
            // v5.10: diagnostic tools only with «Показать технические подробности»
            Item("\uE9CE", "Тест: какая команда включает Game…", (_, _) => RunBisect(), _connected && !_busy && !_offline);
            Item("\uE9D9", "План записи (пробный прогон)", PlanBtn_Click);
            m.Items.Add(new Separator());
        }
        Item("\uE8B5", "Импорт из config.ini…", ImportIni_Click);
        Item("\uE838", "Открыть папку резервных копий", (_, _) => OpenBackupFolder());
        Item("\uE9F9", "Сохранить диагностику…", (_, _) => SaveDiagnostics());
        m.Items.Add(new Separator());
        Item("\uE7F8", "Действия на компьютере…", OpenHost_Click);
        Item("\uE897", "Как пользоваться", (_, _) => ShowWelcome(true));
        Item("\uE950", "Устройство и журнал…", (_, _) => DeviceWindow.Show(this, DeviceInfo(), _deviceImage, LogText.ToString()));
        Item(ShowTech ? "\uE73E" : "\uE9D9", ShowTech ? "Скрыть технические подробности" : "Показать технические подробности", (_, _) => SetShowTech(!ShowTech));
        m.Items.Add(new Separator());
        if (_newer is { } nw) Item("\uE896", $"Открыть страницу версии {nw.Tag}", (_, _) => OpenReleasePage(nw.Url));
        Item("\uE895", "Проверить обновления сейчас", async (_, _) => await CheckUpdatesAsync(manual: true), !_offline);
        Item(_store.Data.CheckUpdates ? "\uE73E" : "", "Проверять обновления (раз в день)", (_, _) => SetCheckUpdates(!_store.Data.CheckUpdates))
            .ToolTip = "Раз в день программа спрашивает GitHub, нет ли новой версии, и сообщает. Скачивает и устанавливает только после вашего «Скачать и обновить» (с проверкой контрольной суммы).";
        m.Items.Add(new Separator());
        Item("\uE7E8", "Выход", (_, _) => ExitApp());
        return m;
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
        RefreshModeBanner();
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

    private void RefreshHostSummary() => BuildHostLine();

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
                "Джойстик будет посылать служебные сигналы для ПК, а программа превратит их обратно в стрелки и добавит 4 диагонали (по умолчанию Home / PgUp / End / PgDn — можно изменить).\n\n" +
                "Важно: прошивка K13 посылает только F13 (←); →, ↓, ↑ идут коротким сочетанием Scroll Lock + F, поэтому удержание и диагонали с ними могут не работать — проверьте на своей клавиатуре.\n\n" +
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
        catch (Exception ex) { MessageBox.Show(this, "Ошибка импорта: " + FriendlyError.Text(ex), Title); }
    }

    private void ImportBin_Click(object sender, RoutedEventArgs e)
    {
        // v5.10: «Восстановить из резервной копии…» — newest copies first; the copy becomes a profile, the keypad changes only via «Записать» + its confirm.
        var folder = BackupStore.EnsureFolder();
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите резервную копию (новые — сверху, если отсортировать по дате)",
            Filter = "Резервная копия K13|*.bin|Все файлы|*.*", InitialDirectory = folder,
            FileName = NewestBackupName(folder) ?? ""
        };
        if (dlg.ShowDialog(this) != true) return;
        try { RestoreFromBackup(dlg.FileName, ask: true); }
        catch (Exception ex) { Log("Резервная копия: " + ex.Message); MessageBox.Show(this, "Не удалось открыть резервную копию: " + FriendlyError.Text(ex) + "\n\nНичего не изменено.", Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    internal static string? NewestBackupName(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).GetFiles("*.bin").Where(x => x.Length >= K13Layout.MinImageLength)
                .OrderByDescending(x => x.LastWriteTimeUtc).FirstOrDefault()?.Name;
        }
        catch { return null; }
    }

    /// <summary>v5.10: a backup image → a new profile (never a device write). Returns the profile name.</summary>
    internal string RestoreFromBackup(string file, bool ask)
    {
        var cfg = K13MemoryCodec.Decode(File.ReadAllBytes(file));
        if (!Layer.Active) cfg = PcLayerPlanner.RevertHostChords(cfg);
        var when = File.GetLastWriteTime(file);
        var name = _store.UniqueName("Копия от " + when.ToString("dd.MM.yyyy HH.mm"));
        _store.Data.Profiles.Add(new AppProfile { Name = name, Device = cfg });
        ActivateProfile(name, "из резервной копии " + Path.GetFileName(file));
        string text = $"Резервная копия от {when:dd.MM.yyyy HH:mm} открыта как профиль «{name}». Клавиатура пока не изменена.\n\n" +
                      "Чтобы вернуть эту копию в клавиатуру, нажмите «Записать»: программа покажет, что изменится, и попросит подтверждение." +
                      (Layer.Active ? "\n\nСейчас включены «Кнопки по профилю (через ПК)» — кнопки запишутся в этом режиме. Чтобы вернуть копию в точности, сначала выключите его." : "");
        StatusText.Text = $"Открыта резервная копия от {when:dd.MM.yyyy HH:mm} — для возврата в клавиатуру нажмите «Записать».";
        if (ask) MessageBox.Show(this, text, "Восстановление из резервной копии", MessageBoxButton.OK, MessageBoxImage.Information);
        return name;
    }

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
        if (_busy || _offline || _polling || _exiting) return;
        _polling = true;
        try { await PollDeviceCore(); }
        finally { _polling = false; }
    }

    private async Task PollDeviceCore()
    {
        string? path;
        try { path = await Task.Run(() => WindowsHid.FindVendorInterfaces().FirstOrDefault()); }
        catch (Exception ex) { Log("Поиск устройства: " + ex.Message); return; }
        bool present = path != null;
        if (present && !_connected)
        {
            _connected = true; _devicePath = path;
            Log("Клавиатура подключена: " + path);
            try { var st0 = await Task.Run(() => { using var d = K13Device.OpenFirst() ?? throw new IOException("не открылась"); return d.GetStatus(); }); SetHwMode(st0, "подключение"); NoteConnectMode(); }
            catch (Exception ex) { Log("Статус: " + ex.Message); }
            UpdateConn();
            _kpForeignVk = 0;
            Dispatcher.BeginInvoke(OnConnectedAsync, DispatcherPriority.Background); // v5.7.4: activate (read-type frames), then v5.7.3 replug question
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
            _connected = false; _devicePath = null; _imageFresh = false; _hwMode = HwMode.Unknown; _selfGame = false;
            Log("Клавиатура отключена.");
            _autoLed.Cancel(); _autoLedTimer?.Stop();
            try { _mapper?.KeypadGone(); } catch (Exception ex) { Log("Сброс удержаний: " + ex.Message); }
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
        catch (Exception ex) { Log("Завершение сеанса: " + FriendlyError.Text(ex)); StatusText.Text = FriendlyError.Text(ex); }
        finally { _busy = false; UpdateConn(); ExitIfPending(); }
    }

    /// <summary>v5.3: «Выход» pressed during a read/write waits for the HID session to finish (and end with 07 18 11/00/20)
    /// instead of killing it half-way.</summary>
    private void ExitIfPending()
    {
        if (_exitPending && !_busy) Dispatcher.BeginInvoke(ExitApp, DispatcherPriority.Background);
    }

    private void UpdateConn()
    {
        ConnDot.Fill = (Brush)FindResource(_connected ? "OkBrush" : "ErrBrush");
        ConnText.Text = _busy ? "Обмен с клавиатурой…" : _connected ? "Клавиатура подключена" + ModeSuffix() : "Клавиатура не найдена";
        ConnText.ToolTip = _connected ? ModeExplain : null;
    }

    /// <summary>v5.6.4: opens the backups folder, creating it first (it used to be missing until the first successful backup).</summary>
    internal void OpenBackupFolder()
    {
        var f = BackupStore.EnsureFolder();
        try { Process.Start(new ProcessStartInfo("explorer.exe", f) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { MessageBox.Show(this, "Не удалось открыть папку " + f + ": " + FriendlyError.Text(ex), Title); }
    }

    /// <summary>v5.6.4: write/read failure dialog with «Открыть папку» and «Сохранить диагностику».</summary>
    internal Window BuildWriteErrorDialog(Exception ex, out Func<MessageBoxResult> answer)
    {
        var folder = BackupStore.EnsureFolder();
        var text = "Ошибка записи: " + FriendlyError.Text(ex) + "\n\n" +
                   (ex is K13ReadMismatchException rm && rm.SavedFiles.Count > 0
                       ? "Оба прохода чтения сохранены для разработчиков:\n" + string.Join("\n", rm.SavedFiles.Take(2).Select(Path.GetFileName)) + "\n\n" : "") +
                   "Папка резервных копий:\n" + folder;
        return MessageBox.Build(IsLoaded ? this : null, text, Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel, out answer,
            new[] { "Сохранить диагностику", "Открыть папку", "Закрыть" });
    }

    private void ShowWriteError(Exception ex)
    {
        var w = BuildWriteErrorDialog(ex, out var answer);
        w.ShowDialog();
        if (answer() == MessageBoxResult.Yes) SaveDiagnostics();
        else if (answer() == MessageBoxResult.No) OpenBackupFolder();
    }

    internal string DiagnosticsInfo() =>
        $"VOROTEX K13 Studio v{typeof(App).Assembly.GetName().Version}\n" +
        $"Создано: {DateTime.Now:yyyy-MM-dd HH:mm:ss} (UTC{DateTimeOffset.Now:zzz})\n" +
        $"Windows: {Environment.OSVersion.VersionString}, {(Environment.Is64BitOperatingSystem ? "x64" : "x86")}, .NET {Environment.Version}\n" +
        $"Режим «Кнопки по профилю»: {(Layer.Active ? (Layer.Legacy ? "включён (старый, F14–F24)" : "включён") : "выключен")}\n" +
        $"Принятый заголовок: {(_store.Data.AcceptedHeader ?? "—")}\n" + DeviceInfo() + "\n";

    /// <summary>v5.6.4: «Сохранить диагностику» — zip with studio.log, dumps (including k13-readpass-A/B) and version info.</summary>
    internal void SaveDiagnostics()
    {
        var name = $"VorotexK13-диагностика-{DateTime.Now:yyyyMMdd-HHmm}.zip";
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = name, Filter = "Архив ZIP|*.zip", InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            App.FlushLog();
            var logs = new[] { App.LogPath, App.LogPath + ".1", App.LogPath + ".old" };
            var zip = DiagnosticsBundle.Create(dlg.FileName, logs, BackupStore.EnsureFolder(), DiagnosticsInfo() + "\nЖурнал окна:\n" + LogText);
            Log("Диагностика сохранена: " + zip);
            StatusText.Text = "Диагностика сохранена: " + zip;
            try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + zip + "\"") { UseShellExecute = true })?.Dispose(); } catch { }
        }
        catch (Exception ex) { MessageBox.Show(this, "Не удалось сохранить диагностику: " + FriendlyError.Text(ex), Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    /// <summary>v5.7: bytes, codes, addresses and hex are shown only when this is on (⋯ menu). Off by default.</summary>
    internal bool ShowTech => _store.Data.ShowTechDetails;

    internal void SetShowTech(bool on)
    {
        _store.Data.ShowTechDetails = on; SaveStore();
        SafeText.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        RefreshAll();
    }

    internal string DeviceInfo() =>
        $"Интерфейс: {(_devicePath ?? "—")}\nVID:PID 30FA:2350, коллекция FF01/1, Report ID 7\n" +
        $"Статус: {(_status?.Hex ?? "—")}\n" +
        $"Режим: {K13Mode.Name(_hwMode)} (байт 3 статуса, бит 7: 1 = Game, 0 = Normal)\n" +
        $"Образ памяти: {(_deviceImage != null ? $"0x{_deviceImage.Length:X} байт" : "не прочитан")}\n" +
        RememberInfo() + "\n" +
        $"Запись: включена (бэкап перед записью, проверка после)\nРезервные копии: {BackupStore.DefaultFolder}";

    private async Task<byte[]?> ReadDevice(bool auto, string? prefix = null)
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
                byte[] image;
                try { image = dev.ReadImageVerified(progress); }
                catch (K13ReadMismatchException rm) { BackupStore.SaveReadPasses(rm); throw; }
                if (dev.LastReadReport.Length > 0) App.Log("read: " + dev.LastReadReport);
                return (image, s, dev.SentLog.ToList());
            });
            if (sent.Any(f => !K13Protocol.IsReadOnlyFrame(f) && !K13Protocol.IsSessionEndFrame(f))) throw new InvalidOperationException("Обнаружена не-читающая команда — прервано.");
            _deviceImage = img; _status = st; _imageFresh = true;
            var path = BackupStore.Save(img, null, prefix ?? (auto ? "k13-auto" : "k13-read"), st);
            Log($"Прочитано 0x{img.Length:X} байт ({sent.Count} кадров, только 00/05/01). Резервная копия: {path}");
            if (_freshStore && auto && TryDecode(img) is { } decoded)
            {
                StickTilts.Fill(decoded); // v5.7.5
                _profile.Device = decoded;
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
            if (ex is K13ReadMismatchException rm) { App.Log("read: " + rm.Details); Log("Оба прохода сохранены: " + string.Join(", ", rm.SavedFiles)); }
            StatusText.Text = "Ошибка чтения: " + FriendlyError.Text(ex);
            return null;
        }
        finally
        {
            _busy = false; Progress.Visibility = Visibility.Collapsed; UpdateConn(); RefreshDirty(); ExitIfPending();
        }
    }

    private static K13DeviceConfig? TryDecode(byte[] img)
    {
        try { return K13MemoryCodec.Decode(img); } catch (K13FormatException) { return null; }
    }

    private async void ReadBtn_Click(object sender, RoutedEventArgs e)
    {
        var img = await ReadDevice(auto: false);
        if (img == null) return;
        if (MessageBox.Show(this, $"Память прочитана и сохранена в резервную копию.\n\nЗаменить настройки профиля «{_profile.Name}» данными с клавиатуры?\n(Действия на ПК профиля сохранятся.)",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            // v5.7.3: stale macro names dropped; v5.10: «Scroll Lock + F» macros of PC-action keys → their F14–F24 trigger again
            try { var kp = K13MemoryCodec.Decode(img); ProfileMigrations.ReplaceDevice(_profile, Layer.Active ? kp : PcLayerPlanner.RevertHostChords(kp)); }
            catch (Exception ex)
            {
                Log("Не удалось разобрать память клавиатуры: " + ex.Message);
                MessageBox.Show(this, "Память прочитана и сохранена, но разобрать её не удалось:\n" + FriendlyError.Text(ex) + "\n\nПрофиль не изменён.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            SaveStore(); RefreshAll();
            Log($"Профиль «{_profile.Name}» заменён прочитанным: " + DescribeKeys(Cfg));
            AfterReadNote(img);
        }
    }

    private async void WriteBtn_Click(object sender, RoutedEventArgs e) => await WriteDevice();

    /// <summary>Writes the active profile to the keypad: backup → changed blocks only → read-back → auto-restore on mismatch.</summary>
    /// <summary>True while <see cref="_deviceImage"/> was read (or written+verified) since the keypad was last connected.</summary>
    private bool _imageFresh;

    internal async Task<bool> WriteDevice(bool quiet = false, K13DeviceConfig? target = null)
    {
        if (_busy || _offline || !_connected) return false;
        if (quiet && !_store.Data.WriteConfirmed) return false;
        var what = target ?? DeviceTarget; // v5.6: in «через ПК» mode the mapped keys are written as their F14–F24 codes
        try { K13MemoryCodec.Validate(what); }
        catch (Exception ex) { MessageBox.Show(this, "Профиль содержит ошибку: " + FriendlyError.Text(ex), Title); return false; }
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
        var cfg = K13MemoryCodec.Decode(K13MemoryCodec.Encode(what, _deviceImage ?? DefaultImage)); // snapshot of the profile
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
            Task<K13WriteResult> Run(string? accepted) => Task.Run(() =>
            {
                using var dev = K13Device.OpenFirst() ?? throw new IOException("Клавиатура не найдена.");
                K13WriteGate.Unlock(K13WriteGate.ConfirmationPhrase);
                try { return K13Writer.WriteProfile(dev, cfg, m => { lock (lines) lines.Add(m); App.Log("[write] " + m); }, progress, cachedImage: cached, acceptedHeader: accepted); }
                finally { K13WriteGate.Lock(); }
            });
            var r = await Run(_store.Data.AcceptedHeader);
            if (r.NeedsConfirmation && r.Header != null)
            {
                foreach (var l in lines) Log(l);
                lines.Clear();
                if (r.After != null) _deviceImage = r.After;
                Progress.Visibility = Visibility.Collapsed;
                BuildLayoutDialog(r.Header, r.BackupBefore, out var answer).ShowDialog();
                if (answer() != MessageBoxResult.Yes)
                {
                    StatusText.Text = "Запись отменена: раскладка памяти не подтверждена. Клавиатура не изменялась.";
                    Log("Запись отменена пользователем (заголовок " + r.Header.RawHex + ").");
                    return false;
                }
                _store.Data.AcceptedHeader = r.Header.RawHex; SaveStore();
                Log("Пользователь разрешил запись при заголовке " + r.Header.RawHex + ".");
                Progress.Visibility = Visibility.Visible;
                cached = null;
                r = await Run(r.Header.RawHex);
            }
            else if (r.Header is { Verdict: HeaderVerdict.Compatible } hc) StatusText.Text = hc.Summary;
            foreach (var l in lines) Log(l);
            double secs = swTotal.Elapsed.TotalSeconds;
            Log($"Запись: {r.Message} Время {secs:0.0} с [{r.Timing}]" + (r.BackupAfter != null ? " Проверка: " + r.BackupAfter : ""));
            if (r.After != null) { _deviceImage = r.After; _imageFresh = r.Ok; } else _imageFresh = false;
            StatusText.Text = r.Message + $" ({secs:0.0} с)";
            if (r.Ok && !r.NothingToDo)
            {
                if (r.Written != null && r.After != null && r.BackupAfter != null) { _store.Data.ReplugCheck = PendingReplugCheck.From(r.Written, r.After, r.BackupAfter); _replugAsked = false; SaveStore(); }
                ClearKeypadWarnings("запись проверена");
                if (r.KeysChanged) ShowWrittenHint(secs);
                else if (!quiet) StatusText.Text = $"Готово за {secs:0.0} с — подсветка уже обновлена на клавиатуре.";
            }
            else if (!r.Ok) MessageBox.Show(this, r.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            _lastWriteOk = r.Ok;
            return r.Ok;
        }
        catch (Exception ex)
        {
            _imageFresh = false; // the keypad may now differ from the cached image – re-read before the next write
            Log("Ошибка записи: " + ex.Message); App.Log("write: " + ex);
            StatusText.Text = "Ошибка записи: " + FriendlyError.Text(ex);
            ShowWriteError(ex);
            return false;
        }
        finally
        {
            _busy = false; Progress.Visibility = Visibility.Collapsed; UpdateConn(); RefreshDirty(); ExitIfPending();
            // 1.0.4: the profile may have changed while this write ran — make the keypad lighting follow the active profile
            if (_lastWriteOk) { _lastWriteOk = false; Dispatcher.BeginInvoke(ReconcileLighting, System.Windows.Threading.DispatcherPriority.Background); }
        }
    }

    /// <summary>v5.2: themed «layout not certain» dialog — names the bytes found at 0xD0 and what did not match.</summary>
    internal Window BuildLayoutDialog(K13HeaderInfo h, string? backup, out Func<MessageBoxResult> answer)
    {
        // v5.7: addresses and hex only with «Технические подробности»
        var text = "Память этой клавиатуры устроена не совсем так, как у проверенной K13, поэтому программа спрашивает перед записью.\n\n" +
                   (ShowTech
                       ? $"Заголовок по адресу 0xD0:\n{h.RawHex.Replace(' ', '\u00A0')}  (у проверенной K13: E1\u00A01E\u00A0E0\u00A000\u00A0F0\u00A00F\u00A000\u00A000)\n\n" +
                         string.Join("\n", h.Problems.Concat(h.Notes).Select(x => "• " + x)) + "\n\n"
                       : "Подробности (адреса и байты) — «⋯ → Показать технические подробности».\n\n") +
                   (backup != null ? $"Резервная копия памяти уже сохранена:\n{backup}\n\n" : "") +
                   "Если продолжить, будут записаны только изменённые блоки, затем они будут прочитаны и сверены; при расхождении память вернётся из резервной копии. " +
                   "Для этого заголовка вопрос больше задаваться не будет.";
        return MessageBox.Build(IsLoaded ? this : null, text, "Проверка памяти клавиатуры", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No, out answer,
            new[] { "Продолжить запись", "Отмена" });
    }

    private int EstimateWriteSeconds(K13DeviceConfig cfg, byte[]? cached)
    {
        try
        {
            var img = cached ?? _deviceImage ?? DefaultImage;
            K13MemoryCodec.Encode(K13Mode.PrepareTarget(cfg, img), img, out var plan);
            int blocks = Math.Max(1, K13Protocol.DiffPlan(plan, img).Count);
            double s = (cached == null ? 10.5 : 0.15) + blocks * 0.17 + 0.3;
            return (int)Math.Ceiling(s);
        }
        catch { return 3; }
    }

    private async void BackupBtn_Click(object sender, RoutedEventArgs e)
    {
        var img = await ReadDevice(auto: false);
        if (img != null && MessageBox.Show(this, "Резервная копия сохранена в\n" + BackupStore.EnsureFolder() + "\n\nОткрыть папку?", Title, MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            OpenBackupFolder();
    }

    private void RefreshDirty()
    {
        if (_mapper != null && Layer.Active) PushMapper(); // v5.9.6: the hold bits in the keypad image may have changed
        string tip; Brush fill; bool dirty = false;
        try
        {
            K13MemoryCodec.Validate(DeviceTarget);
            if (_deviceImage == null) { tip = "Клавиатура ещё не прочитана"; fill = (Brush)FindResource("MutedBrush"); }
            else
            {
                K13MemoryCodec.Encode(K13Mode.PrepareTarget(DeviceTarget, _deviceImage), _deviceImage, out var plan);
                var diff = K13Protocol.DiffPlan(plan, _deviceImage);
                tip = diff.Count == 0 ? "Профиль совпадает с клавиатурой" : $"Профиль отличается от клавиатуры: {diff.Count} блок(ов) по 8 байт — нажмите «Записать».";
                dirty = diff.Count > 0;
                fill = (Brush)FindResource(diff.Count == 0 ? "OkBrush" : "WarnBrush");
            }
        }
        catch (Exception ex) { tip = "Ошибка в профиле: " + FriendlyError.Text(ex); fill = (Brush)FindResource("ErrBrush"); }
        SyncDot.Fill = fill;
        RefreshModeBanner();
        WriteBtn.ToolTip = "Записать настройки профиля в клавиатуру.\nПеред записью сохраняется резервная копия, после — проверка чтением; при ошибке всё возвращается как было.\n\n" + tip;
        WriteBtn.IsEnabled = !_offline && _connected && !_busy && (dirty || _deviceImage == null);
        RefreshWriteNote(dirty);
    }

    private void PlanBtn_Click(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ПРОБНЫЙ ПРОГОН — НИЧЕГО НЕ ОТПРАВЛЯЕТСЯ НА КЛАВИАТУРУ (записывает кнопка «Записать»).");
        sb.AppendLine();
        try
        {
            K13MemoryCodec.Validate(DeviceTarget);
            var baseImg = _deviceImage ?? DefaultImage;
            if (_deviceImage == null) sb.AppendLine("⚠ Клавиатура не прочитана — сравнение со встроенным образом от 12.11.2026.\n");
            K13MemoryCodec.Encode(K13Mode.PrepareTarget(DeviceTarget, baseImg), baseImg, out var plan);
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
        if (!_exiting && _busy) { e.Cancel = true; ExitApp(); return; }
        if (!_exiting) ExitApp();
    }

    internal void ExitApp()
    {
        if (_exiting) return;
        if (_busy)
        {
            // v5.3: never kill a read/write half-way — finish it (it ends the HID session properly), then exit
            if (!_exitPending) { _exitPending = true; Log("Выход после завершения обмена с клавиатурой…"); StatusText.Text = "Выход после завершения обмена с клавиатурой…"; }
            return;
        }
        if (!RunPendingUpdate()) return;      // 1.1.0: start the installer / swap the portable exe; on failure keep running
        if (_recording) ToggleRecording();
        _exiting = true;
        _poll.Stop(); _anim.Stop(); _ledTimer?.Stop(); _autoLedTimer?.Stop();
        MacroPlayer.StopAll();                 // v5.3: release keys a playing macro is holding
        try { _ringOverlay?.Close(); } catch (InvalidOperationException) { }
        try { _toast?.Close(); } catch (InvalidOperationException) { }
        _fg.Dispose(); _mapper?.Dispose(); _k13Keys?.Dispose(); _tray?.Dispose(); _recorder.Dispose();
        SaveStore();
        if (!_offline) Application.Current.Shutdown();
        else Close();
    }
}
