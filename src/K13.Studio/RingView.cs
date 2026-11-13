using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Vorotex.K13;

namespace Vorotex.Studio;

/// <summary>
/// The «Кольцо действий» visual: soft glass discs on a radial ring, accent highlight with a label pill,
/// a centre × to cancel. Used both by the on-screen overlay and by the editor preview (vector, crisp at any DPI).
/// </summary>
public sealed class RingView : Canvas
{
    public const double Size = 340, Center = 170, ItemRadius = 116, Disc = 62;

    private static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly FontFamily Ui = new("Segoe UI Variable Display, Segoe UI");
    private static readonly Color Accent = Color.FromRgb(0xF0, 0x64, 0x23);

    private readonly List<(Border Disc, ScaleTransform Scale, TextBlock Glyph)> _items = new();
    private readonly TextBlock _label = new();
    private readonly Border _labelPill;
    private readonly Grid _labelHost = new() { Width = 200, IsHitTestVisible = false };
    private readonly Border _centerBtn;
    private readonly bool _dark;
    private int _highlight = -1;
    private List<RingSlot> _slots = new();

    public event Action<int>? ItemClicked;
    /// <summary>Disc element of slot i (self-test).</summary>
    internal Border DiscAt(int i) => _items[i].Disc;
    public event Action? CenterClicked;
    public int Highlight { get => _highlight; set => SetHighlight(value); }
    public int Count => _slots.Count;
    public string CenterHint { get; set; } = "Отпустите — выполнить";

