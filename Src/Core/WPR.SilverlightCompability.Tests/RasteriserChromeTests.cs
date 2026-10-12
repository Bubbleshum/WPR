using System.Threading;
using Xunit;

// Stand-ins for the WP toolkit controls, which live in game-shipped assemblies. Chrome is chosen
// by type FULL NAME (WpChrome.Classify), so a type with the same name is recognised the same way.
namespace Microsoft.Phone.Controls
{
    public class Panorama : WPR.SilverlightCompability.Grid
    {
        public object? Title { get; set; }
    }

    public class PanoramaItem : WPR.SilverlightCompability.ContentControl
    {
        public object? Header { get; set; }
    }

    public class ToggleSwitch : WPR.SilverlightCompability.Control
    {
        public object? Header { get; set; }
        public bool? IsChecked { get; set; }
    }
}

namespace WPR.SilverlightCompability.Tests
{
    /// <summary>
    /// WP7 control chrome drawn by the CPU rasteriser (SoftwareVisualRasteriser.Controls.cs) and
    /// its change signature.
    /// </summary>
    public class RasteriserChromeTests
    {
        private static uint[] Paint(UIElement root, int w, int h)
        {
            FrameworkElement.RunLayoutPass(root, w, h);
            var px = new uint[w * h];
            SoftwareVisualRasteriser.Paint(root, w, h, px);
            return px;
        }

        private static (byte R, byte G, byte B, byte A) At(uint[] px, int w, int x, int y)
        {
            uint p = px[(y * w) + x];
            return ((byte)p, (byte)(p >> 8), (byte)(p >> 16), (byte)(p >> 24));
        }

        private static SolidColorBrush Solid(byte r, byte g, byte b) => new SolidColorBrush(Color.FromArgb(255, r, g, b));

        [Fact]
        public void Button_HasThreePixelForegroundBorder_AndTransparentInterior()
        {
            var button = new Button { Content = "", Width = 200, Height = 80 };
            var root = new Grid();
            root.Children.Add(button);

            uint[] px = Paint(root, 200, 80);

            Assert.Equal((byte)255, At(px, 200, 100, 0).A);      // top edge
            Assert.Equal((byte)255, At(px, 200, 100, 2).R);      // still border at y=2
            Assert.Equal((byte)0, At(px, 200, 100, 3).A);        // interior starts at 3
            Assert.Equal((byte)255, At(px, 200, 1, 40).A);       // left edge
            Assert.Equal((byte)0, At(px, 200, 100, 40).A);       // transparent middle
        }

        [Fact]
        public void Button_Pressed_FillsWithForeground_AndChangesSignature()
        {
            var button = new Button { Content = "", Width = 200, Height = 80 };
            var root = new Grid();
            root.Children.Add(button);
            FrameworkElement.RunLayoutPass(root, 200, 80);

            int before = SoftwareVisualRasteriser.Signature(root);
            button.SetPressedByHost(true);
            int after = SoftwareVisualRasteriser.Signature(root);
            Assert.NotEqual(before, after);

            uint[] px = Paint(root, 200, 80);
            var mid = At(px, 200, 100, 40);
            Assert.Equal((255, 255, 255, 255), ((int)mid.R, (int)mid.G, (int)mid.B, (int)mid.A));
        }

        [Fact]
        public void Button_HonoursItsOwnBorderBrushAndThickness()
        {
            var button = new Button
            {
                Content = "", Width = 200, Height = 80,
                BorderBrush = Solid(255, 0, 0), BorderThickness = new Thickness(6),
            };
            var root = new Grid();
            root.Children.Add(button);

            uint[] px = Paint(root, 200, 80);
            var edge = At(px, 200, 100, 5);
            Assert.Equal(255, edge.R);
            Assert.Equal(0, edge.G);
            Assert.Equal((byte)0, At(px, 200, 100, 6).A);
        }

        [Fact]
        public void DeterminateProgressBar_FillsItsFractionInTheAccent()
        {
            var bar = new ProgressBar { Width = 200, Height = 20, Value = 50, IsIndeterminate = false };
            var root = new Grid();
            root.Children.Add(bar);

            uint[] px = Paint(root, 200, 20);
            var left = At(px, 200, 50, 10);
            Assert.Equal(WpChrome.DefaultAccent.R, left.R);
            Assert.Equal(WpChrome.DefaultAccent.B, left.B);
            Assert.Equal((byte)0, At(px, 200, 150, 10).A);       // past 50%
            Assert.Equal((byte)0, At(px, 200, 50, 2).A);         // the bar is 4px, centred
        }

        [Fact]
        public void IndeterminateProgressBar_SignatureAdvancesWithTheAnimation()
        {
            var bar = new ProgressBar { Width = 400, Height = 20, IsIndeterminate = true };
            var root = new Grid();
            root.Children.Add(bar);
            FrameworkElement.RunLayoutPass(root, 400, 20);

            int first = SoftwareVisualRasteriser.Signature(root);
            Thread.Sleep(WpChrome.ProgressFrameMs * 3);
            int second = SoftwareVisualRasteriser.Signature(root);
            Assert.NotEqual(first, second);
        }

