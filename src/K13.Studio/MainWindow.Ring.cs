using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Vorotex.K13;

namespace Vorotex.Studio;

/// <summary>«Кольцо действий» tile, the stick-press probe and the «Кнопка мыши» tile.</summary>
public partial class MainWindow
{
    private int _ringSel;

    internal static readonly string[] RingGlyphs =
    {
        "\uE8C8", "\uE77F", "\uE8C6", "\uE7A7", "\uE722", "\uE9D9", "\uE7F4", "\uE72E",
        "\uE768", "\uE893", "\uE892", "\uE767", "\uE74F", "\uE774", "\uE715", "\uE8B7",
        "\uE713", "\uE721", "\uE8A7", "\uE734", "\uE70F", "\uE8F1", "\uE962", "\uE765",
    };

    private static readonly (string Title, string Glyph, HostActionKind Kind, string Value)[] RingPresets =
    {
        ("Копировать", "\uE8C8", HostActionKind.KeyCombo, "Ctrl+C"), ("Вставить", "\uE77F", HostActionKind.KeyCombo, "Ctrl+V"),
        ("Вырезать", "\uE8C6", HostActionKind.KeyCombo, "Ctrl+X"), ("Отменить", "\uE7A7", HostActionKind.KeyCombo, "Ctrl+Z"),
        ("Скриншот", "\uE722", HostActionKind.KeyCombo, "Win+Shift+S"), ("Диспетчер задач", "\uE9D9", HostActionKind.KeyCombo, "Ctrl+Shift+Esc"),
        ("Рабочий стол", "\uE7F4", HostActionKind.KeyCombo, "Win+D"), ("Заблокировать", "\uE72E", HostActionKind.KeyCombo, "Win+L"),
        ("Плеер", "\uE768", HostActionKind.KeyCombo, "PlayPause"), ("Следующий трек", "\uE893", HostActionKind.KeyCombo, "Next"),
        ("Предыдущий трек", "\uE892", HostActionKind.KeyCombo, "Prev"), ("Без звука", "\uE74F", HostActionKind.KeyCombo, "Mute"),
        ("Браузер", "\uE774", HostActionKind.KeyCombo, "BrowserHome"), ("Почта", "\uE715", HostActionKind.KeyCombo, "Mail"),
        ("Проводник", "\uE8B7", HostActionKind.KeyCombo, "Win+E"), ("Параметры", "\uE713", HostActionKind.KeyCombo, "Win+I"),
    };

