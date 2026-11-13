using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Vorotex.K13;

namespace Vorotex.Studio;

/// <summary>"Кнопки" page: click a control on the device → «Что делает кнопка N?» → choice tiles → one simple editor.</summary>
public partial class MainWindow
{
    private string? _tile;                 // null = overview with tiles
    private bool _captureKey, _captureCombo;
    private byte? _pendingCode;
    private string? _pendingCombo;
    private HostActionPanel? _pendingHost;
    private TextBlock? _captureText;
    private bool _panelShown;

    private static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly FontFamily Emoji = new("Segoe UI Symbol, Segoe UI Emoji, Segoe UI");

    private Brush R(string key) => (Brush)FindResource(key);

    private TextBlock T(string text, double size = 13, bool semi = false, bool muted = false, double bottom = 0)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, bottom) };
        if (semi) t.FontWeight = FontWeights.SemiBold;
        if (muted) t.Foreground = R("MutedBrush");
        return t;
    }

    private Border Card(UIElement content, double bottom = 14) =>
        new() { Style = (Style)FindResource("Card"), Child = content, Margin = new Thickness(0, 0, 0, bottom) };

    private Button Chip(string icon, string text, bool selected, Action click, string? sub = null, string? tip = null)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon.Length > 0) sp.Children.Add(new TextBlock { Text = icon, FontSize = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, FontFamily = Emoji });
        var tx = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        tx.Children.Add(new TextBlock { Text = text });
        if (sub != null) tx.Children.Add(new TextBlock { Text = sub, FontSize = 11, Foreground = R("MutedBrush"), MaxWidth = 240, TextTrimming = TextTrimming.CharacterEllipsis });
        sp.Children.Add(tx);
        var b = new Button { Style = (Style)FindResource("Chip"), Content = sp, ToolTip = tip };
        if (selected) { b.BorderBrush = R("AccentBrush"); b.Background = R("AccentSoftBrush"); b.BorderThickness = new Thickness(1.5); }
        b.Click += (_, _) => click();
        return b;
    }

    // ───────────────────────────── selection
    private bool IsKeyCol(int id) => id >= 0 && id < K13Layout.FactoryColumnCodes.Length;

    private void OnDeviceClicked(int id)
    {
        if (id == DeviceView.JoystickKnob) id = DeviceView.IsJoystick(_selCol) ? _selCol : 15;
        if (id == DeviceView.WheelBody) id = DeviceView.IsWheel(_selCol) ? _selCol : DeviceView.RollUp;
        SelectControl(id);
    }

    /// <summary>Select a control (or -1 to close the panel).</summary>
    internal void SelectControl(int id)
    {
        _selCol = id; _tile = null; ResetPending();
        RefreshLayout();
    }

    private void ResetPending() { _captureKey = _captureCombo = false; _pendingCode = null; _pendingCombo = null; _pendingHost = null; _captureText = null; }

    private void LayoutBackground_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || _selCol < 0) return;
        SelectControl(-1);
    }

    private void SidePanel_MouseUp(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void ShowPanel(bool show)
    {
        if (show == _panelShown) return;
        _panelShown = show;
        LayoutHint.Visibility = show ? Visibility.Hidden : Visibility.Visible;
        if (!show) { SidePanel.Visibility = Visibility.Collapsed; return; }
        SidePanel.Visibility = Visibility.Visible;
        if (_offline) return;
        SideShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(60, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        SidePanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    // ───────────────────────────── panel
    private void BuildEditor()
    {
        EditorHost.Children.Clear();
        EditorFooter.Children.Clear();
        bool valid = IsKeyCol(_selCol) || DeviceView.IsWheel(_selCol);
        ShowPanel(valid);
        if (!valid) return;

        // title row
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var close = new Button { Style = (Style)FindResource("GhostButton"), Width = 32, Height = 32, Padding = new Thickness(0), ToolTip = "Закрыть",
            Content = new TextBlock { Text = "\uE711", FontFamily = Icons, FontSize = 12 } };
        close.Click += (_, _) => SelectControl(-1);
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close);
        head.Children.Add(new TextBlock { Text = $"Что делает {DeviceView.ControlName(_selCol)}?", FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        EditorHost.Children.Add(head);

        if (_tile == null)
        {
            if (DeviceView.IsStick(_selCol)) EditorHost.Children.Add(StickSwitcher());
            if (DeviceView.IsWheel(_selCol)) EditorHost.Children.Add(WheelSwitcher());
            if (_selCol == DeviceView.StickPress) EditorHost.Children.Add(StickPressNote());
        }

        if (DeviceView.IsWheel(_selCol)) { BuildWheelPanel(); return; }
        if (_tile == null) BuildOverview(); else BuildTileEditor(_tile);
    }

    /// <summary>Big, readable selector: a mini D-pad (↑ ← ● → ↓) where ● is the stick press.</summary>
    private UIElement StickSwitcher()
    {
        var g = new Grid { Margin = new Thickness(0, 10, 0, 4), HorizontalAlignment = HorizontalAlignment.Left };
        for (int i = 0; i < 3; i++) { g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) }); g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(50) }); }
        foreach (var (id, glyph, r, c, tip) in new[] { (15, "\uE70E", 0, 1, "Вверх"), (12, "\uE76B", 1, 0, "Влево"), (DeviceView.StickPress, "", 1, 1, "Нажатие стика"), (13, "\uE76C", 1, 2, "Вправо"), (14, "\uE70D", 2, 1, "Вниз") })
        {
            var b = SwitchCell(id, glyph, tip, id == DeviceView.StickPress);
            Grid.SetRow(b, r); Grid.SetColumn(b, c);
            g.Children.Add(b);
        }
        var cap = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        cap.Children.Add(T("Джойстик", 14, semi: true));
        cap.Children.Add(T("Выберите направление\nили нажатие (центр)", 12, muted: true));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(g); row.Children.Add(cap);
        return row;
    }

    private UIElement WheelSwitcher()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 4) };
        foreach (var (id, glyph, text) in new[] { (DeviceView.RollUp, "\uE74A", "Вверх (Roll+)"), (DeviceView.RollDown, "\uE74B", "Вниз (Roll−)") })
        {
            var b = SwitchCell(id, glyph, text, false, text);
            b.Width = double.NaN; b.Padding = new Thickness(14, 0, 16, 0); b.Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(b);
        }
        return row;
    }

    private Border SwitchCell(int id, string glyph, string tip, bool press, string? text = null)
    {
        bool on = id == _selCol;
        UIElement content;
        var fg = on ? Brushes.White : R("TextBrush");
        if (press)
            content = new Grid { Children = {
                new System.Windows.Shapes.Ellipse { Width = 22, Height = 22, Stroke = fg, StrokeThickness = 2 },
                new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = fg } } };
        else
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 20, Foreground = fg, VerticalAlignment = VerticalAlignment.Center });
            if (text != null) sp.Children.Add(new TextBlock { Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = fg, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            content = sp;
        }
        bool changed = DeviceView.Changed(Cfg, id);
        var grid = new Grid { Children = { content } };
        if (changed) grid.Children.Add(new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = on ? Brushes.White : R("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 5, 0) });
        var b = new Border
        {
            Width = 52, Height = 44, Margin = new Thickness(3), CornerRadius = new CornerRadius(12), Cursor = Cursors.Hand, Child = grid,
            ToolTip = $"{tip}: {DeviceView.ActionOf(Cfg, _profile.Host, id)}",
            Background = on ? R("AccentBrush") : R("Panel2Brush"), BorderBrush = on ? R("AccentBrush") : R("BorderBrush"), BorderThickness = new Thickness(1),
        };
        if (!on)
        {
            b.MouseEnter += (_, _) => b.Background = R("HoverBrush");
            b.MouseLeave += (_, _) => b.Background = R("Panel2Brush");
        }
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (_selCol != id) SelectControl(id); };
        return b;
    }

    private UIElement StickPressNote()
    {
        byte code = Cfg.GetKey(DeviceView.StickPress);
        byte? dev = DeviceKey(DeviceView.StickPress);
        var bind = code is >= 0x68 and <= 0x73 ? _profile.Host.Keys.FirstOrDefault(k => k.TriggerVk == 0x7C + (code - 0x68)) : null;
        bool ring = bind?.Tap.Kind == HostActionKind.Ring;
        string glyph, head, body; Brush color;
        if (dev == 0xF4)
        {
            glyph = "\uE7BA"; color = R("WarnBrush");
            head = "Стик этой клавиатуры ещё не перепрограммирован";
            body = "В клавиатуре в ячейке 16 записана заводская функция KBD Mode (0xF4) — в компьютер ничего не приходит. " +
                   "Чтобы стик открывал кольцо, в ячейку 16 нужно записать F13 (0x68), как на тесте 13.11.2026.";
        }
        else if (code == ProfileMigrations.StickCode && ring)
        {
            glyph = "\uE73E"; color = R("OkBrush");
            head = "Нажатие стика → Кольцо действий";
            body = "Стик посылает F13 (проверено 13.11.2026). Программа перехватывает F13, другие программы его не видят. " +
                   "Зажмите стик, наведите мышь или наклоните стик и отпустите. Короткое нажатие оставит кольцо открытым — щелчок или Esc. " +
                   "Работает, пока программа запущена (можно закрыть окно — она останется в трее)." +
                   (dev == null ? "\nКлавиатура ещё не прочитана — после записи может понадобиться переподключить USB." : "");
        }
        else
        {
            glyph = "\uE946"; color = R("AccentBrush");
            head = "Нажатие стика: " + Labels.For(code, Cfg, _profile.Host).Text;
            body = "Стик посылает F13 (ячейка 16 раскладки). Чтобы вернуть кольцо — выберите плитку «Кольцо действий».";
        }
        var sp = new StackPanel();
        var h = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        h.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 14, Foreground = color, Margin = new Thickness(0, 1, 8, 0) });
        h.Children.Add(T(head, 13, semi: true));
        sp.Children.Add(h);
        sp.Children.Add(T(body, 12, muted: true).With(t => t.Margin = new Thickness(22, 0, 0, 0)));
        var probe = new Button { Style = (Style)FindResource("LinkButton"), Content = "Проверить, что приходит от стика…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(22, 6, 0, 0), FontSize = 13 };
        probe.Click += (_, _) => ProbeKey(DeviceView.StickPress);
        sp.Children.Add(probe);
        return Card(sp, 4).With(c => c.Margin = new Thickness(0, 10, 0, 0));
    }

    private void CurrentBlock(string icon, string text, string? desc, bool changed, Action? reset)
    {
        var cur = new StackPanel { Margin = new Thickness(0, 12, 0, 6) };
        cur.Children.Add(T("Сейчас", 12, muted: true, bottom: 2));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon.Length > 0) row.Children.Add(new TextBlock { Text = icon, FontSize = 24, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, FontFamily = Emoji });
        row.Children.Add(new TextBlock { Text = text, FontSize = 24, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 340 });
        cur.Children.Add(row);
        if (!string.IsNullOrEmpty(desc) && desc != text) cur.Children.Add(T(desc, 13, muted: true));
        if (changed && reset != null)
        {
            var link = new Button { Style = (Style)FindResource("LinkButton"), Content = "Вернуть как было с завода", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0), FontSize = 13 };
            link.Click += (_, _) => reset();
            cur.Children.Add(link);
        }
        EditorHost.Children.Add(cur);
    }

    private static string TileOf(byte code, K13DeviceConfig cfg, HostSettings host)
    {
        if (code == KeyCatalog.Disabled) return "off";
        if (KeyCatalog.IsMacro(code))
        {
            var m = cfg.Macros[KeyCatalog.MacroSlotOf(code)];
            if (Labels.MouseOf(m) != null) return "mouse";
            return Labels.ComboOf(m) != null ? "combo" : "macro";
        }
        if (code is >= 0x68 and <= 0x73 && host.Keys.Any(k => k.TriggerVk == 0x7C + (code - 0x68) && k.Tap.Kind == HostActionKind.Ring)) return "ring";
        if (code is >= 0x68 and <= 0x73 && host.Keys.Any(k => k.TriggerVk == 0x7C + (code - 0x68) && k.Tap.Kind != HostActionKind.None)) return "app";
        return KeyCatalog.Get(code).Category switch
        {
            KeyCategory.Media or KeyCategory.Special => "media",
            KeyCategory.Lighting => "light",
            _ => "key",
        };
    }

    private static readonly (string Id, string Glyph, string Title, string Desc)[] Tiles =
    {
        ("key",   "\uE765", "Нажать клавишу",               "Работает как обычная клавиша: буква, F5, Delete…"),
        ("combo", "\uE8C8", "Сочетание клавиш",             "Например Ctrl+C или Alt+Tab"),
        ("media", "\uE8D6", "Мультимедиа",                  "Громкость, пауза, следующий трек"),
        ("app",   "\uE8A7", "Запустить программу или сайт", "Открыть программу, сайт или вставить текст"),
        ("ring",  "\uE71D", "Кольцо действий",              "Круговое меню у курсора: копировать, скриншот, плеер…"),
        ("mouse", "\uE962", "Кнопка мыши",                  "Щелчок левой/правой/средней или автокликер"),
        ("macro", "\uE768", "Макрос",                       "Несколько нажатий подряд одной кнопкой"),
        ("light", "\uE706", "Управление подсветкой",        "Сменить эффект, цвет или яркость"),
        ("off",   "\uE711", "Отключить",                    "Кнопка не будет ничего делать"),
    };

    private void BuildOverview()
    {
        var ctl = K13Layout.ByColumn(_selCol);
        byte code = Cfg.GetKey(_selCol);
        var (icon, text) = Labels.For(code, Cfg, _profile.Host);
        CurrentBlock(icon, text, ActionText.Describe(code, Cfg, _profile.Host), code != ctl.FactoryCode,
            () => { Cfg.SetKey(_selCol, ctl.FactoryCode); Changed(); RefreshMacros(); });

        EditorHost.Children.Add(T("Выберите, что она будет делать:", 13, muted: true, bottom: 8).With(t => t.Margin = new Thickness(0, 14, 0, 8)));
        string cur = TileOf(code, Cfg, _profile.Host);
        var order = Tiles.ToList();
        if (_selCol == DeviceView.StickPress) { var ring = order.First(t => t.Id == "ring"); order.Remove(ring); order.Insert(0, ring); }
        foreach (var (id, glyph, title, desc) in order)
            EditorHost.Children.Add(Tile(glyph, title, desc, id == cur, () => OpenTile(id), id == "ring" && _selCol == DeviceView.StickPress && cur != "ring" ? "рекомендуем" : null));
        if (DeviceView.IsJoystick(_selCol))
            EditorHost.Children.Add(Tile("\uE7AD", "8 направлений (диагонали)", "Джойстик научится нажимать по диагонали", false, () => OpenTile("diag")));
    }

    private Border Tile(string glyph, string title, string desc, bool current, Action click, string? tag = null)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var ic = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(10), Background = current ? R("AccentBrush") : R("AccentSoftBrush"), Margin = new Thickness(0, 0, 14, 0),
            Child = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 16, Foreground = current ? Brushes.White : R("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        var tx = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var tl = new StackPanel { Orientation = Orientation.Horizontal };
        tl.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold });
        if (current) tl.Children.Add(new Border { CornerRadius = new CornerRadius(8), Background = R("AccentSoftBrush"), Padding = new Thickness(7, 0, 7, 1), Margin = new Thickness(8, 1, 0, 0),
            Child = new TextBlock { Text = "сейчас", FontSize = 11, Foreground = R("AccentBrush") } });
        else if (tag != null) tl.Children.Add(new Border { CornerRadius = new CornerRadius(8), Background = R("AccentBrush"), Padding = new Thickness(7, 0, 7, 1), Margin = new Thickness(8, 1, 0, 0),
            Child = new TextBlock { Text = tag, FontSize = 11, Foreground = Brushes.White } });
        tx.Children.Add(tl);
        tx.Children.Add(new TextBlock { Text = desc, FontSize = 12, Foreground = R("MutedBrush"), TextWrapping = TextWrapping.Wrap });
        var chev = new TextBlock { Text = "\uE76C", FontFamily = Icons, FontSize = 12, Foreground = R("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(tx, 1); Grid.SetColumn(chev, 2);
        g.Children.Add(ic); g.Children.Add(tx); g.Children.Add(chev);
        var b = new Border { Child = g, Padding = new Thickness(12, 10, 12, 10), CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 0, 6), Cursor = Cursors.Hand,
            Background = R("PanelBrush"), BorderBrush = current ? R("AccentBrush") : R("BorderBrush"), BorderThickness = new Thickness(current ? 1.5 : 1) };
        var shift = new TranslateTransform();
        b.RenderTransform = shift;
        b.MouseEnter += (_, _) => { b.Background = R("HoverBrush"); shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(3, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase() }); };
        b.MouseLeave += (_, _) => { b.Background = R("PanelBrush"); shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase() }); };
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        return b;
    }

    /// <summary>Scrolls the side panel (used by the screenshot renderer).</summary>
    internal void ScrollEditor(double offset)
    {
        UpdateLayout();
        DependencyObject? d = EditorHost;
        while (d != null && d is not ScrollViewer) d = VisualTreeHelper.GetParent(d);
        (d as ScrollViewer)?.ScrollToVerticalOffset(offset);
    }

    internal void OpenTile(string? id)
    {
        ResetPending();
        _tile = id;
        BuildEditor();
    }

    // ───────────────────────────── tile editors
    private void BuildTileEditor(string tile)
    {
        var info = Tiles.FirstOrDefault(t => t.Id == tile);
        string title = tile == "diag" ? "8 направлений (диагонали)" : info.Title;
        if (tile == "ring") { BuildRingTile(); return; }
        EditorHost.Children.Add(T(title, 16, semi: true, bottom: 12).With(t => { t.Margin = new Thickness(0, 14, 0, 12); t.Foreground = R("AccentBrush"); }));
        byte code = Cfg.GetKey(_selCol);

        Action? done = null;
        switch (tile)
        {
            case "key": done = KeyEditor(code); break;
            case "combo": done = ComboEditor(code); break;
            case "media": done = PickGrid(code, k => k.Category is KeyCategory.Media or KeyCategory.Special, "Выберите действие:"); break;
            case "light": done = PickGrid(code, k => k.Category == KeyCategory.Lighting, "Что делать с подсветкой при нажатии:"); break;
            case "app": done = AppEditor(code); break;
            case "macro": done = MacroPicker(code); break;
            case "mouse": done = MouseEditor(code); break;
            case "diag":
                EditorHost.Children.Add(T("Нажатие двух направлений сразу (например ↑ и →) будет работать как отдельная кнопка-диагональ. " +
                                          "По умолчанию диагонали — Home, PgUp, End, PgDn; их можно поменять в «⋯ → Действия на компьютере».", 13, bottom: 8));
                EditorHost.Children.Add(T("Работает, пока программа запущена (можно свернуть в трей).", 12, muted: true));
                done = SetupDiagonals;
                break;
            default:
                EditorHost.Children.Add(T("Кнопка перестанет что-либо делать. Вернуть её можно в любой момент.", 13));
                done = () => SetCode(KeyCatalog.Disabled);
                break;
        }

        var err = T("", 12); err.Foreground = R("ErrBrush"); err.Margin = new Thickness(0, 0, 0, 6);
        EditorFooter.Children.Add(err);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var ok = new Button { Content = "Готово", Style = (Style)FindResource("AccentButton"), MinWidth = 110, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Назад", Style = (Style)FindResource("GhostButton"), MinWidth = 90 };
        ok.Click += (_, _) =>
        {
            _lastError = null;
            done?.Invoke();
            if (_lastError != null) { err.Text = _lastError; return; }
            OpenTile(null);
        };
        cancel.Click += (_, _) => OpenTile(null);
        row.Children.Add(ok); row.Children.Add(cancel);
        EditorFooter.Children.Add(row);
    }

    private string? _lastError;

    private Border CaptureBox(string prompt, string? value)
    {
        _captureText = new TextBlock { Text = value ?? prompt, FontSize = value != null ? 26 : 14, FontWeight = value != null ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = value != null ? R("TextBrush") : R("MutedBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new TextBlock { Text = "\uE765", FontFamily = Icons, FontSize = 20, Foreground = R("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) });
        sp.Children.Add(_captureText);
        var b = new Border { Height = 120, CornerRadius = new CornerRadius(14), Background = R("AccentSoftBrush"), BorderBrush = R("AccentBrush"), BorderThickness = new Thickness(1.5), Child = sp,
            Focusable = true, Cursor = Cursors.Hand, ToolTip = "Щёлкните здесь и нажмите на клавиатуре" };
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; b.Focus(); if (_tile == "key") _captureKey = true; else _captureCombo = true; };
        Dispatcher.BeginInvoke(() => { if (b.IsLoaded) b.Focus(); }, System.Windows.Threading.DispatcherPriority.Input);
        return b;
    }

    private void SetCaptured(string text)
    {
        if (_captureText == null) return;
        _captureText.Text = text; _captureText.FontSize = 24; _captureText.FontWeight = FontWeights.SemiBold; _captureText.Foreground = R("TextBrush");
    }

    private Action KeyEditor(byte code)
    {
        bool isKey = TileOf(code, Cfg, _profile.Host) == "key";
        EditorHost.Children.Add(CaptureBox("Нажмите нужную клавишу\nна обычной клавиатуре", isKey ? Labels.KeyText(code) : null));
        _captureKey = true;
        EditorHost.Children.Add(T("или найдите в списке:", 13, muted: true).With(t => t.Margin = new Thickness(0, 16, 0, 6)));
        var search = new TextBox { ToolTip = "Например: F5, Delete, Home, Num 1" };
        search.GotKeyboardFocus += (_, _) => _captureKey = false;
        var list = new ListBox { Height = 200, Margin = new Thickness(0, 6, 0, 0) };
        var cats = new[] { KeyCategory.Letter, KeyCategory.Digit, KeyCategory.Function, KeyCategory.Navigation, KeyCategory.Editing, KeyCategory.Numpad, KeyCategory.Modifier, KeyCategory.Symbol };
        var all = KeyCatalog.All.Where(k => cats.Contains(k.Category)).OrderBy(k => k.Category).ThenBy(k => k.Code).ToList();
        void Fill()
        {
            var q = search.Text.Trim();
            list.Items.Clear();
            foreach (var k in all.Where(k => q.Length == 0 || k.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || KeyCatalog.CategoryTitle(k.Category).Contains(q, StringComparison.OrdinalIgnoreCase)))
            {
                var dp = new DockPanel();
                var cat = new TextBlock { Text = KeyCatalog.CategoryTitle(k.Category), Foreground = R("MutedBrush"), FontSize = 12 };
                DockPanel.SetDock(cat, Dock.Right);
                dp.Children.Add(cat); dp.Children.Add(new TextBlock { Text = k.Name });
                var it = new ListBoxItem { Content = dp, Tag = k.Code };
                list.Items.Add(it);
            }
        }
        search.TextChanged += (_, _) => Fill();
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is ListBoxItem it) { _pendingCode = (byte)it.Tag; SetCaptured(KeyCatalog.Name(_pendingCode.Value)); } };
        Fill();
        EditorHost.Children.Add(search);
        EditorHost.Children.Add(list);
        return () => { if (_pendingCode is byte c) SetCode(c); };
    }

    private Action ComboEditor(byte code)
    {
        var cur = KeyCatalog.IsMacro(code) ? Labels.ComboOf(Cfg.Macros[KeyCatalog.MacroSlotOf(code)]) : null;
        EditorHost.Children.Add(CaptureBox("Зажмите и нажмите сочетание\nна обычной клавиатуре", cur));
        _captureCombo = true;
        EditorHost.Children.Add(T("Часто используемые:", 13, muted: true).With(t => t.Margin = new Thickness(0, 16, 0, 6)));
        var wp = new WrapPanel();
        foreach (var (c, d) in new[] { ("Ctrl+C", "копировать"), ("Ctrl+V", "вставить"), ("Ctrl+Z", "отменить"), ("Ctrl+S", "сохранить"), ("Alt+Tab", "окна"), ("Win+D", "рабочий стол"), ("Win+Shift+S", "скриншот"), ("Ctrl+Shift+Esc", "диспетчер") })
        {
            string combo = c;
            wp.Children.Add(Chip("", c, combo == cur, () => { _pendingCombo = combo; SetCaptured(combo); }, d));
        }
        EditorHost.Children.Add(wp);
        EditorHost.Children.Add(T("Сочетание хранится в самой клавиатуре и работает даже без этой программы.", 12, muted: true).With(t => t.Margin = new Thickness(0, 10, 0, 0)));
        return () => { if (_pendingCombo != null) _lastError = ApplyCombo(_pendingCombo); };
    }

    private Action PickGrid(byte code, Func<KeyInfo, bool> filter, string prompt)
    {
        EditorHost.Children.Add(T(prompt, 13, muted: true, bottom: 8));
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3 };
        var cells = new List<(byte, Border)>();
        foreach (var k in KeyCatalog.All.Where(filter).OrderBy(k => k.Category).ThenBy(k => k.Code))
        {
            var (ic, tx) = Labels.For(k.Code, Cfg, _profile.Host);
            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock { Text = ic.Length > 0 ? ic : "•", FontFamily = Emoji, FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = tx, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0) });
            var cell = new Border { Child = sp, Height = 72, Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand, ToolTip = k.Name,
                BorderThickness = new Thickness(1.5) };
            byte c = k.Code;
            cells.Add((c, cell));
            cell.MouseLeftButtonUp += (_, e) => { e.Handled = true; _pendingCode = c; Paint(); };
            grid.Children.Add(cell);
        }
        void Paint()
        {
            byte sel = _pendingCode ?? code;
            foreach (var (c, cell) in cells)
            {
                cell.Background = c == sel ? R("AccentSoftBrush") : R("Panel2Brush");
                cell.BorderBrush = c == sel ? R("AccentBrush") : Brushes.Transparent;
            }
        }
        Paint();
        EditorHost.Children.Add(grid);
        return () => { if (_pendingCode is byte c) SetCode(c); };
    }

    private Action AppEditor(byte code)
    {
        HostAction tap = new() { Kind = HostActionKind.LaunchProgram }, hold = new(); int holdMs = 400;
        if (code is >= 0x68 and <= 0x73)
        {
            var b = _profile.Host.Keys.FirstOrDefault(k => k.TriggerVk == 0x7C + (code - 0x68));
            if (b != null && b.Tap.Kind != HostActionKind.None) { tap = b.Tap; hold = b.Hold; holdMs = b.HoldMs; }
        }
        _pendingHost = new HostActionPanel(tap, ProfileNames);
        EditorHost.Children.Add(_pendingHost);
        EditorHost.Children.Add(T("Работает, пока программа запущена (её можно свернуть в трей).", 12, muted: true).With(t => t.Margin = new Thickness(0, 12, 0, 0)));
        return () =>
        {
            if (_pendingHost == null) return;
            if (!_pendingHost.TryGet(out var a, out var e1)) { _lastError = e1; return; }
            _lastError = ApplyHost(a, hold, holdMs);
        };
    }

    private Action MacroPicker(byte code)
    {
        var used = Enumerable.Range(0, KeyCatalog.MacroCodeCount).Where(i => Cfg.Macros[i].IsUsed).ToList();
        if (used.Count == 0) EditorHost.Children.Add(T("Макросов пока нет.", 13, muted: true, bottom: 8));
        else EditorHost.Children.Add(T("Выберите готовый макрос:", 13, muted: true, bottom: 8));
        var rows = new List<(byte, Border)>();
        foreach (var i in used)
        {
            byte c = (byte)(KeyCatalog.FirstMacro + i);
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = MacroName(i), FontWeight = FontWeights.SemiBold });
            sp.Children.Add(new TextBlock { Text = ActionText.MacroSummary(Cfg.Macros[i]), FontSize = 12, Foreground = R("MutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
            var b = new Border { Child = sp, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand, BorderThickness = new Thickness(1.5) };
            rows.Add((c, b));
            b.MouseLeftButtonUp += (_, e) => { e.Handled = true; _pendingCode = c; Paint(); };
            EditorHost.Children.Add(b);
        }
        void Paint()
        {
            byte sel = _pendingCode ?? code;
            foreach (var (c, b) in rows) { b.Background = c == sel ? R("AccentSoftBrush") : R("Panel2Brush"); b.BorderBrush = c == sel ? R("AccentBrush") : Brushes.Transparent; }
        }
        Paint();
        var rec = new Button { Content = "●  Записать новый", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        rec.Click += (_, _) =>
        {
            int slot = FreeMacroSlot();
            if (slot < 0) { MessageBox.Show(this, "Все 11 мест для макросов заняты. Удалите ненужный на вкладке «Макросы».", Title); return; }
            Cfg.SetKey(_selCol, (byte)(KeyCatalog.FirstMacro + slot));
            Changed();
            _selMacro = slot; _tile = null;
            RefreshMacros();
            ShowPage(2);
            StatusText.Text = $"Новый макрос назначен на {DeviceView.ControlName(_selCol)}. Нажмите «Запись».";
        };
        EditorHost.Children.Add(rec);
        return () => { if (_pendingCode is byte c) SetCode(c); };
    }

    // ───────────────────────────── wheel
    private void BuildWheelPanel()
    {
        bool up = _selCol == DeviceView.RollUp;
        int cur = (up ? Cfg.WheelSecond : Cfg.WheelFirst) & 7;
        int factory = up ? 1 : 0;
        CurrentBlock("", K13Layout.WheelFunctions[cur], up ? "При повороте колеса вверх" : "При повороте колеса вниз", cur != factory,
            () => { if (up) Cfg.WheelSecond = factory; else Cfg.WheelFirst = factory; Changed(); });
        EditorHost.Children.Add(T("Выберите, что будет делать колесо:", 13, muted: true).With(t => t.Margin = new Thickness(0, 14, 0, 8)));
        string[] glyphs = { "\uE993", "\uE995", "\uE70E", "\uE70D", "\uE7C1", "\uE7C1", "\uE706", "\uE706" };
        string[] desc = { "Тише", "Громче", "Как колесо мыши вверх", "Как колесо мыши вниз", "Эффект подсветки быстрее", "Эффект подсветки медленнее", "Подсветка ярче", "Подсветка тусклее" };
        for (int i = 0; i < K13Layout.WheelFunctions.Length; i++)
        {
            int f = i;
            EditorHost.Children.Add(Tile(glyphs[i], K13Layout.WheelFunctions[i], desc[i], cur == i, () =>
            {
                if (up) Cfg.WheelSecond = f; else Cfg.WheelFirst = f;
                Changed();
            }));
        }
        EditorHost.Children.Add(T("Какое направление вращения считается «вверх», уточним на первом тесте записи.", 12, muted: true).With(t => t.Margin = new Thickness(0, 8, 0, 0)));
    }

    // ───────────────────────────── keyboard capture
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_captureKey && !_captureCombo) return;
        if (Keyboard.FocusedElement is TextBox or ListBox or ListBoxItem) return;
        var k = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = true;
        if (_captureKey)
        {
            if (KeyCombo.TryDeviceCode(k, out var code)) { _pendingCode = code; SetCaptured(KeyCatalog.Name(code)); }
            else SetCaptured("Эту клавишу назначить нельзя");
            return;
        }
        var combo = Ui.ComboFromKeyEvent(e);
        if (k is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        { SetCaptured(combo.Length > 0 ? combo + "+…" : "…"); return; }
        _pendingCombo = combo;
        SetCaptured(combo);
    }

    // ───────────────────────────── apply
    private void SetCode(byte code)
    {
        if (!IsKeyCol(_selCol)) return;
        if (Cfg.GetKey(_selCol) == code) return;
        Cfg.SetKey(_selCol, code);
        Log($"{DeviceView.ControlName(_selCol)}: {KeyCatalog.Name(code)}.");
        Changed();
        RefreshMacros();
    }

    private string? ApplyCombo(string combo)
    {
        List<byte> codes;
        try
        {
            var vks = KeyCombo.Parse(combo);
            if (vks.Count == 0) return "Пустое сочетание.";
            codes = new();
            foreach (var vk in vks)
            {
                if (!KeyCatalog.TryFromVirtualKey(vk, out var cd)) return $"Клавишу {KeyCombo.VkName(vk)} нельзя записать в сочетание.";
                codes.Add(cd);
            }
        }
        catch (Exception ex) { return ex.Message; }
        byte current = Cfg.GetKey(_selCol);
        int slot = KeyCatalog.IsMacro(current) && Labels.ComboOf(Cfg.Macros[KeyCatalog.MacroSlotOf(current)]) != null ? KeyCatalog.MacroSlotOf(current) : FreeMacroSlot();
        if (slot < 0) return "Нет свободного места для сочетания (все 11 макросов заняты).";
        var ev = new List<MacroEvent>();
        foreach (var cd in codes) ev.Add(new MacroEvent { Down = true, Delay = 1, Code = cd });
        for (int i = codes.Count - 1; i >= 0; i--) ev.Add(new MacroEvent { Down = false, Delay = 1, Code = codes[i] });
        var m = Cfg.Macros[slot];
        m.Events = ev; m.Repeat = 1; m.RepeatMode = MacroRepeatMode.Count;
        Cfg.SetKey(_selCol, (byte)(KeyCatalog.FirstMacro + slot));
        Log($"{DeviceView.ControlName(_selCol)}: сочетание {combo} → макрос {slot}.");
        Changed(); RefreshMacros();
        return null;
    }

    private string? ApplyHost(HostAction tap, HostAction hold, int holdMs)
    {
        byte current = Cfg.GetKey(_selCol);
        byte trig = current is >= 0x68 and <= 0x73 ? current : FreeTriggerCode();
        if (trig == 0) return "Все служебные клавиши F13–F24 уже заняты.";
        ushort vk = (ushort)(0x7C + (trig - 0x68));
        var b = _profile.Host.Keys.FirstOrDefault(k => k.TriggerVk == vk);
        if (b == null) { b = new HostKeyBinding { TriggerVk = vk }; _profile.Host.Keys.Add(b); }
        b.Tap = tap; b.Hold = hold; b.HoldMs = holdMs;
        _profile.Host.Enabled = true;
        Cfg.SetKey(_selCol, trig);
        Log($"{DeviceView.ControlName(_selCol)}: {KeyCatalog.Name(trig)} → {tap}.");
        Changed(); RefreshHostSummary();
        return null;
    }

    private int FreeMacroSlot()
    {
        for (int i = 0; i < KeyCatalog.MacroCodeCount; i++)
        {
            byte code = (byte)(KeyCatalog.FirstMacro + i);
            bool assigned = Enumerable.Range(0, K13Layout.KeymapColumns).Any(col => Cfg.GetKey(col) == code);
            if (!Cfg.Macros[i].IsUsed && !assigned) return i;
        }
        return -1;
    }

    private byte FreeTriggerCode()
    {
        for (byte code = 0x68; code <= 0x73; code++)
        {
            bool usedKey = Enumerable.Range(0, K13Layout.KeymapColumns).Any(col => Cfg.GetKey(col) == code);
            bool usedHost = _profile.Host.Keys.Any(k => k.TriggerVk == 0x7C + (code - 0x68) && (k.Tap.Kind != HostActionKind.None || k.Hold.Kind != HostActionKind.None));
            if (!usedKey && !usedHost) return code;
        }
        return 0;
    }
}

internal static class UiExt
{
    public static T With<T>(this T x, Action<T> a) { a(x); return x; }
}
