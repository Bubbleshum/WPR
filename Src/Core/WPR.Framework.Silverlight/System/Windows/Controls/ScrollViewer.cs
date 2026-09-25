using System;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.Controls.ScrollViewer</c>: a single-content host that measures
    /// its content unbounded on the scrolling axis and shows a window onto it.
    /// </summary>
    /// <remarks>
    /// <para><b>It really scrolls now.</b> This used to lay its content out at full size with no
    /// viewport, no clip and no input, on the reasoning that the UI these games put in a
    /// ScrollViewer fits a phone screen anyway. That is true of a settings page and false of every
    /// list — and a list that renders its whole extent does not merely fail to scroll, it draws
    /// over whatever is beneath it.</para>
    ///
    /// <para><b>The offset lives here; the clipping and the drag do not.</b> Rendering it is the
    /// rasteriser's job (<c>SoftwareVisualRasteriser</c> offsets and clips the content), hit
    /// testing it is <c>HitTester</c>'s, and turning a finger into an offset is
    /// <c>SilverlightTouchRouter</c>'s. This type owns the number, its bounds and its inertia, so
    /// those three agree by construction rather than by three separate implementations.</para>
    ///
    /// <para>Horizontal scrolling is measured and clamped identically, but WP7 lists are vertical
    /// almost without exception, and the renderer only offsets on both axes because doing one was
    /// no simpler.</para>
    /// </remarks>
    [ContentProperty(nameof(Content))]
    public class ScrollViewer : ContentControl
    {
        /// <summary>Velocity below which a flick is considered finished, in pixels/second.</summary>
        private const double MinimumFlickVelocity = 12.0;

        /// <summary>Fraction of velocity retained per second while coasting.</summary>
        /// <remarks>
        /// Chosen so a hard flick travels a few hundred pixels and settles in well under a second,
        /// which is how a WP7 list behaves. It is a feel constant, not a measurement.
        /// </remarks>
        private const double FlickDecayPerSecond = 0.02;

        private double _velocityX;
        private double _velocityY;

        public static readonly DependencyProperty HorizontalScrollBarVisibilityProperty =
            DependencyProperty.Register(nameof(HorizontalScrollBarVisibility), typeof(ScrollBarVisibility),
                typeof(ScrollViewer), new PropertyMetadata(ScrollBarVisibility.Disabled));

        public static readonly DependencyProperty VerticalScrollBarVisibilityProperty =
            DependencyProperty.Register(nameof(VerticalScrollBarVisibility), typeof(ScrollBarVisibility),
                typeof(ScrollViewer), new PropertyMetadata(ScrollBarVisibility.Visible));

        public ScrollBarVisibility HorizontalScrollBarVisibility
        {
            get => (ScrollBarVisibility)GetValue(HorizontalScrollBarVisibilityProperty)!;
            set => SetValue(HorizontalScrollBarVisibilityProperty, value);
        }

        public ScrollBarVisibility VerticalScrollBarVisibility
        {
            get => (ScrollBarVisibility)GetValue(VerticalScrollBarVisibilityProperty)!;
            set => SetValue(VerticalScrollBarVisibilityProperty, value);
        }

        public double HorizontalOffset { get; private set; }
        public double VerticalOffset { get; private set; }
        public double ViewportWidth { get; private set; }
        public double ViewportHeight { get; private set; }
        public double ExtentWidth { get; private set; }
        public double ExtentHeight { get; private set; }

        public double ScrollableWidth => Math.Max(0, ExtentWidth - ViewportWidth);
        public double ScrollableHeight => Math.Max(0, ExtentHeight - ViewportHeight);

        /// <summary>True while a flick is still coasting.</summary>
        internal bool IsCoasting => Math.Abs(_velocityX) > MinimumFlickVelocity
                                    || Math.Abs(_velocityY) > MinimumFlickVelocity;

        public void ScrollToHorizontalOffset(double offset) => SetOffset(offset, VerticalOffset);

        public void ScrollToVerticalOffset(double offset) => SetOffset(HorizontalOffset, offset);

        /// <summary>Moves the viewport by a delta, clamped to the scrollable range.</summary>
        internal void ScrollBy(double deltaX, double deltaY)
            => SetOffset(HorizontalOffset + deltaX, VerticalOffset + deltaY);

        /// <summary>
        /// Clamps and stores the offset.
        /// </summary>
        /// <remarks>
        /// <b>Clamped, unlike the old setters</b>, which stored whatever a game asked for. An
        /// unclamped offset scrolls a list off its own end and leaves blank space that looks like
        /// missing content; it also makes a drag feel broken, because the finger keeps moving and
        /// nothing comes back.
        /// </remarks>
        private void SetOffset(double x, double y)
        {
            HorizontalOffset = Clamp(x, 0, ScrollableWidth);
            VerticalOffset = Clamp(y, 0, ScrollableHeight);
        }

        /// <summary>Starts a coast after a flick, in pixels/second of CONTENT movement.</summary>
        internal void BeginFlick(double velocityX, double velocityY)
        {
            _velocityX = velocityX;
            _velocityY = velocityY;
        }

        /// <summary>Stops any coast — a new touch takes control immediately.</summary>
        internal void StopFlick()
        {
            _velocityX = 0;
            _velocityY = 0;
        }

        /// <summary>
        /// Advances a coasting flick. Returns true while it is still moving.
        /// </summary>
        internal bool TickInertia(double seconds)
        {
            if (seconds <= 0 || !IsCoasting) return false;

            ScrollBy(_velocityX * seconds, _velocityY * seconds);

            double decay = Math.Pow(FlickDecayPerSecond, seconds);
            _velocityX *= decay;
            _velocityY *= decay;

            // Running into either end stops the coast rather than letting it grind there for the
            // rest of its decay.
            if (VerticalOffset <= 0 || VerticalOffset >= ScrollableHeight) _velocityY = 0;
            if (HorizontalOffset <= 0 || HorizontalOffset >= ScrollableWidth) _velocityX = 0;

            return IsCoasting;
        }

        /// <summary>
        /// Measures the content with the scrolling axes unbounded, so the extent is the content's
        /// natural size rather than the slot's.
        /// </summary>
        protected override Size MeasureOverride(Size availableSize)
        {
            UIElement? content = Presenter;

            double contentWidth = 0;
            double contentHeight = 0;

            if (content != null)
            {
                // Unbounded on an axis that scrolls; the slot's extent on one that does not, so
                // content still wraps to the viewport width in the usual vertical-only case.
                var childAvailable = new Size(
                    CanScrollHorizontally ? double.PositiveInfinity : availableSize.Width,
                    CanScrollVertically ? double.PositiveInfinity : availableSize.Height);

                content.Measure(childAvailable);
                contentWidth = content.DesiredSize.Width;
                contentHeight = content.DesiredSize.Height;
            }

            ExtentWidth = contentWidth;
            ExtentHeight = contentHeight;

            // The viewport is the slot, except where the parent gave us no bound — then there is
            // nothing to scroll within and the viewport is the whole content.
            ViewportWidth = double.IsInfinity(availableSize.Width) ? contentWidth : availableSize.Width;
            ViewportHeight = double.IsInfinity(availableSize.Height) ? contentHeight : availableSize.Height;

            // An offset that was valid before a re-measure may not be now.
            SetOffset(HorizontalOffset, VerticalOffset);

            return new Size(
                Math.Min(contentWidth, ViewportWidth),
                Math.Min(contentHeight, ViewportHeight));
        }

        /// <summary>
        /// Arranges the content at its FULL extent, not the viewport — the renderer and the hit
        /// tester show a window onto it by applying <see cref="VerticalOffset"/>.
        /// </summary>
        protected override Size ArrangeOverride(Size finalSize)
        {
            ViewportWidth = finalSize.Width;
            ViewportHeight = finalSize.Height;

            UIElement? content = Presenter;
            if (content != null)
            {
                content.Arrange(new Rect(
                    0,
                    0,
                    Math.Max(finalSize.Width, ExtentWidth),
                    Math.Max(finalSize.Height, ExtentHeight)));
            }

            SetOffset(HorizontalOffset, VerticalOffset);
            return finalSize;
        }

        private bool CanScrollVertically => VerticalScrollBarVisibility != ScrollBarVisibility.Disabled;

        private bool CanScrollHorizontally => HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled;

        private static double Clamp(double value, double min, double max)
        {
            if (max < min) return min;
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
