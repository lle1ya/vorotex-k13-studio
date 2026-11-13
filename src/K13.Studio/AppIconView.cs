using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Vorotex.Studio;

/// <summary>v5.1: the app icon as vector drawing, laid out on the *device-pixel* grid of the current DPI
/// (same integer layout as tools/icon/make_icon.py), so it is razor sharp at 100/125/150/175 % – no bitmap scaling.</summary>
public sealed class AppIconView : FrameworkElement
{
    private static readonly Brush Orange = Frozen(0xF0, 0x64, 0x23), Body = Frozen(0x24, 0x27, 0x2E), Edge = Frozen(0x14, 0x16, 0x1A),
        Key = Frozen(0xE8, 0xEA, 0xEE), Plate = Frozen(0x18, 0x1A, 0x1F), Wheel = Frozen(0x4E, 0x52, 0x5A);

    private static Brush Frozen(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }

    public AppIconView()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        IsHitTestVisible = false;
        RenderOptions.SetEdgeMode(this, EdgeMode.Unspecified);
        LayoutUpdated += (_, _) => { if (Snap() != _snap) InvalidateVisual(); };
    }

    /// <summary>Offline screenshots render at 150 % through RenderTargetBitmap – tell the icon which pixel grid to use.</summary>
    internal static double? ScaleOverride;
    private Vector _snap;

    private double Scale => ScaleOverride ?? VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>Sub-pixel correction that puts our origin exactly on a device pixel (e.g. 9 DIP × 150 % = 13.5 px → 14 px).</summary>
    private Vector Snap()
    {
        try
        {
            DependencyObject top = this;
            while (VisualTreeHelper.GetParent(top) is { } p) top = p;
            if (top == this || top is not Visual tv) return default;
            var o = TransformToAncestor(tv).Transform(new Point(0, 0));
            double sc = Scale;
            return new Vector(Math.Round(o.X * sc) - o.X * sc, Math.Round(o.Y * sc) - o.Y * sc);
        }
        catch { return default; }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) => InvalidateVisual();

    private static int R(double v) => (int)Math.Round(v, MidpointRounding.ToEven); // = Python round()

    protected override void OnRender(DrawingContext dc)
    {
        double sc = Scale;
        int S = Math.Max(8, (int)Math.Round(Math.Min(ActualWidth, ActualHeight) * sc));
        _snap = Snap();
        dc.PushTransform(new MatrixTransform(1 / sc, 0, 0, 1 / sc, _snap.X / sc, _snap.Y / sc));
        void Rect(int x0, int y0, int x1, int y1, int r, Brush b) => dc.DrawRoundedRectangle(b, null, new Rect(x0, y0, x1 - x0, y1 - y0), r, r);
        void Circle(int cx2, int cy2, int dia, Brush b) { if (dia > 0) dc.DrawEllipse(b, null, new Point(cx2 / 2.0, cy2 / 2.0), dia / 2.0, dia / 2.0); }

        Rect(0, 0, S, S, Math.Max(3, R(S * 0.22)), Orange);
        int m = S < 20 ? Math.Max(1, R(S * 0.09)) : Math.Max(2, R(S * 0.09));
        int by0 = S >= 30 ? R(S * 0.25) : R(S * 0.2);
        int by1 = S - (S >= 30 ? R(S * 0.22) : R(S * 0.2));
        int rb = Math.Max(1, R(S * 0.08));
        if (S >= 30) Rect(m, by0 + 1, S - m, by1 + 1, rb, Edge);
        Rect(m, by0, S - m, by1, rb, Body);
        int p = Math.Max(1, R(S * 0.06));
        int ix0 = m + p, iy0 = by0 + p, ix1 = S - m - p, iy1 = by1 - p;
        int h = iy1 - iy0, w = ix1 - ix0;
        int gap = Math.Max(1, R(S * 0.07)), g = Math.Max(1, R(S / 48.0));
        int kr = S < 40 ? 0 : Math.Max(1, R(S * 0.02));
        if (S < 30)
        {
            int k = Math.Min((h - g) / 2, (w - gap - g - 2) / 3);
            int blk = 2 * k + g;
            int dia = Math.Min(Math.Min(blk + 2, w - blk - gap), h);
            if ((dia - blk) % 2 != 0) dia--;
            iy0 += (h - blk) / 2;
            ix0 += (w - (blk + gap + dia)) / 2;
            ix1 = ix0 + blk + gap + dia;
            for (int cx = 0; cx < 2; cx++)
                for (int cy = 0; cy < 2; cy++)
                { int x = ix0 + cx * (k + g), y = iy0 + cy * (k + g); Rect(x, y, x + k, y + k, 0, Key); }
            int jx = ix1 - dia, cy2 = 2 * iy0 + blk, ring = S < 24 ? 1 : 2;
            Circle(2 * jx + dia, cy2, dia, Orange);
            Circle(2 * jx + dia, cy2, dia - 2 * ring, Plate);
            int cd = Math.Max(1, dia - 2 * ring - 2);
            if ((dia - cd) % 2 != 0) cd--;
            Circle(2 * jx + dia, cy2, cd, Key);
        }
        else
        {
            int k = Math.Min((h - 2 * g) / 3, (w - 3 * g - gap) / 5);
            int top = iy0 + (h - (3 * k + 2 * g)) / 2;
            ix0 += (w - (5 * k + 3 * g + gap)) / 2;
            ix1 = ix0 + 5 * k + 3 * g + gap;
            for (int cx = 0; cx < 3; cx++)
                for (int cy = 0; cy < 2; cy++)
                { int x = ix0 + cx * (k + g), y = top + cy * (k + g); Rect(x, y, x + k, y + k, kr, cx == 0 && cy == 0 ? Orange : Key); }
            int wy = top + 2 * (k + g);
            Rect(ix0, wy, ix0 + 3 * k + 2 * g, wy + k, kr, Key);
            int dia = 2 * k + g, jx = ix1 - dia, ring = Math.Max(1, R(S / 28.0));
            Circle(2 * jx + dia, 2 * top + dia, dia, Orange);
            Circle(2 * jx + dia, 2 * top + dia, dia - 2 * ring, Plate);
            int cd = dia - 2 * ring - 2 * Math.Max(1, R(dia * 0.18));
            if ((dia - cd) % 2 != 0) cd--;
            Circle(2 * jx + dia, 2 * top + dia, Math.Max(2, cd), Key);
            Rect(jx + Math.Max(1, dia / 6), wy + Math.Max(0, k / 5), ix1 - Math.Max(1, dia / 6), wy + k - Math.Max(0, k / 5), Math.Max(0, k / 4), Wheel);
        }
        dc.Pop();
    }
}

