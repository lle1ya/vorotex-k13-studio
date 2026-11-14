using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vorotex.K13;

namespace Vorotex.Studio;

/// <summary>v5.4: profiles that switch by the active program — «Включать в программах» on each profile card,
/// status «Активен: … — почему», fallback profile, pause (also from the tray), program picker with icons.</summary>
public partial class MainWindow
{
    private string _activeWhy = "";
    private TextBlock? _fgLabel;
    private readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private (string Process, string Title)? _demoForeground;

    private List<AppSwitchRule> RulesOf(AppProfile p) =>
        _store.Data.AppRules.Where(r => string.Equals(r.ProfileName, p.Name, StringComparison.OrdinalIgnoreCase) && AppMatch.IsUsable(r)).ToList();

    private void OnAutoDecided(AutoSwitchDecision d, string proc, string title)
    {
        if (_offline || _exiting) return;
        if (string.Equals(d.Profile, _profile.Name, StringComparison.OrdinalIgnoreCase))
        {
            if (_activeWhy != d.Reason) { _activeWhy = d.Reason; BuildAutoSwitchCard(); }
            return;
        }
        App.Log($"[auto] {proc} «{Trim(title, 60)}» → {d.Profile} ({d.Reason})");
        ActivateProfile(d.Profile, "авто", d.Reason, auto: true);
    }

    /// <summary>Auto-switch never writes to the keypad: say so when the keypad holds other buttons/lighting.</summary>
    private string DeviceNote()
    {
        if (_deviceImage == null || _offline) return "";
        try
        {
            K13MemoryCodec.Encode(Cfg, _deviceImage, out var plan);
            return K13Protocol.DiffPlan(plan, _deviceImage).Count > 0 ? "Кнопки и подсветка в клавиатуре прежние — «Записать», если нужно." : "";
        }
        catch { return ""; }
    }

    internal void SetAutoPaused(bool paused)
    {
        _fg.Engine.Paused = paused;
        if (!paused) _fg.Engine.Reset();
        Log(paused ? "Автопереключение профилей на паузе." : "Автопереключение профилей продолжено.");
        RefreshAutoStatus();
        BuildAutoSwitchCard();
    }

    private void RefreshAutoStatus()
    {
        _tray?.SetAutoSwitch(_store.Data.AutoSwitch, _fg.Engine.Paused);
        _tray?.SetTooltip($"VOROTEX K13 — {_profile.Name}" + (_store.Data.AutoSwitch && _fg.Engine.Paused ? " (автопереключение на паузе)" : ""));
    }

    private void RefreshForegroundLabel()
    {
        if (_fgLabel == null) return;
        var (p, t) = _demoForeground ?? _fg.Current;
        _fgLabel.Text = p.Length > 0 ? $"Сейчас на переднем плане: {p}.exe" + (t.Length > 0 ? $" — «{Trim(t, 50)}»" : "")
                      : t.Length > 0 ? $"Сейчас на переднем плане: окно «{Trim(t, 50)}» (имя программы недоступно)" : "";
    }

    private static string Trim(string s, int n) => s.Length > n ? s[..n].TrimEnd() + "…" : s;

