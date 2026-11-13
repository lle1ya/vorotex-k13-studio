using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Vorotex.K13;

namespace Vorotex.Studio;

/// <summary>"Профили" page (v4.7): profile cards with a mini K13 preview, inline rename, quick actions,
/// JSON export/import and auto-switch rules as friendly rows (no tables).</summary>
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

    private void RefreshProfilesList()
    {
        ProfileCards.Children.Clear();
        var list = _store.Data.Profiles;
        for (int i = 0; i < list.Count; i++) ProfileCards.Children.Add(ProfileCard(list[i], i));
        BuildRules();
    }

    private Border ProfileCard(AppProfile p, int index)
    {
        bool active = p == _profile;
        var accent = AccentOf(p, index);
        var root = new StackPanel();

        // head: icon · name (inline edit) · badge
        var head = new DockPanel();
        if (active)
        {
            var badge = new Border { CornerRadius = new CornerRadius(10), Background = accent, Padding = new Thickness(9, 2, 9, 3), VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = "активный", FontSize = 11, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold } };
            DockPanel.SetDock(badge, Dock.Right);
            head.Children.Add(badge);
        }
        var icon = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(12), Background = accent, Margin = new Thickness(0, 0, 12, 0), Cursor = Cursors.Hand, ToolTip = "Сменить цвет и значок",
            Child = new TextBlock { Text = GlyphOf(p, index), FontFamily = Icons, FontSize = 16, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        icon.MouseLeftButtonUp += (_, e) => { e.Handled = true; CycleLook(p, index); };
        head.Children.Add(icon);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(EditableTitle(p.Name, 16, n => RenameProfile(p, n), "Переименовать профиль"));
        var rules = _store.Data.AppRules.Where(r => r.ProfileName == p.Name && r.ProcessName.Length > 0).Select(r => r.ProcessName).Distinct().ToList();
        titles.Children.Add(new TextBlock { Text = rules.Count > 0 ? "авто: " + string.Join(", ", rules.Select(x => x + ".exe")) : (p.Comment.Length > 0 ? p.Comment : "включается вручную"),
            FontSize = 12, Foreground = R("MutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(0, 1, 0, 0) });
        head.Children.Add(titles);
        root.Children.Add(head);

        // mini keypad preview
        var cfg = p.Device ?? K13MemoryCodec.Decode(DefaultImage);
        var dv = new DeviceView { Height = 128, IsHitTestVisible = false, Margin = new Thickness(0, 14, 0, 10) };
        try { dv.Rebuild(cfg, p.Host, -1, showDots: true); } catch { }
        var prevBox = new Border { Background = R("Panel2Brush"), CornerRadius = new CornerRadius(12), Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(0, 12, 0, 10), Child = dv };
        dv.Margin = new Thickness(0, 8, 0, 8);
        root.Children.Add(prevBox);

        // facts
        int changed = K13Layout.Controls.Count(c => cfg.GetKey(c.Column) != c.FactoryCode);
        int macros = Enumerable.Range(0, KeyCatalog.MacroCodeCount).Count(i => cfg.Macros[i].IsUsed);
        int host = p.Host.Keys.Count(k => k.Tap.Kind != HostActionKind.None) + p.Host.Chords.Count;
        var facts = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        facts.Children.Add(Pill("\uE765", $"кнопок изменено: {changed}", false));
        facts.Children.Add(Pill("\uE768", $"макросов: {macros}", false));
        facts.Children.Add(Pill("\uE7F4", $"действий на ПК: {host}", false));
        facts.Children.Add(Pill("\uE781", K13Layout.LedModeName(cfg.Led.Mode), false));
        root.Children.Add(facts);

        // actions
        var acts = new DockPanel();
        Button Icon(string glyph, string tip, Action a)
        {
            var b = new Button { Style = (Style)FindResource("GhostButton"), Padding = new Thickness(9, 6, 9, 6), Margin = new Thickness(4, 0, 0, 0), ToolTip = tip,
                Content = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 13 } };
            b.Click += (_, e) => { e.Handled = true; a(); };
            return b;
        }
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(Icon("\uE8C8", "Дублировать", () => DuplicateProfile(p)));
        right.Children.Add(Icon("\uEDE1", "Экспорт в файл .json", () => ExportProfile(p)));
        right.Children.Add(Icon("\uE74D", "Удалить", () => DeleteProfile(p)));
        DockPanel.SetDock(right, Dock.Right);
        acts.Children.Add(right);
        if (active)
            acts.Children.Add(new TextBlock { Text = "✓  Сейчас используется", Foreground = accent, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        else
        {
            var on = new Button { Content = "Сделать активным", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 6, 14, 6) };
            on.Click += (_, e) => { e.Handled = true; ActivateProfile(p.Name, "вручную"); };
            acts.Children.Add(on);
        }
        root.Children.Add(acts);

        var card = new Border
        {
            Child = new Grid { Children = { root } }, Width = 372, Padding = new Thickness(18, 16, 18, 16), Margin = new Thickness(0, 0, 14, 14), CornerRadius = new CornerRadius(18),
            Background = R("PanelBrush"), BorderBrush = active ? accent : R("BorderBrush"), BorderThickness = new Thickness(active ? 2 : 1),
        };
        // coloured accent strip at the top edge
        var strip = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = accent, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -10, 0, 0), Opacity = active ? 1 : 0.55 };
        ((Grid)card.Child).Children.Insert(0, strip);
        card.MouseLeftButtonUp += (_, e) => { if (e.ClickCount == 1 && !active && e.OriginalSource is not TextBox) { } };
        card.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2 && !active && e.OriginalSource is not TextBox) ActivateProfile(p.Name, "вручную"); };
        return card;
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
        ActivateProfile(p == _profile ? _store.Data.Profiles[0].Name : _profile.Name, null);
    }

    private void ExportProfile(AppProfile p)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "Профиль VOROTEX K13|*.json", FileName = string.Concat(p.Name.Split(Path.GetInvalidFileNameChars())) + ".k13profile.json" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(p, ProfileStore.JsonOptions));
            StatusText.Text = "Профиль сохранён: " + dlg.FileName;
        }
        catch (Exception ex) { MessageBox.Show(this, "Не удалось сохранить: " + ex.Message, Title); }
    }

    private void ImportProfileJson_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Профиль VOROTEX K13|*.json|Все файлы|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var p = JsonSerializer.Deserialize<AppProfile>(File.ReadAllText(dlg.FileName), ProfileStore.JsonOptions) ?? throw new InvalidDataException("пустой файл");
            p.Device ??= K13MemoryCodec.Decode(DefaultImage);
            K13MemoryCodec.Validate(p.Device);
            p.Name = _store.UniqueName(string.IsNullOrWhiteSpace(p.Name) ? "Импорт" : p.Name.Trim());
            var tmp = new ProfileStoreData(); tmp.Profiles.Add(p);
            var notes = TriggerKeyPolicy.DropUnsafe(tmp, KeyCombo.VkName);
            _store.Data.Profiles.Add(p);
            ActivateProfile(p.Name, "импорт");
            if (notes.Count > 0) MessageBox.Show(this, string.Join("\n\n", notes), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { MessageBox.Show(this, "Не удалось импортировать профиль: " + ex.Message, Title); }
    }

    // ───────────────────────────── auto-switch rules (rows, not a table)
    private void BuildRules()
    {
        RulesHost.Children.Clear();
        var names = _store.Data.Profiles.Select(p => p.Name).ToList();
        if (_store.Data.AppRules.Count == 0)
            RulesHost.Children.Add(T("Правил пока нет. Добавьте программу — например, photoshop → «Фото».", 13, muted: true, bottom: 4));
        foreach (var r in _store.Data.AppRules.ToList())
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var ic = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(8), Background = R("PanelBrush"), BorderBrush = R("BorderBrush"), BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = "\uE7C4", FontFamily = Icons, FontSize = 12, Foreground = R("MutedBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            row.Children.Add(ic);
            var exe = Placeholder(r.ProcessName, "программа, например photoshop", v => { r.ProcessName = v.Trim().EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? v.Trim()[..^4] : v.Trim(); SaveStore(); });
            exe.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(exe, 1); row.Children.Add(exe);
            var title = Placeholder(r.TitleContains, "в заголовке окна (необязательно)", v => { r.TitleContains = v.Trim(); SaveStore(); });
            Grid.SetColumn(title, 2); row.Children.Add(title);
            var arrow = new TextBlock { Text = "\uE72A", FontFamily = Icons, FontSize = 12, Foreground = R("MutedBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(arrow, 3); row.Children.Add(arrow);
            var prof = new ComboBox { ItemsSource = names, SelectedItem = names.Contains(r.ProfileName) ? r.ProfileName : null, VerticalAlignment = VerticalAlignment.Center };
            prof.SelectionChanged += (_, _) => { if (prof.SelectedItem is string n) { r.ProfileName = n; SaveStore(); RefreshProfilesListLater(); } };
            Grid.SetColumn(prof, 4); row.Children.Add(prof);
            var del = new Button { Style = (Style)FindResource("GhostButton"), Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Удалить правило",
                Content = new TextBlock { Text = "\uE74D", FontFamily = Icons, FontSize = 12, Foreground = R("MutedBrush") } };
            del.Click += (_, _) => { _store.Data.AppRules.Remove(r); SaveStore(); RefreshProfilesList(); };
            Grid.SetColumn(del, 5); row.Children.Add(del);
            RulesHost.Children.Add(new Border { Child = row, Background = R("Panel2Brush"), CornerRadius = new CornerRadius(12), Padding = new Thickness(8, 7, 8, 7), Margin = new Thickness(0, 0, 0, 6) });
        }
    }

    private void RefreshProfilesListLater() => Dispatcher.BeginInvoke(RefreshProfilesList, System.Windows.Threading.DispatcherPriority.Background);

    private Grid Placeholder(string value, string hint, Action<string> commit)
    {
        var g = new Grid { VerticalAlignment = VerticalAlignment.Center };
        var box = new TextBox { Text = value, Padding = new Thickness(8, 4, 8, 4) };
        var ph = new TextBlock { Text = hint, Foreground = R("MutedBrush"), Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, FontSize = 13,
            Visibility = value.Length == 0 ? Visibility.Visible : Visibility.Collapsed, TextTrimming = TextTrimming.CharacterEllipsis };
        box.TextChanged += (_, _) => ph.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        string orig = value;
        box.LostKeyboardFocus += (_, _) => { if (box.Text != orig) { orig = box.Text; commit(box.Text); RefreshProfilesListLater(); } };
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Keyboard.ClearFocus(); } };
        g.Children.Add(box); g.Children.Add(ph);
        return g;
    }

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        var (proc, _) = _fg.Current;
        var r = new AppSwitchRule { ProcessName = proc is "" or "VorotexK13Studio" or "explorer" ? "" : proc, ProfileName = _profile.Name };
        _store.Data.AppRules.Add(r); SaveStore(); RefreshProfilesList();
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
