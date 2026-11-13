using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Vorotex.K13;

namespace Vorotex.Studio;

/// <summary>
/// "Макросы" page (v4.7): searchable card list on the left, visual step editor on the right
/// (rows with icons, inline delays, drag to reorder, add-step bar by type, recorder, test run, duplicate, rename).
/// </summary>
public partial class MainWindow
{
    private const int MacroMemory = K13Layout.MacroAreaLimit - 0x240;
    private const int ShowPauseFromMs = 24;
    private int _pendingPauseMs;
    private string _macroFilter = "";

    /// <summary>User name of a macro slot (stored in profiles.json), or «Макрос N».</summary>
    internal string MacroName(int slot) =>
        _profile.MacroNames.TryGetValue(slot, out var n) && !string.IsNullOrWhiteSpace(n) ? n : $"Макрос {slot + 1}";

    private string? RenameMacro(int slot, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name == $"Макрос {slot + 1}") { _profile.MacroNames.Remove(slot); }
        else
        {
            if (name.Length > 40) return "Не длиннее 40 символов.";
            _profile.MacroNames[slot] = name;
        }
        foreach (var r in _profile.Host.Ring.Slots.Where(r => r.Action.Kind == HostActionKind.PlayMacro && r.Action.Value == slot.ToString())) r.Title = MacroName(slot);
        Log($"Макрос {slot + 1} теперь называется «{MacroName(slot)}».");
        Changed(); RefreshMacros();
        return null;
    }

    private static string KeyWord(byte code) => Labels.KeyText(code);

    private static string StepText(MacroEvent e) => e.IsWheel ? Labels.EventText(e) : (e.Down ? (e.IsMouseButton ? "Нажать " : "Зажать ") : "Отпустить ") + Labels.EventText(e);

    private List<string> AssignedTo(int slot)
    {
        byte code = (byte)(KeyCatalog.FirstMacro + slot);
        return K13Layout.Controls.Where(c => Cfg.GetKey(c.Column) == code).Select(c => DeviceView.ControlName(c.Column)).ToList();
    }

    // ───────────────────────────── inline editable title (macros, profiles)
    /// <summary>Title that turns into a text box on click / double-click / pencil. Enter = save, Esc = cancel.</summary>
    internal FrameworkElement EditableTitle(string text, double size, Func<string, string?> save, string tip = "Переименовать")
    {
        var host = new Grid();
        var view = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent, Cursor = Cursors.IBeam, ToolTip = tip + " (щёлкните)" };
        var label = new TextBlock { Text = text, FontSize = size, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var pencil = new Button { Style = (Style)FindResource("GhostButton"), Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = tip,
            Content = new TextBlock { Text = "\uE70F", FontFamily = Icons, FontSize = Math.Max(11, size * 0.55), Foreground = R("MutedBrush") }, Cursor = Cursors.Hand };
        view.Children.Add(label); view.Children.Add(pencil);
        var box = new TextBox { Text = text, FontSize = size * 0.85, MinWidth = 220, Visibility = Visibility.Collapsed, Padding = new Thickness(8, 3, 8, 3) };
        var err = new TextBlock { Foreground = R("ErrBrush"), FontSize = 11.5, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 2, 0, 0) };
        var stack = new StackPanel();
        stack.Children.Add(view); stack.Children.Add(box); stack.Children.Add(err);
        host.Children.Add(stack);
        bool editing = false;
        void Begin() { if (editing) return; editing = true; view.Visibility = Visibility.Collapsed; box.Visibility = Visibility.Visible; box.Text = label.Text; box.Focus(); box.SelectAll(); }
        void End(bool commit)
        {
            if (!editing) return;
            if (commit && box.Text.Trim() != label.Text)
            {
                var e = save(box.Text);
                if (e != null) { err.Text = e; err.Visibility = Visibility.Visible; box.Focus(); return; }
                label.Text = box.Text.Trim();
            }
            editing = false; err.Visibility = Visibility.Collapsed; box.Visibility = Visibility.Collapsed; view.Visibility = Visibility.Visible;
        }
        view.MouseLeftButtonUp += (_, e) => { e.Handled = true; Begin(); };
        pencil.Click += (_, e) => { e.Handled = true; Begin(); };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; End(true); }
            else if (e.Key == Key.Escape) { e.Handled = true; End(false); }
        };
        box.LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(() => End(true));
        return host;
    }

    // ───────────────────────────── list (left)
    private void RefreshMacros()
    {
        bool old = _loading; _loading = true;
        try
        {
            var slots = Enumerable.Range(0, KeyCatalog.MacroCodeCount)
                .Where(i => Cfg.Macros[i].IsUsed || AssignedTo(i).Count > 0 || i == _selMacro).ToList();
            int usedCount = Enumerable.Range(0, KeyCatalog.MacroCodeCount).Count(i => Cfg.Macros[i].IsUsed);
            MacroCountText.Text = $"Используется {usedCount} из {KeyCatalog.MacroCodeCount}";
            MacroSearchHint.Visibility = MacroSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            MacroCards.Children.Clear();
            string f = _macroFilter.Trim().ToLowerInvariant();
            int shown = 0;
            foreach (var i in slots)
            {
                var m = Cfg.Macros[i];
                var keys = AssignedTo(i);
                if (f.Length > 0)
                {
                    string hay = (MacroName(i) + " " + string.Join(" ", keys) + " " + string.Join(" ", m.Events.Select(Labels.EventText))).ToLowerInvariant();
                    if (!hay.Contains(f)) continue;
                }
                MacroCards.Children.Add(MacroCard(i, m, keys));
                shown++;
            }
            if (shown == 0)
                MacroCards.Children.Add(T(f.Length > 0 ? "Ничего не найдено." : "Макросов пока нет. Нажмите «Новый».", 13, muted: true).With(t => t.Margin = new Thickness(6, 8, 0, 0)));

            int used = Enumerable.Range(0, K13Layout.MacroSlots).Where(i => Cfg.Macros[i].IsUsed).Sum(i => K13Layout.Align8(Cfg.Macros[i].BodyLength));
            double frac = Math.Clamp((double)used / MacroMemory, 0, 1);
            MemBar.Value = frac;
            MemPct.Text = frac < 0.01 && used > 0 ? "<1%" : $"{frac:P0}";
            MemText.Text = $"Занято {used:N0} из {MacroMemory:N0} байт · один шаг ≈ 2 байта, прокрутка/курсор 4–6";
            BuildMacroEditor();
        }
        finally { _loading = old; }
    }

    private void MacroSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _macroFilter = MacroSearch.Text;
        if (!_loading) RefreshMacros();
    }

    private static double MacroSeconds(MacroSlot m) => m.Events.Sum(e => e.DelayMs) / 1000.0;

    private static string StepsWord(int n) => n % 10 == 1 && n % 100 != 11 ? "шаг" : n % 10 is >= 2 and <= 4 && (n % 100 < 10 || n % 100 >= 20) ? "шага" : "шагов";

    private Border MacroCard(int slot, MacroSlot m, List<string> keys)
    {
        bool sel = slot == _selMacro;
        var groups = StepGroups(m.Events);
        var sp = new StackPanel();
        var top = new DockPanel();
        var count = new TextBlock { Text = m.IsUsed ? $"{groups.Count} {StepsWord(groups.Count)} · {MacroSeconds(m):0.0} с" : "пустой", FontSize = 11.5, Foreground = R("MutedBrush"), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(count, Dock.Right);
        top.Children.Add(count);
        var icon = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 10, 0), Background = sel ? R("AccentBrush") : R("AccentSoftBrush"),
            Child = new TextBlock { Text = m.Events.Any(e => e.IsMouseButton || e.IsWheel || e.Code == MouseCodes.Move) ? "\uE962" : "\uE765", FontFamily = Icons, FontSize = 13,
                Foreground = sel ? Brushes.White : R("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        top.Children.Add(icon);
        top.Children.Add(new TextBlock { Text = MacroName(slot), FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        sp.Children.Add(top);

        // step preview
        var prev = new WrapPanel { Margin = new Thickness(38, 8, 0, 0) };
        foreach (var g in groups.Take(5)) prev.Children.Add(MiniCap(GroupShort(m.Events, g)));
        if (groups.Count > 5) prev.Children.Add(new TextBlock { Text = $"+{groups.Count - 5}", FontSize = 11, Foreground = R("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 4) });
        if (groups.Count == 0) prev.Children.Add(new TextBlock { Text = "нет шагов — нажмите «Записать»", FontSize = 11.5, Foreground = R("MutedBrush") });
        sp.Children.Add(prev);

        // bound keys
        var kp = new WrapPanel { Margin = new Thickness(38, 4, 0, 0) };
        if (keys.Count == 0) kp.Children.Add(new TextBlock { Text = "не назначен на кнопку", FontSize = 11.5, Foreground = R("MutedBrush") });
        foreach (var k in keys) kp.Children.Add(Pill("\uE765", k));
        sp.Children.Add(kp);

        var card = new Border { Child = sp, Padding = new Thickness(14, 12, 14, 10), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(14), Cursor = Cursors.Hand,
            Background = sel ? R("SelBrush") : R("PanelBrush"), BorderBrush = sel ? R("AccentBrush") : R("BorderBrush"), BorderThickness = new Thickness(sel ? 1.5 : 1) };
        card.MouseEnter += (_, _) => { if (slot != _selMacro) card.Background = R("HoverBrush"); };
        card.MouseLeave += (_, _) => { if (slot != _selMacro) card.Background = R("PanelBrush"); };
        card.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (_recording || slot == _selMacro) return; _selMacro = slot; _pendingPauseMs = 0; Dispatcher.BeginInvoke(RefreshMacros); };
        return card;
    }

    private Border MiniCap(string text) => new()
    {
        Child = new TextBlock { Text = text, FontSize = 11, Foreground = R("TextBrush") },
        Background = R("Panel2Brush"), BorderBrush = R("BorderBrush"), BorderThickness = new Thickness(1, 1, 1, 2), CornerRadius = new CornerRadius(5),
        Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 4, 4)
    };

    private Border Pill(string glyph, string text, bool accent = true)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        if (glyph.Length > 0) sp.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 10, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = accent ? R("AccentBrush") : R("MutedBrush") });
        sp.Children.Add(new TextBlock { Text = text, FontSize = 11.5, Foreground = accent ? R("AccentBrush") : R("MutedBrush") });
        return new Border { Child = sp, Background = accent ? R("AccentSoftBrush") : R("Panel2Brush"), CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 2, 8, 3), Margin = new Thickness(0, 0, 5, 4) };
    }

    // ───────────────────────────── steps model: a press = down + up of the same key shown as one row
    private readonly record struct StepGroup(int Start, int Count);

    private static List<StepGroup> StepGroups(List<MacroEvent> ev)
    {
        var res = new List<StepGroup>();
        for (int i = 0; i < ev.Count; i++)
        {
            var e = ev[i];
            bool pair = e.Down && !e.IsWheel && e.Code != MouseCodes.Move && i + 1 < ev.Count && !ev[i + 1].Down && ev[i + 1].Code == e.Code && !ev[i + 1].IsWheel;
            res.Add(new StepGroup(i, pair ? 2 : 1));
            if (pair) i++;
        }
        return res;
    }

    private static string GroupShort(List<MacroEvent> ev, StepGroup g)
    {
        var e = ev[g.Start];
        if (e.Code == MouseCodes.Move) return "↔ курсор";
        if (e.IsWheel) return Labels.EventText(e);
        string k = Labels.EventText(e);
        return g.Count == 2 ? k : (e.Down ? "↓" : "↑") + k;
    }

    // ───────────────────────────── editor (right)
    private void BuildMacroEditor()
    {
        MacroHeader.Children.Clear(); MacroHost.Children.Clear();
        var slot = Cfg.Macros[_selMacro];
        int sel = _selMacro;
        var keys = AssignedTo(sel);
        var groups = StepGroups(slot.Events);

        // ── header: icon · editable name · actions
        var head = new DockPanel();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(actions, Dock.Right);
        Button Act(string glyph, string tip, Action a, bool enabled = true, string? text = null)
        {
            var c = new StackPanel { Orientation = Orientation.Horizontal };
            c.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            if (text != null) c.Children.Add(new TextBlock { Text = text, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var b = new Button { Style = (Style)FindResource("GhostButton"), Content = c, ToolTip = tip, Padding = text == null ? new Thickness(9, 6, 9, 6) : new Thickness(12, 6, 12, 6), Margin = new Thickness(6, 0, 0, 0), IsEnabled = enabled && !_recording };
            b.Click += (_, _) => a();
            actions.Children.Add(b);
            return b;
        }
        Act("\uE768", "Проверить: проиграть макрос в тестовом окне на этом компьютере", () => TestMacro(sel), slot.IsUsed && !slot.Unsupported, "Проверить");
        Act("\uE8C8", "Дублировать в свободное место", () => DuplicateMacro(sel), slot.IsUsed && !slot.Unsupported);
        Act("\uE74D", "Удалить макрос", () => DeleteMacro(sel), slot.IsUsed || keys.Count > 0);
        head.Children.Add(actions);
        var tile = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(12), Background = R("AccentSoftBrush"), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = "\uE765", FontFamily = Icons, FontSize = 19, Foreground = R("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        head.Children.Add(tile);
        var titleCol = new StackPanel();
        titleCol.Children.Add(EditableTitle(MacroName(sel), 22, n => RenameMacro(sel, n), "Переименовать макрос"));
        var sub = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        if (keys.Count == 0) sub.Children.Add(Pill("\uE7BA", "не назначен на кнопку", false));
        foreach (var k in keys) sub.Children.Add(Pill("\uE765", "на " + k));
        if (slot.IsUsed) sub.Children.Add(Pill("\uE916", $"{groups.Count} {StepsWord(groups.Count)} · {MacroSeconds(slot):0.0} с", false));
        var assign = new Button { Style = (Style)FindResource("LinkButton"), Content = keys.Count == 0 ? "Назначить на кнопку…" : "Изменить кнопку…", Margin = new Thickness(4, 0, 0, 4), VerticalAlignment = VerticalAlignment.Center, IsEnabled = !_recording };
        assign.Click += (_, _) => AssignMacro();
        sub.Children.Add(assign);
        titleCol.Children.Add(sub);
        head.Children.Add(titleCol);
        MacroHeader.Children.Add(head);

        // ── recorder + repeat
        var bar = new DockPanel { Margin = new Thickness(0, 16, 0, 6) };
        var rec = new Button { Style = (Style)FindResource("AccentButton"), MinWidth = 132, Padding = new Thickness(16, 7, 16, 7) };
        var rc = new StackPanel { Orientation = Orientation.Horizontal };
        rc.Children.Add(new TextBlock { Text = _recording ? "■" : "●", Foreground = _recording ? Brushes.White : new SolidColorBrush(Color.FromRgb(0xFF, 0xE2, 0xD6)), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        rc.Children.Add(new TextBlock { Text = _recording ? "Стоп" : "Записать", FontWeight = FontWeights.SemiBold });
        rec.Content = rc;
        rec.Click += (_, _) => ToggleRecording();
        rec.IsEnabled = !slot.Unsupported;
        rec.ToolTip = "Запишет нажатия клавиш" + (_recMouse ? ", щелчки и колесо мыши" : "") + " с паузами между ними. Запись заменит текущие шаги.";
        _recBtn = rec;
        DockPanel.SetDock(rec, Dock.Left);
        bar.Children.Add(rec);
        if (_recording)
        {
            var live = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
            live.Children.Add(new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Fill = R("ErrBrush"), Margin = new Thickness(0, 1, 8, 0) });
            live.Children.Add(T(_recMouse ? "Идёт запись: клавиши, щелчки и колесо мыши. Шагов: " + slot.Events.Count : "Идёт запись: нажимайте клавиши. Шагов: " + slot.Events.Count, 13).With(t => t.Foreground = R("AccentBrush")));
            bar.Children.Add(live);
        }
        else
        {
            var mouse = new CheckBox { Content = "мышь тоже", IsChecked = _recMouse, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
                ToolTip = "Записывать щелчки левой, правой, средней кнопкой и прокрутку. Щелчок по «Стоп» не записывается." };
            mouse.Checked += (_, _) => _recMouse = true; mouse.Unchecked += (_, _) => _recMouse = false;
            DockPanel.SetDock(mouse, Dock.Left);
            bar.Children.Add(mouse);
            // repeat (segmented)
            var rep = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            rep.Children.Add(T("Повтор:", 12.5, muted: true).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(0, 0, 8, 0); }));
            var mode = slot.RepeatMode;
            void Opt(string text, bool on, Action set, string tip)
            {
                var rb = new RadioButton { Style = (Style)FindResource("SubTab"), Content = text, GroupName = "rep" + sel, IsChecked = on, ToolTip = tip, Margin = new Thickness(0) };
                rb.Checked += (_, _) => { if (_loading) return; set(); Changed(false); RefreshMacros(); };
                rep.Children.Add(rb);
            }
            Opt("1 раз", mode == MacroRepeatMode.Count && slot.Repeat <= 1, () => { slot.RepeatMode = MacroRepeatMode.Count; slot.Repeat = 1; }, "Один раз на нажатие");
            Opt("N раз", mode == MacroRepeatMode.Count && slot.Repeat > 1, () => { slot.RepeatMode = MacroRepeatMode.Count; slot.Repeat = Math.Max(2, slot.Repeat); }, "Несколько раз подряд");
            Opt("пока зажата", mode == MacroRepeatMode.WhileHeld, () => slot.RepeatMode = MacroRepeatMode.WhileHeld, "Повторять, пока кнопка удерживается");
            if (mode == MacroRepeatMode.Toggle) Opt("до повторного", true, () => slot.RepeatMode = MacroRepeatMode.Toggle, "До повторного нажатия");
            if (mode == MacroRepeatMode.Count && slot.Repeat > 1)
            {
                var box = new TextBox { Text = slot.Repeat.ToString(), Width = 52, Margin = new Thickness(8, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
                box.TextChanged += (_, _) => { if (int.TryParse(box.Text, out var r) && r is >= 2 and <= 65535) { slot.Repeat = r; Changed(false); } };
                rep.Children.Add(box);
                rep.Children.Add(T("раз", 12.5).With(t => t.VerticalAlignment = VerticalAlignment.Center));
            }
            bar.Children.Add(rep);
        }
        MacroHeader.Children.Add(bar);
        if (slot.Unsupported)
            MacroHeader.Children.Add(T("Этот макрос создан в родной программе и содержит блок текста/подсветки, который мы пока не умеем менять. Его можно только удалить.", 12.5).With(t => { t.Foreground = R("WarnBrush"); t.Margin = new Thickness(0, 4, 0, 4); }));

        // ── steps
        if (groups.Count == 0 && !_recording)
        {
            var empty = new Border { CornerRadius = new CornerRadius(14), BorderBrush = R("BorderBrush"), BorderThickness = new Thickness(1.5), Padding = new Thickness(24), Margin = new Thickness(0, 10, 0, 0), Background = R("Panel2Brush") };
            var es = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            es.Children.Add(new TextBlock { Text = "\uE7C8", FontFamily = Icons, FontSize = 28, Foreground = R("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) });
            es.Children.Add(T("Шагов пока нет", 15, semi: true).With(t => t.HorizontalAlignment = HorizontalAlignment.Center));
            es.Children.Add(T("Нажмите «Записать» и выполните действия — или соберите макрос кнопками внизу.", 12.5, muted: true).With(t => { t.HorizontalAlignment = HorizontalAlignment.Center; t.TextAlignment = TextAlignment.Center; t.Margin = new Thickness(0, 4, 0, 0); }));
            empty.Child = es;
            MacroHost.Children.Add(empty);
        }
        for (int gi = 0; gi < groups.Count; gi++) MacroHost.Children.Add(StepRow(slot, groups, gi));
        if (_pendingPauseMs > 0)
        {
            var pr = new Border { CornerRadius = new CornerRadius(12), BorderBrush = R("AccentBrush"), BorderThickness = new Thickness(1), Padding = new Thickness(14, 8, 10, 8), Margin = new Thickness(36, 6, 0, 0), Background = R("AccentSoftBrush") };
            var pd = new DockPanel();
            var x = new Button { Style = (Style)FindResource("GhostButton"), Padding = new Thickness(6, 2, 6, 2), Content = new TextBlock { Text = "\uE711", FontFamily = Icons, FontSize = 10 }, ToolTip = "Убрать паузу" };
            x.Click += (_, _) => { _pendingPauseMs = 0; BuildMacroEditor(); };
            DockPanel.SetDock(x, Dock.Right); pd.Children.Add(x);
            pd.Children.Add(T($"⏱  Пауза {_pendingPauseMs} мс — перед следующим добавленным шагом", 12.5).With(t => { t.Foreground = R("AccentBrush"); t.VerticalAlignment = VerticalAlignment.Center; }));
            pr.Child = pd;
            MacroHost.Children.Add(pr);
        }
        BuildAddBar(slot);
    }

    private static (string Glyph, string Verb, string Key, string Kind) Describe(List<MacroEvent> ev, StepGroup g)
    {
        var e = ev[g.Start];
        if (e.Code == MouseCodes.Move) return ("\uE7C2", "Сдвинуть курсор", "", "move");
        if (e.IsWheel) return (e.WheelAmount >= 0 ? "\uE70E" : "\uE70D", e.WheelAmount >= 0 ? "Прокрутить вверх" : "Прокрутить вниз", "", "wheel");
        string key = e.IsMouseButton ? MouseCodes.Short(e.Code) : Labels.KeyText(e.Code);
        if (g.Count == 2) return (e.IsMouseButton ? "\uE962" : "\uE765", e.IsMouseButton ? "Щелчок" : "Нажать", key, e.IsMouseButton ? "click" : "press");
        return (e.Down ? "\uE74B" : "\uE74A", e.Down ? "Зажать" : "Отпустить", key, e.IsMouseButton ? "mouse" : "key");
    }

    private Border StepRow(MacroSlot slot, List<StepGroup> groups, int gi)
    {
        var ev = slot.Events;
        var g = groups[gi];
        var e = ev[g.Start];
        var (glyph, verb, key, kind) = Describe(ev, g);
        bool mouse = kind is "click" or "mouse" or "wheel" or "move";
        bool ro = _recording || slot.Unsupported;

        var row = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        foreach (var w in new[] { 22.0, 30.0, 44.0 }) row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var grip = new TextBlock { Text = "\uE700", FontFamily = Icons, FontSize = 12, Foreground = R("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Cursor = ro ? Cursors.Arrow : Cursors.SizeNS, ToolTip = "Перетащите, чтобы изменить порядок", Background = Brushes.Transparent, Padding = new Thickness(2, 8, 4, 8) };
        row.Children.Add(grip);
        var num = new TextBlock { Text = (gi + 1).ToString(), FontSize = 12, Foreground = R("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        Grid.SetColumn(num, 1); row.Children.Add(num);
        var ic = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(10), Background = mouse ? R("AccentSoftBrush") : R("Panel2Brush"), BorderBrush = R("BorderBrush"), BorderThickness = new Thickness(mouse ? 0 : 1),
            Child = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 14, Foreground = mouse ? R("AccentBrush") : R("TextBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        Grid.SetColumn(ic, 2); row.Children.Add(ic);

        // main text: verb + keycap / inline values
        var main = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0) };
        main.Children.Add(new TextBlock { Text = verb, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        if (key.Length > 0)
        {
            var cap = new Border { Child = new TextBlock { Text = key, FontSize = 13, FontWeight = FontWeights.SemiBold }, Background = R("PanelBrush"), BorderBrush = R("BorderBrush"),
                BorderThickness = new Thickness(1, 1, 1, 2.5), CornerRadius = new CornerRadius(6), Padding = new Thickness(9, 2, 9, 2), Cursor = ro ? Cursors.Arrow : Cursors.Hand, ToolTip = ro ? null : "Изменить" };
            if (!ro) cap.MouseLeftButtonUp += (_, a) => { a.Handled = true; EditGroup(g); };
            main.Children.Add(cap);
        }
        if (kind == "wheel")
        {
            main.Children.Add(Inline(Math.Abs(e.WheelAmount).ToString(), 40, s => { if (int.TryParse(s, out var n) && n is >= 1 and <= 127) { e.Extra![0] = (byte)(sbyte)(Math.Sign(e.WheelAmount == 0 ? 1 : e.WheelAmount) * n); return true; } return false; }, ro));
            main.Children.Add(new TextBlock { Text = "щелч.", FontSize = 12, Foreground = R("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) });
        }
        if (kind == "move")
        {
            var (dx, dy) = MoveOf(e);
            main.Children.Add(new TextBlock { Text = "X", FontSize = 12, Foreground = R("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            main.Children.Add(Inline(dx.ToString(), 52, s => { if (int.TryParse(s, out var v) && v is >= -32768 and <= 32767) { SetMove(e, v, MoveOf(e).dy); return true; } return false; }, ro));
            main.Children.Add(new TextBlock { Text = "Y", FontSize = 12, Foreground = R("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) });
            main.Children.Add(Inline(dy.ToString(), 52, s => { if (int.TryParse(s, out var v) && v is >= -32768 and <= 32767) { SetMove(e, MoveOf(e).dx, v); return true; } return false; }, ro));
        }
        if (g.Count == 2)
        {
            var up = ev[g.Start + 1];
            main.Children.Add(new TextBlock { Text = "держать", FontSize = 12, Foreground = R("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0) });
            main.Children.Add(Inline(up.DelayMs.ToString(), 46, s => SetDelay(up, s), ro));
            main.Children.Add(new TextBlock { Text = "мс", FontSize = 12, Foreground = R("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) });
        }
        Grid.SetColumn(main, 3); row.Children.Add(main);

        // delay before the step
        var delay = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Пауза перед этим шагом (0–1016 мс, шаг 8 мс)" };
        delay.Children.Add(new TextBlock { Text = "\uE916", FontFamily = Icons, FontSize = 11, Foreground = e.DelayMs >= ShowPauseFromMs ? R("AccentBrush") : R("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        delay.Children.Add(Inline(e.DelayMs.ToString(), 50, s => SetDelay(e, s), ro));
        delay.Children.Add(new TextBlock { Text = "мс", FontSize = 12, Foreground = R("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) });
        Grid.SetColumn(delay, 4); row.Children.Add(delay);

        // buttons
        var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Button Mini(string gl, string tip, Action a, bool en = true)
        {
            var b = new Button { Style = (Style)FindResource("GhostButton"), Padding = new Thickness(7, 4, 7, 4), Margin = new Thickness(2, 0, 0, 0), ToolTip = tip, IsEnabled = en && !ro,
                Content = new TextBlock { Text = gl, FontFamily = Icons, FontSize = 11, Foreground = R("MutedBrush") } };
            b.Click += (_, _) => a();
            btns.Children.Add(b);
            return b;
        }
        Mini("\uE70E", "Выше", () => MoveGroup(gi, gi - 1), gi > 0);
        Mini("\uE70D", "Ниже", () => MoveGroup(gi, gi + 2), gi < groups.Count - 1);
        Mini("\uE70F", "Изменить", () => EditGroup(g), kind is not ("wheel" or "move"));
        Mini("\uE74D", "Удалить шаг", () => { ev.RemoveRange(g.Start, g.Count); SyncMacro(); });
        Grid.SetColumn(btns, 5); row.Children.Add(btns);

        var border = new Border { Child = row, CornerRadius = new CornerRadius(12), Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 6),
            Background = R("PanelBrush"), BorderBrush = R("BorderBrush"), BorderThickness = new Thickness(1), AllowDrop = !ro };
        border.MouseEnter += (_, _) => border.Background = R("Panel2Brush");
        border.MouseLeave += (_, _) => border.Background = R("PanelBrush");

        // drag to reorder (by the grip)
        if (!ro)
        {
            grip.MouseLeftButtonDown += (_, a) =>
            {
                a.Handled = true;
                border.Opacity = 0.5;
                try { DragDrop.DoDragDrop(border, new DataObject("k13step", gi), DragDropEffects.Move); }
                finally { border.Opacity = 1; }
            };
            border.DragOver += (_, a) =>
            {
                if (!a.Data.GetDataPresent("k13step")) { a.Effects = DragDropEffects.None; a.Handled = true; return; }
                bool below = a.GetPosition(border).Y > border.ActualHeight / 2;
                border.BorderBrush = R("AccentBrush");
                border.BorderThickness = below ? new Thickness(1, 1, 1, 3) : new Thickness(1, 3, 1, 1);
                a.Effects = DragDropEffects.Move; a.Handled = true;
            };
            border.DragLeave += (_, _) => { border.BorderBrush = R("BorderBrush"); border.BorderThickness = new Thickness(1); };
            border.Drop += (_, a) =>
            {
                border.BorderBrush = R("BorderBrush"); border.BorderThickness = new Thickness(1);
                if (a.Data.GetData("k13step") is not int from) return;
                bool below = a.GetPosition(border).Y > border.ActualHeight / 2;
                a.Handled = true;
                Dispatcher.BeginInvoke(() => MoveGroup(from, below ? gi + 1 : gi));
            };
        }
        return border;
    }

    private TextBox Inline(string value, double width, Func<string, bool> commit, bool ro)
    {
        var box = new TextBox { Text = value, Width = width, TextAlignment = TextAlignment.Center, Padding = new Thickness(2, 2, 2, 2), VerticalAlignment = VerticalAlignment.Center, IsReadOnly = ro, FontSize = 12.5 };
        string orig = value;
        void Commit()
        {
            if (box.Text.Trim() == orig) return;
            if (commit(box.Text.Trim())) { orig = box.Text.Trim(); Changed(false); Dispatcher.BeginInvoke(RefreshMacros); }
            else { box.Text = orig; }
        }
        box.KeyDown += (_, a) =>
        {
            if (a.Key == Key.Enter) { a.Handled = true; Commit(); }
            else if (a.Key == Key.Escape) { a.Handled = true; box.Text = orig; Keyboard.ClearFocus(); }
        };
        box.LostKeyboardFocus += (_, _) => Commit();
        return box;
    }

    private static bool SetDelay(MacroEvent e, string s)
    {
        if (!int.TryParse(s, out var ms) || ms < 0 || ms > 1016) return false;
        e.Delay = Math.Clamp((int)Math.Round(ms / 8.0), 0, 127);
        return true;
    }

    private static (int dx, int dy) MoveOf(MacroEvent e) =>
        e.Extra is { Length: >= 4 } x ? ((short)(x[0] | x[1] << 8), (short)(x[2] | x[3] << 8)) : (0, 0);

    private static void SetMove(MacroEvent e, int dx, int dy) =>
        e.Extra = new[] { (byte)dx, (byte)(dx >> 8), (byte)dy, (byte)(dy >> 8) };

    /// <summary>Moves step group <paramref name="from"/> so that it lands before group index <paramref name="to"/> (groups.Count = end).</summary>
    private void MoveGroup(int from, int to)
    {
        var ev = Cfg.Macros[_selMacro].Events;
        var groups = StepGroups(ev);
        if (from < 0 || from >= groups.Count) return;
        to = Math.Clamp(to, 0, groups.Count);
        if (to == from || to == from + 1) return;
        var g = groups[from];
        var chunk = ev.GetRange(g.Start, g.Count);
        int insertAt = to == groups.Count ? ev.Count : groups[to].Start;
        ev.RemoveRange(g.Start, g.Count);
        if (insertAt > g.Start) insertAt -= g.Count;
        ev.InsertRange(insertAt, chunk);
        SyncMacro();
    }

    private void EditGroup(StepGroup g)
    {
        var ev = Cfg.Macros[_selMacro].Events;
        if (g.Count == 2)
        {
            byte oldCode = ev[g.Start].Code;
            var picked = PickKey(ev[g.Start].IsMouseButton, oldCode);
            if (picked is not byte c) return;
            ev[g.Start].Code = c; ev[g.Start + 1].Code = c;
            SyncMacro();
            return;
        }
        EditStep(g.Start);
    }

    /// <summary>Small picker: press a key (or choose from the list), or pick a mouse button.</summary>
    private byte? PickKey(bool mouse, byte current)
    {
        var w = Ui.MakeWindow(this, mouse ? "Кнопка мыши" : "Клавиша", 440, mouse ? 200 : 230);
        var sp = new StackPanel { Margin = new Thickness(20) };
        byte? result = null;
        if (mouse)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (c, n) in new[] { (MouseCodes.Left, "Левая"), (MouseCodes.Right, "Правая"), (MouseCodes.Middle, "Средняя") })
            {
                var b = new Button { Content = n, Margin = new Thickness(0, 0, 8, 0), MinWidth = 100, Style = (Style)FindResource(c == current ? "AccentButton" : "GhostButton") };
                b.Click += (_, _) => { result = c; w.DialogResult = true; };
                row.Children.Add(b);
            }
            sp.Children.Add(T("Какой кнопкой щёлкать:", 12.5, muted: true, bottom: 10));
            sp.Children.Add(row);
        }
        else
        {
            sp.Children.Add(T("Нажмите клавишу или выберите из списка:", 12.5, muted: true, bottom: 8));
            var keys = KeyCatalog.All.Where(k => k.Category is not (KeyCategory.Disabled or KeyCategory.Macro or KeyCategory.Unknown or KeyCategory.Lighting) && k.Vk != 0).OrderBy(k => k.Category).ThenBy(k => k.Code).ToList();
            var box = new ComboBox { ItemsSource = keys, DisplayMemberPath = "Name", SelectedItem = keys.FirstOrDefault(k => k.Code == current), MaxDropDownHeight = 320 };
            box.PreviewKeyDown += (_, e) =>
            {
                var k = e.Key == Key.System ? e.SystemKey : e.Key;
                if (k is Key.Up or Key.Down or Key.Enter) return;
                if (KeyCombo.TryDeviceCode(k, out var c)) { box.SelectedItem = keys.FirstOrDefault(x => x.Code == c); e.Handled = true; }
            };
            sp.Children.Add(box);
            sp.Children.Add(Ui.OkCancel(w, () => { if (box.SelectedItem is KeyInfo ki) { result = ki.Code; return true; } return false; }));
            w.Loaded += (_, _) => box.Focus();
        }
        w.Content = sp;
        return w.ShowDialog() == true ? result : null;
    }

    // ───────────────────────────── add-step bar
    private void BuildAddBar(MacroSlot slot)
    {
        var wp = new WrapPanel();
        bool en = !_recording && !slot.Unsupported;
        void Add(string glyph, string text, string tip, Action a)
        {
            var c = new StackPanel { Orientation = Orientation.Horizontal };
            c.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 12, Foreground = R("AccentBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
            c.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            var b = new Button { Style = (Style)FindResource("Chip"), Content = c, ToolTip = tip, Margin = new Thickness(0, 0, 6, 6), IsEnabled = en };
            b.Click += (_, _) => a();
            wp.Children.Add(b);
        }
        Add("\uE765", "Клавиша", "Нажать и отпустить клавишу", () => AddKey(0));
        Add("\uE74B", "Зажать", "Зажать клавишу (например, Ctrl перед буквой)", () => AddKey(1));
        Add("\uE74A", "Отпустить", "Отпустить ранее зажатую клавишу", () => AddKey(2));
        Add("\uE8D2", "Текст", "Напечатать текст (латиница, цифры; русские буквы — при русской раскладке на ПК)", AddText);
        Add("\uE916", "Пауза", "Пауза перед следующим шагом", AddPause);
        Add("\uE962", "Щелчок", "Щелчок кнопкой мыши", () =>
        {
            var m = new ContextMenu();
            foreach (var (c, n) in new[] { (MouseCodes.Left, "Левой кнопкой"), (MouseCodes.Right, "Правой кнопкой"), (MouseCodes.Middle, "Средней кнопкой (колесом)") })
            {
                var mi = new MenuItem { Header = n };
                mi.Click += (_, _) => AddEvents(new() { new MacroEvent { Down = true, Code = c, Delay = 2 }, new MacroEvent { Down = false, Code = c, Delay = 6 } });
                m.Items.Add(mi);
            }
            m.IsOpen = true;
        });
        Add("\uE7C2", "Курсор", "Сдвинуть курсор мыши на X/Y точек", () =>
        {
            var s = Ui.Input(this, "Сдвиг курсора", "Сдвиг по X и Y в точках, через пробел (например: 100 -50):", "100 0");
            if (s == null) return;
            var p = s.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length != 2 || !int.TryParse(p[0], out var dx) || !int.TryParse(p[1], out var dy) || Math.Abs(dx) > 32767 || Math.Abs(dy) > 32767) { MessageBox.Show(this, "Нужно два числа, например: 100 -50", Title); return; }
            var e = new MacroEvent { Down = false, Code = MouseCodes.Move, Delay = 2 };
            SetMove(e, dx, dy);
            AddEvents(new() { e });
        });
        Add("\uE70D", "Прокрутка", "Прокрутка колесом мыши", () => EditStep(-1, presetType: 2));
        MacroAddBar.Child = wp;
    }

    private void AddKey(int act)
    {
        var picked = PickKey(false, Cfg.Macros[_selMacro].Events.LastOrDefault(e => !e.IsWheel && !e.IsMouseButton && e.Code != MouseCodes.Move)?.Code ?? (byte)0x04);
        if (picked is not byte c) return;
        AddEvents(act switch
        {
            1 => new() { new MacroEvent { Down = true, Code = c, Delay = 2 } },
            2 => new() { new MacroEvent { Down = false, Code = c, Delay = 2 } },
            _ => new() { new MacroEvent { Down = true, Code = c, Delay = 2 }, new MacroEvent { Down = false, Code = c, Delay = 2 } },
        });
    }

    private void AddEvents(List<MacroEvent> add)
    {
        if (add.Count == 0) return;
        var list = Cfg.Macros[_selMacro].Events;
        if (_pendingPauseMs > 0) { add[0].Delay = Math.Clamp((int)Math.Round(_pendingPauseMs / 8.0), 0, 127); _pendingPauseMs = 0; }
        else if (list.Count == 0) add[0].Delay = 2;
        list.AddRange(add);
        SyncMacro();
        Dispatcher.BeginInvoke(() => MacroScroll.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void AddPause()
    {
        var s = Ui.Input(this, "Пауза", "Пауза перед следующим шагом, мс (до 1016):", _pendingPauseMs > 0 ? _pendingPauseMs.ToString() : "200");
        if (s == null) return;
        if (!int.TryParse(s, out var ms) || ms < 8 || ms > 1016) { MessageBox.Show(this, "Пауза в одном шаге — от 8 до 1016 мс (так хранит клавиатура). Для длинной паузы добавьте несколько шагов.", Title); return; }
        _pendingPauseMs = (int)Math.Round(ms / 8.0) * 8;
        BuildMacroEditor();
    }

    private void AddText()
    {
        var s = Ui.Input(this, "Текст", "Что напечатать (латиница, цифры, пробел и знаки; русские буквы — если на ПК включена русская раскладка):", "");
        if (s == null) return;
        var (events, bad) = TextToEvents(s);
        if (events.Count == 0) { MessageBox.Show(this, "Эти символы клавиатура напечатать не может.", Title); return; }
        if (events.Count / 2 > 400) { MessageBox.Show(this, "Слишком длинный текст для памяти клавиатуры (до 400 символов).", Title); return; }
        AddEvents(events);
        if (bad.Length > 0) StatusText.Text = "Пропущены символы: " + bad;
    }

    private const string RuRow = "йцукенгшщзхъфывапролджэячсмитьбюё";
    private const string EnRow = "qwertyuiop[]asdfghjkl;'zxcvbnm,.`";

    /// <summary>Text → HID key presses (US positions; Cyrillic by ЙЦУКЕН position; uppercase via Shift).</summary>
    internal static (List<MacroEvent> Events, string Skipped) TextToEvents(string text)
    {
        var res = new List<MacroEvent>(); var bad = new System.Text.StringBuilder();
        const string shifted = "!@#$%^&*()_+{}:\"<>?~|";
        const string plain = "1234567890-=[];',./`\\";
        foreach (var ch0 in text)
        {
            char ch = ch0; bool shift = false;
            int ri = RuRow.IndexOf(char.ToLowerInvariant(ch));
            int si = shifted.IndexOf(ch);
            if (ri >= 0) { shift = char.IsUpper(ch); ch = EnRow[ri]; }
            else if (ch < 128 && char.IsLetter(ch) && char.IsUpper(ch)) { shift = true; ch = char.ToLowerInvariant(ch); }
            else if (si >= 0) { shift = true; ch = plain[si]; }
            byte code = ch switch
            {
                >= 'a' and <= 'z' => (byte)(0x04 + (ch - 'a')),
                >= '1' and <= '9' => (byte)(0x1E + (ch - '1')),
                '0' => 0x27, ' ' => 0x2C, '\n' => 0x28, '\t' => 0x2B, '-' => 0x2D, '=' => 0x2E, '[' => 0x2F, ']' => 0x30, '\\' => 0x31,
                ';' => 0x33, '\'' => 0x34, '`' => 0x35, ',' => 0x36, '.' => 0x37, '/' => 0x38,
                _ => 0,
            };
            if (code == 0) { if (ch0 != '\r') bad.Append(ch0); continue; }
            if (shift) res.Add(new MacroEvent { Down = true, Code = 0xE1, Delay = 1 });
            res.Add(new MacroEvent { Down = true, Code = code, Delay = 1 });
            res.Add(new MacroEvent { Down = false, Code = code, Delay = 1 });
            if (shift) res.Add(new MacroEvent { Down = false, Code = 0xE1, Delay = 1 });
        }
        return (res, bad.ToString());
    }

    // ───────────────────────────── duplicate / delete / test
    private void DuplicateMacro(int src)
    {
        int slot = FreeMacroSlot();
        if (slot < 0) { MessageBox.Show(this, $"Все {KeyCatalog.MacroCodeCount} мест для макросов заняты. Удалите ненужный макрос.", Title); return; }
        Cfg.Macros[slot] = Cfg.Macros[src].Clone();
        _profile.MacroNames[slot] = MacroName(src) + " (копия)";
        _selMacro = slot;
        Log($"«{MacroName(src)}» скопирован в место {slot + 1}.");
        Changed(); RefreshMacros();
    }

    private void DeleteMacro(int sel)
    {
        var keys = AssignedTo(sel);
        if (MessageBox.Show(this, $"Удалить «{MacroName(sel)}»?" + (keys.Count > 0 ? $"\n\nКнопки, на которые он назначен ({string.Join(", ", keys)}), станут как с завода." : ""),
                Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        byte code = (byte)(KeyCatalog.FirstMacro + sel);
        foreach (var c in K13Layout.Controls.Where(c => Cfg.GetKey(c.Column) == code)) Cfg.SetKey(c.Column, c.FactoryCode);
        var slot = Cfg.Macros[sel];
        slot.Events = new(); slot.Repeat = 1; slot.RepeatMode = MacroRepeatMode.Count; slot.Unsupported = false;
        _profile.MacroNames.Remove(sel);
        Changed(); RefreshMacros();
    }

    /// <summary>Plays the macro on this PC into a test box (keyboard only unless «мышь тоже» is ticked).</summary>
    private void TestMacro(int sel)
    {
        var m = Cfg.Macros[sel];
        var w = Ui.MakeWindow(this, "Проверка: " + MacroName(sel), 520, 360);
        var sp = new DockPanel { Margin = new Thickness(20) };
        var head = T("Макрос проиграется здесь, на компьютере (в клавиатуру ничего не записывается). Текст появится в поле ниже.", 12.5, muted: true, bottom: 10);
        DockPanel.SetDock(head, Dock.Top); sp.Children.Add(head);
        var bottom = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        var mouse = new CheckBox { Content = "Мышь тоже (щелчки и прокрутка будут настоящими)", VerticalAlignment = VerticalAlignment.Center, IsEnabled = m.Events.Any(e => e.IsMouseButton || e.IsWheel || e.Code == MouseCodes.Move) };
        var run = new Button { Style = (Style)FindResource("AccentButton"), Content = "▶  Запустить", MinWidth = 120 };
        DockPanel.SetDock(run, Dock.Right);
        bottom.Children.Add(run); bottom.Children.Add(mouse);
        sp.Children.Add(bottom);
        var status = T("", 12, muted: true).With(t => t.Margin = new Thickness(0, 8, 0, 0));
        DockPanel.SetDock(status, Dock.Bottom); sp.Children.Add(status);
        var box = new TextBox { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 14 };
        sp.Children.Add(box);
        w.Content = sp;
        bool busy = false;
        run.Click += async (_, _) =>
        {
            if (busy) return;
            busy = true; run.IsEnabled = false;
            box.Focus();
            bool withMouse = mouse.IsChecked == true;
            var events = m.Events.Select(e => e.Clone()).ToList();
            int repeat = m.RepeatMode == MacroRepeatMode.Count ? Math.Clamp(m.Repeat, 1, 5) : 1;
            status.Text = "Играет…";
            await Task.Delay(300);
            await Task.Run(() =>
            {
                var held = new HashSet<byte>();
                try
                {
                    for (int r = 0; r < repeat; r++)
                        foreach (var e in events)
                        {
                            if (e.DelayMs > 0) Thread.Sleep(e.DelayMs);
                            bool isMouse = e.IsMouseButton || e.IsWheel || e.Code == MouseCodes.Move;
                            if (isMouse && !withMouse) continue;
                            MacroPlayer.Send(e);
                            if (!e.IsWheel && e.Code != MouseCodes.Move) { if (e.Down) held.Add(e.Code); else held.Remove(e.Code); }
                        }
                }
                finally { foreach (var c in held) MacroPlayer.Send(new MacroEvent { Down = false, Code = c }); } // never leave a key stuck
            });
            status.Text = (repeat > 1 ? $"Готово ({repeat} раза). " : "Готово. ") + (m.RepeatMode != MacroRepeatMode.Count ? "На клавиатуре этот макрос повторяется, пока кнопка зажата / до повторного нажатия." : "");
            busy = false; run.IsEnabled = true;
        };
        w.ShowDialog();
    }

    private void SyncMacro()
    {
        Changed(false);
        RefreshMacros();
    }

    private void EditPause(int idx)
    {
        var ev = Cfg.Macros[_selMacro].Events[idx];
        var s = Ui.Input(this, "Пауза", "Пауза перед шагом, мс (0–1016, шаг 8 мс):", ev.DelayMs.ToString());
        if (s == null || !int.TryParse(s, out var ms)) return;
        ev.Delay = Math.Clamp((int)Math.Round(ms / 8.0), 0, 127);
        SyncMacro();
    }

    private void EditStep(int idx, int presetType = 0, int presetAct = 0)
    {
        var list = Cfg.Macros[_selMacro].Events;
        var prev = idx < 0 ? list.LastOrDefault() : null;
        var ev = idx >= 0 ? list[idx].Clone() : new MacroEvent { Down = prev == null || !prev.Down || prev.IsWheel, Code = prev is { IsWheel: false } ? prev.Code : (byte)0xE0, Delay = 2 };
        var w = Ui.MakeWindow(this, idx >= 0 ? "Изменить шаг" : "Добавить шаг", 470, 470);
        var sp = new StackPanel { Margin = new Thickness(20) };

        // step type
        int type = idx < 0 ? presetType : ev.IsWheel ? 2 : ev.IsMouseButton ? 1 : 0;
        var types = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        var tKey = new RadioButton { Style = (Style)FindResource("SubTab"), Content = "Клавиша", GroupName = "typ", IsChecked = type == 0 };
        var tMouse = new RadioButton { Style = (Style)FindResource("SubTab"), Content = "Кнопка мыши", GroupName = "typ", IsChecked = type == 1 };
        var tWheel = new RadioButton { Style = (Style)FindResource("SubTab"), Content = "Колесо мыши", GroupName = "typ", IsChecked = type == 2 };
        types.Children.Add(tKey); types.Children.Add(tMouse); types.Children.Add(tWheel);
        sp.Children.Add(types);

        // press/release (keys and mouse buttons)
        var act = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        var click = new RadioButton { Style = (Style)FindResource("SubTab"), Content = "Щелчок", GroupName = "act", IsChecked = idx < 0 && presetAct == 0 };
        var down = new RadioButton { Style = (Style)FindResource("SubTab"), Content = "Зажать", GroupName = "act", IsChecked = idx >= 0 ? ev.Down : presetAct == 1 };
        var up = new RadioButton { Style = (Style)FindResource("SubTab"), Content = "Отпустить", GroupName = "act", IsChecked = idx >= 0 ? !ev.Down : presetAct == 2 };
        if (idx >= 0) click.Visibility = Visibility.Collapsed;
        act.Children.Add(click); act.Children.Add(down); act.Children.Add(up);
        sp.Children.Add(act);

        // key
        var keyPanel = new StackPanel();
        keyPanel.Children.Add(T("Клавиша (щёлкните в поле и нажмите её или выберите из списка):", 12, muted: true, bottom: 4));
        var keys = KeyCatalog.All.Where(k => k.Category is not (KeyCategory.Disabled or KeyCategory.Macro or KeyCategory.Unknown or KeyCategory.Lighting) && k.Vk != 0).OrderBy(k => k.Category).ThenBy(k => k.Code).ToList();
        var box = new ComboBox { ItemsSource = keys, DisplayMemberPath = "Name", SelectedItem = keys.FirstOrDefault(k => k.Code == ev.Code), IsEditable = false, MaxDropDownHeight = 320 };
        box.PreviewKeyDown += (_, e) =>
        {
            var k = e.Key == Key.System ? e.SystemKey : e.Key;
            if (k is Key.Up or Key.Down or Key.Enter) return;
            if (KeyCombo.TryDeviceCode(k, out var c)) { box.SelectedItem = keys.FirstOrDefault(x => x.Code == c); e.Handled = true; }
        };
        keyPanel.Children.Add(box);
        sp.Children.Add(keyPanel);

        // mouse button
        var mousePanel = new StackPanel();
        mousePanel.Children.Add(T("Кнопка мыши:", 12, muted: true, bottom: 4));
        var mb = new StackPanel { Orientation = Orientation.Horizontal };
        var bL = new RadioButton { Style = (Style)FindResource("SubTab"), Content = "Левая", GroupName = "mb", IsChecked = !ev.IsMouseButton || ev.Code == MouseCodes.Left };
        var bR = new RadioButton { Style = (Style)FindResource("SubTab"), Content = "Правая", GroupName = "mb", IsChecked = ev.Code == MouseCodes.Right && ev.IsMouseButton };
        var bM = new RadioButton { Style = (Style)FindResource("SubTab"), Content = "Средняя", GroupName = "mb", IsChecked = ev.Code == MouseCodes.Middle && ev.IsMouseButton };
        mb.Children.Add(bL); mb.Children.Add(bR); mb.Children.Add(bM);
        mousePanel.Children.Add(mb);
        mousePanel.Children.Add(T("Левая кнопка проверена; правая и средняя — по анализу родной программы, проверим при первой записи.", 11.5, muted: true).With(t => t.Margin = new Thickness(0, 6, 0, 0)));
        sp.Children.Add(mousePanel);

        // wheel
        var wheelPanel = new StackPanel();
        wheelPanel.Children.Add(T("Прокрутка:", 12, muted: true, bottom: 4));
        var wr = new StackPanel { Orientation = Orientation.Horizontal };
        var wUp = new RadioButton { Style = (Style)FindResource("SubTab"), Content = "Вверх", GroupName = "wd", IsChecked = ev.WheelAmount >= 0 };
        var wDn = new RadioButton { Style = (Style)FindResource("SubTab"), Content = "Вниз", GroupName = "wd", IsChecked = ev.WheelAmount < 0 };
        var amt = new TextBox { Text = Math.Max(1, Math.Abs(ev.WheelAmount)).ToString(), Width = 50, Margin = new Thickness(12, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        wr.Children.Add(wUp); wr.Children.Add(wDn); wr.Children.Add(amt); wr.Children.Add(T("щелчков колеса", 12.5).With(t => t.VerticalAlignment = VerticalAlignment.Center));
        wheelPanel.Children.Add(wr);
        wheelPanel.Children.Add(T("Формат прокрутки взят из родной программы — проверим при первой записи.", 11.5, muted: true).With(t => t.Margin = new Thickness(0, 6, 0, 0)));
        sp.Children.Add(wheelPanel);

        void Show()
        {
            int t = tMouse.IsChecked == true ? 1 : tWheel.IsChecked == true ? 2 : 0;
            keyPanel.Visibility = t == 0 ? Visibility.Visible : Visibility.Collapsed;
            mousePanel.Visibility = t == 1 ? Visibility.Visible : Visibility.Collapsed;
            wheelPanel.Visibility = t == 2 ? Visibility.Visible : Visibility.Collapsed;
            act.Visibility = t == 2 ? Visibility.Collapsed : Visibility.Visible;
        }
        tKey.Checked += (_, _) => Show(); tMouse.Checked += (_, _) => Show(); tWheel.Checked += (_, _) => Show();
        Show();

        sp.Children.Add(T("Пауза перед шагом, мс:", 12, muted: true, bottom: 4).With(t => t.Margin = new Thickness(0, 12, 0, 4)));
        var ms = new TextBox { Text = (idx < 0 && _pendingPauseMs > 0 ? _pendingPauseMs : ev.DelayMs).ToString(), Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
        sp.Children.Add(ms);
        sp.Children.Add(Ui.OkCancel(w, () =>
        {
            int delay = int.TryParse(ms.Text, out var m) ? Math.Clamp((int)Math.Round(m / 8.0), 0, 127) : ev.Delay;
            if (idx < 0 && _pendingPauseMs > 0) { delay = Math.Clamp((int)Math.Round(_pendingPauseMs / 8.0), 0, 127); _pendingPauseMs = 0; }
            var add = new List<MacroEvent>();
            if (tWheel.IsChecked == true)
            {
                int n = int.TryParse(amt.Text, out var a) ? Math.Clamp(a, 1, 127) : 1;
                add.Add(MacroEvent.Wheel(wUp.IsChecked == true ? n : -n, delay));
            }
            else
            {
                byte code;
                if (tMouse.IsChecked == true) code = bR.IsChecked == true ? MouseCodes.Right : bM.IsChecked == true ? MouseCodes.Middle : MouseCodes.Left;
                else if (box.SelectedItem is KeyInfo ki) code = ki.Code;
                else return false;
                if (click.IsChecked == true && idx < 0)
                {
                    add.Add(new MacroEvent { Down = true, Code = code, Delay = delay });
                    add.Add(new MacroEvent { Down = false, Code = code, Delay = 1 });
                }
                else add.Add(new MacroEvent { Down = down.IsChecked == true, Code = code, Delay = delay });
            }
            if (idx >= 0) { list.RemoveAt(idx); list.InsertRange(idx, add); } else list.AddRange(add);
            return true;
        }));
        w.Content = new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        if (w.ShowDialog() == true) SyncMacro();
    }
    private void AssignMacro()
    {
        var w = Ui.MakeWindow(this, "Назначить на кнопку", 760, 470);
        var sp = new DockPanel { Margin = new Thickness(20) };
        var hint = T($"Нажмите на кнопку, на которую поставить «{MacroName(_selMacro)}»", 16, semi: true, bottom: 12);
        DockPanel.SetDock(hint, Dock.Top);
        sp.Children.Add(hint);
        var dv = new DeviceView();
        dv.Rebuild(Cfg, _profile.Host, -1);
        dv.Clicked += id =>
        {
            if (id == DeviceView.JoystickKnob) id = 15;
            if (!IsKeyCol(id)) { hint.Text = "Колесо нельзя назначить на макрос — выберите кнопку или направление джойстика."; return; }
            Cfg.SetKey(id, (byte)(KeyCatalog.FirstMacro + _selMacro));
            Log($"{DeviceView.ControlName(id)}: {MacroName(_selMacro)}.");
            w.DialogResult = true;
        };
        sp.Children.Add(dv);
        w.Content = sp;
        if (w.ShowDialog() == true) { Changed(); RefreshMacros(); }
    }
    internal void SelectMacro(int slot) { _selMacro = slot; RefreshMacros(); }

    /// <summary>Preview only: fills a free slot with a demo macro that uses the mouse (Ctrl + click, wheel).</summary>
    internal int DemoMouseMacro()
    {
        int slot = FreeMacroSlot();
        if (slot < 0) slot = KeyCatalog.MacroCodeCount - 1;
        Cfg.Macros[slot] = new MacroSlot
        {
            Events =
            {
                new MacroEvent { Down = true, Code = 0xE0, Delay = 2 },
                new MacroEvent { Down = true, Code = MouseCodes.Left, Delay = 4 },
                new MacroEvent { Down = false, Code = MouseCodes.Left, Delay = 6 },
                new MacroEvent { Down = false, Code = 0xE0, Delay = 2 },
                new MacroEvent { Down = true, Code = MouseCodes.Right, Delay = 12 },
                new MacroEvent { Down = false, Code = MouseCodes.Right, Delay = 5 },
                MacroEvent.Wheel(-3, 6),
            }
        };
        return slot;
    }

    private void NewMacro_Click(object sender, RoutedEventArgs e)
    {
        if (_recording) return;
        int slot = FreeMacroSlot();
        if (slot < 0) { MessageBox.Show(this, "Все 11 мест для макросов заняты. Удалите ненужный макрос.", Title); return; }
        _selMacro = slot;
        RefreshMacros();
    }

    // ───────────────────────────── recording
    private readonly MacroRecorder _recorder = new();
    private bool _recWired, _recMouse = true;
    private Button? _recBtn;
    private long _recLastMs;

    internal void ToggleRecording()
    {
        _recording = !_recording;
        var slot = Cfg.Macros[_selMacro];
        if (_recording)
        {
            slot.Events = new(); slot.Unsupported = false; _recDown.Clear(); _recClock.Restart(); _recLastMs = 0;
            PreviewKeyDown += RecKeyDown; PreviewKeyUp += RecKeyUp;
            if (!_offline)
            {
                if (!_recWired) { _recorder.Captured += OnRecInput; _recWired = true; }
                _recorder.RecordMouse = _recMouse;
                _recorder.IgnoreAt = InsideRecButton;
                _recorder.Start();
            }
            Focus();
        }
        else
        {
            _recorder.Stop();
            PreviewKeyDown -= RecKeyDown; PreviewKeyUp -= RecKeyUp;
            // drop a dangling press of the «Стоп» click if any slipped through
            Log($"{MacroName(_selMacro)}: записано {slot.Events.Count} шагов.");
            Changed();
        }
        RefreshMacros();
    }

    private bool InsideRecButton(int x, int y)
    {
        var b = _recBtn;
        if (b == null || !b.IsVisible) return false;
        try
        {
            var p0 = b.PointToScreen(new Point(0, 0));
            var p1 = b.PointToScreen(new Point(b.ActualWidth, b.ActualHeight));
            return x >= p0.X - 2 && x <= p1.X + 2 && y >= p0.Y - 2 && y <= p1.Y + 2;
        }
        catch { return false; }
    }

    private void RecKeyDown(object sender, KeyEventArgs e) => RecKey(e, true);
    private void RecKeyUp(object sender, KeyEventArgs e) => RecKey(e, false);

    private void RecKey(KeyEventArgs e, bool down)
    {
        if (!_recording) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!KeyCombo.TryDeviceCode(key, out var code)) return;
        e.Handled = true;                 // keys must not press our own buttons while recording
        if (_recorder.IsRunning) return;  // system-wide hook records them
        AddRecorded(down, code, _recClock.ElapsedMilliseconds, e.IsRepeat);
    }

    private void OnRecInput(MacroRecorder.Input i)
    {
        if (!_recording) return;
        switch (i.Kind)
        {
            case MacroRecorder.Kind.Key:
            case MacroRecorder.Kind.MouseButton:
                AddRecorded(i.Down, i.Code, i.Ms, false);
                break;
            case MacroRecorder.Kind.Wheel:
            {
                var list = Cfg.Macros[_selMacro].Events;
                var last = list.LastOrDefault();
                if (last is { IsWheel: true } && Math.Sign(last.WheelAmount) == Math.Sign(i.Wheel) && i.Ms - _recLastMs < 80 && Math.Abs(last.WheelAmount + i.Wheel) <= 127)
                    last.Extra![0] = (byte)(sbyte)(last.WheelAmount + i.Wheel);
                else list.Add(MacroEvent.Wheel(i.Wheel, Delay(list, i.Ms)));
                _recLastMs = i.Ms;
                BuildMacroEditor();
                break;
            }
            default:
                if (i.Note != null) StatusText.Text = i.Note;
                break;
        }
    }

    private int Delay(List<MacroEvent> list, long ms) => list.Count == 0 ? 2 : (int)Math.Clamp((ms - _recLastMs) / 8, 1, 127);

    private void AddRecorded(bool down, byte code, long ms, bool repeat)
    {
        if (down && (repeat || _recDown.Contains(code))) return;
        if (down) _recDown.Add(code); else if (!_recDown.Remove(code)) return;
        var list = Cfg.Macros[_selMacro].Events;
        list.Add(new MacroEvent { Down = down, Code = code, Delay = Delay(list, ms) });
        _recLastMs = ms;
        BuildMacroEditor();
    }
}
