namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.Media.ImageSource</c>. In Silverlight this is the
    /// abstract base for <c>BitmapSource</c> / <c>BitmapImage</c> / <c>WriteableBitmap</c>.
    /// We make it concrete so the XAML loader can wrap a raw string path (e.g.
    /// <c>Source="foo.png"</c>) without an explicit subclass, and so user assemblies
    /// that hold the type by reference but never actually instantiate it still load.
    ///
    /// <para><see cref="SilverlightImageDecoder"/> resolves <see cref="Path"/> lazily: as a file,
    /// under the install folder, or as an entry in the app's resource bundle.</para>
    ///
    /// <para>The WindowsCompability shims <c>BitmapSource</c>, <c>BitmapImage</c>,
    /// <c>WriteableBitmap</c> derive from this type so that
    /// <c>image.Source = new BitmapImage(...)</c> remains assignable.</para>
    /// </summary>
    public class ImageSource
    {
        internal string? Path;

        public ImageSource() { }

        public ImageSource(string path) { Path = path; }
    }
}
