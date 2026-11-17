using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Vorotex.K13;

namespace Vorotex.Studio;

/// <summary>v5.4: export/import of profiles (.k13profile file, text in the clipboard, drag and drop onto the window)
/// with a preview before anything is changed.</summary>
public partial class MainWindow
{
    private static string AppVersion => typeof(App).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}" : "";

    // ───────────────────────────── export
    private void ExportAll_Click(object sender, RoutedEventArgs e) => ExportProfiles(_store.Data.Profiles);

    private void ExportProfiles(IEnumerable<AppProfile> selected) => BuildExportDialog(selected).ShowDialog();

    internal Window BuildExportDialog(IEnumerable<AppProfile> selected)
    {
        var sel = new HashSet<AppProfile>(selected);
        var w = Ui.MakeWindow(IsLoaded ? this : null, "Экспорт профилей", 600, 580);
        w.MinWidth = 520; w.MinHeight = 480;
        var root = new DockPanel { Margin = new Thickness(24, 12, 24, 20) };
        var top = new StackPanel();
        top.Children.Add(T("Экспорт профилей", 18, semi: true, bottom: 4));
        top.Children.Add(T("Файл .k13profile можно отправить другу или открыть на другом компьютере: «Импорт», перетаскивание в окно или «Вставить», если скопировать как текст.", 13, muted: true, bottom: 12));
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var bottom = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        var withRules = new CheckBox { Style = (Style)FindResource("Switch"), Content = "Вместе со списками «Включать в программах»", IsChecked = true, Margin = new Thickness(0, 0, 0, 6) };
        var strip = new CheckBox { Style = (Style)FindResource("Switch"), Content = "Убрать пути этого компьютера (папка пользователя → %USERPROFILE%)", IsChecked = true };
        bottom.Children.Add(withRules); bottom.Children.Add(strip);
        var notes = T("", 12, muted: true); notes.Margin = new Thickness(0, 10, 0, 0);
        bottom.Children.Add(notes);
        var bar = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        var copy = new Button { Style = (Style)FindResource("GhostButton"), Padding = new Thickness(14, 7, 14, 7),
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children =
            {
                new TextBlock { Text = "\uE8C8", FontFamily = Icons, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) },
                new TextBlock { Text = "Скопировать как текст" },
            } } };
        DockPanel.SetDock(copy, Dock.Left);
        bar.Children.Add(copy);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = new Button { Content = "Сохранить в файл…", IsDefault = true, Style = (Style)FindResource("AccentButton"), Padding = new Thickness(16, 7, 16, 7) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(16, 7, 16, 7), MinWidth = 96 };
        right.Children.Add(save); right.Children.Add(cancel);
        bar.Children.Add(right);
        bottom.Children.Add(bar);
        root.Children.Add(bottom);

        var list = new StackPanel();
        var profiles = _store.Data.Profiles;
        for (int i = 0; i < profiles.Count; i++)
        {
            var p = profiles[i]; int idx = i;
            var cfg = p.Device ?? K13MemoryCodec.Decode(DefaultImage);
            var row = new DockPanel();
            var cb = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = sel.Contains(p), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            row.Children.Add(cb);
            row.Children.Add(new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(9), Background = AccentOf(p, idx), Margin = new Thickness(0, 0, 12, 0),
                Child = new TextBlock { Text = GlyphOf(p, idx), FontFamily = Icons, FontSize = 13, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
            var tx = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            tx.Children.Add(new TextBlock { Text = p.Name, FontSize = 14, FontWeight = FontWeights.SemiBold });
            var rules = RulesOf(p);
            tx.Children.Add(new TextBlock { FontSize = 12, Foreground = R("MutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis,
                Text = $"кнопок изменено: {K13Layout.Controls.Count(c => cfg.GetKey(c.Column) != c.FactoryCode)} · {RuPlural.Of(cfg.Macros.Count(m => m.IsUsed), "макрос", "макроса", "макросов")}" +
                       (rules.Count > 0 ? " · " + string.Join(", ", rules.Select(AppMatch.Label)) : "") });
            row.Children.Add(tx);
            var b = new Border { Child = row, Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand };
            b.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not CheckBox) cb.IsChecked = cb.IsChecked != true; };
            cb.Checked += (_, _) => { sel.Add(p); Update(); };
            cb.Unchecked += (_, _) => { sel.Remove(p); Update(); };
            list.Children.Add(b);
        }
        root.Children.Add(new Border { Child = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, BorderBrush = R("BorderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(6) });

        ExportResult Make() => ProfileExchange.Export(profiles.Where(sel.Contains).ToList(), _store.Data.AppRules, AppVersion, withRules.IsChecked == true, strip.IsChecked == true);
        void Update()
        {
            save.IsEnabled = copy.IsEnabled = sel.Count > 0;
            if (sel.Count == 0) { notes.Text = "Отметьте хотя бы один профиль."; return; }
            var r = Make();
            notes.Text = $"{RuPlural.Of(sel.Count, "профиль", "профиля", "профилей")}, {Math.Max(1, Encoding.UTF8.GetByteCount(r.Json) / 1024)} КБ." + (r.Notes.Count > 0 ? "\n" + string.Join("\n", r.Notes.Take(4).Select(n => "• " + n)) : "");
        }
        withRules.Checked += (_, _) => Update(); withRules.Unchecked += (_, _) => Update();
        strip.Checked += (_, _) => Update(); strip.Unchecked += (_, _) => Update();
        save.Click += (_, _) =>
        {
            var chosen = profiles.Where(sel.Contains).ToList();
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Сохранить профили", Filter = "Профиль VOROTEX K13 (*.k13profile)|*.k13profile", DefaultExt = ProfileExchange.Extension, AddExtension = true,
                FileName = (chosen.Count == 1 ? ProfileExchange.SafeFileName(chosen[0].Name) : "Профили VOROTEX K13") + ProfileExchange.Extension,
            };
            if (dlg.ShowDialog(w) != true) return;
            try
            {
                File.WriteAllText(dlg.FileName, Make().Json, new UTF8Encoding(false));
                Log($"Экспорт: {string.Join(", ", chosen.Select(c => "«" + c.Name + "»"))} → {dlg.FileName}");
                StatusText.Text = "Профили сохранены: " + dlg.FileName;
                w.DialogResult = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { notes.Text = "Не удалось сохранить: " + FriendlyError.Text(ex); }
        };
        copy.Click += (_, _) =>
        {
            if (TrySetClipboard(Make().Json))
            {
                StatusText.Text = "Профили скопированы как текст — вставьте их в сообщение, а на другом компьютере нажмите «Вставить» на вкладке «Профили».";
                Log("Экспорт: скопировано в буфер обмена.");
                w.DialogResult = true;
            }
            else notes.Text = "Буфер обмена занят другой программой — попробуйте ещё раз.";
        };
        Update();
        w.Content = root;
        return w;
    }

    private static bool TrySetClipboard(string text)
    {
        for (int i = 0; i < 5; i++)
        {
            try { Clipboard.SetText(text); return true; }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(60); }
        }
        return false;
    }

    // ───────────────────────────── import
    private void ImportProfileJson_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Импорт профилей", Filter = "Профили VOROTEX K13 (*.k13profile; *.json)|*.k13profile;*.json|Все файлы|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        ImportFromFile(dlg.FileName);
    }

    private void PasteProfile_Click(object sender, RoutedEventArgs e) => ImportFromClipboard();

    private void ImportFromClipboard()
    {
        string text = "";
        try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); }
        catch (System.Runtime.InteropServices.COMException) { }
        if (text.Length == 0)
        {
            MessageBox.Show(this, "В буфере обмена нет текста. Скопируйте профиль кнопкой «Экспорт → Скопировать как текст» и попробуйте снова.", "Вставить профиль", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ImportPreviewed(() => ProfileExchange.Read(text, _deviceImage ?? DefaultImage, _store.Data.Profiles.Select(p => p.Name), "буфер обмена"));
    }

    internal void ImportFromFile(string path) =>
        ImportPreviewed(() => ProfileExchange.ReadFile(path, _deviceImage ?? DefaultImage, _store.Data.Profiles.Select(p => p.Name)));

    private void ImportPreviewed(Func<ImportPreview> read)
    {
        ImportPreview pv;
        try { pv = read(); }
        catch (ProfileImportException ex)
        {
            Log("Импорт отклонён: " + ex.Message);
            MessageBox.Show(this, "Этот профиль нельзя импортировать.\n\n" + FriendlyError.Text(ex) + "\n\nНичего не изменено.", "Импорт профилей", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        catch (Exception ex)
        {
            App.Log("import: " + ex);
            MessageBox.Show(this, "Не удалось прочитать профиль: " + FriendlyError.Text(ex) + "\n\nНичего не изменено.", "Импорт профилей", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var dlg = BuildImportDialog(pv, out var answer);
        dlg.ShowDialog();
        if (answer() is not { } mode) return;
        ApplyImport(pv, mode);
    }

    internal List<string> ApplyImport(ImportPreview pv, ImportConflict mode)
    {
        var names = ProfileExchange.Apply(pv, _store.Data, mode);
        var tmp = new ProfileStoreData();
        tmp.Profiles.AddRange(_store.Data.Profiles.Where(p => names.Contains(p.Name)));
        var unsafeNotes = TriggerKeyPolicy.DropUnsafe(tmp, KeyCombo.VkName);
        foreach (var p in tmp.Profiles) ProfileMigrations.EnsureStickRing(p, setStickKey: false);
        _store.Data.Normalize();
        SaveStore();
        _fg.Engine.Reset();
        Log($"Импорт из {pv.Source}: {string.Join(", ", names.Select(n => "«" + n + "»"))}" + (mode == ImportConflict.Replace ? " (с заменой)" : ""));
        if (names.Count == 1 && names[0] != _profile.Name) ActivateProfile(names[0], "импорт", "импортирован");
        else ActivateProfile(_profile.Name, null);
        StatusText.Text = $"Импортировано: {RuPlural.Of(names.Count, "профиль", "профиля", "профилей")}. Кнопки и подсветка попадут в клавиатуру после «Записать».";
        if (unsafeNotes.Count > 0) MessageBox.Show(this, string.Join("\n\n", unsafeNotes), "Импорт профилей", MessageBoxButton.OK, MessageBoxImage.Warning);
        return names;
    }

    /// <summary>Preview: what is inside, conflicts (replace / keep both), warnings. answer() = null when cancelled.</summary>
    internal Window BuildImportDialog(ImportPreview pv, out Func<ImportConflict?> answer)
    {
        ImportConflict? result = null;
        answer = () => result;
        var w = Ui.MakeWindow(IsLoaded ? this : null, "Импорт профилей", 640, 740);
        w.MinWidth = 540; w.MinHeight = 480;
        var root = new DockPanel { Margin = new Thickness(24, 12, 24, 20) };
        var top = new StackPanel();
        top.Children.Add(T(pv.Items.Count == 1 ? "Импорт профиля" : $"Импорт: {RuPlural.Of(pv.Items.Count, "профиль", "профиля", "профилей")}", 18, semi: true, bottom: 4));
        top.Children.Add(T($"Из «{pv.Source}»" + (ShowTech ? (pv.FormatVersion > 0 ? $" · формат {pv.FormatVersion}" : " · файл прежних версий") : "") + ". До «Импортировать» ничего не меняется.", 13, muted: true, bottom: 12));
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var bottom = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        int conflicts = pv.Items.Count(i => i.Conflicts);
        RadioButton? keepBoth = null;
        if (conflicts > 0)
        {
            var c = pv.Items.First(i => i.Conflicts).Profile.Name;
            bottom.Children.Add(T(conflicts == 1 ? $"Профиль «{c}» уже есть" : $"Уже есть профили с такими именами: {conflicts}", 13, semi: true, bottom: 6));
            keepBoth = new RadioButton { Content = conflicts == 1 ? $"Сохранить оба — новый будет «{NextFree(c)}»" : "Сохранить оба — новые получат номер в имени", IsChecked = true, GroupName = "imp", Margin = new Thickness(0, 0, 0, 4) };
            var replace = new RadioButton { Content = conflicts == 1 ? $"Заменить существующий «{c}» (и его программы)" : "Заменить существующие (и их программы)", GroupName = "imp" };
            bottom.Children.Add(keepBoth); bottom.Children.Add(replace);
        }
        bottom.Children.Add(T("Кнопки и подсветка попадут в клавиатуру только после «Записать»; действия на ПК начнут работать сразу.", 12, muted: true).With(t => t.Margin = new Thickness(0, 10, 0, 0)));
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var ok = new Button { Content = "Импортировать", IsDefault = true, Style = (Style)FindResource("AccentButton"), Padding = new Thickness(16, 7, 16, 7), MinWidth = 120 };
        var cancel = new Button { Content = "Отмена", IsCancel = true, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(16, 7, 16, 7), MinWidth = 96 };
        bar.Children.Add(ok); bar.Children.Add(cancel);
        bottom.Children.Add(bar);
        root.Children.Add(bottom);
        ok.Click += (_, _) => { result = keepBoth == null || keepBoth.IsChecked == true ? ImportConflict.KeepBoth : ImportConflict.Replace; w.DialogResult = true; };

        var list = new StackPanel();
        for (int i = 0; i < pv.Items.Count; i++)
        {
            var it = pv.Items[i]; var p = it.Profile;
            var card = new StackPanel();
            var head = new DockPanel();
            if (it.Conflicts)
            {
                var badge = Pill("\uE7BA", "имя уже есть", false);
                badge.VerticalAlignment = VerticalAlignment.Top;
                DockPanel.SetDock(badge, Dock.Right); head.Children.Add(badge);
            }
            int idx = _store.Data.Profiles.Count + i;
            head.Children.Add(new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(11), Background = AccentOf(p, idx), Margin = new Thickness(0, 0, 12, 0),
                Child = new TextBlock { Text = GlyphOf(p, idx), FontFamily = Icons, FontSize = 15, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
            var nm = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            nm.Children.Add(new TextBlock { Text = p.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            if (p.Comment.Length > 0) nm.Children.Add(new TextBlock { Text = p.Comment, FontSize = 12, Foreground = R("MutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
            head.Children.Add(nm);
            card.Children.Add(head);
            var facts = new WrapPanel { Margin = new Thickness(48, 8, 0, 0) };
            facts.Children.Add(Pill("\uE765", $"кнопок изменено: {it.KeysChanged}", false));
            facts.Children.Add(Pill("\uE768", $"макросов: {it.Macros}", false));
            facts.Children.Add(Pill("\uE7F4", $"действий на ПК: {it.PcActions}", false));
            if (p.Device != null) facts.Children.Add(Pill("\uE781", K13Layout.LedModeName(p.Device.Led.Mode), false));
            card.Children.Add(facts);
            if (it.Rules.Count > 0)
            {
                var rw = new WrapPanel { Margin = new Thickness(48, 4, 0, 0) };
                rw.Children.Add(new TextBlock { Text = "Включать в: ", FontSize = 12, Foreground = R("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 6) });
                foreach (var r in it.Rules)
                {
                    var chip = new StackPanel { Orientation = Orientation.Horizontal };
                    chip.Children.Add(AppIcon(r.ProcessName, null, 16, r.DisplayName).With(x => x.Margin = new Thickness(0, 0, 6, 0)));
                    chip.Children.Add(new TextBlock { Text = AppMatch.Label(r), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
                    if (r.TitleContains.Length > 0 && (r.DisplayName.Length > 0 || r.ProcessName.Length > 0))
                        chip.Children.Add(new TextBlock { Text = $" · «{r.TitleContains}»", FontSize = 12, Foreground = R("MutedBrush"), VerticalAlignment = VerticalAlignment.Center });
                    rw.Children.Add(new Border { Child = chip, Background = R("Panel2Brush"), CornerRadius = new CornerRadius(9), Padding = new Thickness(6, 3, 8, 3), Margin = new Thickness(0, 0, 6, 6), ToolTip = AppMatch.Describe(r) });
                }
                card.Children.Add(rw);
            }
            if (it.Launches.Count > 0)
            {
                var lw = new DockPanel { Margin = new Thickness(48, 4, 0, 0) };
                lw.Children.Add(new TextBlock { Text = "\uE7BA", FontFamily = Icons, FontSize = 12, Foreground = R("WarnBrush"), Margin = new Thickness(0, 2, 6, 0), VerticalAlignment = VerticalAlignment.Top });
                lw.Children.Add(new TextBlock { Text = "Запускает: " + string.Join(", ", it.Launches.Take(4)) + (it.Launches.Count > 4 ? $" и ещё {it.Launches.Count - 4}" : ""), FontSize = 12, Foreground = R("WarnBrush"), TextWrapping = TextWrapping.Wrap });
                card.Children.Add(lw);
            }
            list.Children.Add(new Border { Child = card, Background = R("PanelBrush"), BorderBrush = it.Conflicts ? R("WarnBrush") : R("BorderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 0, 8) });
        }
        if (pv.Warnings.Count > 0)
        {
            var ws = new StackPanel();
            foreach (var x in pv.Warnings) ws.Children.Add(T("• " + x, 12.5).With(t => t.Margin = new Thickness(0, 0, 0, 3)));
            list.Children.Add(new Border { Child = ws, Background = R("Panel2Brush"), CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 10, 12, 8), Margin = new Thickness(0, 4, 0, 0) });
        }
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        w.Content = root;
        return w;
    }

    private string NextFree(string name)
    {
        var b = name.Length > 34 ? name[..34].TrimEnd() : name;
        for (int k = 2; ; k++) if (_store.Find($"{b} ({k})") == null) return $"{b} ({k})";
    }

    // ───────────────────────────── drag and drop / Ctrl+V
    private static bool IsProfileFile(string path) =>
        path.EndsWith(ProfileExchange.Extension, StringComparison.OrdinalIgnoreCase) || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    private string? DropFile(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } f && IsProfileFile(f[0]) ? f[0] : null;

    private bool DropText(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.UnicodeText) && e.Data.GetData(DataFormats.UnicodeText) is string s && s.TrimStart().StartsWith('{');

    private void WireDragDrop()
    {
        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += (_, _) => DropOverlay.Visibility = Visibility.Collapsed;
        Drop += (_, e) =>
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            if (_busy || Welcome.Visibility == Visibility.Visible) return;
            if (DropFile(e) is { } f) { e.Handled = true; Dispatcher.BeginInvoke(() => ImportFromFile(f)); }
            else if (DropText(e) && e.Data.GetData(DataFormats.UnicodeText) is string s)
            {
                e.Handled = true;
                Dispatcher.BeginInvoke(() => ImportPreviewed(() => ProfileExchange.Read(s, _deviceImage ?? DefaultImage, _store.Data.Profiles.Select(p => p.Name), "перетащенный текст")));
            }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && PageProfiles.Visibility == Visibility.Visible && Keyboard.FocusedElement is not TextBox && !_recording)
            { e.Handled = true; ImportFromClipboard(); }
        };
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool ok = !_busy && (DropFile(e) != null || DropText(e));
        if (!ok && e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            // something else: say what is accepted
            DropTitle.Text = "Сюда можно перетащить файл профиля .k13profile";
            DropHint.Text = "Этот файл не подходит";
            DropOverlay.Visibility = Visibility.Visible;
            e.Effects = DragDropEffects.None; e.Handled = true;
            return;
        }
        DropTitle.Text = "Отпустите, чтобы импортировать профиль";
        DropHint.Text = "Сначала покажем, что внутри — ничего не изменится без вашего подтверждения";
        DropOverlay.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Screenshots: the drop overlay as it looks while dragging a file over the window.</summary>
    internal void ShowDropOverlay(bool show) => DropOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
}

public partial class MainWindow
{
    internal AppProfile? ProfileByName(string n) => _store.Find(n);
    internal List<AppProfile> AllProfiles() => _store.Data.Profiles.ToList();

    internal void ScrollProfiles(double y)
    {
        if (PageProfiles.Children.Count > 0 && PageProfiles.Children[0] is ScrollViewer sv) { sv.UpdateLayout(); sv.ScrollToVerticalOffset(y); }
    }

    /// <summary>Screenshots/smoke test: a package from a friend — «Фото» (conflicts with ours) and «Стрим» that opens a link.</summary>
    internal ImportPreview DemoImportPreview()
    {
        var foto = (_store.Find("Фото") ?? _profile).Clone("Фото");
        foto.Comment = "Кисти на колесе, Ctrl+Z на стике";
        var stream = _profile.Clone("Стрим");
        stream.Accent = AccentPalette[3]; stream.Glyph = GlyphPalette[5];
        stream.Host.Keys.Add(new HostKeyBinding { TriggerVk = 0x86, Tap = new HostAction { Kind = HostActionKind.OpenUrl, Value = "https://dashboard.twitch.tv" } });
        stream.Host.Keys.Add(new HostKeyBinding { TriggerVk = 0x87, Tap = new HostAction { Kind = HostActionKind.LaunchProgram, Value = @"%USERPROFILE%\AppData\Local\obs-studio\obs64.exe" } });
        var rules = new[]
        {
            new AppSwitchRule { ProcessName = "Photoshop", ProfileName = "Фото", DisplayName = "Adobe Photoshop 2025" },
            new AppSwitchRule { ProcessName = "obs64", ProfileName = "Стрим", DisplayName = "OBS Studio" },
            new AppSwitchRule { ProcessName = "chrome", TitleContains = "Twitch", ProfileName = "Стрим", DisplayName = "Google Chrome" },
        };
        var json = ProfileExchange.Export(new[] { foto, stream }, rules, AppVersion).Json;
        return ProfileExchange.Read(json, DefaultImage, _store.Data.Profiles.Select(p => p.Name), "Профили от Миши.k13profile");
    }
}
