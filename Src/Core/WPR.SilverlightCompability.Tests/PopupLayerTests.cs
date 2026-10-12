using System;
using Xunit;

namespace WPR.SilverlightCompability.Tests
{
    /// <summary>The open-popup registry is process-wide, so these must not run beside each other.</summary>
    [CollectionDefinition("PopupRegistry", DisableParallelization = true)]
    public sealed class PopupRegistryCollection { }

    /// <summary>
    /// Popups in the CPU rasteriser, the hit tester and the touch router: drawn above the page in
    /// the order they opened, not clipped by their parent, and the first thing a tap reaches.
    /// </summary>
    [Collection("PopupRegistry")]
    public sealed class PopupLayerTests : IDisposable
    {
        private const uint Red = 0xFF0000FF;     // premultiplied RGBA packed as XNA Color: R low
        private const uint Blue = 0xFFFF0000;
        private const uint Green = 0xFF008000;

        private readonly TimeSpan _floor;

        public PopupLayerTests()
        {
            Popup.ResetForNewLaunch();
            _floor = Popup.MinimumDisplayDuration;
            Popup.MinimumDisplayDuration = TimeSpan.Zero;   // closes take effect at once
        }

        public void Dispose()
        {
            Popup.MinimumDisplayDuration = _floor;
            Popup.ResetForNewLaunch();
        }

        private static Border Box(Color c, double size) =>
            new Border { Width = size, Height = size, Background = new SolidColorBrush(c) };

        private static Color C(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);

        private static Grid LaidOut(Grid root, int w = 100, int h = 100)
        {
            root.Measure(new Size(w, h));
            root.Arrange(new Rect(0, 0, w, h));
            return root;
        }

        private static uint[] Paint(UIElement root, int w = 100, int h = 100)
        {
            var px = new uint[w * h];
            SoftwareVisualRasteriser.Paint(root, w, h, px);
            return px;
        }

        private static uint At(uint[] px, int x, int y, int w = 100) => px[(y * w) + x];

        [Fact]
        public void OpenTreePopup_IsPaintedAtItsOffset_AndClosedOneIsNot()
        {
            var popup = new Popup { Child = Box(C(255, 0, 0), 20), HorizontalOffset = 30, VerticalOffset = 40 };
            var root = LaidOut(new Grid { Children = { popup } });

            Assert.Equal(0u, At(Paint(root), 35, 45));

            popup.IsOpen = true;
            uint[] open = Paint(root);
            Assert.Equal(Red, At(open, 35, 45));
            Assert.Equal(0u, At(open, 25, 45));
            Assert.Equal(0u, At(open, 55, 45));

            popup.IsOpen = false;
            Assert.Equal(0u, At(Paint(root), 35, 45));
        }

        [Fact]
        public void Popup_IsNotClippedByItsParent()
        {
            // A 10x10 border at (5,5) holding the popup: Silverlight draws the popup's child in a
            // layer above the page, so it is not clipped to that border.
            var popup = new Popup { Child = Box(C(255, 0, 0), 20), HorizontalOffset = 50, VerticalOffset = 50, IsOpen = true };
            var holder = new Border
            {
                Width = 10, Height = 10,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(5, 5, 0, 0),
                Child = popup,
            };
            var root = LaidOut(new Grid { Children = { holder } });

            uint[] px = Paint(root);
            // The popup's origin is its own slot (inside the border) plus the offsets.
            Assert.Equal(Red, At(px, 60, 60));
        }

        [Fact]
        public void PopupDrawsAboveLaterSiblings()
        {
            // Declared FIRST, so the tree pass would draw the blue sibling over it.
            var popup = new Popup { Child = Box(C(255, 0, 0), 20), IsOpen = true };
            var cover = Box(C(0, 0, 255), 100);
            var root = LaidOut(new Grid { Children = { popup, cover } });

            uint[] px = Paint(root);
            Assert.Equal(Red, At(px, 10, 10));
            Assert.Equal(Blue, At(px, 50, 50));
        }