    // ───────────────────────────── ring tile
    private void BuildRingTile()
    {
        var ring = _profile.Host.Ring;
        if (ring.Slots.Count == 0) ring.Slots = RingSettings.CreateDefault().Slots;
        _ringSel = Math.Clamp(_ringSel, 0, ring.Slots.Count - 1);
        EditorHost.Children.Add(T("Кольцо действий", 17, semi: true).With(t => { t.Margin = new Thickness(0, 14, 0, 4); t.Foreground = R("AccentBrush"); }));
        EditorHost.Children.Add(T("Зажмите кнопку — у курсора появится кольцо. Наведите мышь (или наклоните стик) и отпустите — действие выполнится. " +
                                  "Короткое нажатие (до 0,25 с, без движения) оставит кольцо открытым — выберите щелчком. Esc, × или щелчок мимо — отмена.", 12.5, muted: true, bottom: 6));

        // preview
        var view = new RingView(App.IsDark, tooltips: false, editor: true) { CenterHint = "Выберите ячейку" };
        view.SetSlots(ring.Slots);
        view.SetHighlightNow(_ringSel);
        view.ItemClicked += i =>
        {   // rebuild after the click finished (rebuilding inside MouseUp left a stale tooltip/hover on screen — «зависание»)
            if (i == _ringSel) return;
            _ringSel = i; App.Log($"[editor] кольцо: выбрана ячейка {i + 1}");
            Dispatcher.BeginInvoke(BuildEditor, System.Windows.Threading.DispatcherPriority.Background);
        };
        EditorHost.Children.Add(new Viewbox { Child = view, Width = 300, Height = 300, Margin = new Thickness(0, 4, 0, 6), HorizontalAlignment = HorizontalAlignment.Center });

        var countRow = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) };
        Button Small(string text, bool enabled, Action a)
        {
            var b = new Button { Style = (Style)FindResource("GhostButton"), Content = text, IsEnabled = enabled, Margin = new Thickness(3, 0, 3, 0), Padding = new Thickness(10, 4, 10, 4), FontSize = 12.5 };
            b.Click += (_, _) => a();
            return b;
        }
        countRow.Children.Add(Small("+ Добавить", ring.Slots.Count < RingSettings.MaxSlots, () =>
        { ring.Slots.Add(new RingSlot { Title = "Новое", Glyph = "\uE734" }); _ringSel = ring.Slots.Count - 1; Changed(false); BuildEditor(); }));
        countRow.Children.Add(Small("− Убрать", ring.Slots.Count > RingSettings.MinSlots, () =>
        { ring.Slots.RemoveAt(_ringSel); _ringSel = Math.Max(0, _ringSel - 1); Changed(false); BuildEditor(); }));
        countRow.Children.Add(Small("Стандартное", true, () =>
        { ring.Slots = RingSettings.CreateDefault().Slots; _ringSel = 0; Changed(false); BuildEditor(); }));
        EditorHost.Children.Add(countRow);
        EditorHost.Children.Add(RingTriggerStatus());

        // slot editor
        var slot = ring.Slots[_ringSel];
        var card = new StackPanel();
        card.Children.Add(T($"Ячейка {_ringSel + 1}", 14, semi: true, bottom: 8));
        var title = new TextBox { Text = slot.Title, ToolTip = "Подпись в кольце" };
        title.TextChanged += (_, _) => { slot.Title = title.Text; Changed(false); };
        card.Children.Add(T("Подпись", 12, muted: true, bottom: 3));
        card.Children.Add(title);

        card.Children.Add(T("Значок", 12, muted: true, bottom: 3).With(t => t.Margin = new Thickness(0, 10, 0, 3)));
        var icons = new WrapPanel();
        foreach (var g in RingGlyphs)
        {
            string glyph = g;
            bool on = slot.Glyph == g;
            var b = new Border { Width = 34, Height = 34, Margin = new Thickness(0, 0, 4, 4), CornerRadius = new CornerRadius(9), Cursor = Cursors.Hand,
                Background = on ? R("AccentBrush") : R("Panel2Brush"), BorderBrush = R("BorderBrush"), BorderThickness = new Thickness(on ? 0 : 1),
                Child = new TextBlock { Text = g, FontFamily = Icons, FontSize = 15, Foreground = on ? Brushes.White : R("TextBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            b.MouseLeftButtonUp += (_, e) => { e.Handled = true; slot.Glyph = glyph; Changed(false); BuildEditor(); };
            icons.Children.Add(b);
        }
        card.Children.Add(icons);

        card.Children.Add(T("Готовые действия", 12, muted: true, bottom: 3).With(t => t.Margin = new Thickness(0, 10, 0, 3)));
        var presets = new WrapPanel();
        foreach (var p in RingPresets)
        {
            var pp = p;
            bool on = slot.Action.Kind == p.Kind && slot.Action.Value == p.Value;
            presets.Children.Add(Chip("", p.Title, on, () =>
            {
                slot.Title = pp.Title; slot.Glyph = pp.Glyph; slot.Action = new HostAction { Kind = pp.Kind, Value = pp.Value };
                Changed(false); BuildEditor();
            }).With(c => { c.Margin = new Thickness(0, 0, 5, 5); c.Padding = new Thickness(10, 4, 10, 4); }));
        }
        foreach (var i in Enumerable.Range(0, KeyCatalog.MacroCodeCount).Where(i => Cfg.Macros[i].IsUsed))
        {
            int mi = i;
            bool on = slot.Action.Kind == HostActionKind.PlayMacro && slot.Action.Value == i.ToString();
            presets.Children.Add(Chip("", MacroName(i), on, () =>
            {
                slot.Title = MacroName(mi); slot.Glyph = "\uE768"; slot.Action = new HostAction { Kind = HostActionKind.PlayMacro, Value = mi.ToString() };
                Changed(false); BuildEditor();
            }, tip: ActionText.MacroSummary(Cfg.Macros[i])).With(c => { c.Margin = new Thickness(0, 0, 5, 5); c.Padding = new Thickness(10, 4, 10, 4); }));
        }
        card.Children.Add(presets);

        card.Children.Add(T("Или своё: программа, сайт, текст, сочетание, профиль", 12, muted: true, bottom: 3).With(t => t.Margin = new Thickness(0, 10, 0, 3)));
        var own = new HostActionPanel(slot.Action.Kind is HostActionKind.PlayMacro ? new HostAction() : slot.Action, ProfileNames);
        card.Children.Add(own);
        var err = T("", 12); err.Foreground = R("ErrBrush");
        var save = new Button { Content = "Применить к ячейке", Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        save.Click += (_, _) =>
        {
            if (!own.TryGet(out var a, out var e1)) { err.Text = e1; return; }
            slot.Action = a; Changed(false); BuildEditor();
        };
        card.Children.Add(save); card.Children.Add(err);
        card.Children.Add(T("Сейчас: " + slot.Action, 12, muted: true).With(t => t.Margin = new Thickness(0, 6, 0, 0)));
        EditorHost.Children.Add(Card(card));

        var dark = new CheckBox { Content = "Тёмное кольцо", IsChecked = ring.DarkStyle, Margin = new Thickness(0, 0, 0, 6) };
        dark.Checked += (_, _) => { ring.DarkStyle = true; Changed(false); };
        dark.Unchecked += (_, _) => { ring.DarkStyle = false; Changed(false); };
        EditorHost.Children.Add(dark);
        var test = new Button { Content = "Показать кольцо на экране", HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = _ringOverlay != null, Margin = new Thickness(0, 0, 0, 6) };
        test.Click += (_, _) => { _ringOverlay?.TriggerDown(ring); _ringOverlay?.TriggerUp(0); };
        EditorHost.Children.Add(test);

        // footer
        bool assigned = TileOf(Cfg.GetKey(_selCol), Cfg, _profile.Host) == "ring";
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var ferr = T("", 12); ferr.Foreground = R("ErrBrush");
        var ok = new Button { Content = assigned ? "Готово" : "Назначить кольцо", Style = (Style)FindResource("AccentButton"), MinWidth = 140, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) =>
        {
            if (!assigned) { var e2 = ApplyHost(new HostAction { Kind = HostActionKind.Ring }, new HostAction(), 400); if (e2 != null) { ferr.Text = e2; return; } }
            OpenTile(null);
        };
        var back = new Button { Content = "Назад", Style = (Style)FindResource("GhostButton"), MinWidth = 90 };
        back.Click += (_, _) => OpenTile(null);
        row.Children.Add(ok); row.Children.Add(back);
        EditorFooter.Children.Add(ferr);
        EditorFooter.Children.Add(row);
    }

    private ushort? ProbedTriggerFor(int col) => _profile.Host.Keys.FirstOrDefault(k => k.Tap.Kind == HostActionKind.Ring && !(k.TriggerVk is >= 0x7C and <= 0x87))?.TriggerVk;

    private UIElement RingTriggerStatus()
    {
        byte code = Cfg.GetKey(_selCol);
        bool isTrig = code is >= 0x68 and <= 0x73 && _profile.Host.Keys.Any(k => k.TriggerVk == 0x7C + (code - 0x68) && k.Tap.Kind == HostActionKind.Ring);
        byte? onDevice = DeviceKey(_selCol);
        bool needsWrite = isTrig && onDevice != code;
        var probed = ProbedTriggerFor(_selCol);
        string head, body; string glyph; Brush color;
        if (isTrig && !needsWrite) { glyph = "\uE73E"; color = R("OkBrush"); head = $"Готово: кнопка посылает {KeyCatalog.Name(code)} — программа откроет кольцо"; body = "Программа перехватывает эту клавишу. Работает, пока программа запущена (можно закрыть окно — она останется в трее)."; }
        else if (probed is ushort vk) { glyph = "\uE73E"; color = R("OkBrush"); head = $"Кольцо откроется по клавише {KeyCombo.VkName(vk)} (проверено)"; body = "Эту клавишу программа перехватывает и не пропускает дальше."; }
        else
        {
            glyph = "\uE7BA"; color = R("WarnBrush");
            head = isTrig ? $"Нужна запись: клавиатура должна посылать {KeyCatalog.Name(code)}" : "Кнопка должна посылать служебную клавишу F13–F24";
            body = $"Сейчас {DeviceView.ControlName(_selCol)} посылает «{KeyCatalog.Name(onDevice ?? code)}». " +
                   "Программа назначит свободную F13–F24, в клавиатуру это попадёт после нажатия «Записать». " +
                   "Если кнопка уже посылает какую-то клавишу — нажмите «Проверить» и используйте её.";
        }
        var sp = new StackPanel();
        var h = new DockPanel();
        h.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 14, Foreground = color, Margin = new Thickness(0, 1, 8, 0) });
        h.Children.Add(T(head, 13, semi: true));
        sp.Children.Add(h);
        sp.Children.Add(T(body, 12, muted: true).With(t => t.Margin = new Thickness(22, 2, 0, 0)));
        var probe = new Button { Style = (Style)FindResource("LinkButton"), Content = "Проверить, что посылает кнопка…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(22, 6, 0, 0), FontSize = 12.5 };
        probe.Click += (_, _) => ProbeKey(_selCol);
        sp.Children.Add(probe);
        return Card(sp, 8);
    }

    /// <summary>The keymap code currently stored in the keypad (from the last read), if known.</summary>
    private byte? DeviceKey(int col)
    {
        if (_deviceImage == null) return null;
        try { return K13MemoryCodec.Decode(_deviceImage).GetKey(col); } catch { return null; }
    }

    /// <summary>Waits for the next physical key and offers to use it as the ring trigger (no device writes).</summary>
    internal void ProbeKey(int col)
    {
        var w = Ui.MakeWindow(this, "Что посылает кнопка", 460, 300);
        var sp = new StackPanel { Margin = new Thickness(22) };
        var title = T($"Нажмите на VOROTEX: {DeviceView.ControlName(col)}", 16, semi: true, bottom: 8);
        var info = T("Ждём нажатие… (10 секунд). Обычную клавиатуру не трогайте (Shift, Ctrl, Alt, Win не учитываются).", 13, muted: true, bottom: 14);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var use = new Button { Content = "Открывать этой клавишей кольцо", Style = (Style)FindResource("AccentButton"), Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 8, 0) };
        var close = new Button { Content = "Закрыть", Style = (Style)FindResource("GhostButton"), MinWidth = 90 };
        close.Click += (_, _) => w.Close();
        row.Children.Add(use); row.Children.Add(close);
        sp.Children.Add(title); sp.Children.Add(info); sp.Children.Add(row);
        w.Content = sp;
        if (_mapper == null) { info.Text = "Недоступно в режиме предпросмотра."; w.ShowDialog(); return; }
        ushort got = 0;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        timer.Tick += (_, _) =>
        {
            timer.Stop(); _mapper.ProbeNextKey = null;
            info.Text = "Ничего не пришло. Если клавиатуру только что перепрограммировали — отключите и снова подключите USB-кабель " +
                        "(новая раскладка включается после переподключения). Иначе кнопка выполняет внутреннюю функцию клавиатуры и не посылает клавишу в компьютер.";
        };
        _mapper.ProbeNextKey = vk =>
        {
            timer.Stop(); got = vk;
            var safety = TriggerKeyPolicy.Classify(vk);
            bool ringOk = safety != TriggerSafety.Forbidden && _profile.Host.Keys.Any(k => k.TriggerVk == vk && k.Tap.Kind == HostActionKind.Ring);
            info.Text = $"Пришла клавиша: {KeyCombo.VkName(vk)} (VK 0x{vk:X2})." + (ringOk
                ? "\nВсё в порядке: по этой клавише уже открывается кольцо действий."
                : safety == TriggerSafety.Forbidden
                ? "\nЭто обычная клавиша основной клавиатуры — её перехватывать нельзя (она перестала бы работать везде). " +
                  "Если вы нажали её на VOROTEX — назначьте кнопке кольцо: программа запишет в неё F13–F24."
                : safety == TriggerSafety.NeedsConfirmation
                ? "\nТакая клавиша может быть и на основной клавиатуре — после перехвата она перестанет там работать. Надёжнее F13–F24."
                : "\nЕё можно перехватывать для кольца действий без записи в клавиатуру.");
            if (safety == TriggerSafety.Forbidden) ringOk = true; // hides «use»
            use.Visibility = ringOk ? Visibility.Collapsed : Visibility.Visible;
            Log($"Проверка: {DeviceView.ControlName(col)} → {KeyCombo.VkName(vk)} (VK 0x{vk:X2}).");
        };
        use.Click += (_, _) =>
        {
            if (got == 0) return;
            var safety = TriggerKeyPolicy.Classify(got);
            if (safety == TriggerSafety.Forbidden) return;
            if (safety == TriggerSafety.NeedsConfirmation && MessageBox.Show(w,
                    $"Клавиша {KeyCombo.VkName(got)} может быть и на основной клавиатуре. Пока программа запущена, она будет открывать кольцо и перестанет работать как обычно.\n\nВсё равно использовать её?",
                    Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _profile.Host.Keys.RemoveAll(k => k.TriggerVk == got);
            _profile.Host.Keys.Add(new HostKeyBinding { TriggerVk = got, Tap = new HostAction { Kind = HostActionKind.Ring }, Suppress = true });
            _profile.Host.Enabled = true;
            Changed();
            w.Close();
            BuildEditor();
        };
        w.Closed += (_, _) => { timer.Stop(); if (_mapper.ProbeNextKey != null) _mapper.ProbeNextKey = null; };
        timer.Start();
        w.ShowDialog();
    }

    // ───────────────────────────── mouse tile
    private byte _mouseBtn = MouseCodes.Left;
    private int _mouseMode; // 0 click, 1 double, 2 autoclick while held

    private Action MouseEditor(byte code)
    {
        if (KeyCatalog.IsMacro(code) && Labels.MouseOf(Cfg.Macros[KeyCatalog.MacroSlotOf(code)]) != null)
        {
            var m = Cfg.Macros[KeyCatalog.MacroSlotOf(code)];
            _mouseBtn = m.Events[0].Code;
            _mouseMode = m.RepeatMode == MacroRepeatMode.WhileHeld ? 2 : m.Repeat == 2 ? 1 : 0;
        }
        var host = new StackPanel();
        void Paint()
        {
            host.Children.Clear();
            host.Children.Add(T("Какая кнопка:", 12.5, muted: true, bottom: 6));
            var g = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 12) };
            foreach (var (c, t, sub) in new[] { (MouseCodes.Left, "Левая", "ЛКМ"), (MouseCodes.Right, "Правая", "ПКМ"), (MouseCodes.Middle, "Средняя", "колесо") })
            {
                byte cc = c;
                g.Children.Add(BigOption("\uE962", t, sub, _mouseBtn == c, () => { _mouseBtn = cc; Paint(); }));
            }
            host.Children.Add(g);
            host.Children.Add(T("Как нажимать:", 12.5, muted: true, bottom: 6));
            var g2 = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3 };
            foreach (var (i, t, sub) in new[] { (0, "Щелчок", "один раз"), (1, "Двойной", "два щелчка"), (2, "Автоклик", "пока зажата") })
            {
                int ii = i;
                g2.Children.Add(BigOption(i == 2 ? "\uE895" : "\uE8B0", t, sub, _mouseMode == i, () => { _mouseMode = ii; Paint(); }));
            }
            host.Children.Add(g2);
            bool sure = MouseCodes.IsConfirmed(_mouseBtn);
            host.Children.Add(T(sure
                ? "Код левой кнопки подтверждён: именно так её записывает родная программа (ваш прежний макрос «ЛКМ»)."
                : "Код этой кнопки определён по анализу родной программы — будет проверено при первой записи.", 12, muted: true).With(t => t.Margin = new Thickness(0, 12, 0, 0)));
            host.Children.Add(T("Щелчок хранится в клавиатуре как маленький макрос и работает без программы.", 12, muted: true).With(t => t.Margin = new Thickness(0, 4, 0, 0)));
        }
        Paint();
        EditorHost.Children.Add(host);
        return () => _lastError = ApplyMouse(_mouseBtn, _mouseMode);
    }

    private Border BigOption(string glyph, string title, string sub, bool on, Action click)
    {
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 20, Foreground = on ? R("AccentBrush") : R("TextBrush"), HorizontalAlignment = HorizontalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 0) });
        sp.Children.Add(new TextBlock { Text = sub, FontSize = 11.5, Foreground = R("MutedBrush"), HorizontalAlignment = HorizontalAlignment.Center });
        var b = new Border { Child = sp, Height = 84, Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(12), Cursor = Cursors.Hand, BorderThickness = new Thickness(1.5),
            Background = on ? R("AccentSoftBrush") : R("Panel2Brush"), BorderBrush = on ? R("AccentBrush") : Brushes.Transparent };
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        return b;
    }

    private string? ApplyMouse(byte btn, int mode)
    {
        byte current = Cfg.GetKey(_selCol);
        int slot = KeyCatalog.IsMacro(current) && Labels.MouseOf(Cfg.Macros[KeyCatalog.MacroSlotOf(current)]) != null ? KeyCatalog.MacroSlotOf(current) : FreeMacroSlot();
        if (slot < 0) return "Нет свободного места (все 11 макросов заняты).";
        var m = Cfg.Macros[slot];
        m.Events = new() { new MacroEvent { Down = true, Delay = 1, Code = btn }, new MacroEvent { Down = false, Delay = 1, Code = btn } };
        if (mode == 2) { m.RepeatMode = MacroRepeatMode.WhileHeld; m.Repeat = 1; }
        else { m.RepeatMode = MacroRepeatMode.Count; m.Repeat = mode == 1 ? 2 : 1; }
        Cfg.SetKey(_selCol, (byte)(KeyCatalog.FirstMacro + slot));
        Log($"{DeviceView.ControlName(_selCol)}: {Labels.MouseOf(m)} → макрос {slot + 1}.");
        Changed(); RefreshMacros();
        return null;
    }

    /// <summary>Self-test of the ring editor: hover + click + leave on slot i through real routed events; returns a verdict.</summary>
    internal int RingSlotCount() => _profile.Host.Ring.Slots.Count;

    internal string RingEditorClickTest(int i)
    {
        RingView? Find() => EditorHost.Children.OfType<Viewbox>().Select(v => v.Child).OfType<RingView>().FirstOrDefault();
        var view = Find();
        if (view == null) return $"ячейка {i + 1}: редактор кольца не найден";
        var disc = view.DiscAt(i);
        double x0 = Canvas.GetLeft(disc), y0 = Canvas.GetTop(disc);
        disc.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
        disc.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        disc.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
        for (int k = 0; k < 4; k++) Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => { }));
        var v2 = Find();
        if (v2 == null) return $"ячейка {i + 1}: после щелчка редактор пропал";
        // mouse leaves the whole ring → nothing may change
        v2.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
        var d2 = v2.DiscAt(i);
        bool pos = Math.Abs(Canvas.GetLeft(d2) - x0) < 0.01 && Math.Abs(Canvas.GetTop(d2) - y0) < 0.01;
        bool noTransform = Enumerable.Range(0, v2.Count).All(k => v2.DiscAt(k).RenderTransform == null || v2.DiscAt(k).RenderTransform.Value.IsIdentity);
        bool ok = _ringSel == i && v2.Highlight == i && pos && noTransform && d2.BorderThickness.Left > 2;
        return $"ячейка {i + 1} «{_profile.Host.Ring.Slots[i].Title}»: {(ok ? "OK" : "ОШИБКА")} (выбрано {_ringSel + 1}, подсветка {v2.Highlight + 1}, позиция {(pos ? "та же" : "сдвинута")}, масштаб {(noTransform ? "1" : "≠1")})";
    }
}