        [Fact]
        public void ProgressDots_StayInsideTheBar()
        {
            for (long t = 0; t < 2200; t += 50)
            {
                foreach (double x in WpChrome.ProgressDots(300, t))
                    Assert.InRange(x, -WpChrome.ProgressDotDiameter, 300);
            }
        }

        [Fact]
        public void ToggleSwitch_OnFillsTheTrackWithAccent_OffLeavesItHollow()
        {
            var toggle = new Microsoft.Phone.Controls.ToggleSwitch { IsChecked = true, Width = 300, Height = 60 };
            var root = new Grid();
            root.Children.Add(toggle);

            uint[] on = Paint(root, 300, 60);
            // Track starts at y=4 (no header); a point on the left of the track, outside the
            // thumb (which sits right when on).
            var trackOn = At(on, 300, 6, 20);
            Assert.Equal(WpChrome.DefaultAccent.B, trackOn.B);

            int sigOn = SoftwareVisualRasteriser.Signature(root);
            toggle.IsChecked = false;
            Assert.NotEqual(sigOn, SoftwareVisualRasteriser.Signature(root));

            uint[] off = Paint(root, 300, 60);
            // Off: thumb on the left, so the right of the track is hollow.
            Assert.Equal((byte)0, At(off, 300, 80, 20).A);
            Assert.Equal((byte)255, At(off, 300, 80, 4).A);      // but the outline is there
        }

        [Fact]
        public void Panorama_ArrangesTheCurrentItemAtTheLeft_AndRepaintsWhenItChanges()
        {
            var pano = new Microsoft.Phone.Controls.Panorama { Background = Solid(10, 20, 30) };
            var a = new Microsoft.Phone.Controls.PanoramaItem { Content = new Grid { Background = Solid(255, 0, 0) } };
            var b = new Microsoft.Phone.Controls.PanoramaItem { Content = new Grid { Background = Solid(0, 255, 0) } };
            pano.Children.Add(a);
            pano.Children.Add(b);

            uint[] first = Paint(pano, 480, 800);
            Assert.Equal(0.0, a.ArrangedRect.X);
            Assert.True(b.ArrangedRect.X >= 480, $"second item at {b.ArrangedRect.X}");
            Assert.Equal(255, At(first, 480, 240, 400).R);

            int sig = SoftwareVisualRasteriser.Signature(pano);
            PanoramaStateTable.GetOrCreate(pano).CurrentIndex = 1;
            Assert.NotEqual(sig, SoftwareVisualRasteriser.Signature(pano));

            uint[] second = Paint(pano, 480, 800);
            Assert.Equal(0.0, b.ArrangedRect.X);
            var mid = At(second, 480, 240, 400);
            Assert.Equal(0, mid.R);
            Assert.Equal(255, mid.G);
        }

        [Fact]
        public void Panorama_DragOffset_ShiftsTheItems()
        {
            var pano = new Microsoft.Phone.Controls.Panorama { Background = Solid(10, 20, 30) };
            var a = new Microsoft.Phone.Controls.PanoramaItem { Content = new Grid() };
            pano.Children.Add(a);
            Paint(pano, 480, 800);

            PanoramaStateTable.GetOrCreate(pano).DragOffset = 100;
            Paint(pano, 480, 800);
            Assert.Equal(100.0, a.ArrangedRect.X);
        }

        [Fact]
        public void RoundedBorder_LeavesItsCornersTransparent()
        {
            var border = new Border
            {
                Width = 100, Height = 100,
                Background = Solid(255, 255, 255),
                CornerRadius = new CornerRadius(30),
            };
            var root = new Grid();
            root.Children.Add(border);

            uint[] px = Paint(root, 100, 100);
            Assert.Equal((byte)0, At(px, 100, 0, 0).A);
            Assert.Equal((byte)0, At(px, 100, 99, 99).A);
            Assert.Equal((byte)255, At(px, 100, 50, 50).A);
            Assert.Equal((byte)255, At(px, 100, 50, 0).A);       // straight edge between corners
            // The arc is anti-aliased: somewhere along the diagonal there is partial coverage.
            bool partial = false;
            for (int i = 0; i < 30; i++)
            {
                byte aa = At(px, 100, i, i).A;
                if (aa > 0 && aa < 255) partial = true;
            }
            Assert.True(partial);
        }

        [Fact]
        public void TextBlock_InheritsAnAncestorsForeground()
        {
            if (!TextRasteriser.IsAvailable) return;   // no system font on this machine

            var text = new TextBlock { Text = "MMMM", FontSize = 40 };
            var root = new Grid { Foreground = Solid(255, 0, 0) };
            root.Children.Add(text);

            uint[] px = Paint(root, 200, 80);
            bool sawRed = false, sawWhite = false;
            for (int i = 0; i < px.Length; i++)
            {
                uint p = px[i];
                if ((p >> 24) < 200) continue;
                if ((p & 0xFF) > 200 && ((p >> 8) & 0xFF) < 40) sawRed = true;
                if ((p & 0xFF) > 200 && ((p >> 8) & 0xFF) > 200) sawWhite = true;
            }
            Assert.True(sawRed);
            Assert.False(sawWhite);
        }
    }
}
