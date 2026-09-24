using System;
using System.IO;
using System.Reflection;
using WPR.SilverlightCompability;

/// <summary>
/// Checks WPR's gradient painting: that a linear gradient actually ramps along its axis, that a
/// radial one falls off from its origin, that spread methods differ outside the extent, and that
/// interpolating to a transparent stop does not darken (the premultiply trap).
/// </summary>
internal static class Gradients
{
    private static int _failures;

    public static int Run()
    {
        Console.WriteLine("gradients:");

        _failures += Linear();
        _failures += Radial();
        _failures += TransparentFade();
        _failures += Spread();

        return _failures;
    }

    private static int Linear()
    {
        // Black at the top, white at the bottom.
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
        };
        brush.GradientStops.Add(new GradientStop { Offset = 0, Color = Color.FromArgb(255, 0, 0, 0) });
        brush.GradientStops.Add(new GradientStop { Offset = 1, Color = Color.FromArgb(255, 255, 255, 255) });

        uint[] px = Paint(brush, 40, 100);

        int top = (int)(px[(2 * 40) + 20] & 0xFF);
        int middle = (int)(px[(50 * 40) + 20] & 0xFF);
        int bottom = (int)(px[(97 * 40) + 20] & 0xFF);

        int failures = 0;
        failures += Check("linear ramps top→bottom", top < 20 && middle > 100 && middle < 160 && bottom > 235,
            $"top={top} middle={middle} bottom={bottom}");

        // Nothing should vary across the axis it does not run along.
        int leftMid = (int)(px[(50 * 40) + 2] & 0xFF);
        int rightMid = (int)(px[(50 * 40) + 37] & 0xFF);
        failures += Check("linear is flat across its axis", Math.Abs(leftMid - rightMid) <= 1,
            $"left={leftMid} right={rightMid}");

        return failures;
    }

    private static int Radial()
    {
        var brush = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
        };
        brush.GradientStops.Add(new GradientStop { Offset = 0, Color = Color.FromArgb(255, 255, 255, 255) });
        brush.GradientStops.Add(new GradientStop { Offset = 1, Color = Color.FromArgb(255, 0, 0, 0) });

        uint[] px = Paint(brush, 101, 101);

        int centre = (int)(px[(50 * 101) + 50] & 0xFF);
        int edge = (int)(px[(50 * 101) + 99] & 0xFF);
        int corner = (int)(px[(2 * 101) + 2] & 0xFF);

        return Check("radial falls off from the origin", centre > 235 && edge < 30 && corner < 30,
            $"centre={centre} edge={edge} corner={corner}");
    }

    private static int TransparentFade()
    {
        // Opaque red to TRANSPARENT red. Interpolating premultiplied would darken the midpoint
        // towards black instead of just thinning it — the classic artefact.
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
        };
        brush.GradientStops.Add(new GradientStop { Offset = 0, Color = Color.FromArgb(255, 255, 0, 0) });
        brush.GradientStops.Add(new GradientStop { Offset = 1, Color = Color.FromArgb(0, 255, 0, 0) });

        uint[] px = Paint(brush, 20, 100);

        uint mid = px[(50 * 20) + 10];
        uint alpha = mid >> 24;
        uint red = mid & 0xFF;

        // Premultiplied: red should track alpha, i.e. the hue stays red as it thins.
        bool halfAlpha = alpha > 100 && alpha < 155;
        bool redTracksAlpha = Math.Abs((int)red - (int)alpha) <= 2;

        return Check("fade to transparent keeps its hue", halfAlpha && redTracksAlpha,
            $"alpha={alpha} red={red}");
    }

    private static int Spread()
    {
        // The gradient occupies only the top third, so the area below it is outside the extent
        // and the spread method decides what goes there.
        LinearGradientBrush Make(GradientSpreadMethod spread)
        {
            var b = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 0.25),
                SpreadMethod = spread,
            };
            b.GradientStops.Add(new GradientStop { Offset = 0, Color = Color.FromArgb(255, 0, 0, 0) });
            b.GradientStops.Add(new GradientStop { Offset = 1, Color = Color.FromArgb(255, 255, 255, 255) });
            return b;
        }

        uint[] pad = Paint(Make(GradientSpreadMethod.Pad), 20, 100);
        uint[] repeat = Paint(Make(GradientSpreadMethod.Repeat), 20, 100);
        uint[] reflect = Paint(Make(GradientSpreadMethod.Reflect), 20, 100);

        int padLow = (int)(pad[(90 * 20) + 10] & 0xFF);
        int repeatLow = (int)(repeat[(90 * 20) + 10] & 0xFF);
        int reflectLow = (int)(reflect[(90 * 20) + 10] & 0xFF);

        int failures = 0;
        failures += Check("pad holds the end colour", padLow > 240, $"value={padLow}");
        failures += Check("repeat restarts the ramp", repeatLow != padLow, $"value={repeatLow}");
        failures += Check("reflect differs from repeat", reflectLow != repeatLow,
            $"reflect={reflectLow} repeat={repeatLow}");
        return failures;
    }

    /// <summary>Paints a rectangle filled with <paramref name="brush"/> and returns the pixels.</summary>
    private static uint[] Paint(Brush brush, int w, int h)
    {
        var rect = new Rectangle { Fill = brush, Width = w, Height = h };
        var root = new Grid();
        root.Children.Add(rect);
        FrameworkElement.RunLayoutPass(root, w, h);

        Assembly asm = typeof(FrameworkElement).Assembly;
        Type rasteriser = asm.GetType("WPR.SilverlightCompability.SoftwareVisualRasteriser");
        MethodInfo paint = rasteriser.GetMethod(
            "Paint", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

        var buffer = new uint[w * h];
        paint.Invoke(null, new object[] { root, w, h, buffer });
        return buffer;
    }

    private static int Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}  ({detail})");
        return ok ? 0 : 1;
    }
}
