namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Controls.Primitives.Selector</c>.
    /// We collapsed this onto our flat ListBox earlier; expose as a separate
    /// type-only alias for IL refs that name <c>Selector</c> directly.</summary>
    public class Selector : ItemsControl
    {
        public static readonly DependencyProperty SelectedIndexProperty =
            DependencyProperty.Register(nameof(SelectedIndex), typeof(int), typeof(Selector),
                new PropertyMetadata((object)(-1)));

        public static readonly DependencyProperty SelectedItemProperty =
            DependencyProperty.Register(nameof(SelectedItem), typeof(object), typeof(Selector),
                new PropertyMetadata((object?)null));

        public int SelectedIndex
        {
            get => (int)GetValue(SelectedIndexProperty)!;
            set => SetValue(SelectedIndexProperty, value);
        }

        public object? SelectedItem
        {
            get => GetValue(SelectedItemProperty);
            set => SetValue(SelectedItemProperty, value);
        }

#pragma warning disable CS0067
        public event SelectionChangedEventHandler? SelectionChanged;
#pragma warning restore CS0067
    }
}
