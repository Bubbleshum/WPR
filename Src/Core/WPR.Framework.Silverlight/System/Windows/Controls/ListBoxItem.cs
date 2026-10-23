namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Controls.ListBoxItem</c>: the container a list puts
    /// round an item. Draws its content; selection is stored.</summary>
    public class ListBoxItem : ContentControl
    {
        public static readonly DependencyProperty IsSelectedProperty =
            DependencyProperty.Register(nameof(IsSelected), typeof(bool), typeof(ListBoxItem),
                new PropertyMetadata((object)false));

        public bool IsSelected
        {
            get => (bool)GetValue(IsSelectedProperty)!;
            set => SetValue(IsSelectedProperty, value);
        }
    }
}
