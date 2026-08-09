namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Absolute-positioning panel. Children are placed at their attached
    /// <see cref="LeftProperty"/> / <see cref="TopProperty"/> coordinates with their natural size.
    /// Canvas does not constrain children — they get unbounded available size during measure.
    /// </summary>
    public class Canvas : Panel
    {
        public static readonly DependencyProperty LeftProperty = DependencyProperty.RegisterAttached(
            "Left", typeof(double), typeof(Canvas), new PropertyMetadata(0.0));

        public static readonly DependencyProperty TopProperty = DependencyProperty.RegisterAttached(
            "Top", typeof(double), typeof(Canvas), new PropertyMetadata(0.0));

        public static readonly DependencyProperty ZIndexProperty = DependencyProperty.RegisterAttached(
            "ZIndex", typeof(int), typeof(Canvas), new PropertyMetadata(0));

        public static double GetLeft(DependencyObject element) => (double)element.GetValue(LeftProperty)!;
        public static void SetLeft(DependencyObject element, double value) => element.SetValue(LeftProperty, value);

        public static double GetTop(DependencyObject element) => (double)element.GetValue(TopProperty)!;
        public static void SetTop(DependencyObject element, double value) => element.SetValue(TopProperty, value);

        public static int GetZIndex(DependencyObject element) => (int)element.GetValue(ZIndexProperty)!;
        public static void SetZIndex(DependencyObject element, int value) => element.SetValue(ZIndexProperty, value);

        protected override Size MeasureOverride(Size availableSize)
        {
            // Canvas does not constrain or size to its children.
            var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
            foreach (UIElement child in Children)
                child.Measure(unbounded);
            return Size.Empty;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            foreach (UIElement child in Children)
            {
                double x = GetLeft(child);
                double y = GetTop(child);
                child.Arrange(new Rect(x, y, child.DesiredSize.Width, child.DesiredSize.Height));
            }
            return finalSize;
        }
    }
}
