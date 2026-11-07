namespace System.Windows.Media
{
    /// <summary>
    /// Shim for <c>System.Windows.Media.VideoBrush</c>: paints with the video of a
    /// <see cref="System.Windows.Controls.MediaElement"/> or a camera preview.
    /// </summary>
    /// <remarks>
    /// Kept, not drawn: the rasteriser paints solid, gradient and image brushes, and reports any
    /// other brush as unsupported. Marmalade sets one as a canvas background for a camera preview
    /// WPR never has (see <c>Microsoft.Devices.Camera</c>), so nothing visible is lost there.
    /// </remarks>
    public sealed class VideoBrush : TileBrush
    {
        /// <summary>The element whose video this brush shows, if one was set.</summary>
        public System.Windows.Controls.MediaElement? Source { get; private set; }

        public string? SourceName { get; set; }

        public void SetSource(System.Windows.Controls.MediaElement source) => Source = source;
    }
}
