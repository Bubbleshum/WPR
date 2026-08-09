namespace WPR.SilverlightCompability
{
    public abstract class Brush : DependencyObject
    {
        public static readonly DependencyProperty OpacityProperty =
            DependencyProperty.Register(nameof(Opacity), typeof(double), typeof(Brush),
                new PropertyMetadata(1.0));

        public double Opacity
        {
            get => (double)GetValue(OpacityProperty)!;
            set => SetValue(OpacityProperty, value);
        }
    }
}
