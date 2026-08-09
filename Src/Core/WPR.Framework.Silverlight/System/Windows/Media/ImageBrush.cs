namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Silverlight's <c>System.Windows.Media.ImageBrush</c> shim. References to this
    /// type are rewritten by <c>WPR.ApplicationPatcher</c> from the WP-era
    /// <c>System.Windows</c> assembly to this shim. Without the shim, <c>XmlSerializer</c>
    /// reflecting over any user type that exposes an <c>ImageBrush</c> property
    /// (e.g. Minesweeper's <c>Powerup</c>) raises <see cref="System.TypeLoadException"/>
    /// when resolving the property getter's return-type signature, because the
    /// WP <c>System.Windows.dll</c>'s <c>TypeForwardedTo</c> for ImageBrush points
    /// at a target that doesn't exist on modern .NET.
    ///
    /// Behaviour: just exists. <see cref="ImageSource"/> stores whatever the user
    /// assigned (typed as <see cref="object"/> rather than a phantom ImageSource
    /// base, because we don't model that hierarchy yet). Render time is a no-op —
    /// our renderer doesn't paint image brushes, but the property lives long
    /// enough for serialization round-trips and CLR property access to succeed.
    /// </summary>
    public class ImageBrush : TileBrush
    {
        // Re-typed from object to ImageSource — user IL emits the strong setter
        // signature `set_ImageSource(ImageSource)` so the DP type and the CLR
        // accessor must both be ImageSource. Anything castable to ImageSource
        // (BitmapImage, BitmapSource, WriteableBitmap) flows through fine.
        public static readonly DependencyProperty ImageSourceProperty =
            DependencyProperty.Register(nameof(ImageSource), typeof(ImageSource), typeof(ImageBrush),
                new PropertyMetadata((object?)null));

        public ImageSource? ImageSource
        {
            get => (ImageSource?)GetValue(ImageSourceProperty);
            set => SetValue(ImageSourceProperty, value);
        }

        public ImageBrush() { }
    }
}