    // ───────────────────────────── the card above the profiles
    private void BuildAutoSwitchCard()
    {
        var host = AutoSwitchHost;
        host.Children.Clear();
        var d = _store.Data;

        var head = new DockPanel();
        var sw = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = d.AutoSwitch, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Включить или выключить автопереключение" };
        sw.Checked += (_, _) => SetAutoSwitch(true);
        sw.Unchecked += (_, _) => SetAutoSwitch(false);
        DockPanel.SetDock(sw, Dock.Right);
        head.Children.Add(sw);
        if (d.AutoSwitch)
        {
            bool paused = _fg.Engine.Paused;
            var pause = new Button { Style = (Style)FindResource("GhostButton"), Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
                ToolTip = paused ? "Снова переключать профили по программам" : "Временно не переключать (до продолжения или перезапуска). То же — в меню значка в трее.",
                Content = new StackPanel { Orientation = Orientation.Horizontal, Children =
                {
                    new TextBlock { Text = paused ? "\uE768" : "\uE769", FontFamily = Icons, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) },
                    new TextBlock { Text = paused ? "Продолжить" : "Пауза" },
                } } };
            pause.Click += (_, _) => SetAutoPaused(!_fg.Engine.Paused);
            DockPanel.SetDock(pause, Dock.Right);
            head.Children.Add(pause);
        }
        head.Children.Add(new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(10), Background = R("AccentSoftBrush"), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = "\uE8AB", FontFamily = Icons, FontSize = 16, Foreground = R("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(T("Автопереключение по программам", 16, semi: true));
        titles.Children.Add(T("Профиль включается сам, когда вы переходите в программу из его списка «Включать в программах» (ниже, на карточке профиля).", 12, muted: true));
        head.Children.Add(titles);
        host.Children.Add(head);

        // status: which profile and why
        int idx = Math.Max(0, d.Profiles.IndexOf(_profile));
        var accent = AccentOf(_profile, idx);
        var status = new DockPanel();
        var dot = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(8), Background = accent, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = GlyphOf(_profile, idx), FontFamily = Icons, FontSize = 12, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        status.Children.Add(dot);
        var st = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var line = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap };
        line.Inlines.Add(new System.Windows.Documents.Run("Активен: ") { Foreground = R("MutedBrush") });
        line.Inlines.Add(new System.Windows.Documents.Run(_profile.Name) { FontWeight = FontWeights.SemiBold });
        line.Inlines.Add(new System.Windows.Documents.Run(" — " + (_activeWhy.Length > 0 ? _activeWhy : "последний использованный")));
        if (d.AutoSwitch && _fg.Engine.Paused) line.Inlines.Add(new System.Windows.Documents.Run("   · автопереключение на паузе") { Foreground = R("WarnBrush") });
        else if (!d.AutoSwitch) line.Inlines.Add(new System.Windows.Documents.Run("   · автопереключение выключено") { Foreground = R("MutedBrush") });
        st.Children.Add(line);
        _fgLabel = T("", 12, muted: true);
        _fgLabel.Margin = new Thickness(0, 2, 0, 0);
        st.Children.Add(_fgLabel);
        RefreshForegroundLabel();
        status.Children.Add(st);
        host.Children.Add(new Border { Child = status, Background = R("Panel2Brush"), CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 14, 0, 0) });

