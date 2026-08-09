namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Controls.Primitives.ToggleButton</c>.</summary>
    public class ToggleButton : ContentControl
    {
        public static readonly DependencyProperty IsCheckedProperty =
            DependencyProperty.Register(nameof(IsChecked), typeof(bool?), typeof(ToggleButton),
                new PropertyMetadata((object?)false));

        public bool? IsChecked
        {
            get => (bool?)GetValue(IsCheckedProperty);
            set => SetValue(IsCheckedProperty, value);
        }

#pragma warning disable CS0067
        public event RoutedEventHandler? Checked;
        public event RoutedEventHandler? Unchecked;
#pragma warning restore CS0067
    }
}
