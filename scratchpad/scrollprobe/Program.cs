using System;
using System.Reflection;
using Microsoft.Phone.Controls;
using WPR.SilverlightCompability;

// Drives WPR's Silverlight scrolling end to end without a game: builds a ScrollViewer over
// oversized content, lays it out, drags it with the real touch router, and checks that the
// offset moved, that it clamps at both ends, that a flick coasts, and that the rasterised
// pixels actually change.
internal static class Program
{
    private static int _failures;

    private static int Main()
    {
        const double ViewportH = 200;
        const double ItemH = 60;
        const int ItemCount = 10;   // 600px of content in a 200px window

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        for (int i = 0; i < ItemCount; i++)
        {
            stack.Children.Add(new Rectangle
            {
                Height = ItemH,
                Width = 300,
                // A DISTINCT colour per item. Alternating two colours makes the rasterised output
                // periodic, so a scroll by exactly one period is invisible and the pixel check
                // passes or fails for the wrong reason.
                Fill = new SolidColorBrush(Color.FromArgb(255, (byte)(20 + (i * 22)), 60, 200)),
            });
        }

        var scroller = new ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Width = 300,
            Height = ViewportH,
        };

        var root = new Grid { Background = new SolidColorBrush(Color.FromArgb(255, 0, 0, 0)) };
        root.Children.Add(scroller);

        FrameworkElement.RunLayoutPass(root, 300, ViewportH);

        Check("extent measured", Math.Abs(scroller.ExtentHeight - (ItemH * ItemCount)) < 1,
            $"ExtentHeight={scroller.ExtentHeight}");
        Check("viewport measured", Math.Abs(scroller.ViewportHeight - ViewportH) < 1,
            $"ViewportHeight={scroller.ViewportHeight}");
        Check("scrollable range", Math.Abs(scroller.ScrollableHeight - ((ItemH * ItemCount) - ViewportH)) < 1,
            $"ScrollableHeight={scroller.ScrollableHeight}");

        uint[] before = Render(root, 300, (int)ViewportH);

        // Drag UP by 120px: content follows the finger, so the viewport moves DOWN by 120.
        SilverlightTouchRouter.Press(root, 150, 150);
        SilverlightTouchRouter.Move(150, 110);
        SilverlightTouchRouter.Move(150, 70);
        SilverlightTouchRouter.Move(150, 30);
        SilverlightTouchRouter.Release(150, 30);

        Check("drag scrolled down", scroller.VerticalOffset > 100,
            $"VerticalOffset={scroller.VerticalOffset:F1} (expected ~120)");

        uint[] after = Render(root, 300, (int)ViewportH);
        Check("pixels changed", !SamePixels(before, after),
            $"before red={Red(before)} after red={Red(after)}, distinct before={Distinct(before)}");

        // Clamp at the top: drag far further than the content allows.
        SilverlightTouchRouter.Press(root, 150, 30);
        for (int i = 0; i < 20; i++) SilverlightTouchRouter.Move(150, 30 + (i * 40));
        SilverlightTouchRouter.Release(150, 830);
        Check("clamped at top", scroller.VerticalOffset == 0,
            $"VerticalOffset={scroller.VerticalOffset:F1} (expected 0)");

        // Clamp at the bottom. The PRESS must be inside the viewport — a press outside it hit
        // tests to nothing, so no scroller is picked up and the drag does nothing at all.
        SilverlightTouchRouter.Press(root, 150, 190);
        for (int i = 0; i < 20; i++) SilverlightTouchRouter.Move(150, 190 - (i * 40));
        SilverlightTouchRouter.Release(150, 190 - (20 * 40));
        Check("clamped at bottom", Math.Abs(scroller.VerticalOffset - scroller.ScrollableHeight) < 0.5,
            $"VerticalOffset={scroller.VerticalOffset:F1} expected {scroller.ScrollableHeight:F1}");

        // A flick coasts, then settles. Paced like a real finger — without the sleeps the samples
        // are microseconds apart and the derived velocity is nonsense.
        scroller.ScrollToVerticalOffset(100);
        SilverlightTouchRouter.Press(root, 150, 150);
        System.Threading.Thread.Sleep(16);
        SilverlightTouchRouter.Move(150, 130);
        System.Threading.Thread.Sleep(16);
        SilverlightTouchRouter.Move(150, 110);
        System.Threading.Thread.Sleep(16);
        SilverlightTouchRouter.Release(150, 90);

        double afterRelease = scroller.VerticalOffset;
        bool coastedAtAll = false;
        for (int frame = 0; frame < 120; frame++)
        {
            if (!SilverlightTouchRouter.TickInertia(1.0 / 60.0)) break;
            coastedAtAll = true;
        }

        Check("flick coasted", coastedAtAll && scroller.VerticalOffset > afterRelease,
            $"after release {afterRelease:F1} -> settled {scroller.VerticalOffset:F1}");
        Check("flick settled", !SilverlightTouchRouter.TickInertia(1.0 / 60.0),
            "still coasting after 2 seconds");

        // A tap (no movement past the slop) must not scroll.
        double beforeTap = scroller.VerticalOffset;
        SilverlightTouchRouter.Press(root, 150, 100);
        SilverlightTouchRouter.Move(150, 103);
        SilverlightTouchRouter.Release(150, 103);
        Check("tap does not scroll", Math.Abs(scroller.VerticalOffset - beforeTap) < 0.001,
            $"moved from {beforeTap:F1} to {scroller.VerticalOffset:F1}");

        _failures += Gradients.Run();

        Console.WriteLine(_failures == 0 ? "ALL PASS" : $"{_failures} FAILED");
        return _failures == 0 ? 0 : 1;
    }

    private static uint[] Render(UIElement root, int w, int h)
    {
        Assembly asm = typeof(FrameworkElement).Assembly;
        Type rasteriser = asm.GetType("WPR.SilverlightCompability.SoftwareVisualRasteriser");
        MethodInfo paint = rasteriser.GetMethod(
            "Paint", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

        var buffer = new uint[w * h];
        paint.Invoke(null, new object[] { root, w, h, buffer });
        return buffer;
    }

    private static int Red(uint[] b)
    {
        int n = 0;
        foreach (uint p in b) if ((p & 0xFF) > 150) n++;
        return n;
    }

    private static int Distinct(uint[] b)
    {
        var s = new System.Collections.Generic.HashSet<uint>();
        foreach (uint p in b) s.Add(p);
        return s.Count;
    }

    private static int Ink(uint[] b)
    {
        int n = 0;
        foreach (uint p in b) if (p != 0) n++;
        return n;
    }

    private static bool SamePixels(uint[] a, uint[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}  ({detail})");
        if (!ok) _failures++;
    }
}
