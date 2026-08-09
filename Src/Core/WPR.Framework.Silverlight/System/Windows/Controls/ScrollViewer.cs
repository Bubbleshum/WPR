using System;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Stub for <c>System.Windows.Controls.ScrollViewer</c>. Single-content host with optional
    /// scrolling. WPR currently doesn't implement viewport clipping or scroll input — the
    /// content is just laid out at full size. Most Silverlight games we target use ScrollViewer
    /// for incidental UI (settings, leaderboards) which fit on a phone screen anyway.
    /// </summary>
    [ContentProperty(nameof(Content))]
    public class ScrollViewer : ContentControl
    {
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
        public double ScrollableWidth { get; private set; }
        public double ScrollableHeight { get; private set; }

        public void ScrollToHorizontalOffset(double offset) { HorizontalOffset = offset; }
        public void ScrollToVerticalOffset(double offset) { VerticalOffset = offset; }
    }
}
