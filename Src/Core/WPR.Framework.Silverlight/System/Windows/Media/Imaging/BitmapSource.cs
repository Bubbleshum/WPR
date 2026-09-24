// System.Windows.Media.Imaging "imitation"

using System;
using System.Runtime.InteropServices;

using WPR.Xna.Rhi;

using SlImageSource = WPR.SilverlightCompability.ImageSource;

namespace WPR.WindowsCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.Media.Imaging.BitmapSource</c>. Inherits from
    /// <see cref="SlImageSource"/> so instances can be assigned to <c>Image.Source</c>. Mirrors
    /// Silverlight's chain: ImageSource &lt;- BitmapSource &lt;- BitmapImage / WriteableBitmap.
    ///
    /// <para><b><see cref="SetSource"/> really decodes now.</b> It used to be an empty method and
    /// <see cref="get_PixelWidth"/>/<see cref="get_PixelHeight"/> returned a hardcoded 1, so a game
    /// that loaded an image this way got a 1x1 bitmap with no pixels — and, because
    /// <see cref="WriteableBitmap.get_Pixels"/> handed back a fresh zero-filled array of the wrong
    /// length, nothing it wrote stuck either. Games do not check any of that; they read
    /// <c>PixelWidth</c>/<c>Pixels</c> and index straight into their own buffers sized from them.
    /// Feed Me Oil is the reference case: <c>SBLevelData</c> builds its collision grid from
    /// <c>res/data/level&lt;n&gt;.png</c> this way, so every level came out 1 pixel wide with an
    /// empty mask, and the first tap walked <c>AddActiveCollisions</c> off the end of the array —
    /// an IndexOutOfRangeException thrown out of <c>Director.Update</c> on every frame thereafter,
    /// which reads as "the game renders but is frozen and ignores taps".</para>
    ///
    /// <para><b>Pixels are ARGB ints (0xAARRGGBB)</b>, which is what Silverlight documents and what
    /// games rely on: they routinely do <c>BitConverter.GetBytes(Pixels[i])</c> and index the
    /// resulting little-endian bytes as B,G,R,A. Decoding hands back RGBA bytes, so the channels are
    /// reassembled rather than blitted.</para>
    ///
    /// <para><b>Decoding borrows the XNA graphics backend's codec</b>
    /// (<see cref="IGraphicsBackend.ReadImageStream"/>, FNA3D's stb_image) rather than carrying a
    /// second one. It is device-independent — it touches no GraphicsDevice — and it is already what
    /// <c>Texture2D.FromStream</c> uses, so PNG and JPEG behave identically whichever way a game
    /// loads them. When no backend is registered the bitmap keeps its declared size with a blank
    /// buffer, which is exactly the old behaviour: absent means unavailable, never an exception.</para>
    /// </summary>
    public class BitmapSource : SlImageSource
    {
        /// <summary>ARGB (0xAARRGGBB) pixels, row-major. Null until sized or decoded.</summary>
        private protected int[]? _pixels;

        private protected int _pixelWidth;
        private protected int _pixelHeight;

        /// <summary>
        /// The raw buffer, for same-assembly callers that hold a base-typed reference — a
        /// <c>private protected</c> field is not reachable through a <see cref="BitmapSource"/>
        /// qualifier even from a derived type (CS1540).
        /// </summary>
        internal int[]? PixelBuffer => _pixels;

        /// <summary>
        /// The decoded size, or zero when nothing has been decoded or sized. Deliberately NOT
        /// <see cref="get_PixelWidth"/>, which answers 1 in that case because games divide by it —
        /// a rasteriser needs to be able to tell "one pixel wide" from "no pixels at all".
        /// </summary>
        internal int DecodedWidth => _pixelWidth;

        /// <summary>See <see cref="DecodedWidth"/>.</summary>
        internal int DecodedHeight => _pixelHeight;

        public BitmapSource()
        {
        }

        // Lets BitmapImage(Uri) seed the inherited Path field through the public
        // ImageSource(string) constructor, since Path itself is internal to the
        // SilverlightCompability assembly.
        protected BitmapSource(string path) : base(path)
        {
        }

        /// <summary>
        /// Decodes <paramref name="stream"/> and replaces this bitmap's content and dimensions, as
        /// Silverlight does — the size a <see cref="WriteableBitmap"/> was constructed with is a
        /// starting point, not a constraint.
        /// </summary>
        public void SetSource(System.IO.Stream stream)
        {
            if (stream == null) return;

            if (!XnaBackend.HasGraphics)
            {
                // No codec available (e.g. a Silverlight-only host). Keep whatever size we were
                // given so callers that already sized buffers from it stay self-consistent.
                EnsureBuffer();
                return;
            }

            IntPtr pixels = IntPtr.Zero;
            try
            {
                IGraphicsBackend gfx = XnaBackend.Graphics;
                pixels = gfx.ReadImageStream(stream, out int w, out int h, out int len);
                if (pixels == IntPtr.Zero || w <= 0 || h <= 0)
                {
                    EnsureBuffer();
                    return;
                }

                // stb_image gives RGBA bytes; Silverlight wants 0xAARRGGBB ints.
                var rgba = new byte[len];
                Marshal.Copy(pixels, rgba, 0, len);

                var argb = new int[w * h];
                for (int i = 0, b = 0; i < argb.Length; i++, b += 4)
                {
                    argb[i] = (rgba[b + 3] << 24) | (rgba[b] << 16) | (rgba[b + 1] << 8) | rgba[b + 2];
                }

                _pixels = argb;
                _pixelWidth = w;
                _pixelHeight = h;
            }
            catch (Exception ex)
            {
                // A game feeding a format stb_image cannot read must not take the launch down —
                // it gets a blank bitmap of the declared size, as it did before decoding existed.
                WPR.Common.Log.Warn(WPR.Common.LogCategory.AppList,
                    $"BitmapSource.SetSource: decode failed ({ex.GetType().Name}: {ex.Message}); blank bitmap.");
                EnsureBuffer();
            }
            finally
            {
                if (pixels != IntPtr.Zero && XnaBackend.HasGraphics)
                {
                    try { XnaBackend.Graphics.FreeImage(pixels); } catch { /* best-effort */ }
                }
            }
        }

        /// <summary>
        /// Allocates the backing buffer for the declared size if a decode did not supply one, so
        /// <c>Pixels.Length</c> always agrees with <c>PixelWidth * PixelHeight</c>. Games size their
        /// own arrays off one and index with the other; letting the two disagree is the whole bug.
        /// </summary>
        private protected void EnsureBuffer()
        {
            if (_pixels != null && _pixels.Length == _pixelWidth * _pixelHeight) return;
            if (_pixelWidth <= 0 || _pixelHeight <= 0) return;
            _pixels = new int[_pixelWidth * _pixelHeight];
        }

        /// <summary>
        /// Width in pixels. Falls back to 1 rather than 0 when nothing has been decoded or sized —
        /// this type's historic answer, and callers divide by it.
        /// </summary>
        public Int32 get_PixelWidth()
        {
            return _pixelWidth > 0 ? _pixelWidth : 1;
        }

        /// <summary>See <see cref="get_PixelWidth"/>.</summary>
        public Int32 get_PixelHeight()
        {
            return _pixelHeight > 0 ? _pixelHeight : 1;
        }

    }//BitmapSource

}
