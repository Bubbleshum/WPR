namespace WPR.SilverlightCompability
{
    public class ColumnDefinition : DependencyObject
    {
        public static readonly DependencyProperty WidthProperty =
            DependencyProperty.Register(nameof(Width), typeof(GridLength), typeof(ColumnDefinition),
                new PropertyMetadata(new GridLength(1, GridUnitType.Star)));

        public static readonly DependencyProperty MinWidthProperty =
            DependencyProperty.Register(nameof(MinWidth), typeof(double), typeof(ColumnDefinition),
                new PropertyMetadata(0.0));

        public static readonly DependencyProperty MaxWidthProperty =
            DependencyProperty.Register(nameof(MaxWidth), typeof(double), typeof(ColumnDefinition),
                new PropertyMetadata(double.PositiveInfinity));

        public GridLength Width
        {
            get => (GridLength)GetValue(WidthProperty)!;
            set => SetValue(WidthProperty, value);
        }

        public double MinWidth
        {
            get => (double)GetValue(MinWidthProperty)!;
            set => SetValue(MinWidthProperty, value);
        }

        public double MaxWidth
        {
            get => (double)GetValue(MaxWidthProperty)!;
            set => SetValue(MaxWidthProperty, value);
        }

        public double ActualWidth { get; internal set; }
    }
}
