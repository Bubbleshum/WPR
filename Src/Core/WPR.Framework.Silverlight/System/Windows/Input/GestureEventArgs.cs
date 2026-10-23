namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.Input.GestureEventArgs</c>, the argument of WP 7.1/8's
    /// <see cref="UIElement.Tap"/>, <see cref="UIElement.DoubleTap"/> and <see cref="UIElement.Hold"/>.
    /// Not the toolkit's <c>Microsoft.Phone.Controls.GestureEventArgs</c>, which is a different type.
    /// </summary>
    public class GestureEventArgs : RoutedEventArgs
    {
        /// <summary>Where the finger was, in page coordinates.</summary>
        internal Point PagePosition { get; set; }

        /// <summary>The position relative to <paramref name="relativeTo"/>, or to the page when that is null.</summary>
        public Point GetPosition(UIElement? relativeTo) =>
            new MouseEventArgs { PagePosition = PagePosition }.GetPosition(relativeTo);
    }
}