        [Fact]
        public void DetachedPopups_ArePainted_InOpenOrder()
        {
            var root = LaidOut(new Grid());
            var first = new Popup { Child = Box(C(255, 0, 0), 30), HorizontalOffset = 10, VerticalOffset = 10 };
            var second = new Popup { Child = Box(C(0, 128, 0), 30), HorizontalOffset = 20, VerticalOffset = 20 };

            first.IsOpen = true;
            second.IsOpen = true;
            uint[] px = Paint(root);
            Assert.Equal(Red, At(px, 15, 15));
            Assert.Equal(Green, At(px, 25, 25));   // opened last, on top

            // Reopening the first raises it.
            first.IsOpen = false;
            first.IsOpen = true;
            Assert.Equal(Red, At(Paint(root), 25, 25));
        }

        [Fact]
        public void PopupsAreNotPainted_WhenTheRootIsNotTheApplicationRoot()
        {
            // A game rasterising one of its own pages through UIElementRenderer gets the page,
            // not the popup layer — the overlays it already draws must not change.
            var popup = new Popup { Child = Box(C(255, 0, 0), 20), IsOpen = true };
            var page = new Grid { Children = { popup } };
            var frame = LaidOut(new Grid { Children = { page } });
            Assert.NotNull(page.Parent);

            Assert.Equal(0u, At(Paint(page), 10, 10));
            Assert.Equal(Red, At(Paint(frame), 10, 10));
        }

        [Fact]
        public void Signature_ChangesWhenAPopupOpensOrCloses()
        {
            var root = LaidOut(new Grid());
            int closed = SoftwareVisualRasteriser.Signature(root);

            var popup = new Popup { Child = Box(C(255, 0, 0), 20) };
            Assert.Equal(closed, SoftwareVisualRasteriser.Signature(root));

            popup.IsOpen = true;
            int open = SoftwareVisualRasteriser.Signature(root);
            Assert.NotEqual(closed, open);

            popup.HorizontalOffset = 5;
            Assert.NotEqual(open, SoftwareVisualRasteriser.Signature(root));

            popup.IsOpen = false;
            Assert.Equal(closed, SoftwareVisualRasteriser.Signature(root));
        }

        [Fact]
        public void HitTest_ReachesTheTopmostPopupFirst_AndFallsThroughOutsideIt()
        {
            var under = new Border { Background = new SolidColorBrush(C(0, 0, 255)) };
            var root = LaidOut(new Grid { Children = { under } });

            var lowerButton = new Button { Content = "low", Width = 40, Height = 40 };
            var upperButton = new Button { Content = "up", Width = 40, Height = 40 };
            var lower = new Popup { Child = lowerButton, HorizontalOffset = 10, VerticalOffset = 10 };
            var upper = new Popup { Child = upperButton, HorizontalOffset = 30, VerticalOffset = 30 };
            lower.IsOpen = true;
            upper.IsOpen = true;

            Assert.Contains(upperButton, HitTester.HitTest(root, 35, 35));   // overlap: upper wins
            Assert.Contains(lowerButton, HitTester.HitTest(root, 15, 15));
            Assert.Contains(under, HitTester.HitTest(root, 90, 90));          // outside both
        }

        [Fact]
        public void Tap_OnAPopupButton_ClicksIt_AndNotTheButtonUnderneath()
        {
            var pageButton = new Button { Content = "page" };
            int pageClicks = 0;
            pageButton.Click += (_, _) => pageClicks++;
            var root = LaidOut(new Grid { Children = { pageButton } });

            var ok = new Button { Content = "OK", Width = 40, Height = 20 };
            int okClicks = 0;
            ok.Click += (_, _) => okClicks++;
            var dialog = new Popup { Child = ok, HorizontalOffset = 30, VerticalOffset = 40, IsOpen = true };

            SilverlightTouchRouter.Press(root, 45, 50);
            SilverlightTouchRouter.Release(45, 50);

            Assert.Equal(1, okClicks);
            Assert.Equal(0, pageClicks);

            dialog.IsOpen = false;
            SilverlightTouchRouter.Press(root, 45, 50);
            SilverlightTouchRouter.Release(45, 50);
            Assert.Equal(1, pageClicks);
        }
    }
}
