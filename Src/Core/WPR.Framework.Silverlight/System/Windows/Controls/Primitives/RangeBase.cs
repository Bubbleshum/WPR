namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.Controls.Primitives.RangeBase</c> — the base Silverlight gives
    /// <see cref="ProgressBar"/>, <c>Slider</c> and <c>ScrollBar</c>.
    /// </summary>
    /// <remarks>
    /// It exists as its own type rather than having <see cref="ProgressBar"/> declare these four
    /// properties itself, because a game's IL names the type that <i>declares</i> the member:
    /// <c>progressBar.Value = 50</c> compiles to <c>callvirt RangeBase::set_Value</c>, and a
    /// hierarchy that puts the setter on the wrong class does not resolve at JIT time.
    /// </remarks>
    public class RangeBase : Control
    {
        public static readonly DependencyProperty MinimumProperty =
            DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(RangeBase),
                new PropertyMetadata(0.0));

        public static readonly DependencyProperty MaximumProperty =
            DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(RangeBase),
                new PropertyMetadata(1.0));

        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register(nameof(Value), typeof(double), typeof(RangeBase),
                new PropertyMetadata(0.0, OnValueChanged));

        public static readonly DependencyProperty SmallChangeProperty =
            DependencyProperty.Register(nameof(SmallChange), typeof(double), typeof(RangeBase),
                new PropertyMetadata(0.1));

        public static readonly DependencyProperty LargeChangeProperty =
            DependencyProperty.Register(nameof(LargeChange), typeof(double), typeof(RangeBase),
                new PropertyMetadata(1.0));

        public double Minimum
        {
            get => (double)GetValue(MinimumProperty)!;
            set => SetValue(MinimumProperty, value);
        }

        public double Maximum
        {
            get => (double)GetValue(MaximumProperty)!;
            set => SetValue(MaximumProperty, value);
        }

        public double Value
        {
            get => (double)GetValue(ValueProperty)!;
            set => SetValue(ValueProperty, value);
        }

        public double SmallChange
        {
            get => (double)GetValue(SmallChangeProperty)!;
            set => SetValue(SmallChangeProperty, value);
        }

        public double LargeChange
        {
            get => (double)GetValue(LargeChangeProperty)!;
            set => SetValue(LargeChangeProperty, value);
        }

        public event RoutedPropertyChangedEventHandler<double>? ValueChanged;

        protected virtual void OnValueChanged(double oldValue, double newValue)
            => ValueChanged?.Invoke(this, new RoutedPropertyChangedEventArgs<double>(oldValue, newValue));

        private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is RangeBase rb)
                rb.OnValueChanged((double)(e.OldValue ?? 0.0), (double)(e.NewValue ?? 0.0));
        }
    }
}