/// <summary>Exact-size window and tray icons from the multi-size .ico (no 256 px → 24 px downscaling).</summary>
internal static class CrispIcons
{
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    private const int SM_CXICON = 11, SM_CXSMICON = 49, WM_SETICON = 0x80;
    private static readonly List<System.Drawing.Icon> Keep = new();
    private const string Uri = "pack://application:,,,/VorotexK13Studio;component/Assets/app.ico";

    public static System.Drawing.Icon? Load(int px)
    {
        try
        {
            var res = Application.GetResourceStream(new Uri(Uri));
            if (res == null) return null;
            using (res.Stream) { var ic = new System.Drawing.Icon(res.Stream, new System.Drawing.Size(px, px)); Keep.Add(ic); return ic; }
        }
        catch { return null; }
    }

    public static int SmallIconPx(uint dpi = 0)
    {
        try { return GetSystemMetricsForDpi(SM_CXSMICON, dpi == 0 ? GetDpiForSystem() : dpi); }
        catch { return 16; }
    }

    /// <summary>Taskbar shows ICON_BIG at 24 DIP; title/Alt+Tab use ICON_SMALL – give both at their exact pixel size.</summary>
    public static void Apply(Window w)
    {
        try
        {
            var h = new WindowInteropHelper(w).Handle;
            if (h == IntPtr.Zero) return;
            double scale = VisualTreeHelper.GetDpi(w).DpiScaleX;
            uint dpi = (uint)Math.Round(96 * scale);
            var big = Load((int)Math.Round(24 * scale));
            var small = Load(SmallIconPx(dpi));
            if (big != null) SendMessage(h, WM_SETICON, (IntPtr)1, big.Handle);
            if (small != null) SendMessage(h, WM_SETICON, IntPtr.Zero, small.Handle);
        }
        catch { }
    }
}
