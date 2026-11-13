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
/// The VOROTEX K13 drawn entirely in WPF vector graphics (crisp at any DPI): graphite body with a soft bevel,
/// sculpted two-tone keycaps, L-shaped ENTER, raised joystick module with a clickable stick centre (press),
/// direction chips around the stick, knurled metal wheel with Roll± chips, animated selection glow, hover lift,
/// remapped badges and an optional live LED preview.
/// </summary>
public sealed class DeviceView : Viewbox
{
    public const int StickPress = 16, RollUp = 100, RollDown = 101, JoystickKnob = 102, WheelBody = 103;
    public const double W = 540, H = 262;

    private static readonly Point StickC = new(451, 95);
    private const double StickChipDist = 56;

    /// <summary>Key shapes (union of rectangles, device coordinates).</summary>
    private static readonly Dictionary<int, Rect[]> KeyShapes = new()
    {
        [0] = new[] { new Rect(30, 30, 62, 62) }, [1] = new[] { new Rect(96, 30, 62, 62) }, [2] = new[] { new Rect(162, 30, 62, 62) },
        [3] = new[] { new Rect(228, 30, 62, 62) }, [4] = new[] { new Rect(294, 30, 62, 62) },
        [5] = new[] { new Rect(30, 96, 95, 62) }, [6] = new[] { new Rect(129, 96, 62, 62) }, [7] = new[] { new Rect(195, 96, 62, 62) },
        [8] = new[] { new Rect(261, 96, 95, 62), new Rect(278, 96, 78, 128) },
        [9] = new[] { new Rect(30, 162, 112, 62) }, [10] = new[] { new Rect(146, 162, 62, 62) }, [11] = new[] { new Rect(212, 162, 62, 62) },
    };

    /// <summary>Bounding boxes of every clickable control (used by the LED preview and the dialogs).</summary>
    public static readonly Dictionary<int, Rect> Zones = BuildZones();

    private static Dictionary<int, Rect> BuildZones()
    {
        var z = new Dictionary<int, Rect>();
        foreach (var (id, rs) in KeyShapes) { var b = rs[0]; foreach (var r in rs) b.Union(r); z[id] = b; }
        z[15] = Chip(0, -1); z[14] = Chip(0, 1); z[12] = Chip(-1, 0); z[13] = Chip(1, 0);
        z[StickPress] = new Rect(StickC.X - 15, StickC.Y - 15, 30, 30);
        z[RollUp] = new Rect(382, 186, 48, 28); z[RollDown] = new Rect(382, 220, 48, 28);
        return z;
        static Rect Chip(int dx, int dy) => new(StickC.X + dx * StickChipDist - 14, StickC.Y + dy * StickChipDist - 14, 28, 28);
    }

    public static string Legend(int id) => id switch
    {
        >= 0 and <= 4 => (id + 1).ToString(),
        5 => "6", 6 => "7", 7 => "8", 8 => "Enter", 9 => "Tab", 10 => "9", 11 => "0",
        12 => "←", 13 => "→", 14 => "↓", 15 => "↑",
        StickPress => "●",
        RollUp => "Roll+", RollDown => "Roll−",
        17 => "17",
        _ => "?",
    };

    public static string ControlName(int id) => id switch
    {
        >= 12 and <= 15 => "джойстик " + Legend(id),
        StickPress => "нажатие стика",
        RollUp => "колесо вверх (Roll+)",
        RollDown => "колесо вниз (Roll−)",
        17 => "скрытая кнопка 17",
        _ => "кнопка " + Legend(id),
    };

    public static bool IsJoystick(int id) => id is >= 12 and <= 15;
    public static bool IsStick(int id) => IsJoystick(id) || id == StickPress;
    public static bool IsWheel(int id) => id is RollUp or RollDown;

    public event Action<int>? Clicked;
    public event Action<int?>? Hovered;

    private sealed class Part
    {
        public FrameworkElement Root = null!;
        public Shape Sel = null!;
        public FrameworkElement Badge = null!;
        public TextBlock? Sub;
        public TranslateTransform? Lift;
        public UIElement? HoverLayer;
    }

