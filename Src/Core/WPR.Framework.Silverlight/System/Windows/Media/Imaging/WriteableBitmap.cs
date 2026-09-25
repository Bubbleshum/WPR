// System.Windows.Media.Imaging "imitation"

using System;
// UIElement and Transform live in the sibling namespace; Render's signature has to name them
// because that is what the patcher rescopes a game's System.Windows.* typerefs to.
using WPR.SilverlightCompability;

namespace WPR.WindowsCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.Media.Imaging.WriteableBitmap</c>. In Silverlight this derives
    /// from BitmapSource (-&gt; ImageSource); we mirror the chain so <c>Image.Source</c> accepts a
    /// WriteableBitmap.
    ///
    /// <para><b><see cref="get_Pixels"/> returns the SAME array every call.</b> That is the whole
    /// point of the type — Silverlight's <c>Pixels</c> is a live buffer a game writes through, and
    /// the usual idiom is <c>bmp.Pixels[i] = argb;</c> followed by <c>bmp.Invalidate()</c>. This
    /// used to allocate and return a fresh array on every access, so every write went straight to
    /// the garbage collector. It was also the wrong length — it allocated <c>height</c> ints rather
    /// than <c>width * height</c> — which is what actually crashed games, because they size their
    /// own buffers from <c>Pixels.Length</c> and then index them with <c>width * y + x</c>.
    /// See the note on <see cref="BitmapSource"/> for the failure this produced in Feed Me Oil.</para>
    /// </summary>
    public class WriteableBitmap : BitmapSource
    {
        public WriteableBitmap(Int32 pixelWidth, Int32 pixelHeight)
        {
            _pixelWidth = pixelWidth > 0 ? pixelWidth : 0;
            _pixelHeight = pixelHeight > 0 ? pixelHeight : 0;
            EnsureBuffer();
        }

        /// <summary>
        /// Copy-construct from an existing bitmap. Silverlight used this to snapshot a
        /// BitmapSource into a writable surface; Kinectimals' <c>MediaUtils.MediaImage.CreateBitmap</c>
        /// takes this path, and without the overload it is a MissingMethodException.
        /// </summary>
        public WriteableBitmap(BitmapSource source)
            : this(source?.get_PixelWidth() ?? 0, source?.get_PixelHeight() ?? 0)
        {
            // Copy the source's content when it has some, so the snapshot is a snapshot.
            int[]? src = source?.PixelBuffer;
            if (src != null && _pixels != null && src.Length == _pixels.Length)
            {
                Array.Copy(src, _pixels, _pixels.Length);
            }
        }

        /// <summary>
        /// Pushes pending writes to the screen. WPR draws from <c>Pixels</c> on demand, so there is
        /// nothing to flush — but games call it after every edit, so it must exist and be cheap.
        /// </summary>
        public void Invalidate()
        {
            return;
        }

        /// <summary>The live ARGB (0xAARRGGBB) buffer — see the note on the type.</summary>
        public Int32[] get_Pixels()
        {
            EnsureBuffer();
            return _pixels ?? Array.Empty<Int32>();
        }

        /// <summary>
        /// Rasterises a UIElement into this bitmap. <b>A no-op here, deliberately.</b>
        /// </summary>
        /// <remarks>
        /// Silverlight's <c>Render</c> walks a live visual tree through the compositor. WPR has
        /// no such rasteriser on any path a game reaches — and on the mixed-mode path there is
        /// nothing to rasterise in the first place: those titles render entirely with XNA and
        /// their visual tree holds a MediaElement and nothing else.
        ///
        /// <para>Leaving the buffer untouched is the honest outcome, and it is what Silverlight
        /// itself produced for an element that was not in the tree. A game that renders and then
        /// reads gets the transparent bitmap it started with rather than a wrong picture. Cut the
        /// Rope calls this and needs it only to resolve.</para>
        /// </remarks>
        public void Render(UIElement element, Transform transform)
        {
        }

    }
}
