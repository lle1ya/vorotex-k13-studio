using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Vorotex.K13;

namespace Vorotex.Studio;

/// <summary>"Профили" page (v5.7.1): a calm list of profiles on the left, the selected profile on the right
/// (preview, «Включать в программах», «Действия на компьютере»), settings for all profiles below it.</summary>
public partial class MainWindow
{
    private static readonly string[] AccentPalette = { "#F06423", "#3B82F6", "#22A45D", "#A855F7", "#E11D48", "#0EA5E9", "#D99A06", "#64748B" };
    private static readonly string[] GlyphPalette = { "\uE765", "\uE7FC", "\uE8B9", "\uE7F4", "\uE821", "\uE8D6", "\uE771", "\uE80F" }; // keyboard, game, photo, monitor, work, music, brush, home

    private static Brush AccentOf(AppProfile p, int index)
    {
        string hex = p.Accent.Length == 7 ? p.Accent : AccentPalette[index % AccentPalette.Length];
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); } catch { return new SolidColorBrush(Color.FromRgb(0xF0, 0x64, 0x23)); }
    }

    private static string GlyphOf(AppProfile p, int index) => p.Glyph.Length > 0 ? p.Glyph : GlyphPalette[index % GlyphPalette.Length];

    // v5.7.1: the page is a list (left) and the selected profile (right), like «Макросы».
    // Selecting a profile only shows it; «Сделать активным» (or a double click) switches to it.
    private string? _viewName;
    private bool _renameNext;
    private bool _profAdvOpen;
    private TextBlock? _hostSummary;

    internal AppProfile ViewProfile => (_viewName != null ? _store.Find(_viewName) : null) ?? _profile;

    internal AppProfile ActiveProfileForTest => _profile;
    internal CheckBox TrayBoxForTest => TrayBox;

    internal void SetProfilesAdvancedOpen(bool open) { _profAdvOpen = open; RefreshProfilesList(); }

    internal void SelectProfileView(string? name) { _viewName = name; RefreshProfilesList(); }

    private void RefreshProfilesList()
    {
        ProfileCards.Children.Clear();
        var list = _store.Data.Profiles;
        var view = ViewProfile;
        for (int i = 0; i < list.Count; i++) ProfileCards.Children.Add(ProfileRow(list[i], i, list[i] == view));
        ProfileCountText.Text = RuPlural.Of(list.Count, "профиль", "профиля", "профилей");
        BuildProfileDetail(view, Math.Max(0, list.IndexOf(view)));
        BuildAutoSwitchCard();
    }

    private Border ProfileIcon(AppProfile p, int index, double size) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.29), Background = AccentOf(p, index), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = GlyphOf(p, index), FontFamily = Icons, FontSize = size * 0.42, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
    };

    private string ModeLine(AppProfile p)
    {
        int ruleCount = RulesOf(p).Count;
        bool isFallback = string.Equals(_store.Data.FallbackProfile, p.Name, StringComparison.OrdinalIgnoreCase);
        return ruleCount > 0 ? "включается сам — " + RuPlural.Of(ruleCount, "программа", "программы", "программ") + (isFallback ? " · и в остальных" : "")
             : isFallback ? "включается в остальных программах" : "включается вручную";
    }

    /// <summary>One row of the list: icon · name · «сейчас»; under it the program icons. Actions show up on hover.</summary>
    private Border ProfileRow(AppProfile p, int index, bool sel)
    {
        bool active = p == _profile;
        var sp = new StackPanel();
        var top = new DockPanel();

        // right cell: «● сейчас» normally, row actions on hover (same place, so the name never jumps)
        var right = new Grid { VerticalAlignment = VerticalAlignment.Center };
        var now = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Visibility = active ? Visibility.Visible : Visibility.Hidden };
        now.Children.Add(new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = R("OkBrush"), Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        now.Children.Add(new TextBlock { Text = "сейчас", FontSize = 12, Foreground = R("MutedBrush"), VerticalAlignment = VerticalAlignment.Center });
        right.Children.Add(now);
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Opacity = 0 };
        Button Mini(string glyph, string tip, Action a)
        {
            var b = new Button { Style = (Style)FindResource("GhostButton"), Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(1, 0, 0, 0), ToolTip = tip,
                Content = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 11, Foreground = R("MutedBrush") } };
            b.Click += (_, e) => { e.Handled = true; a(); };
            btns.Children.Add(b);
            return b;
        }
        Mini("\uE70F", "Переименовать", () => { _viewName = p.Name; _renameNext = true; RefreshProfilesList(); });
        Mini("\uE8C8", "Дублировать", () => DuplicateProfile(p));
        Mini("\uEDE1", "Экспорт в файл .k13profile или как текст", () => ExportProfiles(new[] { p }));
        Mini("\uE74D", "Удалить", () => DeleteProfile(p));
        right.Children.Add(btns);
        DockPanel.SetDock(right, Dock.Right);
        top.Children.Add(right);

        top.Children.Add(ProfileIcon(p, index, 28).With(x => x.Margin = new Thickness(0, 0, 10, 0)));
        top.Children.Add(new TextBlock { Text = p.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        sp.Children.Add(top);

        // programs it switches on in: small icons, or one muted word
        var rules = RulesOf(p);
        var apps = new WrapPanel { Margin = new Thickness(38, 6, 0, 0) };
        foreach (var r in rules.Take(5))
            apps.Children.Add(AppIcon(r.ProcessName, r.IconPath, 16, r.DisplayName).With(x => { x.Margin = new Thickness(0, 0, 5, 0); x.ToolTip = AppMatch.Label(r); }));
        if (rules.Count > 5) apps.Children.Add(new TextBlock { Text = $"+{rules.Count - 5}", FontSize = 11, Foreground = R("MutedBrush"), VerticalAlignment = VerticalAlignment.Center });
        if (rules.Count == 0)
            apps.Children.Add(new TextBlock { Text = string.Equals(_store.Data.FallbackProfile, p.Name, StringComparison.OrdinalIgnoreCase) ? "в остальных программах" : "вручную", FontSize = 12, Foreground = R("MutedBrush") });
        sp.Children.Add(apps);

        var card = new Border { Child = sp, Padding = new Thickness(14, 12, 12, 11), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(14), Cursor = Cursors.Hand,
            Background = sel ? R("SelBrush") : R("PanelBrush"), BorderBrush = sel ? R("AccentBrush") : Brushes.Transparent, BorderThickness = new Thickness(1.5),
            ToolTip = active ? "Активный профиль" : "Щёлкните, чтобы открыть; двойной щелчок — сделать активным" };
        void Show(bool on) { btns.Opacity = on ? 1 : 0; now.Opacity = on ? 0 : 1; btns.IsHitTestVisible = on; }
        Show(false);
        card.MouseEnter += (_, _) => { if (!sel) card.Background = R("HoverBrush"); Show(true); };
        card.MouseLeave += (_, _) => { if (!sel) card.Background = R("PanelBrush"); Show(btns.IsKeyboardFocusWithin); };
        btns.IsKeyboardFocusWithinChanged += (_, _) => Show(btns.IsKeyboardFocusWithin || card.IsMouseOver);
        card.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is DependencyObject o && Ui.IsInside<Button>(o)) return; e.Handled = true; if (!sel) { _viewName = p.Name; Dispatcher.BeginInvoke(RefreshProfilesList); } };
        card.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2 && !active) { e.Handled = true; ActivateProfile(p.Name, "вручную"); } };
        card.Tag = "profile-row";
        return card;
    }

    /// <summary>Right panel: the selected profile — head with one main action, preview, programs, actions on the PC.</summary>
    private void BuildProfileDetail(AppProfile p, int index)
    {
        var host = ProfileDetailHost;
        host.Children.Clear();
        bool active = p == _profile;

        // head: icon · name / one muted line …… main action
        var head = new DockPanel();
        FrameworkElement main;
        if (active)
            main = new TextBlock { Text = "✓  Сейчас используется", Foreground = R("MutedBrush"), FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        else
        {
            var on = new Button { Style = (Style)FindResource("AccentButton"), Padding = new Thickness(16, 7, 16, 7), VerticalAlignment = VerticalAlignment.Center, Content = "Сделать активным",
                ToolTip = "Переключиться на этот профиль (в клавиатуру ничего не пишется)" };
            on.Click += (_, e) => { e.Handled = true; ActivateProfile(p.Name, "вручную"); };
            main = on;
        }
        DockPanel.SetDock(main, Dock.Right);
        head.Children.Add(main);
        var icon = ProfileIcon(p, index, 44);
        icon.Margin = new Thickness(0, 0, 14, 0); icon.Cursor = Cursors.Hand; icon.ToolTip = "Сменить цвет и значок";
        icon.MouseLeftButtonUp += (_, e) => { e.Handled = true; CycleLook(p, index); };
        head.Children.Add(icon);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = EditableTitle(p.Name, 20, n => RenameProfile(p, n), "Переименовать профиль");
        titles.Children.Add(title);
        titles.Children.Add(new TextBlock { Text = p.Comment.Length > 0 ? p.Comment : ModeLine(p), FontSize = 12.5, Foreground = R("MutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
        head.Children.Add(titles);
        host.Children.Add(head);
        if (_renameNext)
        {
            _renameNext = false;
            Dispatcher.BeginInvoke(() => Ui.Descendants<Button>(title).FirstOrDefault()?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // keypad preview + one muted line of facts
        var cfg = p.Device ?? K13MemoryCodec.Decode(DefaultImage);
        var dv = new DeviceView { Height = 150, MaxWidth = 460, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 20, 0, 8) };
        try { dv.Rebuild(cfg, p.Host, -1, showDots: true); } catch { }
        host.Children.Add(dv);
        int changed = K13Layout.Controls.Count(c => cfg.GetKey(c.Column) != c.FactoryCode);
        int macros = Enumerable.Range(0, KeyCatalog.MacroCodeCount).Count(i => cfg.Macros[i].IsUsed);
        int hostN = p.Host.Keys.Count(k => k.Tap.Kind != HostActionKind.None) + p.Host.Chords.Count;
        host.Children.Add(new TextBlock
        {
            Text = string.Join("  ·  ", new[]
            {
                changed > 0 ? RuPlural.Of(changed, "кнопка изменена", "кнопки изменены", "кнопок изменено") : "кнопки как с завода",
                macros > 0 ? RuPlural.Of(macros, "макрос", "макроса", "макросов") : "",
                K13Layout.LedModeName(cfg.Led.Mode).ToLowerInvariant(),
            }.Where(x => x.Length > 0)),
            FontSize = 12.5, Foreground = R("MutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis,
        });

        // «Включать в программах»
        host.Children.Add(AppRulesSection(p, AccentOf(p, index)));

        // «Действия на компьютере»
        host.Children.Add(HostSection(p, active));
    }

    private FrameworkElement SectionHead(string title, string? help, params UIElement[] right)
    {
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var r = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in right) r.Children.Add(c);
        DockPanel.SetDock(r, Dock.Right);
        head.Children.Add(r);
        var t = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        t.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        if (help != null) t.Children.Add(InfoDot(help));
        head.Children.Add(t);
        return head;
    }

    private static string HostSummaryText(AppProfile p)
    {
        int n = p.Host.Keys.Count, c = p.Host.Chords.Count;
        return n + c == 0 ? "Пока ничего — запуск программ, ссылки, текст, удержание кнопок." : RuPlural.Of(n + c, "действие настроено", "действия настроены", "действий настроено") + ".";
    }

    private const string HostHelp =
        "Запуск программ и ссылок, ввод текста, действие при удержании кнопки и диагонали джойстика. " +
        "Это выполняет сама программа, поэтому работает, пока она запущена (можно свернуть в трей и включить запуск вместе с Windows).";

    private FrameworkElement HostSection(AppProfile p, bool active)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 26, 0, 0) };
        var sw = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = p.Host.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0), ToolTip = "Включить или выключить действия на компьютере для этого профиля" };
        sw.Tag = "host-enabled";
        void Set(bool v)
        {
            if (_loading || p.Host.Enabled == v) return;
            p.Host.Enabled = v;
            if (p == _profile) Changed(false); else SaveStore();
        }
        sw.Checked += (_, _) => Set(true);
        sw.Unchecked += (_, _) => Set(false);
        sp.Children.Add(SectionHead("Действия на компьютере", HostHelp, sw));
        _hostSummary = new TextBlock { Text = HostSummaryText(p), FontSize = 13, Foreground = R("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        sp.Children.Add(_hostSummary);
        var link = new Button { Style = (Style)FindResource("LinkButton"), HorizontalAlignment = HorizontalAlignment.Left, Content = active ? "Настроить…" : "Сделать активным и настроить…" };
        link.Click += (_, e) =>
        {
            e.Handled = true;
            if (p != _profile) ActivateProfile(p.Name, "вручную");
            OpenHost_Click(link, new RoutedEventArgs());
        };
        sp.Children.Add(link);
        return sp;
    }

    /// <summary>⋯ next to «Новый»: the rare whole-list actions.</summary>
    private void ProfilesMore_Click(object sender, RoutedEventArgs e) => ProfilesMenu(ProfilesMoreBtn).IsOpen = true;

    internal ContextMenu ProfilesMenu(FrameworkElement target)
    {
        var m = new ContextMenu { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Item(string glyph, string text, RoutedEventHandler click)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, Width = 26, VerticalAlignment = VerticalAlignment.Center });
            header.Children.Add(new TextBlock { Text = text });
            var mi = new MenuItem { Header = header };
            mi.Click += click;
            m.Items.Add(mi);
        }
        Item("\uE8B5", "Импорт из файла… (или перетащите файл в окно)", ImportProfileJson_Click);
        Item("\uE77F", "Вставить профиль из буфера (Ctrl+V)", PasteProfile_Click);
        Item("\uEDE1", "Экспорт всех профилей…", ExportAll_Click);
        return m;
    }

    private void CycleLook(AppProfile p, int index)
    {
        int ai = Array.IndexOf(AccentPalette, p.Accent.Length == 7 ? p.Accent : AccentPalette[index % AccentPalette.Length]);
        int gi = Array.IndexOf(GlyphPalette, GlyphOf(p, index));
        p.Accent = AccentPalette[(ai + 1) % AccentPalette.Length];
        p.Glyph = GlyphPalette[(gi + 1) % GlyphPalette.Length];
        SaveStore(); RefreshProfilesList();
    }

    private string? RenameProfile(AppProfile p, string name)
    {
        name = name.Trim();
        if (name.Length == 0) return "Название не может быть пустым.";
        if (name.Length > 40) return "Не длиннее 40 символов.";
        if (name == p.Name) return null;
        if (_store.Data.Profiles.Any(x => x != p && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))) return "Профиль с таким именем уже есть.";
        foreach (var r in _store.Data.AppRules.Where(r => r.ProfileName == p.Name)) r.ProfileName = name;
        foreach (var h in _store.Data.Profiles.SelectMany(x => x.Host.Keys.SelectMany(k => new[] { k.Tap, k.Hold }).Concat(x.Host.Chords.Select(c => c.Action)).Concat(x.Host.Ring.Slots.Select(s => s.Action))))
            if (h.Kind == HostActionKind.SwitchProfile && h.Value == p.Name) h.Value = name;
        if (_store.Data.ActiveProfile == p.Name) _store.Data.ActiveProfile = name;
        if (_store.Data.FallbackProfile == p.Name) _store.Data.FallbackProfile = name;
        if (_viewName == p.Name) _viewName = name;
        Log($"Профиль «{p.Name}» переименован в «{name}».");
        p.Name = name;
        Dispatcher.BeginInvoke(() => ActivateProfile(_profile.Name, null));
        return null;
    }

    private void ProfileBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProfileBox.SelectedItem is not string n || n == _profile.Name) return;
        Dispatcher.BeginInvoke(() => ActivateProfile(n, "вручную"));
    }

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = _store.UniqueName("Новый профиль");
        _store.Data.Profiles.Add(new AppProfile { Name = name, Device = K13MemoryCodec.Decode(_deviceImage ?? DefaultImage), Accent = AccentPalette[_store.Data.Profiles.Count % AccentPalette.Length] });
        ProfileMigrations.EnsureStickRing(_store.Data.Profiles[^1]);
        ActivateProfile(name, "создан");
        StatusText.Text = "Профиль создан — щёлкните по названию, чтобы переименовать.";
    }

    private void DuplicateProfile(AppProfile src)
    {
        var copy = src.Clone(_store.UniqueName(src.Name + " (копия)"));
        copy.Accent = "";
        _store.Data.Profiles.Add(copy);
        ActivateProfile(copy.Name, "копия");
    }

    private void DeleteProfile(AppProfile p)
    {
        if (_store.Data.Profiles.Count <= 1) { MessageBox.Show(this, "Нельзя удалить последний профиль.", Title); return; }
        int rules = _store.Data.AppRules.Count(r => r.ProfileName == p.Name);
        if (MessageBox.Show(this, $"Удалить профиль «{p.Name}»?" + (rules > 0 ? $"\n\nПравила автопереключения на него ({rules}) тоже удалятся." : "") + "\n\nКлавиатура не изменится, пока вы не нажмёте «Записать».",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _store.Data.Profiles.Remove(p);
        _store.Data.AppRules.RemoveAll(r => r.ProfileName == p.Name);
        if (string.Equals(_store.Data.FallbackProfile, p.Name, StringComparison.OrdinalIgnoreCase)) _store.Data.FallbackProfile = "";
        if (_viewName == p.Name) _viewName = null;
        ActivateProfile(p == _profile ? _store.Data.Profiles[0].Name : _profile.Name, null);
    }

    /// <summary>Screenshots only (offline store): demo profiles, rules and named macros.</summary>
    internal void DemoV47(int mouseMacro)
    {
        if (!_offline) return;
        _profile.MacroNames[mouseMacro] = "Ctrl+клик и прокрутка";
        int t = FreeMacroSlot();
        if (t >= 0)
        {
            Cfg.Macros[t].Events = TextToEvents("Добрый день!").Events;
            _profile.MacroNames[t] = "Приветствие";
            Cfg.SetKey(6, (byte)(KeyCatalog.FirstMacro + t));
        }
        _profile.Accent = AccentPalette[0];
        var games = _profile.Clone("Игры"); games.Accent = AccentPalette[1]; games.Glyph = GlyphPalette[1]; games.MacroNames.Clear();
        var photo = _profile.Clone("Фото"); photo.Accent = AccentPalette[2]; photo.Glyph = GlyphPalette[2];
        _store.Data.Profiles.Add(games); _store.Data.Profiles.Add(photo);
        _store.Data.AppRules.Add(new AppSwitchRule { ProcessName = "photoshop", ProfileName = "Фото" });
        _store.Data.AppRules.Add(new AppSwitchRule { ProcessName = "cs2", TitleContains = "", ProfileName = "Игры" });
        _store.Data.AutoSwitch = true;
        _selMacro = mouseMacro;
        RefreshAll();
    }
}
