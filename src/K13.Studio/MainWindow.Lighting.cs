using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using Vorotex.K13;

namespace Vorotex.Studio;

/// <summary>"Подсветка" page: live preview on top, then 1) effect, 2) colour, 3) brightness/speed, «Дополнительно».</summary>
public partial class MainWindow
{
    private int _selColor;
    private readonly List<(int Mode, Border Card, Rectangle[] Cells)> _modeCards = new();
    private const int StripCells = 12;

    private static readonly string[] ModeDescriptions =
    {
        "Подсветка не горит", "Плавно загорается и гаснет", "Ровный постоянный свет", "Кнопка вспыхивает при нажатии",
        "Все кнопки плавно меняют цвет", "Мягкий перелив цветов", "Бегущий огонёк с хвостом", "Огонёк перебегает по кнопкам",
        "Радуга течёт по клавиатуре", "Подсветка мигает", "Волна одного цвета", "Две половины разными цветами", "У каждой кнопки свой цвет",
    };

    /// <summary>0 = no colour needed, 1 = one colour, 2 = two colours (partition).</summary>
    private static int ColorsNeeded(int mode) => mode switch { 0 or 4 or 5 or 8 or 12 => 0, 11 => 2, _ => 1 };

    private static readonly (string Name, int R, int G, int B)[] Presets =
    {
        ("Красный", 15, 0, 0), ("Оранжевый", 15, 6, 0), ("Жёлтый", 15, 13, 0), ("Зелёный", 0, 15, 0), ("Бирюзовый", 0, 15, 12),
        ("Голубой", 0, 9, 15), ("Синий", 0, 0, 15), ("Фиолетовый", 9, 0, 15), ("Розовый", 15, 0, 9), ("Белый", 15, 15, 15), ("Тёплый белый", 15, 10, 5),
    };

