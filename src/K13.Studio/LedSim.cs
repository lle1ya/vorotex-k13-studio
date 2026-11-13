using System.Windows.Media;
using Vorotex.K13;

namespace Vorotex.Studio;

/// <summary>Approximate on-screen simulation of the K13 LED effects (preview only; the firmware renders the real thing).</summary>
public static class LedSim
{
    public static Color Slot(LedSettings led, int i)
    {
        var c = led.Colors[Math.Clamp(i, 0, 6)];
        return Color.FromRgb((byte)(c.R * 17), (byte)(c.G * 17), (byte)(c.B * 17));
    }

    public static Color Hsv(double h, double s = 1, double v = 1)
    {
        h = (h % 1 + 1) % 1 * 6;
        int i = (int)h; double f = h - i;
        double p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        var (r, g, b) = i switch { 0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t), 3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q) };
        return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    private static Color Lerp(Color a, Color b, double k) =>
        Color.FromRgb((byte)(a.R + (b.R - a.R) * k), (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k));

    private static double Frac(double v) => v - Math.Floor(v);
    private static double Hash(int i) => Frac(Math.Sin(i * 12.9898 + 4.1414) * 43758.5453);

    /// <summary>Current colour of the selection (single slot or cycling through enabled slots).</summary>
    public static Color BaseColor(LedSettings led, double t, double speed)
    {
        if (led.SingleSlot is int s) return Slot(led, s);
        var slots = Enumerable.Range(0, 7).Where(i => (led.CycleMask & (1 << i)) != 0).ToList();
        if (slots.Count < 2) slots = Enumerable.Range(0, 7).ToList();
        double p = t * speed * 0.35;
        int a = (int)Math.Floor(p) % slots.Count;
        return Lerp(Slot(led, slots[a]), Slot(led, slots[(a + 1) % slots.Count]), Math.Clamp((Frac(p) - 0.7) / 0.3, 0, 1));
    }

    /// <summary>Colour and intensity (0..1) of one key. x,y in 0..1 across the board; idx = key order.</summary>
    public static (Color C, double A) Key(LedSettings led, int mode, double x, double y, int idx, int count, double t)
    {
        double speed = 0.45 + Math.Clamp(led.Speed, 0, 7) * 0.22;
        double bright = (led.BrightnessLevel + 1) / 7.0;
        var bc = BaseColor(led, t, speed);
        (Color c, double a) r = mode switch
        {
            0 => (bc, 0),
            1 => (bc, 0.12 + 0.88 * (0.5 - 0.5 * Math.Cos(2 * Math.PI * t * speed * 0.45))),
            2 => (bc, 1),
            3 => (bc, Math.Max(0.1, 1 - Frac(t * speed * 0.5 + Hash(idx)) * 3.2)),
            4 => (Hsv(t * speed * 0.12), 1),
            5 => (Hsv(x * 0.45 + y * 0.15 + t * speed * 0.08), 1),
            6 => Comet(bc, x, t, speed),
            7 => (bc, (int)Math.Floor(t * speed * 3) % count == idx ? 1 : (int)Math.Floor(t * speed * 3 - 1) % count == idx ? 0.35 : 0.04),
            8 => (Hsv(x * 0.8 - t * speed * 0.25), 1),
            9 => (bc, Frac(t * speed * 0.7) < 0.5 ? 1 : 0.03),
            10 => (bc, 0.15 + 0.85 * (0.5 + 0.5 * Math.Sin(2 * Math.PI * (x * 1.1 - t * speed * 0.35)))),
            11 => (((x < 0.5) ^ (Frac(t * speed * 0.15) < 0.5)) ? Slot(led, 0) : Slot(led, 1), 1),
            12 => (Hsv((double)idx / count + t * speed * 0.1), 1),
            _ => (bc, 1),
        };
        return (r.c, Math.Clamp(r.a * bright, 0, 1));
    }

    private static (Color, double) Comet(Color bc, double x, double t, double speed)
    {
        double pos = Frac(t * speed * 0.3) * 1.5 - 0.1;
        double d = pos - x;
        return (bc, d >= 0 && d < 0.4 ? 1 - d / 0.4 : 0.03);
    }
}
