using System;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Base for layout containers. Holds an ordered <see cref="UIElementCollection"/>
    /// of children. Concrete subclasses (StackPanel, Grid, Canvas) provide layout.
    /// The default Measure stacks children at their natural size; default Arrange
    /// places each child at (0,0) sized to its DesiredSize — useful only for
    /// single-child cases. Real layout requires a subclass.
    /// </summary>
    [ContentProperty(nameof(Children))]
    public class Panel : FrameworkElement
    {
        // Alias of the canonical FrameworkElement.BackgroundProperty so user
        // IL that loads this static field (ldsfld Panel::BackgroundProperty)
        // still resolves, but DP-storage is shared with every other place
        // that reads/writes Background — see FrameworkElement for the rationale.
        public static readonly DependencyProperty BackgroundProperty = FrameworkElement.BackgroundProperty;

        // The CLR `Background` property is inherited from FrameworkElement —
        // no shadow declaration here; readers and writers all use the same DP.

        public UIElementCollection Children { get; }

        public Panel()
        {
            Children = new UIElementCollection(this, InvalidateMeasure);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double maxW = 0, maxH = 0;
            foreach (UIElement child in Children)
            {
                child.Measure(availableSize);
                Size d = child.DesiredSize;
                if (d.Width > maxW) maxW = d.Width;
                if (d.Height > maxH) maxH = d.Height;
            }
            return new Size(maxW, maxH);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            foreach (UIElement child in Children)
            {
                child.Arrange(new Rect(0, 0, child.DesiredSize.Width, child.DesiredSize.Height));
            }
            return finalSize;
        }
    }
}