    private UIElement StepHead(int n, string text)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = R("AccentBrush"), Margin = new Thickness(0, 0, 10, 0),
            Child = new TextBlock { Text = n.ToString(), Foreground = Brushes.White, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
        sp.Children.Add(new TextBlock { Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    private void BuildModeCards()
    {
        LightKbHost.Content ??= _kbLight;
        Step1Head.Children.Clear(); Step1Head.Children.Add(StepHead(1, "Выберите эффект"));
        Step2Head.Children.Clear(); Step2Head.Children.Add(StepHead(2, "Цвет"));
        Step3Head.Children.Clear(); Step3Head.Children.Add(StepHead(3, "Яркость и скорость"));
        ModeCards.Children.Clear();
        _modeCards.Clear();
        int count = Math.Max(K13Layout.LedModeNames.Length, (_profile?.Device?.Led.Mode ?? 0) + 1);
        for (int m = 0; m < count; m++)
        {
            var cells = new Rectangle[StripCells];
            var strip = new UniformGrid { Rows = 2, Columns = StripCells / 2 };
            for (int i = 0; i < StripCells; i++)
            {
                cells[i] = new Rectangle { RadiusX = 2, RadiusY = 2, Margin = new Thickness(1.5), Fill = new SolidColorBrush(Colors.Black) };
                strip.Children.Add(cells[i]);
            }
            var stripBorder = new Border { CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromRgb(0x24, 0x25, 0x29)), Padding = new Thickness(4), Height = 26, Child = strip };
            var sp = new StackPanel();
            sp.Children.Add(stripBorder);
            sp.Children.Add(new TextBlock { Text = K13Layout.LedModeName(m), FontWeight = FontWeights.SemiBold, FontSize = 12, Margin = new Thickness(2, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            sp.Children.Add(new TextBlock { Text = m < ModeDescriptions.Length ? ModeDescriptions[m] : $"Режим {m}", FontSize = 11, Foreground = R("MutedBrush"), Margin = new Thickness(2, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            var card = new Border
            {
                Width = 151, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(7), CornerRadius = new CornerRadius(12),
                Background = R("PanelBrush"), BorderBrush = R("BorderBrush"), BorderThickness = new Thickness(1), Child = sp,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = (m < ModeDescriptions.Length ? ModeDescriptions[m] + "\n" : "") + "Превью примерное — настоящий эффект рисует сама клавиатура.",
            };
            int mode = m;
            card.MouseLeftButtonUp += (_, _) => { Cfg.Led.Mode = mode; Changed(false); RefreshLed(); };
            ModeCards.Children.Add(card);
            _modeCards.Add((m, card, cells));
        }
    }

    private void AnimateModeCards(double t)
    {
        var led = Cfg.Led;
        foreach (var (mode, _, cells) in _modeCards)
        {
            for (int i = 0; i < cells.Length; i++)
            {
                int col = i % (StripCells / 2), rowi = i / (StripCells / 2);
                var (c, a) = LedSim.Key(led, mode, col / (StripCells / 2 - 1.0), rowi, i, cells.Length, t);
                double k = Math.Max(0.06, a);
                var fill = Color.FromRgb((byte)(0x2E + (c.R - 0x2E) * k), (byte)(0x2F + (c.G - 0x2F) * k), (byte)(0x34 + (c.B - 0x34) * k));
                ((SolidColorBrush)cells[i].Fill).Color = fill;
            }
        }
    }

    private void RefreshLed()
    {
        var led = Cfg.Led;
        bool old = _loading; _loading = true;
        if (_modeCards.Count <= led.Mode) BuildModeCards();
        foreach (var (mode, card, _) in _modeCards)
        {
            bool sel = mode == led.Mode;
            card.BorderBrush = R(sel ? "AccentBrush" : "BorderBrush");
            card.BorderThickness = new Thickness(sel ? 2 : 1);
            card.Background = R(sel ? "AccentSoftBrush" : "PanelBrush");
        }
        BrightSlider.Value = led.BrightnessLevel;
        SpeedSlider.Value = Math.Clamp(led.Speed, 0, 6);
        if (led.SingleSlot is int ss) _selColor = ss;
        BuildColorPanel();
        BuildParams();
        _loading = old;
    }

    private FrameworkElement Swatch(Color c, double size, bool selected, string tip, Action click)
    {
        var g = new Grid { Width = size + 8, Height = size + 8, Margin = new Thickness(0, 0, 6, 6), Cursor = System.Windows.Input.Cursors.Hand, ToolTip = tip };
        if (selected) g.Children.Add(new Ellipse { Stroke = R("AccentBrush"), StrokeThickness = 2.2 });
        g.Children.Add(new Ellipse { Margin = new Thickness(4), Fill = new SolidColorBrush(c), Stroke = R("BorderBrush"), StrokeThickness = 1 });
        g.MouseLeftButtonUp += (_, _) => click();
        return g;
    }

    private void BuildColorPanel()
    {
        var led = Cfg.Led;
        ColorPanel.Children.Clear();
        int need = ColorsNeeded(led.Mode);
        if (need == 0)
        {
            ColorPanel.Children.Add(T(led.Mode == 0 ? "Подсветка выключена — цвет не нужен." : "Этот эффект сам переливается цветами радуги — выбирать цвет не нужно.", 13, muted: true));
            return;
        }
        bool multi = need == 2 || led.SingleSlot == null;
        int editing = need == 2 ? Math.Clamp(_selColor, 0, 1) : multi ? _selColor : led.SingleSlot!.Value;

        // main colour + presets
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var main = new Border { Width = 54, Height = 54, CornerRadius = new CornerRadius(27), Background = new SolidColorBrush(LedSim.Slot(led, editing)), BorderBrush = R("BorderBrush"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 14, 0) };
        row.Children.Add(main);
        var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        mid.Children.Add(T(multi ? $"Цвет {editing + 1}" : "Основной цвет", 13.5, semi: true, bottom: 6));
        var pick = new Button { Content = "Выбрать любой цвет…", Style = (Style)FindResource("GhostButton"), HorizontalAlignment = HorizontalAlignment.Left };
        pick.Click += PickColor_Click;
        mid.Children.Add(pick);
        row.Children.Add(mid);
        ColorPanel.Children.Add(row);

        var presets = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var cur = led.Colors[editing];
        foreach (var p in Presets)
        {
            var pp = p;
            presets.Children.Add(Swatch(Color.FromRgb((byte)(p.R * 17), (byte)(p.G * 17), (byte)(p.B * 17)), 26, cur.R == p.R && cur.G == p.G && cur.B == p.B, p.Name,
                () => { _selColor = editing; SetSelectedColor(pp.R, pp.G, pp.B); }));
        }
        ColorPanel.Children.Add(presets);

        if (need == 1)
        {
            var sw = new CheckBox { Style = (Style)FindResource("Switch"), Content = "Несколько цветов по очереди", IsChecked = led.SingleSlot == null, Margin = new Thickness(0, 8, 0, 0) };
            sw.Checked += (_, _) => { if (_loading) return; led.ColorIndex = 0; Changed(false); RefreshLed(); };
            sw.Unchecked += (_, _) => { if (_loading) return; led.ColorIndex = Math.Clamp(_selColor, 0, 6) + 1; Changed(false); RefreshLed(); };
            ColorPanel.Children.Add(sw);
        }

        if (multi)
        {
            ColorPanel.Children.Add(T(need == 2 ? "Цвета половин — нажмите, чтобы изменить:" : "Цвета по очереди — нажмите кружок, чтобы изменить; галочка — участвует:", 12.5, muted: true).With(t => t.Margin = new Thickness(0, 10, 0, 6)));
            var slots = new StackPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < (need == 2 ? 2 : 7); i++)
            {
                int idx = i;
                var col = new StackPanel { Margin = new Thickness(0, 0, 4, 0) };
                col.Children.Add(Swatch(LedSim.Slot(led, i), 30, i == editing, $"Цвет {i + 1}", () => { _selColor = idx; RefreshLed(); }));
                if (need == 1)
                {
                    var cb = new CheckBox { IsChecked = (led.CycleMask & (1 << i)) != 0, HorizontalAlignment = HorizontalAlignment.Center, ToolTip = "Участвует в переборе" };
                    cb.Click += (_, _) => { led.CycleMask = (byte)((led.CycleMask ^ (1 << idx)) | 0x80); Changed(false); RefreshLed(); };
                    col.Children.Add(cb);
                }
                slots.Children.Add(col);
            }
            ColorPanel.Children.Add(slots);
        }
    }

    private void BuildParams()
    {
        var led = Cfg.Led;
        ParamsPanel.Children.Clear();
        var gen = new StackPanel();
        gen.Children.Add(T("Направление эффекта", 13, muted: true, bottom: 4));
        var dir = new ComboBox(); for (int i = 0; i < 4; i++) dir.Items.Add($"Направление {i + 1}"); dir.SelectedIndex = Math.Clamp(led.Direction, 0, 3);
        dir.SelectionChanged += (_, _) => { if (_loading) return; led.Direction = dir.SelectedIndex; Changed(false); };
        gen.Children.Add(dir);
        var sym = new CheckBox { Style = (Style)FindResource("Switch"), Content = "Симметрия", IsChecked = led.Symmetry, Margin = new Thickness(0, 12, 0, 4) };
        sym.Checked += (_, _) => { if (_loading) return; led.Symmetry = true; Changed(false); };
        sym.Unchecked += (_, _) => { if (_loading) return; led.Symmetry = false; Changed(false); };
        gen.Children.Add(sym);
        var react = new CheckBox { Style = (Style)FindResource("Switch"), Content = "Реакция на нажатия (экспериментально)", IsChecked = led.Colors[0].X >= 2,
            ToolTip = "Где прошивка хранит этот флаг, точно не установлено." };
        react.Checked += (_, _) => { if (_loading) return; led.Colors[0].X = 2; Changed(false); };
        react.Unchecked += (_, _) => { if (_loading) return; led.Colors[0].X = 1; Changed(false); };
        gen.Children.Add(react);
        ParamsPanel.Children.Add(Card(gen));

        var fine = new StackPanel();
        int editing = led.SingleSlot ?? _selColor;
        var sc = led.Colors[editing];
        fine.Children.Add(T($"Точная настройка цвета {editing + 1}", 14, semi: true, bottom: 8));
        void Channel(string name, int value, Action<int> set)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            var lbl = T(name); lbl.VerticalAlignment = VerticalAlignment.Center;
            var s = new Slider { Minimum = 0, Maximum = 15, Value = value, VerticalAlignment = VerticalAlignment.Center, IsSnapToTickEnabled = true, TickFrequency = 1 };
            var v = T(value.ToString(), muted: true); v.TextAlignment = TextAlignment.Right; v.VerticalAlignment = VerticalAlignment.Center;
            s.ValueChanged += (_, _) => { if (_loading) return; set((int)s.Value); v.Text = ((int)s.Value).ToString(); Changed(false); };
            s.PreviewMouseUp += (_, _) => RefreshLed();
            Grid.SetColumn(s, 1); Grid.SetColumn(v, 2);
            g.Children.Add(lbl); g.Children.Add(s); g.Children.Add(v);
            fine.Children.Add(g);
        }
        Channel("Красный", sc.R, x => sc.R = x);
        Channel("Зелёный", sc.G, x => sc.G = x);
        Channel("Синий", sc.B, x => sc.B = x);
        fine.Children.Add(T("Клавиатура хранит 16 уровней на канал.", 12, muted: true));
        ParamsPanel.Children.Add(Card(fine));
        ParamsPanel.Children.Add(T($"Память: BASE+0x18 = {(led.Direction << 6 | led.Brightness << 3 | led.Speed):X2}, BASE+0x19 = {((led.Symmetry ? 0x80 : 0) | led.ColorIndex << 4 | led.Mode):X2}, BASE+0x1A = {led.CycleMask:X2}", 11, muted: true));
    }

    internal void ScrollLightToEnd() { LightScroll.UpdateLayout(); LightScroll.ScrollToEnd(); }

    private void LightMore_Click(object sender, RoutedEventArgs e) => ShowLightParams(ParamsPanel.Visibility != Visibility.Visible);

    internal void ShowLightParams(bool p)
    {
        ParamsPanel.Visibility = p ? Visibility.Visible : Visibility.Collapsed;
        LightMoreBtn.Content = p ? "Дополнительно ▴" : "Дополнительно ▾";
    }

    private void Led_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || _profile == null) return;
        var led = Cfg.Led;
        led.BrightnessLevel = (int)Math.Round(BrightSlider.Value);
        led.Speed = (int)Math.Round(SpeedSlider.Value);
        Changed(false);
    }

    private void SetSelectedColor(int r, int g, int b)
    {
        var c = Cfg.Led.Colors[Math.Clamp(_selColor, 0, 6)];
        c.R = r; c.G = g; c.B = b;
        Changed(false); RefreshLed();
    }

    private void PickColor_Click(object sender, RoutedEventArgs e)
    {
        var c = Cfg.Led.Colors[Math.Clamp(_selColor, 0, 6)];
        using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb(c.R * 17, c.G * 17, c.B * 17) };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            SetSelectedColor((int)Math.Round(dlg.Color.R / 17.0), (int)Math.Round(dlg.Color.G / 17.0), (int)Math.Round(dlg.Color.B / 17.0));
    }
}