        // fallback + what switches
        var grid = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var fb = new StackPanel();
        fb.Children.Add(T("Когда ни одно правило не подошло", 13, semi: true, bottom: 6));
        const string keep = "Оставлять текущий профиль";
        var items = new List<string> { keep };
        items.AddRange(d.Profiles.Select(p => p.Name));
        var combo = new ComboBox { ItemsSource = items, SelectedItem = d.FallbackProfile.Length > 0 && items.Contains(d.FallbackProfile) ? d.FallbackProfile : keep, IsEnabled = d.AutoSwitch };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is not string s) return;
            d.FallbackProfile = s == keep ? "" : s;
            SaveStore(); _fg.Engine.Reset();
            Log(d.FallbackProfile.Length > 0 ? $"Профиль по умолчанию (нет правила): «{d.FallbackProfile}»." : "Без правила профиль больше не меняется.");
            Dispatcher.BeginInvoke(RefreshProfilesList, System.Windows.Threading.DispatcherPriority.Background);
        };
        fb.Children.Add(combo);
        fb.Children.Add(T(d.FallbackProfile.Length > 0 ? $"В остальных программах включится «{d.FallbackProfile}»." : "В остальных программах остаётся тот профиль, что был.", 12, muted: true).With(t => t.Margin = new Thickness(0, 6, 0, 0)));
        grid.Children.Add(fb);

        var info = new DockPanel();
        info.Children.Add(new TextBlock { Text = "\uE946", FontFamily = Icons, FontSize = 14, Foreground = R("AccentBrush"), Margin = new Thickness(0, 1, 10, 0), VerticalAlignment = VerticalAlignment.Top });
        var it = new StackPanel();
        var l1 = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 0, 0, 4) };
        l1.Inlines.Add(new System.Windows.Documents.Run("Сразу переключаются действия на ПК") { FontWeight = FontWeights.SemiBold });
        l1.Inlines.Add(new System.Windows.Documents.Run(": кольцо, сочетания клавиш, запуск программ и ссылок, макросы на компьютере (клавиши F13–F24)."));
        var l2 = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Foreground = R("MutedBrush") };
        l2.Inlines.Add(new System.Windows.Documents.Run("Кнопки и подсветка хранятся в самой клавиатуре") { FontWeight = FontWeights.SemiBold });
        l2.Inlines.Add(new System.Windows.Documents.Run(" и меняются только кнопкой «Записать»: после записи клавиатуру нужно переподключить, а частая запись изнашивает её память. Поэтому автопереключение в клавиатуру не пишет."));
        it.Children.Add(l1); it.Children.Add(l2);
        info.Children.Add(it);
        var infoBox = new Border { Child = info, BorderBrush = R("BorderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 10, 12, 10) };
        Grid.SetColumn(infoBox, 2);
        grid.Children.Add(infoBox);
        host.Children.Add(grid);
        _tray?.SetAutoSwitch(d.AutoSwitch, _fg.Engine.Paused);
    }

    private void SetAutoSwitch(bool on)
    {
        if (_loading || _store.Data.AutoSwitch == on) return;
        _store.Data.AutoSwitch = on; SaveStore();
        _fg.Engine.Reset();
        Log(on ? "Автопереключение профилей включено." : "Автопереключение профилей выключено.");
        Dispatcher.BeginInvoke(() => { BuildAutoSwitchCard(); RefreshAutoStatus(); }, System.Windows.Threading.DispatcherPriority.Background);
    }

    // ───────────────────────────── «Включать в программах» on a profile card
    private FrameworkElement AppRulesSection(AppProfile p, Brush accent)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var add = new Button { Style = (Style)FindResource("GhostButton"), Padding = new Thickness(9, 3, 10, 4), ToolTip = "Добавить программу, в которой этот профиль включается сам",
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children =
            {
                new TextBlock { Text = "\uE710", FontFamily = Icons, FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) },
                new TextBlock { Text = "Программа", FontSize = 12 },
            } } };
        add.Click += (_, e) => { e.Handled = true; BuildAppPicker(p).ShowDialog(); };
        DockPanel.SetDock(add, Dock.Right);
        head.Children.Add(add);
        head.Children.Add(new TextBlock { Text = "Включать в программах", FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(head);

        var rules = RulesOf(p);
        if (rules.Count == 0)
        {
            bool fb = string.Equals(_store.Data.FallbackProfile, p.Name, StringComparison.OrdinalIgnoreCase);
            sp.Children.Add(T(fb ? "Нет своих программ — включается, когда ни одно правило не подошло." : "Нет — профиль включается вручную, из трея или кнопкой.", 12, muted: true));
            return sp;
        }
        var wrap = new WrapPanel();
        foreach (var r in rules) wrap.Children.Add(RuleChip(r));
        sp.Children.Add(wrap);
        if (!_store.Data.AutoSwitch) sp.Children.Add(T("Автопереключение выключено — правила сейчас не действуют.", 11.5, muted: true).With(t => t.Margin = new Thickness(0, 2, 0, 0)));
        return sp;
    }

    private Border RuleChip(AppSwitchRule r)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(AppIcon(r.ProcessName, r.IconPath, 18, r.DisplayName).With(x => x.Margin = new Thickness(0, 0, 7, 0)));
        var label = new TextBlock { Text = AppMatch.Label(r), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 170, TextTrimming = TextTrimming.CharacterEllipsis };
        row.Children.Add(label);
        if (r.TitleContains.Trim().Length > 0 && AppMatch.NormalizeProcess(r.ProcessName).Length > 0)
            row.Children.Add(new TextBlock { Text = $" · «{Trim(r.TitleContains.Trim(), 18)}»", FontSize = 12, Foreground = R("MutedBrush"), VerticalAlignment = VerticalAlignment.Center });
        var x = new Button { Style = (Style)FindResource("GhostButton"), Padding = new Thickness(4, 2, 4, 2), Margin = new Thickness(4, 0, -2, 0), MinWidth = 0, MinHeight = 0, ToolTip = "Убрать",
            Content = new TextBlock { Text = "\uE711", FontFamily = Icons, FontSize = 9, Foreground = R("MutedBrush") } };
        x.Click += (_, e) =>
        {
            e.Handled = true;
            _store.Data.AppRules.Remove(r); SaveStore(); _fg.Engine.Reset();
            Log($"«{r.ProfileName}» больше не включается в {AppMatch.Describe(r)}.");
            Dispatcher.BeginInvoke(RefreshProfilesList, System.Windows.Threading.DispatcherPriority.Background);
        };
        row.Children.Add(x);
        return new Border { Child = row, Background = R("Panel2Brush"), CornerRadius = new CornerRadius(10), Padding = new Thickness(7, 4, 4, 4), Margin = new Thickness(0, 0, 6, 6),
            ToolTip = "Профиль включается: " + AppMatch.Describe(r) };
    }

    /// <summary>Program icon from its .exe (cached), or a coloured letter tile when the file is not available.</summary>
    private FrameworkElement AppIcon(string process, string? exePath, double size, string? display = null)
    {
        var proc = AppMatch.NormalizeProcess(process);
        ImageSource? img = null;
        if (!_offline && !string.IsNullOrEmpty(exePath))
        {
            if (!_iconCache.TryGetValue(exePath, out img))
            {
                try
                {
                    if (File.Exists(exePath))
                    {
                        using var ic = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                        if (ic != null)
                        {
                            var bs = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(ic.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                            bs.Freeze(); img = bs;
                        }
                    }
                }
                catch { img = null; }
                _iconCache[exePath] = img;
            }
        }
        if (img != null) return new Image { Source = img, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        var name = (display ?? "").Trim().Length > 0 ? display!.Trim() : proc.Length > 0 ? proc : "?";
        int h = 0; foreach (var ch in proc.ToLowerInvariant()) h = h * 31 + ch;
        var color = (Color)ColorConverter.ConvertFromString(AccentPalette[Math.Abs(h % AccentPalette.Length)]);
        return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.28), Background = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = proc.Length == 0 ? "\uE7C4" : name[..1].ToUpperInvariant(), FontFamily = proc.Length == 0 ? Icons : FontFamily, FontSize = size * 0.55, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
    }

    // ───────────────────────────── program picker
    private sealed record RunningApp(string Process, string Path, string Title, string Display);

    private List<RunningApp> RunningApps()
    {
        if (_offline)
            return new()
            {
                new("Photoshop", "", "Безымянный-1 @ 66,7% (RGB/8)", "Adobe Photoshop 2025"),
                new("chrome", "", "YouTube — Google Chrome", "Google Chrome"),
                new("cs2", "", "Counter-Strike 2", "Counter-Strike 2"),
                new("Telegram", "", "Telegram", "Telegram Desktop"),
                new("Code", "", "MainWindow.cs — studio — Visual Studio Code", "Visual Studio Code"),
                new("explorer", "", "Загрузки", "Проводник"),
            };
        var list = new List<RunningApp>();
        try
        {
            foreach (var (proc, path, title) in Native.RunningApps())
            {
                if (string.Equals(proc, AppMatch.OwnProcess, StringComparison.OrdinalIgnoreCase) || AppMatch.Transient.Contains(proc)) continue;
                string disp = "";
                try { if (path.Length > 0) disp = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim() ?? ""; } catch { }
                list.Add(new(proc, path, title, disp));
            }
        }
        catch (Exception ex) { Log("Список программ: " + ex.Message); }
        return list.OrderBy(a => (a.Display.Length > 0 ? a.Display : a.Process), StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    internal Window BuildAppPicker(AppProfile p, string? preselect = null)
    {
        var w = Ui.MakeWindow(IsLoaded ? this : null, "Включать в программе", 600, 660);
        w.MinWidth = 520; w.MinHeight = 520;
        var root = new DockPanel { Margin = new Thickness(24, 12, 24, 20) };

        var top = new StackPanel();
        top.Children.Add(T($"Включать «{p.Name}» в программе", 18, semi: true, bottom: 4));
        top.Children.Add(T("Выберите запущенную программу, укажите её файл .exe или введите имя процесса.", 13, muted: true, bottom: 12));
        var search = new TextBox { Padding = new Thickness(10, 6, 10, 6) };
        var searchHint = new TextBlock { Text = "Поиск среди запущенных программ", Foreground = R("MutedBrush"), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        search.TextChanged += (_, _) => searchHint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        top.Children.Add(new Grid { Children = { search, searchHint }, Margin = new Thickness(0, 0, 0, 8) });
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        // bottom: manual entry, title filter, errors, buttons
        var bottom = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        var manual = new Grid();
        manual.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        manual.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var procBox = new TextBox { Padding = new Thickness(10, 6, 10, 6), Text = preselect ?? "" };
        var procHint = new TextBlock { Text = "имя процесса, например photoshop", Foreground = R("MutedBrush"), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
            Visibility = procBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed };
        manual.Children.Add(procBox); manual.Children.Add(procHint);
        var browse = new Button { Style = (Style)FindResource("GhostButton"), Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 6, 12, 6),
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children =
            {
                new TextBlock { Text = "\uE8E5", FontFamily = Icons, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) },
                new TextBlock { Text = "Файл .exe…" },
            } } };
        Grid.SetColumn(browse, 1); manual.Children.Add(browse);
        bottom.Children.Add(T("Программа", 13, semi: true, bottom: 6));
        bottom.Children.Add(manual);
        var useTitle = new CheckBox { Style = (Style)FindResource("Switch"), Content = "Только если в заголовке окна есть:", Margin = new Thickness(0, 12, 0, 6) };
        var titleBox = new TextBox { Padding = new Thickness(10, 6, 10, 6), IsEnabled = false };
        useTitle.Checked += (_, _) => { titleBox.IsEnabled = true; titleBox.Focus(); };
        useTitle.Unchecked += (_, _) => titleBox.IsEnabled = false;
        bottom.Children.Add(useTitle);
        bottom.Children.Add(titleBox);
        bottom.Children.Add(T("Например, «YouTube» для вкладки браузера. Если игра или программа, запущенная от администратора, не определяется по имени, оставьте имя пустым и укажите заголовок.", 12, muted: true).With(t => t.Margin = new Thickness(0, 6, 0, 0)));
        var err = T("", 12.5); err.Foreground = R("WarnBrush"); err.Margin = new Thickness(0, 8, 0, 0);
        bottom.Children.Add(err);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var ok = new Button { Content = "Добавить", MinWidth = 110, IsDefault = true, Style = (Style)FindResource("AccentButton"), Padding = new Thickness(16, 7, 16, 7) };
        var cancel = new Button { Content = "Отмена", MinWidth = 96, IsCancel = true, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(16, 7, 16, 7) };
        bar.Children.Add(ok); bar.Children.Add(cancel);
        bottom.Children.Add(bar);
        root.Children.Add(bottom);

        // running programs
        var listHost = new StackPanel();
        var scroll = new ScrollViewer { Content = listHost, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(new Border { Child = scroll, BorderBrush = R("BorderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(6) });

        var apps = RunningApps();
        string selPath = "", selDisplay = "";
        Border? selRow = null;
        void Fill()
        {
            listHost.Children.Clear();
            var q = search.Text.Trim();
            var shown = apps.Where(a => q.Length == 0 || a.Process.Contains(q, StringComparison.OrdinalIgnoreCase) || a.Display.Contains(q, StringComparison.OrdinalIgnoreCase) || a.Title.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            if (shown.Count == 0) { listHost.Children.Add(T(apps.Count == 0 ? "Не удалось получить список программ — укажите файл или имя ниже." : "Ничего не найдено.", 13, muted: true).With(t => t.Margin = new Thickness(10))); return; }
            foreach (var a in shown)
            {
                var row = new DockPanel();
                var owner = _store.Data.AppRules.FirstOrDefault(r => r.TitleContains.Trim().Length == 0 && string.Equals(AppMatch.NormalizeProcess(r.ProcessName), a.Process, StringComparison.OrdinalIgnoreCase));
                if (owner != null)
                {
                    var pill = Pill("", string.Equals(owner.ProfileName, p.Name, StringComparison.OrdinalIgnoreCase) ? "уже в списке" : $"сейчас: «{owner.ProfileName}»", false);
                    pill.VerticalAlignment = VerticalAlignment.Center; pill.Margin = new Thickness(8, 0, 4, 0);
                    DockPanel.SetDock(pill, Dock.Right); row.Children.Add(pill);
                }
                row.Children.Add(AppIcon(a.Process, a.Path, 28, a.Display).With(x => x.Margin = new Thickness(0, 0, 12, 0)));
                var tx = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                tx.Children.Add(new TextBlock { Text = a.Display.Length > 0 ? a.Display : a.Process, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                tx.Children.Add(new TextBlock { Text = $"{a.Process}.exe · {a.Title}", FontSize = 12, Foreground = R("MutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
                row.Children.Add(tx);
                var b = new Border { Child = row, Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand, Background = Brushes.Transparent, BorderThickness = new Thickness(1.5), BorderBrush = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 2) };
                void Select()
                {
                    if (selRow != null) { selRow.Background = Brushes.Transparent; selRow.BorderBrush = Brushes.Transparent; }
                    selRow = b; b.Background = R("AccentSoftBrush"); b.BorderBrush = R("AccentBrush");
                    procBox.Text = a.Process; selPath = a.Path; selDisplay = a.Display; err.Text = "";
                }
                b.MouseLeftButtonUp += (_, _) => Select();
                b.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { Select(); ok.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); } };
                if (preselect != null && string.Equals(preselect, a.Process, StringComparison.OrdinalIgnoreCase)) Select();
                listHost.Children.Add(b);
            }
        }
        search.TextChanged += (_, _) => Fill();
        procBox.TextChanged += (_, _) =>
        {
            procHint.Visibility = procBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (selRow != null && !string.Equals(AppMatch.NormalizeProcess(procBox.Text), apps.FirstOrDefault(a => a.Path == selPath)?.Process ?? "", StringComparison.OrdinalIgnoreCase))
            { selRow.Background = Brushes.Transparent; selRow.BorderBrush = Brushes.Transparent; selRow = null; selPath = ""; selDisplay = ""; }
        };
        browse.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Программы (*.exe)|*.exe", Title = "Выберите программу" };
            if (dlg.ShowDialog(w) != true) return;
            procBox.Text = AppMatch.NormalizeProcess(dlg.FileName);
            selPath = dlg.FileName;
            try { selDisplay = System.Diagnostics.FileVersionInfo.GetVersionInfo(dlg.FileName).FileDescription?.Trim() ?? ""; } catch { selDisplay = ""; }
        };
        ok.Click += (_, _) =>
        {
            var msg = AddRule(p, procBox.Text, useTitle.IsChecked == true ? titleBox.Text : "", selDisplay, selPath);
            if (msg != null) { err.Text = msg; return; }
            w.DialogResult = true;
        };
        Fill();
        w.Content = root;
        w.Loaded += (_, _) => search.Focus();
        return w;
    }

    /// <summary>Adds a «Включать в программах» rule; returns an error text or null.</summary>
    internal string? AddRule(AppProfile p, string process, string title, string display = "", string path = "")
    {
        var proc = AppMatch.NormalizeProcess(process);
        title = (title ?? "").Trim();
        if (proc.Length == 0 && title.Length == 0) return "Выберите программу, укажите файл или введите имя процесса (или заголовок окна).";
        if (proc.IndexOfAny(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }) >= 0) return "В имени процесса недопустимые символы.";
        if (proc.Length > 120 || title.Length > 200) return "Слишком длинно.";
        if (string.Equals(proc, AppMatch.OwnProcess, StringComparison.OrdinalIgnoreCase)) return "Это сама VOROTEX K13 Studio — в её окне профиль не переключается, чтобы вы могли спокойно его настраивать.";
        var same = _store.Data.AppRules.FirstOrDefault(r => string.Equals(AppMatch.NormalizeProcess(r.ProcessName), proc, StringComparison.OrdinalIgnoreCase) && string.Equals(r.TitleContains.Trim(), title, StringComparison.OrdinalIgnoreCase));
        if (same != null && string.Equals(same.ProfileName, p.Name, StringComparison.OrdinalIgnoreCase)) return "Эта программа уже в списке профиля.";
        if (same != null)
        {
            _store.Data.AppRules.Remove(same);
            Log($"{AppMatch.Describe(same)}: правило перенесено из «{same.ProfileName}» в «{p.Name}».");
        }
        var rule = new AppSwitchRule { ProcessName = proc, TitleContains = title, ProfileName = p.Name, DisplayName = display.Trim(), IconPath = path };
        _store.Data.AppRules.Add(rule);
        SaveStore(); _fg.Engine.Reset();
        Log($"«{p.Name}» включается: {AppMatch.Describe(rule)}.");
        Dispatcher.BeginInvoke(RefreshProfilesList, System.Windows.Threading.DispatcherPriority.Background);
        return null;
    }

    /// <summary>Screenshots/smoke test (offline store): rules with names, a fallback and a reason.</summary>
    internal void DemoV54()
    {
        if (!_offline) return;
        var d = _store.Data;
        d.AppRules.Clear();
        d.AppRules.Add(new AppSwitchRule { ProcessName = "Photoshop", ProfileName = "Фото", DisplayName = "Adobe Photoshop 2025" });
        d.AppRules.Add(new AppSwitchRule { ProcessName = "Lightroom", ProfileName = "Фото", DisplayName = "Lightroom Classic" });
        d.AppRules.Add(new AppSwitchRule { ProcessName = "cs2", ProfileName = "Игры", DisplayName = "Counter-Strike 2" });
        d.AppRules.Add(new AppSwitchRule { ProcessName = "", TitleContains = "Elden Ring", ProfileName = "Игры" });
        d.AppRules.Add(new AppSwitchRule { ProcessName = "chrome", TitleContains = "YouTube", ProfileName = "Игры", DisplayName = "Google Chrome" });
        d.FallbackProfile = "Основной";
        d.AutoSwitch = true;
        _demoForeground = ("Photoshop", "Безымянный-1 @ 66,7% (RGB/8)");
        ActivateProfile("Фото", "авто", "по программе Photoshop.exe", auto: true);
    }
}
