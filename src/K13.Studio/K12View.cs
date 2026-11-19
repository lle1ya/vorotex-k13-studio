using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Vorotex.K13;

namespace Vorotex.Studio;

/// <summary>1.3.3: a plain READ-ONLY schematic of the VOROTEX K12 for «Проверка кнопок» (photo of 19.11.2026):
/// 1–5 / 6–9 + tall ENTER / knob, long VOROTEX, dot. Lights the pressed key; nothing is editable.</summary>
public sealed class K12View : Viewbox
{
    private readonly Dictionary<int, Border> _keys = new();
    private readonly HashSet<int> _pressed = new();

    public K12View()
    {
        Stretch = Stretch.Uniform;
        var body = new Border { Background = new SolidColorBrush(Color.FromRgb(0x24, 0x26, 0x2B)), CornerRadius = new CornerRadius(18), Padding = new Thickness(18), Width = 420, Height = 300 };
        var g = new Grid();
        for (int i = 0; i < 5; i++) g.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < 3; i++) g.RowDefinitions.Add(new RowDefinition());
        void Key(int id, int row, int col, int rowSpan = 1, int colSpan = 1, bool round = false)
        {
            var b = new Border
            {
                Margin = new Thickness(5), CornerRadius = new CornerRadius(round ? 40 : 8), BorderThickness = new Thickness(2),
                Child = new TextBlock { Text = K12Layout.Legend(id), FontSize = id == 10 ? 17 : 20, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                ToolTip = K12Layout.ControlName(id),
            };
            if (round) { b.Width = 56; b.Height = 56; }
            Grid.SetRow(b, row); Grid.SetColumn(b, col); Grid.SetRowSpan(b, rowSpan); Grid.SetColumnSpan(b, colSpan);
            g.Children.Add(b); _keys[id] = b; Paint(id);
        }
        for (int i = 0; i < 5; i++) Key(i, 0, i);
        Key(9, 1, 0); Key(8, 1, 1); Key(7, 1, 2); Key(6, 1, 3); Key(5, 1, 4, rowSpan: 2);
        // knob: press in the middle, turn arrows around it
        var knob = new Grid { Margin = new Thickness(2) };
        Grid.SetRow(knob, 2); Grid.SetColumn(knob, 0); g.Children.Add(knob);
        var kb = new Border
        {
            Width = 54, Height = 54, CornerRadius = new CornerRadius(27), BorderThickness = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = K12Layout.Legend(K12Layout.KnobPress), FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            ToolTip = K12Layout.ControlName(K12Layout.KnobPress),
        };
        knob.Children.Add(kb); _keys[K12Layout.KnobPress] = kb; Paint(K12Layout.KnobPress);
        foreach (var (id, ha) in new[] { (K12Layout.KnobLeft, HorizontalAlignment.Left), (K12Layout.KnobRight, HorizontalAlignment.Right) })
        {
            var a = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), HorizontalAlignment = ha, VerticalAlignment = VerticalAlignment.Top, BorderThickness = new Thickness(1.5),
                Child = new TextBlock { Text = K12Layout.Legend(id), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }, ToolTip = K12Layout.ControlName(id) };
            knob.Children.Add(a); _keys[id] = a; Paint(id);
        }
        Key(10, 2, 1, colSpan: 2); Key(11, 2, 3);
        var outer = new StackPanel();
        outer.Children.Add(body);
        outer.Children.Add(new TextBlock { Text = "VOROTEX K12 — только просмотр", Foreground = Brushes.Gray, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) });
        body.Child = g;
        Child = outer;
    }

    private void Paint(int id)
    {
        if (!_keys.TryGetValue(id, out var b)) return;
        bool on = _pressed.Contains(id);
        bool accent = id is 0 or 5 or 11 || id == 10;
        b.Background = on ? new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x1A))
                     : id == 10 ? new SolidColorBrush(Color.FromRgb(0xF2, 0xB1, 0x1C))
                     : accent ? new SolidColorBrush(Color.FromRgb(0xE8, 0x3A, 0x55))
                     : id is K12Layout.KnobPress or K12Layout.KnobLeft or K12Layout.KnobRight ? new SolidColorBrush(Color.FromRgb(0xB8, 0xBC, 0xC2))
                     : new SolidColorBrush(Color.FromRgb(0xA9, 0xAD, 0xB5));
        b.BorderBrush = on ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0));
        if (b.Child is TextBlock t) t.Foreground = id == 10 && !on ? Brushes.White : Brushes.White;
    }

    public void SetPressed(int id, bool on) { if (on) _pressed.Add(id); else _pressed.Remove(id); Paint(id); }
    public void ClearPressed() { var all = _pressed.ToList(); _pressed.Clear(); foreach (var id in all) Paint(id); }
    public IReadOnlyCollection<int> Pressed => _pressed;
}
