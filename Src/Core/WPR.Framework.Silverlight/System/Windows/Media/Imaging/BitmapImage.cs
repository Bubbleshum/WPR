// System.Windows.Media.Imaging "imitation"

using System;
using WPR.SilverlightCompability;


namespace WPR.WindowsCompability
{
    // projection: System.Windows.Media.Imaging.BitmapImage. In Silverlight this
    // derives from BitmapSource which derives from ImageSource — we preserve the
    // chain so Image.Source (ImageSource-typed) accepts a BitmapImage.
    public class BitmapImage : BitmapSource
    {
        /// <summary>
        /// Silverlight's BitmapImage exposes a CreateOptions DP that gates whether
        /// the bitmap should be decoded lazily / off the cache / on a background
        /// thread. User code (Minesweeper's <c>LoadPanorama</c>) sets this before
        /// assigning the image, so the property MUST exist or the Loaded handler
        /// throws <c>MissingMethodException</c> mid-load and downstream cleanup
        /// (the splash-screen popup close) never runs.
        /// Our renderer always decodes synchronously on first paint so the value
        /// has no semantic effect — we just round-trip it.
        /// </summary>
        public BitmapCreateOptions CreateOptions { get; set; } = BitmapCreateOptions.None;

        public BitmapImage()
        {
        }

        /// <summary>
        /// Silverlight's <c>new BitmapImage(uri)</c>. Resolves the URI to a path
        /// the <c>Image</c> shim's lazy <c>File.Exists</c> loader can use:
        ///   - Absolute file URIs → <c>LocalPath</c>.
        ///   - Relative URIs like <c>"/Resources/foo.png"</c> → strip the leading
        ///     slash so it resolves relative to the install dir (= cwd at launch).
        /// Truly remote URIs (http(s)://) are stored verbatim — they won't load
        /// today but a future renderer pass could fetch them.
        /// </summary>
        public BitmapImage(Uri uri) : base(UriToPath(uri))
        {
        }

        private static string UriToPath(Uri? uri)
        {
            if (uri == null) return string.Empty;
            string s = uri.IsAbsoluteUri ? uri.LocalPath : uri.OriginalString;
            if (s.StartsWith("/", StringComparison.Ordinal)) s = s.Substring(1);
            return s;
        }

        //static 
        //BitmapImage(int ActualWidth, int ActualHeight)
        //{
        /*
        writeableBitmap = new WriteableBitmap(
            (int)ActualWidth,
            (int)ActualHeight,
            96,
            96,
            default,//PixelFormats.Bgr32,
            null);
        */
        //    return;
        //}

        
        public void SetSource(System.IO.Stream stream)
        {
            //BitmapImage bit =
            //new BitmapImage(new Uri("/Resources/1.jpg", UriKind.Relative));
            //img.Source = bit;
            //TODO

            //Image image = new Image
            //{
            //    Source = ImageSource.FromFile("forest.png")
            //};

            //Content = image;
            //return;
        }

        public Int32 get_PixelWidth()
        {
            return (Int32)4; //RnD
        }

        public Int32 get_PixelHeight()
        {
            return (Int32)4; //RnD
        }

    }//BitmapSource

   
}

