namespace WPR.SilverlightCompability
{
    public class SolidColorBrush : Brush
    {
        public static readonly DependencyProperty ColorProperty =
            DependencyProperty.Register(nameof(Color), typeof(Color), typeof(SolidColorBrush),
                new PropertyMetadata(default(Color)));

        public Color Color
        {
            get => (Color)GetValue(ColorProperty)!;
            set => SetValue(ColorProperty, value);
        }

        public SolidColorBrush() { }

        public SolidColorBrush(Color color)
        {
            Color = color;
        }
    }
}
