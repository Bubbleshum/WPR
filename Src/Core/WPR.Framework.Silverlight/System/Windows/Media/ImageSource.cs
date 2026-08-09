using AvBitmap = Avalonia.Media.Imaging.Bitmap;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.Media.ImageSource</c>. In Silverlight this is the
    /// abstract base for <c>BitmapSource</c> / <c>BitmapImage</c> / <c>WriteableBitmap</c>.
    /// We make it concrete so the XAML loader can wrap a raw string path (e.g.
    /// <c>Source="foo.png"</c>) without an explicit subclass, and so user assemblies
    /// that hold the type by reference but never actually instantiate it still load.
    ///
    /// <para>The renderer (see <see cref="Image.GetAvaloniaBitmap"/>) reads either
    /// <see cref="Path"/> (resolved lazily via <c>File.Exists</c>) or
    /// <see cref="NativeBitmap"/> (already-loaded Avalonia bitmap).</para>
    ///
    /// <para>The WindowsCompability shims <c>BitmapSource</c>, <c>BitmapImage</c>,
    /// <c>WriteableBitmap</c> derive from this type so that
    /// <c>image.Source = new BitmapImage(...)</c> remains assignable.</para>
    /// </summary>
    public class ImageSource
    {
        internal string? Path;
        internal AvBitmap? NativeBitmap;

        public ImageSource() { }

        public ImageSource(string path) { Path = path; }

        public ImageSource(AvBitmap bitmap) { NativeBitmap = bitmap; }
    }
}
