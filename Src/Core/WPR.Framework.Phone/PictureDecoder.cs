using System;
using System.IO;
using WPR.WindowsCompability;

namespace Microsoft.Phone
{
    /// <summary>
    /// Shim for <c>Microsoft.Phone.PictureDecoder</c> — WP7's stream-to-bitmap decoder.
    /// </summary>
    /// <remarks>
    /// Delegates to <see cref="BitmapSource.SetSource"/>, which decodes through
    /// <c>IGraphicsBackend.ReadImageStream</c> (FNA3D's stb_image). That matters for more than
    /// tidiness: stb_image sniffs the container, so despite the API's name these overloads decode
    /// PNG as happily as JPEG — which is what WP7's own decoder did, and what titles rely on.
    /// Cut the Rope loads every one of its non-XNB textures through <c>DecodeJpeg</c> from
    /// <c>Texture2D.initWithPath</c>, and they are PNGs.
    ///
    /// <para>Missing entirely until 2026-09-21, and fatal rather than degraded: the type is
    /// resolved when <c>initWithPath</c> is JIT-compiled, so the TypeLoadException escaped through
    /// the page's constructor and killed the navigation to it — the game never reached a frame.</para>
    /// </remarks>
    public static class PictureDecoder
    {
        /// <summary>
        /// Decodes an image stream at its natural size.
        /// </summary>
        public static WriteableBitmap DecodeJpeg(Stream source)
        {
            var bitmap = new WriteableBitmap(0, 0);
            if (source != null)
            {
                // SetSource swallows and reports a decode failure, leaving a blank bitmap, which
                // is the right degradation here too: a title that cannot decode one texture
                // should lose that texture, not the launch.
                bitmap.SetSource(source);
            }
            return bitmap;
        }

        /// <summary>
        /// Decodes an image stream, scaled to fit within the given bounds.
        /// </summary>
        /// <remarks>
        /// The bounds are deliberately ignored and the full-size image returned. WP7 used them to
        /// keep a large camera JPEG out of a 90 MB app's memory budget; WPR has no such budget,
        /// and returning fewer pixels than asked for would silently change the geometry a caller
        /// derives from <c>PixelWidth</c>/<c>PixelHeight</c>. Downscaling honestly would need a
        /// resampler the shim layer does not have.
        /// </remarks>
        public static WriteableBitmap DecodeJpeg(Stream source, int maxPixelWidth, int maxPixelHeight)
        {
            return DecodeJpeg(source);
        }
    }
}
