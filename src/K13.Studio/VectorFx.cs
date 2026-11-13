using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Vorotex.Studio;

/// <summary>
/// Pure-vector replacements for WPF bitmap effects. DropShadowEffect/BlurEffect render the element (and all its
/// children, including text) through an off-screen bitmap, which makes keys and legends soft. Here a shadow or glow is
/// a stack of semi-transparent strokes of the same geometry, so everything stays razor-sharp at any zoom/DPI.
/// </summary>
internal static class VectorFx
{

    /// <summary>Soft shadow/glow made of stacked strokes. <paramref name="brush"/> may be shared/animated (alpha via Opacity).</summary>
    public static Canvas Halo(Geometry g, Brush brush, double blur, double opacity, double dy, bool fill)
    {
        var c = new Canvas { IsHitTestVisible = false, SnapsToDevicePixels = false };
        int Layers = (int)Math.Clamp(Math.Ceiling(blur * 2), 4, 32);          // ≤ 0.5 unit per step → no visible bands
        double per = 1 - Math.Pow(1 - Math.Clamp(opacity, 0, 0.99), 1.0 / Layers); // n layers overlap to ≈ opacity
        for (int i = Layers; i >= 1; i--)
            c.Children.Add(new Path
            {
                Data = g, Stroke = brush, StrokeThickness = Math.Max(0.5, blur * 2 * i / Layers), StrokeLineJoin = PenLineJoin.Round,
                Fill = fill && i == 1 ? brush : null, Opacity = per, IsHitTestVisible = false,
            });
        if (Math.Round(dy) != 0) c.RenderTransform = new TranslateTransform(0, Math.Round(dy)); // v5.1: whole-pixel offsets only
        return c;
    }

    /// <summary>Replaces every DropShadowEffect/BlurEffect under <paramref name="root"/> with vector halos.</summary>
    public static void ConvertEffects(Panel root)
    {
        foreach (var child in root.Children.OfType<UIElement>().ToList())
        {
            if (child is FrameworkElement fe && fe.Effect != null) Convert(root, fe);
            if (child is Panel p) ConvertEffects(p);
            else if (child is Border { Child: Panel bp }) ConvertEffects(bp);
            else if (child is Decorator { Child: Panel dp }) ConvertEffects(dp);
        }
    }

    private static void Convert(Panel parent, FrameworkElement fe)
    {
        var fx = fe.Effect;
        fe.Effect = null;
        if (parent is not Canvas) return; // auto-sized content: just drop the bitmap effect
        Color color = Colors.Black; double blur, op = 1, depth = 0;
        bool isBlur = false;
        switch (fx)
        {
            case DropShadowEffect d:
                color = d.Color; blur = d.BlurRadius / 2; op = d.Opacity; depth = d.ShadowDepth * -Math.Sin(d.Direction * Math.PI / 180);
                break;
            case BlurEffect b: blur = b.Radius / 2; isBlur = true; break;
            default: return;
        }
        UIElement? layer = null;
        if (fe is TextBlock tb)
        {
            if (isBlur || depth == 0) return;
            layer = new TextBlock
            {
                Text = tb.Text, FontFamily = tb.FontFamily, FontSize = tb.FontSize, FontWeight = tb.FontWeight, TextAlignment = tb.TextAlignment, Width = tb.Width,
                LineHeight = tb.LineHeight, LineStackingStrategy = tb.LineStackingStrategy, TextTrimming = tb.TextTrimming, IsHitTestVisible = false,
                Foreground = new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Min(1, op * 1.4)), color.R, color.G, color.B)),
                RenderTransform = new TranslateTransform(0, Math.Max(1, Math.Round(depth * 0.8))), // v5.1: integer → no smeared text
            };
        }
        else
        {
            Geometry? g = fe switch
            {
                Path p => p.Data,
                Ellipse e when !double.IsNaN(e.Width) => new EllipseGeometry(new Rect(0, 0, e.Width, e.Height)),
                Rectangle r when !double.IsNaN(r.Width) => new RectangleGeometry(new Rect(0, 0, r.Width, r.Height), r.RadiusX, r.RadiusY),
                Border b when !double.IsNaN(b.Width) => new RectangleGeometry(new Rect(0, 0, b.Width, b.Height), b.CornerRadius.TopLeft, b.CornerRadius.TopLeft),
                _ => null,
            };
            if (g == null) return;
            Brush brush;
            if (isBlur && fe is Shape s && s.Stroke != null) { brush = s.Stroke; blur += s.StrokeThickness / 2; }
            else brush = new SolidColorBrush(color);
            bool filled = fe is Shape sh ? sh.Fill != null && !isBlur : true;
            var halo = Halo(g, brush, blur, isBlur ? 0.9 : op, 0, filled && depth != 0);
            var tg = new TransformGroup();
            if (fe.RenderTransform is { } rt && rt != Transform.Identity) tg.Children.Add(rt);
            if (Math.Round(depth) != 0) tg.Children.Add(new TranslateTransform(0, Math.Round(depth)));
            halo.RenderTransform = tg;
            halo.RenderTransformOrigin = fe.RenderTransformOrigin;
            layer = halo;
        }
        Canvas.SetLeft(layer, Canvas.GetLeft(fe)); Canvas.SetTop(layer, Canvas.GetTop(fe));
        BindingOperations.SetBinding(layer, UIElement.VisibilityProperty, new Binding("Visibility") { Source = fe });
        BindingOperations.SetBinding(layer, UIElement.OpacityProperty, new Binding("Opacity") { Source = fe });
        parent.Children.Insert(parent.Children.IndexOf(fe), layer);
    }
}
