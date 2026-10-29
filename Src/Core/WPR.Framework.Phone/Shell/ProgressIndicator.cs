using WPR.SilverlightCompability;

namespace Microsoft.Phone.Shell
{
    /// <summary>
    /// Shim for <c>Microsoft.Phone.Shell.ProgressIndicator</c>: the loading bar and text a WP7.5+
    /// page shows in the status bar (<c>SystemTray.ProgressIndicator</c>). WPR draws no status
    /// bar, so it is stored and not drawn; a page that binds it to "loading" still constructs.
    /// </summary>
    public class ProgressIndicator : DependencyObject
    {
        public static readonly DependencyProperty IsIndeterminateProperty =
            DependencyProperty.Register(nameof(IsIndeterminate), typeof(bool), typeof(ProgressIndicator), new PropertyMetadata(false));

        public static readonly DependencyProperty IsVisibleProperty =
            DependencyProperty.Register(nameof(IsVisible), typeof(bool), typeof(ProgressIndicator), new PropertyMetadata(false));

        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(nameof(Text), typeof(string), typeof(ProgressIndicator), new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register(nameof(Value), typeof(double), typeof(ProgressIndicator), new PropertyMetadata(0.0));

        public bool IsIndeterminate
        {
            get => (bool)(GetValue(IsIndeterminateProperty) ?? false);
            set => SetValue(IsIndeterminateProperty, value);
        }

        public bool IsVisible
        {
            get => (bool)(GetValue(IsVisibleProperty) ?? false);
            set => SetValue(IsVisibleProperty, value);
        }

        public string Text
        {
            get => (string?)GetValue(TextProperty) ?? string.Empty;
            set => SetValue(TextProperty, value);
        }

        public double Value
        {
            get => (double)(GetValue(ValueProperty) ?? 0.0);
            set => SetValue(ValueProperty, value);
        }
    }
}