    private readonly Canvas _canvas = new() { Width = W, Height = H };
    private readonly Dictionary<int, Part> _parts = new();
    private readonly Dictionary<int, (SolidColorBrush Glow, SolidColorBrush Fill)> _led = new();
    private readonly bool _interactive;
    public bool ShowLed { get; }
    public bool ShowSubLabels { get; set; } = true;

    private static readonly FontFamily Ui = new("Segoe UI Variable Display, Segoe UI");
    private static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly Color Cyan = Color.FromRgb(0x2E, 0xC8, 0xFF);
    private static readonly Color Accent = Color.FromRgb(0xF0, 0x64, 0x23);

    private static Color C(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    private static LinearGradientBrush V(params (double o, uint c)[] stops) => Grad(new Point(0, 0), new Point(0, 1), stops);
    private static LinearGradientBrush Hz(params (double o, uint c)[] stops) => Grad(new Point(0, 0), new Point(1, 0), stops);
    private static LinearGradientBrush Grad(Point a, Point b, (double o, uint c)[] stops)
    {
        var g = new LinearGradientBrush { StartPoint = a, EndPoint = b };
        foreach (var (o, c) in stops) g.GradientStops.Add(new GradientStop(C(c), o));
        g.Freeze();
        return g;
    }

    private static Geometry Shape(Rect[] rects, Point origin, double l, double t, double r, double b, double radius)
    {
        Geometry? acc = null;
        foreach (var x in rects)
        {
            var rr = new Rect(x.X - origin.X + l, x.Y - origin.Y + t, Math.Max(1, x.Width - l - r), Math.Max(1, x.Height - t - b));
            Geometry g = new RectangleGeometry(rr, radius, radius);
            acc = acc == null ? g : new CombinedGeometry(GeometryCombineMode.Union, acc, g);
        }
        var flat = acc!.GetFlattenedPathGeometry(0.05, ToleranceType.Absolute);
        flat.Freeze();
        return flat;
    }

    public DeviceView(bool interactive = true, bool showLed = false)
    {
        _interactive = interactive;
        ShowLed = showLed;
        Stretch = Stretch.Uniform;
        Child = _canvas;
        // Crisp vector rendering: the model is scaled by this Viewbox, so text must use Ideal formatting (Display mode
        // snaps glyphs at the unscaled size and then they get resampled = fuzzy); no bitmap effects anywhere (see VectorFx).
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Auto);
        TextOptions.SetTextHintingMode(this, TextHintingMode.Fixed);
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        RenderOptions.SetClearTypeHint(this, ClearTypeHint.Enabled);
        DrawBody();
        if (showLed) DrawLedUnderglow();
        foreach (var id in KeyShapes.Keys) DrawKey(id);
        DrawModule();
        DrawWheel();
        if (showLed) DrawLedTint();
        VectorFx.ConvertEffects(_canvas);
    }

