using WPR.SilverlightCompability;

namespace Microsoft.Phone.Controls
{
    /// <summary>
    /// Shim for <c>Microsoft.Phone.Controls.PivotItem</c> (in Microsoft.Phone on WP8). The
    /// renderer reads <see cref="Header"/> by name.
    /// </summary>
    public class PivotItem : ContentControl
    {
        public static readonly DependencyProperty HeaderProperty =
            DependencyProperty.Register(nameof(Header), typeof(object), typeof(PivotItem),
                new PropertyMetadata((object?)null));

        public object? Header
        {
            get => GetValue(HeaderProperty);
            set => SetValue(HeaderProperty, value);
        }
    }
}
