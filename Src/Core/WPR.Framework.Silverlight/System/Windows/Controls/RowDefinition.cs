namespace WPR.SilverlightCompability
{
    public class RowDefinition : DependencyObject
    {
        public static readonly DependencyProperty HeightProperty =
            DependencyProperty.Register(nameof(Height), typeof(GridLength), typeof(RowDefinition),
                new PropertyMetadata(new GridLength(1, GridUnitType.Star)));

        public static readonly DependencyProperty MinHeightProperty =
            DependencyProperty.Register(nameof(MinHeight), typeof(double), typeof(RowDefinition),
                new PropertyMetadata(0.0));

        public static readonly DependencyProperty MaxHeightProperty =
            DependencyProperty.Register(nameof(MaxHeight), typeof(double), typeof(RowDefinition),
                new PropertyMetadata(double.PositiveInfinity));

        public GridLength Height
        {
            get => (GridLength)GetValue(HeightProperty)!;
            set => SetValue(HeightProperty, value);
        }

        public double MinHeight
        {
            get => (double)GetValue(MinHeightProperty)!;
            set => SetValue(MinHeightProperty, value);
        }

        public double MaxHeight
        {
            get => (double)GetValue(MaxHeightProperty)!;
            set => SetValue(MaxHeightProperty, value);
        }

        public double ActualHeight { get; internal set; }
    }
}
