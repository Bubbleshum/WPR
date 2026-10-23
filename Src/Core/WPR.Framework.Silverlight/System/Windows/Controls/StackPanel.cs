using System;

namespace WPR.SilverlightCompability
{
    public class StackPanel : Panel
    {
        public static readonly DependencyProperty OrientationProperty =
            DependencyProperty.Register(nameof(Orientation), typeof(Orientation), typeof(StackPanel),
                new PropertyMetadata(Orientation.Vertical, OnOrientationChanged));

        public Orientation Orientation
        {
            get => (Orientation)GetValue(OrientationProperty)!;
            set => SetValue(OrientationProperty, value);
        }

        private static void OnOrientationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is StackPanel sp) sp.InvalidateMeasure();
        }

        internal override Size WprMeasure(Size availableSize) => MeasureStack(Children, Orientation, availableSize);

        internal override Size WprArrange(Size finalSize) => ArrangeStack(Children, Orientation, finalSize);

        // Shared with VirtualizingStackPanel, which is not a StackPanel in Silverlight's hierarchy.
        internal static Size MeasureStack(UIElementCollection children, Orientation orientation, Size availableSize)
        {
            bool vertical = orientation == Orientation.Vertical;

            // Children get unbounded extent along the stacking axis, the slot's extent on the cross axis.
            Size childAvail = vertical
                ? new Size(availableSize.Width, double.PositiveInfinity)
                : new Size(double.PositiveInfinity, availableSize.Height);

            double main = 0;
            double cross = 0;

            foreach (UIElement child in children)
            {
                child.Measure(childAvail);
                Size d = child.DesiredSize;
                if (vertical)
                {
                    main += d.Height;
                    if (d.Width > cross) cross = d.Width;
                }
                else
                {
                    main += d.Width;
                    if (d.Height > cross) cross = d.Height;
                }
            }

            return vertical ? new Size(cross, main) : new Size(main, cross);
        }

        internal static Size ArrangeStack(UIElementCollection children, Orientation orientation, Size finalSize)
        {
            bool vertical = orientation == Orientation.Vertical;
            double offset = 0;

            foreach (UIElement child in children)
            {
                Size d = child.DesiredSize;
                Rect slot = vertical
                    ? new Rect(0, offset, finalSize.Width, d.Height)
                    : new Rect(offset, 0, d.Width, finalSize.Height);
                child.Arrange(slot);
                offset += vertical ? d.Height : d.Width;
            }

            return finalSize;
        }
    }
}