    // ───────────────────────────── body
    private void DrawBody()
    {
        var halo = new Ellipse { Width = 700, Height = 360, IsHitTestVisible = false,
            Fill = new RadialGradientBrush(C(0x16FFFFFF), C(0x00FFFFFF)) };
        Place(halo, -80, -50);
        var body = new Rectangle
        {
            Width = 524, Height = 242, RadiusX = 24, RadiusY = 24,
            Fill = V((0, 0xFF34373D), (0.5, 0xFF26292E), (1, 0xFF1A1C20)),
            Stroke = V((0, 0x40FFFFFF), (0.25, 0x14FFFFFF), (1, 0x80000000)), StrokeThickness = 1.5,
            Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 10, Direction = 270, Opacity = 0.42, Color = Colors.Black },
        };
        Place(body, 8, 8);
        var bevel = new Rectangle { Width = 516, Height = 234, RadiusX = 21, RadiusY = 21, Stroke = V((0, 0x22FFFFFF), (0.12, 0x00FFFFFF)), StrokeThickness = 2, IsHitTestVisible = false };
        Place(bevel, 12, 12);
        // key well with a soft inner shadow
        var well = new Rectangle { Width = 338, Height = 206, RadiusX = 13, RadiusY = 13, Fill = V((0, 0xFF0E0F12), (1, 0xFF16181B)),
            Stroke = V((0, 0xC0000000), (0.6, 0x30000000), (1, 0x1CFFFFFF)), StrokeThickness = 1.5 };
        Place(well, 24, 24);
        var wellShade = new Rectangle { Width = 334, Height = 30, RadiusX = 12, RadiusY = 12, Fill = V((0, 0x70000000), (1, 0x00000000)), IsHitTestVisible = false };
        Place(wellShade, 26, 26);
        // brand
        var brand = new TextBlock { Text = "K13", FontFamily = Ui, FontWeight = FontWeights.SemiBold, FontSize = 13, Foreground = new SolidColorBrush(C(0x40FFFFFF)), IsHitTestVisible = false };
        Place(brand, 482, 206);
        var brand2 = new TextBlock { Text = "VOROTEX", FontFamily = Ui, FontSize = 6.5, Foreground = new SolidColorBrush(C(0x30FFFFFF)), IsHitTestVisible = false };
        Place(brand2, 478, 224);
    }

    private void Place(UIElement e, double x, double y) { Canvas.SetLeft(e, x); Canvas.SetTop(e, y); _canvas.Children.Add(e); }

    // ───────────────────────────── keys
    private void DrawKey(int id)
    {
        var rects = KeyShapes[id];
        var bounds = Zones[id];
        var o = bounds.TopLeft;
        bool light = id <= 4;
        var root = new Canvas { Width = bounds.Width, Height = bounds.Height, Background = Brushes.Transparent };
        Canvas.SetLeft(root, o.X); Canvas.SetTop(root, o.Y);

        var skirtGeo = Shape(rects, o, 0, 0, 0, 0, 8);
        var skirt = new Path
        {
            Data = skirtGeo,
            Fill = light ? V((0, 0xFFB9BDC4), (1, 0xFF8C9098)) : V((0, 0xFF555A63), (1, 0xFF34373D)),
            Stroke = new SolidColorBrush(C(0x70000000)), StrokeThickness = 0.8,
            Effect = new DropShadowEffect { BlurRadius = 7, ShadowDepth = 2.5, Direction = 270, Opacity = 0.55, Color = Colors.Black },
        };
        root.Children.Add(skirt);

        var lift = new TranslateTransform();
        var cap = new Canvas { RenderTransform = lift, IsHitTestVisible = false };
        var topGeo = Shape(rects, o, 5, 3, 5, 9, 7);
        var top = new Path
        {
            Data = topGeo,
            Fill = light ? V((0, 0xFFF7F8FA), (0.55, 0xFFE6E8EC), (1, 0xFFD6D9DE)) : V((0, 0xFF858A93), (0.55, 0xFF6E737C), (1, 0xFF60656D)),
            Stroke = V((0, light ? 0xB0FFFFFFu : 0x50FFFFFFu), (0.3, 0x00FFFFFF), (1, 0x30000000)), StrokeThickness = 1,
        };
        cap.Children.Add(top);
        var hover = new Path { Data = topGeo, Fill = new SolidColorBrush(C(light ? 0x30FFFFFFu : 0x22FFFFFFu)), Opacity = 0 };
        cap.Children.Add(hover);

        // legend
        var face = topGeo.Bounds;
        var fg = new SolidColorBrush(light ? C(0xFF2A2D33) : Colors.White);
        TextBlock legend;
        if (id == 8)
        {
            legend = new TextBlock { Text = "E\nN\nT\nE\nR", FontFamily = Ui, FontWeight = FontWeights.SemiBold, FontSize = 10.5, LineHeight = 12.5,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight, TextAlignment = TextAlignment.Center, Foreground = fg, Width = 20 };
            Canvas.SetLeft(legend, 278 - o.X + 78 / 2.0 - 10 - 1); Canvas.SetTop(legend, face.Top + 22);
        }
        else
        {
            legend = new TextBlock { Text = id == 9 ? "TAB" : Legend(id), FontFamily = Ui, FontWeight = FontWeights.SemiBold, FontSize = id == 9 ? 15 : 22,
                Foreground = fg, TextAlignment = TextAlignment.Center, Width = face.Width };
            Canvas.SetLeft(legend, face.Left); Canvas.SetTop(legend, face.Top + (id == 9 ? 12 : 6));
        }
        legend.Effect = light ? null : new DropShadowEffect { BlurRadius = 2, ShadowDepth = 1, Direction = 270, Opacity = 0.35, Color = Colors.Black };
        cap.Children.Add(legend);
        TextBlock? sub = null;
        if (id != 8)
        {
            sub = new TextBlock { FontFamily = Ui, FontSize = 8.5, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, Width = face.Width - 8, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(light ? C(0xFFD8531A) : C(0xFFFFB38F)) };
            Canvas.SetLeft(sub, face.Left + 4); Canvas.SetTop(sub, face.Bottom - 15);
            cap.Children.Add(sub);
        }
        else
        {
            sub = new TextBlock { FontFamily = Ui, FontSize = 8, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, Width = 70, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(C(0xFFFFB38F)) };
            Canvas.SetLeft(sub, 278 - o.X + 4); Canvas.SetTop(sub, face.Bottom - 15);
            cap.Children.Add(sub);
        }
        var badge = Badge();
        Canvas.SetLeft(badge, face.Right - 12); Canvas.SetTop(badge, face.Top + 5);
        cap.Children.Add(badge);
        root.Children.Add(cap);

        var sel = SelectionPath(Shape(rects, o, -3, -3, -3, -3, 10));
        root.Children.Insert(0, sel);
        _canvas.Children.Add(root);
        Register(id, new Part { Root = root, Sel = sel, Badge = badge, Sub = sub, Lift = lift, HoverLayer = hover });
    }

    private static Ellipse Badge() => new()
    {
        Width = 7, Height = 7, Fill = new SolidColorBrush(Accent), Stroke = new SolidColorBrush(C(0xE0FFFFFF)), StrokeThickness = 1.2, Visibility = Visibility.Collapsed,
    };

    private static Path SelectionPath(Geometry g) => new()
    {
        Data = g, Stroke = new SolidColorBrush(Cyan), StrokeThickness = 2.4, Fill = new SolidColorBrush(C(0x182EC8FF)), Visibility = Visibility.Collapsed, IsHitTestVisible = false,
        Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 0, Color = Cyan, Opacity = 1 },
    };

    // ───────────────────────────── joystick module
    private void DrawModule()
    {
        var mod = new Rectangle
        {
            Width = 146, Height = 150, RadiusX = 18, RadiusY = 18,
            Fill = V((0, 0xFF3A3D44), (0.5, 0xFF2C2F35), (1, 0xFF222428)),
            Stroke = V((0, 0x40FFFFFF), (0.3, 0x10FFFFFF), (1, 0x90000000)), StrokeThickness = 1.3,
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 3.5, Direction = 270, Opacity = 0.6, Color = Colors.Black },
        };
        Place(mod, 378, 20);

        // knob well and outer ring (ring area = "joystick" click target)
        var ringRoot = new Canvas { Width = 92, Height = 92, Background = Brushes.Transparent };
        Canvas.SetLeft(ringRoot, StickC.X - 46); Canvas.SetTop(ringRoot, StickC.Y - 46);
        var ringSel = new Ellipse { Width = 92, Height = 92, Stroke = new SolidColorBrush(Cyan), StrokeThickness = 2.2, Visibility = Visibility.Collapsed, IsHitTestVisible = false,
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 0, Color = Cyan } };
        ringRoot.Children.Add(ringSel);
        var well = new Ellipse { Width = 84, Height = 84, Fill = new RadialGradientBrush(C(0xFF0A0B0D), C(0xFF1D1F23)) { GradientOrigin = new Point(0.5, 0.35) },
            Stroke = V((0, 0x90000000), (1, 0x26FFFFFF)), StrokeThickness = 1.5 };
        Canvas.SetLeft(well, 4); Canvas.SetTop(well, 4);
        ringRoot.Children.Add(well);
        var lift = new TranslateTransform();
        var knobGrp = new Canvas { RenderTransform = lift, IsHitTestVisible = false };
        var knob = new Ellipse
        {
            Width = 60, Height = 60,
            Fill = new RadialGradientBrush { GradientOrigin = new Point(0.42, 0.3), Center = new Point(0.5, 0.45), RadiusX = 0.62, RadiusY = 0.62,
                GradientStops = { new GradientStop(C(0xFF6A6F78), 0), new GradientStop(C(0xFF3D4148), 0.55), new GradientStop(C(0xFF24272C), 1) } },
            Stroke = V((0, 0x50FFFFFF), (0.5, 0x10FFFFFF), (1, 0x80000000)), StrokeThickness = 1.2,
            Effect = new DropShadowEffect { BlurRadius = 9, ShadowDepth = 3, Direction = 270, Opacity = 0.7, Color = Colors.Black },
        };
        Canvas.SetLeft(knob, 16); Canvas.SetTop(knob, 16);
        knobGrp.Children.Add(knob);
        foreach (var (r, a) in new[] { (25.0, 0x16u), (19.0, 0x1Cu) })
        {
            var ring = new Ellipse { Width = r * 2, Height = r * 2, Stroke = new SolidColorBrush(Color.FromArgb((byte)a, 255, 255, 255)), StrokeThickness = 0.8 };
            Canvas.SetLeft(ring, 46 - r); Canvas.SetTop(ring, 46 - r);
            knobGrp.Children.Add(ring);
        }
        // four direction triangles on the knob
        for (int i = 0; i < 4; i++)
        {
            var tri = new Path { Data = Geometry.Parse("M 0,-4 L 3.6,1.6 L -3.6,1.6 Z"), Fill = new SolidColorBrush(C(0xC8FFFFFF)),
                RenderTransform = new TransformGroup { Children = { new RotateTransform(i * 90), new TranslateTransform(46 + 22 * Math.Sin(i * Math.PI / 2), 46 - 22 * Math.Cos(i * Math.PI / 2)) } } };
            knobGrp.Children.Add(tri);
        }
        var hoverRing = new Ellipse { Width = 60, Height = 60, Fill = new SolidColorBrush(C(0x18FFFFFF)), Opacity = 0 };
        Canvas.SetLeft(hoverRing, 16); Canvas.SetTop(hoverRing, 16);
        knobGrp.Children.Add(hoverRing);
        ringRoot.Children.Add(knobGrp);
        _canvas.Children.Add(ringRoot);
        Register(JoystickKnob, new Part { Root = ringRoot, Sel = ringSel, Badge = new Ellipse(), Lift = lift, HoverLayer = hoverRing });

        // centre = stick press (column 16)
        var press = new Canvas { Width = 30, Height = 30, Background = Brushes.Transparent };
        Canvas.SetLeft(press, StickC.X - 15); Canvas.SetTop(press, StickC.Y - 15);
        var pSel = new Ellipse { Width = 30, Height = 30, Stroke = new SolidColorBrush(Cyan), StrokeThickness = 2.2, Fill = new SolidColorBrush(C(0x302EC8FF)), Visibility = Visibility.Collapsed, IsHitTestVisible = false,
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 0, Color = Cyan } };
        var dish = new Ellipse { Width = 22, Height = 22, Fill = new RadialGradientBrush(C(0xFF2C2F35), C(0xFF4A4E56)) { GradientOrigin = new Point(0.5, 0.75) },
            Stroke = V((0, 0x70000000), (1, 0x30FFFFFF)), StrokeThickness = 1 };
        Canvas.SetLeft(dish, 4); Canvas.SetTop(dish, 4);
        var dot = new Path { Data = Geometry.Parse("M 15,9.5 L 15,17.5 M 11.5,14.5 L 15,18.5 L 18.5,14.5"), Stroke = new SolidColorBrush(C(0xE6FFFFFF)), StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        var pHover = new Ellipse { Width = 22, Height = 22, Fill = new SolidColorBrush(C(0x30FFFFFF)), Opacity = 0, IsHitTestVisible = false };
        Canvas.SetLeft(pHover, 4); Canvas.SetTop(pHover, 4);
        var pBadge = Badge();
        Canvas.SetLeft(pBadge, 22); Canvas.SetTop(pBadge, 1);
        press.Children.Add(pSel); press.Children.Add(dish); press.Children.Add(dot); press.Children.Add(pHover); press.Children.Add(pBadge);
        _canvas.Children.Add(press);
        Register(StickPress, new Part { Root = press, Sel = pSel, Badge = pBadge, HoverLayer = pHover });

        // direction chips around the stick
        foreach (var (id, glyph) in new[] { (15, "\uE70E"), (14, "\uE70D"), (12, "\uE76B"), (13, "\uE76C") })
            ChipPart(id, Zones[id], new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 11, Foreground = new SolidColorBrush(C(0xE6FFFFFF)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
    }

    private void ChipPart(int id, Rect r, UIElement content)
    {
        var root = new Canvas { Width = r.Width, Height = r.Height, Background = Brushes.Transparent };
        Canvas.SetLeft(root, r.X); Canvas.SetTop(root, r.Y);
        var sel = new Rectangle { Width = r.Width + 6, Height = r.Height + 6, RadiusX = 11, RadiusY = 11, Stroke = new SolidColorBrush(Cyan), StrokeThickness = 2.2,
            Fill = new SolidColorBrush(C(0x202EC8FF)), Visibility = Visibility.Collapsed, IsHitTestVisible = false, Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 0, Color = Cyan } };
        Canvas.SetLeft(sel, -3); Canvas.SetTop(sel, -3);
        var lift = new TranslateTransform();
        var face = new Border
        {
            Width = r.Width, Height = r.Height, CornerRadius = new CornerRadius(9), RenderTransform = lift,
            Background = V((0, 0xFF474B53), (1, 0xFF2B2E34)), BorderBrush = V((0, 0x40FFFFFF), (1, 0x60000000)), BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 5, ShadowDepth = 1.5, Direction = 270, Opacity = 0.6, Color = Colors.Black },
            Child = content, IsHitTestVisible = false,
        };
        var hover = new Border { Width = r.Width, Height = r.Height, CornerRadius = new CornerRadius(9), Background = new SolidColorBrush(C(0x22FFFFFF)), Opacity = 0, RenderTransform = lift, IsHitTestVisible = false };
        var badge = Badge();
        Canvas.SetLeft(badge, r.Width - 8); Canvas.SetTop(badge, 1);
        root.Children.Add(sel); root.Children.Add(face); root.Children.Add(hover); root.Children.Add(badge);
        _canvas.Children.Add(root);
        Register(id, new Part { Root = root, Sel = sel, Badge = badge, Lift = lift, HoverLayer = hover });
    }

    // ───────────────────────────── wheel
    private void DrawWheel()
    {
        var housing = new Rectangle { Width = 34, Height = 70, RadiusX = 9, RadiusY = 9, Fill = V((0, 0xFF07080A), (1, 0xFF131417)), Stroke = V((0, 0xA0000000), (1, 0x28FFFFFF)), StrokeThickness = 1.2 };
        Place(housing, 434, 178);
        // drawn at its real size (no ScaleTransform → no moiré/banding on the ridges)
        const double W = 26, H = 62;
        var root = new Canvas { Width = W, Height = H, Background = Brushes.Transparent };
        Canvas.SetLeft(root, 438); Canvas.SetTop(root, 182);
        var sel = new Rectangle { Width = W + 6, Height = H + 6, RadiusX = 9, RadiusY = 9, Stroke = new SolidColorBrush(Cyan), StrokeThickness = 2.2, Visibility = Visibility.Collapsed, IsHitTestVisible = false,
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 0, Color = Cyan } };
        Canvas.SetLeft(sel, -3); Canvas.SetTop(sel, -3);
        root.Children.Add(sel);
        var cyl = new Canvas { Width = W, Height = H, Clip = new RectangleGeometry(new Rect(0, 0, W, H), 7, 7), IsHitTestVisible = false };
        cyl.Children.Add(new Rectangle { Width = W, Height = H, Fill = Hz((0, 0xFF3A3D42), (0.18, 0xFF8C9097), (0.42, 0xFFE4E7EB), (0.55, 0xFFF6F7F9), (0.75, 0xFF9DA1A8), (1, 0xFF3C3F44)) });
        // knurled ridges as one vector geometry each (dark groove + light edge), 4 units apart
        var grooves = new GeometryGroup(); var edges = new GeometryGroup();
        for (double y = 3; y < H; y += 4)
        {
            grooves.Children.Add(new LineGeometry(new Point(0, y), new Point(W, y)));
            edges.Children.Add(new LineGeometry(new Point(0, y + 1.3), new Point(W, y + 1.3)));
        }
        grooves.Freeze(); edges.Freeze();
        cyl.Children.Add(new Path { Data = grooves, Stroke = new SolidColorBrush(C(0x66000000)), StrokeThickness = 1.4 });
        cyl.Children.Add(new Path { Data = edges, Stroke = new SolidColorBrush(C(0x40FFFFFF)), StrokeThickness = 0.8 });
        cyl.Children.Add(new Rectangle { Width = W, Height = H, Fill = V((0, 0x90000000), (0.2, 0x00000000), (0.8, 0x00000000), (1, 0xA0000000)) });
        root.Children.Add(cyl);
        var hover = new Rectangle { Width = W, Height = H, RadiusX = 7, RadiusY = 7, Fill = new SolidColorBrush(C(0x26FFFFFF)), Opacity = 0, IsHitTestVisible = false };
        root.Children.Add(hover);
        _canvas.Children.Add(root);
        Register(WheelBody, new Part { Root = root, Sel = sel, Badge = new Ellipse(), HoverLayer = hover });

        foreach (var (id, glyph, text) in new[] { (RollUp, "\uE74A", "Roll+"), (RollDown, "\uE74B", "Roll−") })
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 9.5, Foreground = new SolidColorBrush(C(0xE6FFFFFF)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            sp.Children.Add(new TextBlock { Text = text, FontFamily = Ui, FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(C(0xE6FFFFFF)), VerticalAlignment = VerticalAlignment.Center });
            ChipPart(id, Zones[id], sp);
        }
    }

    // ───────────────────────────── LED preview
    private void DrawLedUnderglow()
    {
        foreach (var (id, rects) in KeyShapes)
        {
            var o = Zones[id].TopLeft;
            var glow = new SolidColorBrush(Colors.Transparent);
            var p = new Path { Data = Shape(rects, o, -2, -2, -2, -2, 10), Stroke = glow, StrokeThickness = 5, IsHitTestVisible = false, Effect = new BlurEffect { Radius = 9 } };
            Canvas.SetLeft(p, o.X); Canvas.SetTop(p, o.Y);
            _canvas.Children.Add(p);
            _led[id] = (glow, new SolidColorBrush(Colors.Transparent));
        }
    }

    private void DrawLedTint()
    {
        foreach (var (id, rects) in KeyShapes)
        {
            var o = Zones[id].TopLeft;
            var p = new Path { Data = Shape(rects, o, 5, 3, 5, 9, 7), Fill = _led[id].Fill, IsHitTestVisible = false };
            Canvas.SetLeft(p, o.X); Canvas.SetTop(p, o.Y);
            _canvas.Children.Add(p);
        }
    }

    // ───────────────────────────── interaction
    private void Register(int id, Part p)
    {
        _parts[id] = p;
        if (!_interactive) { p.Root.IsHitTestVisible = false; return; }
        p.Root.Cursor = Cursors.Hand;
        var dur = TimeSpan.FromMilliseconds(130);
        p.Root.MouseEnter += (_, _) =>
        {
            p.Lift?.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-1.6, dur) { EasingFunction = new CubicEase() });
            p.HoverLayer?.BeginAnimation(OpacityProperty, new DoubleAnimation(1, dur));
            Hovered?.Invoke(id);
        };
        p.Root.MouseLeave += (_, _) =>
        {
            p.Lift?.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, dur) { EasingFunction = new CubicEase() });
            p.HoverLayer?.BeginAnimation(OpacityProperty, new DoubleAnimation(0, dur));
            Hovered?.Invoke(null);
        };
        p.Root.MouseLeftButtonDown += (_, e) => { e.Handled = true; p.Lift?.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(60))); };
        p.Root.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            p.Lift?.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-1.6, dur));
            Clicked?.Invoke(id);
        };
    }

    /// <summary>Updates selection glow, remapped badges, sub-labels and tooltips.</summary>
    public void Rebuild(K13DeviceConfig cfg, HostSettings host, int selected, bool showDots = true)
    {
        foreach (var (id, p) in _parts)
        {
            bool sel = id == selected;
            SetSelected(p, sel);
            bool real = id is (>= 0 and <= 16) or RollUp or RollDown;
            if (!real) { if (_interactive) p.Root.ToolTip = id == JoystickKnob ? "Джойстик: выберите направление" : "Колесо: поворот вверх и вниз"; continue; }
            bool changed = Changed(cfg, id);
            p.Badge.Visibility = showDots && changed ? Visibility.Visible : Visibility.Collapsed;
            var action = ActionOf(cfg, host, id);
            if (p.Sub != null) p.Sub.Text = ShowSubLabels && changed ? (action == "—" ? "выкл." : Short(action)) : "";
            if (_interactive) p.Root.ToolTip = $"{char.ToUpper(ControlName(id)[0])}{ControlName(id)[1..]}: {action}" + (changed ? "  (изменено)" : "");
        }
    }

    private static string Short(string s) => s.Length <= 14 ? s : s[..13] + "…";

    private static void SetSelected(Part p, bool sel)
    {
        if (sel == (p.Sel.Visibility == Visibility.Visible)) return;
        p.Sel.Visibility = sel ? Visibility.Visible : Visibility.Collapsed;
        if (sel)
            p.Sel.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.55, TimeSpan.FromMilliseconds(1100)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });
        else p.Sel.BeginAnimation(OpacityProperty, null);
    }

    public static bool Changed(K13DeviceConfig cfg, int id) => id switch
    {
        RollUp => cfg.WheelSecond != 1,
        RollDown => cfg.WheelFirst != 0,
        _ when id >= 0 && id < K13Layout.FactoryColumnCodes.Length => cfg.GetKey(id) != K13Layout.FactoryColumnCodes[id],
        _ => false,
    };

    public static string ActionOf(K13DeviceConfig cfg, HostSettings host, int id)
    {
        if (id == RollUp) return K13Layout.WheelFunctions[cfg.WheelSecond & 7];
        if (id == RollDown) return K13Layout.WheelFunctions[cfg.WheelFirst & 7];
        var (_, text) = Labels.For(cfg.GetKey(id), cfg, host);
        return text;
    }

    /// <summary>Live LED preview (only when created with showLed).</summary>
    public void Animate(LedSettings led, double t)
    {
        if (!ShowLed) return;
        foreach (var (col, (glow, fill)) in _led)
        {
            var r = Zones[col];
            double x = (r.X + r.Width / 2 - 30) / 326.0, y = (r.Y + r.Height / 2 - 30) / 194.0;
            var (c, a) = LedSim.Key(led, led.Mode, x, y, col, 12, t);
            a = Math.Clamp(a, 0, 1);
            glow.Color = Color.FromArgb((byte)(255 * a), c.R, c.G, c.B);
            fill.Color = Color.FromArgb((byte)(55 * a), c.R, c.G, c.B);
        }
    }
}