    private static Color C(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private readonly bool _tooltips;

    /// <param name="tooltips">Off for the on-screen overlay: a tooltip of a hidden non-activating window can stay on screen.</param>
    private readonly bool _editor;
    private readonly List<Brush> _borders = new();

    /// <param name="editor">Static editor preview: no hover effects, no scaling/glow; the selected slot gets an orange outline in place.</param>
    public RingView(bool dark = true, bool tooltips = true, bool editor = false)
    {
        _dark = dark; _tooltips = tooltips && !editor; _editor = editor;
        Width = Size; Height = Size;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal); // also shown scaled (Viewbox, animation)
        SnapsToDevicePixels = true; UseLayoutRounding = false;
        _labelPill = new Border
        {
            CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 4, 12, 5), Child = _label,
            Background = new SolidColorBrush(dark ? C(0xF0202226) : C(0xF8FFFFFF)), BorderBrush = new SolidColorBrush(dark ? C(0x30FFFFFF) : C(0x22000000)), BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
        };
        _label.FontFamily = Ui; _label.FontSize = 13; _label.FontWeight = FontWeights.SemiBold;
        _label.Foreground = new SolidColorBrush(dark ? Colors.White : C(0xFF1F2328));
        var cx = new TextBlock { Text = "\uE711", FontFamily = Icons, FontSize = 14, Foreground = new SolidColorBrush(dark ? C(0xD0FFFFFF) : C(0xFF3A3F47)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _centerBtn = new Border
        {
            Width = 46, Height = 46, CornerRadius = new CornerRadius(23), Child = cx, Cursor = Cursors.Hand,
            Background = Glass(false), BorderBrush = new SolidColorBrush(dark ? C(0x40FFFFFF) : C(0x30000000)), BorderThickness = new Thickness(1),
        };
        _centerBtn.MouseLeftButtonUp += (_, e) => { e.Handled = true; CenterClicked?.Invoke(); };
    }

    private Brush Glass(bool hi)
    {
        if (hi) return new LinearGradientBrush(C(0xFFFF8A4C), Accent, 90);
        return _dark
            ? new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1), GradientStops = { new GradientStop(C(0xE84A4E57), 0), new GradientStop(C(0xE82A2D33), 1) } }
            : new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1), GradientStops = { new GradientStop(C(0xF8FFFFFF), 0), new GradientStop(C(0xF0E9ECF0), 1) } };
    }

    public void SetSlots(IEnumerable<RingSlot> slots)
    {
        _slots = slots.ToList();
        Children.Clear();
        _items.Clear();
        _glows.Clear();
        _borders.Clear();
        // backdrop: soft glass donut
        var back = new Ellipse
        {
            Width = 316, Height = 316,
            Fill = new RadialGradientBrush { GradientStops = {
                new GradientStop(_dark ? C(0x00181A1E) : C(0x00FFFFFF), 0.32), new GradientStop(_dark ? C(0xC0181A1E) : C(0xD0F4F6F9), 0.42),
                new GradientStop(_dark ? C(0xD0181A1E) : C(0xE0F4F6F9), 0.92), new GradientStop(_dark ? C(0x00181A1E) : C(0x00F4F6F9), 1) } },
            Stroke = new SolidColorBrush(_dark ? C(0x22FFFFFF) : C(0x18000000)), StrokeThickness = 1, IsHitTestVisible = false,
            Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 6, Direction = 270, Opacity = 0.35, Color = Colors.Black },
        };
        SetLeft(back, Center - 158); SetTop(back, Center - 158);
        Children.Add(back);
        int n = _slots.Count;
        for (int i = 0; i < n; i++)
        {
            double a = RingMath.AngleOf(i, n) * Math.PI / 180;
            double x = Center + ItemRadius * Math.Sin(a), y = Center - ItemRadius * Math.Cos(a);
            var glyph = new TextBlock { Text = string.IsNullOrEmpty(_slots[i].Glyph) ? "\uE734" : _slots[i].Glyph, FontFamily = Icons, FontSize = 22,
                Foreground = new SolidColorBrush(_dark ? C(0xF2FFFFFF) : C(0xFF2A2F36)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var scale = new ScaleTransform(1, 1, Disc / 2, Disc / 2);
            var disc = new Border
            {
                Width = Disc, Height = Disc, CornerRadius = new CornerRadius(Disc / 2), Child = glyph, Cursor = Cursors.Hand, RenderTransform = scale,
                Background = Glass(false), BorderBrush = new LinearGradientBrush(_dark ? C(0x60FFFFFF) : C(0xFFFFFFFF), _dark ? C(0x10FFFFFF) : C(0x26000000), 90), BorderThickness = new Thickness(1.2),
                Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Direction = 270, Opacity = 0.4, Color = Colors.Black },
                ToolTip = _tooltips ? _slots[i].Title : null,
            };
            int idx = i;
            _borders.Add(disc.BorderBrush);
            if (_editor)
            {   // hover = light outline on this disc only (its own hit area), cleared on leave; no angle logic, no scaling
                disc.RenderTransform = null;
                disc.MouseEnter += (_, _) => { if (idx != _highlight) { disc.BorderBrush = new SolidColorBrush(Color.FromArgb(0x90, Accent.R, Accent.G, Accent.B)); disc.BorderThickness = new Thickness(2); } };
                disc.MouseLeave += (_, _) => { if (idx != _highlight) { disc.BorderBrush = _borders[idx]; disc.BorderThickness = new Thickness(1.2); } };
            }
            else disc.MouseEnter += (_, _) => SetHighlight(idx);
            disc.MouseLeftButtonUp += (_, e) => { e.Handled = true; ItemClicked?.Invoke(idx); };
            SetLeft(disc, x - Disc / 2); SetTop(disc, y - Disc / 2);
            // highlight glow (vector, hidden until hovered)
            var glow = VectorFx.Halo(new EllipseGeometry(new Rect(0, 0, Disc, Disc)), new SolidColorBrush(Accent), 10, 0.75, 0, false);
            glow.RenderTransform = scale; glow.Visibility = Visibility.Collapsed;
            SetLeft(glow, x - Disc / 2); SetTop(glow, y - Disc / 2);
            Children.Add(glow);
            Children.Add(disc);
            _glows.Add(glow);
            _items.Add((disc, scale, glyph));
        }
        SetLeft(_centerBtn, Center - 23); SetTop(_centerBtn, Center - 23);
        Children.Add(_centerBtn);
        _labelHost.Children.Clear();
        _labelHost.Children.Add(_labelPill);
        SetLeft(_labelHost, Center - 100); SetTop(_labelHost, Center + 31);
        Children.Add(_labelHost);
        _highlight = -1;
        VectorFx.ConvertEffects(this);
        UpdateLabel();
    }

    private readonly List<Canvas> _glows = new();

    private void SetHighlight(int i)
    {
        if (i >= _items.Count) i = -1;
        if (i == _highlight) return;
        var dur = TimeSpan.FromMilliseconds(120);
        if (_highlight >= 0) Paint(_highlight, false, dur);
        _highlight = i;
        if (i >= 0) Paint(i, true, dur);
        UpdateLabel();
    }

    private void Paint(int i, bool hi, TimeSpan dur)
    {
        var (disc, scale, glyph) = _items[i];
        if (_editor)
        {   // static: outline only, exact position, no transforms/animations
            disc.BorderBrush = hi ? new SolidColorBrush(Accent) : _borders[i];
            disc.BorderThickness = new Thickness(hi ? 3 : 1.2);
            glyph.Foreground = new SolidColorBrush(hi ? Accent : _dark ? C(0xF2FFFFFF) : C(0xFF2A2F36));
            _glows[i].Visibility = Visibility.Collapsed;
            return;
        }
        disc.Background = Glass(hi);
        glyph.Foreground = new SolidColorBrush(hi ? Colors.White : _dark ? C(0xF2FFFFFF) : C(0xFF2A2F36));
        _glows[i].Visibility = hi ? Visibility.Visible : Visibility.Collapsed;
        double to = hi ? 1.14 : 1;
        if (dur == TimeSpan.Zero)
        {   // drop running/held animations, otherwise the local value is ignored and the old disc stays enlarged
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = scale.ScaleY = to; return;
        }
        var an = new DoubleAnimation(to, dur) { EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, an);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, an);
    }

    private void UpdateLabel()
    {
        string text = !_editor && _highlight >= 0 && _highlight < _slots.Count ? _slots[_highlight].Title : CenterHint;
        _label.Text = string.IsNullOrWhiteSpace(text) ? "—" : text;
        _labelPill.HorizontalAlignment = HorizontalAlignment.Center;
        _labelPill.Opacity = _highlight >= 0 && !_editor ? 1 : 0.75;
    }

    /// <summary>Static highlight without animation (screenshots/editor).</summary>
    public void SetHighlightNow(int i)
    {
        if (_highlight >= 0 && _highlight < _items.Count) Paint(_highlight, false, TimeSpan.Zero);
        _highlight = i;
        if (i >= 0 && i < _items.Count) Paint(i, true, TimeSpan.Zero);
        UpdateLabel();
    }
}
