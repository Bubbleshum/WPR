using WPR.SilverlightCompability;

namespace Microsoft.Phone.Controls
{
    /// <summary>
    /// Shim for <c>Microsoft.Phone.Controls.PanoramaItem</c> (in Microsoft.Phone on WP8). The
    /// renderer reads <see cref="Header"/> by name.
    /// </summary>
    public class PanoramaItem : ContentControl
    {
        public static readonly DependencyProperty HeaderProperty =
            DependencyProperty.Register(nameof(Header), typeof(object), typeof(PanoramaItem),
                new PropertyMetadata((object?)null));

        public static readonly DependencyProperty HeaderTemplateProperty =
            DependencyProperty.Register(nameof(HeaderTemplate), typeof(DataTemplate), typeof(PanoramaItem),
                new PropertyMetadata((object?)null));

        public static readonly DependencyProperty OrientationProperty =
            DependencyProperty.Register(nameof(Orientation), typeof(Orientation), typeof(PanoramaItem),
                new PropertyMetadata((object)Orientation.Vertical));

        public object? Header
        {
            get => GetValue(HeaderProperty);
            set => SetValue(HeaderProperty, value);
        }

        public DataTemplate? HeaderTemplate
        {
            get => (DataTemplate?)GetValue(HeaderTemplateProperty);
            set => SetValue(HeaderTemplateProperty, value);
        }

        /// <summary>Vertical items fill one screen; horizontal ones may run wider. Stored only.</summary>
        public Orientation Orientation
        {
            get => (Orientation)GetValue(OrientationProperty)!;
            set => SetValue(OrientationProperty, value);
        }
    }
}
