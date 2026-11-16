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
        BuildAutoSwitchCard();
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
        int ruleCount = RulesOf(p).Count;
        bool isFallback = string.Equals(_store.Data.FallbackProfile, p.Name, StringComparison.OrdinalIgnoreCase);
        titles.Children.Add(new TextBlock { Text = p.Comment.Length > 0 ? p.Comment : ruleCount > 0 ? "включается сам — " + RuPlural.Of(ruleCount, "программа", "программы", "программ") + (isFallback ? " · по умолчанию" : "")
                                                 : isFallback ? "по умолчанию, когда правило не подошло" : "включается вручную",
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
        // v5.6: one muted line instead of four pills
        var factText = string.Join("  ·  ", new[]
        {
            changed > 0 ? RuPlural.Of(changed, "кнопка изменена", "кнопки изменены", "кнопок изменено") : "кнопки как с завода",
            macros > 0 ? RuPlural.Of(macros, "макрос", "макроса", "макросов") : "",
            host > 0 ? RuPlural.Of(host, "действие на ПК", "действия на ПК", "действий на ПК") : "",
        }.Where(x => x.Length > 0));
        var facts = new TextBlock { Text = factText, FontSize = 12, Foreground = R("MutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 0, 0, 14),
            ToolTip = $"Кнопок изменено: {changed}\nМакросов: {macros}\nДействий на ПК: {host}\nПодсветка: {K13Layout.LedModeName(cfg.Led.Mode)}" };
        root.Children.Add(facts);

        // v5.4: «Включать в программах»
        root.Children.Add(AppRulesSection(p, accent));

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
        right.Children.Add(Icon("\uEDE1", "Экспорт в файл .k13profile или как текст", () => ExportProfiles(new[] { p })));
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
        if (_store.Data.FallbackProfile == p.Name) _store.Data.FallbackProfile = name;
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
