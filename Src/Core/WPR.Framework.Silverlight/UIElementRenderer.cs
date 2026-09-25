using System;
using WPR.SilverlightCompability;

namespace Microsoft.Xna.Framework.Graphics
{
    /// <summary>
    /// Shim for <c>Microsoft.Xna.Framework.Graphics.UIElementRenderer</c>, from the WP7.1
    /// <c>Microsoft.Xna.Framework.Interop</c> assembly. It rasterises a Silverlight visual tree
    /// into an XNA <see cref="Texture2D"/> so a mixed-mode game can draw phone controls into its
    /// own scene with a <c>SpriteBatch</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>Why it lives in WPR.Framework.Silverlight rather than WPR.Framework.Xna, despite
    /// the namespace.</b> It names <see cref="UIElement"/> *and* <see cref="Texture2D"/>, and the
    /// reference between those two assemblies already runs Silverlight → Xna (BitmapSource borrows
    /// the graphics backend's image decoder). Declaring it on the Xna side would invert that into
    /// a cycle. Namespace and assembly are independent, and `WPR.Framework.Phone` sets the
    /// precedent of declaring a foreign namespace; the patcher entry carries the scope.</para>
    ///
    /// <para><b>The rasteriser is a CPU one and deliberately so.</b> The other renderer in this
    /// assembly draws into an Avalonia <c>DrawingContext</c>, and the mixed-mode host never
    /// initialises Avalonia — that is what lets these titles run on Android at all. See
    /// <see cref="SoftwareVisualRasteriser"/> for exactly what it paints; the headline omission
    /// is text, which needs a glyph rasteriser.</para>
    ///
    /// <para><b>An empty result is transparent, never opaque.</b> These games composite the
    /// texture over their own scene — and some, like Rabbids Go Phone, composite it *under* one —
    /// so transparent leaves the game visible and only the overlay missing. An opaque fill of any
    /// colour would hide the game entirely and look like a worse failure than the one it
    /// replaces.</para>
    ///
    /// <para><b>The reference case is Rabbids Go Phone's main menu</b>, whose entire background is
    /// one <c>ImageBrush</c> on the page's root <c>Canvas</c> — drawn by <c>GamePage.OnDraw</c>
    /// *before* the screen manager, so with nothing rasterised the menu's 3D logo and labels sat
    /// on a black screen. It is worth knowing that the image is not a file: it lives in the
    /// assembly's <c>.g.resources</c>, which is why <see cref="SilverlightImageDecoder"/> exists.</para>
    /// </remarks>
    public class UIElementRenderer : IDisposable
    {
        private readonly int _width;
        private readonly int _height;
        private Texture2D _texture;
        private uint[] _pixels;
        private uint[] _uploaded;
        private int _signature;
        private bool _everPainted;
        private bool _disposed;
        private static bool _announced;

        [ThreadStatic]
        private static UIElement _renderedThisFrame;

        /// <summary>
        /// Forgets what the game rasterised last frame. The host calls this immediately before
        /// pumping the page's Draw handlers.
        /// </summary>
        internal static void BeginHostFrame() => _renderedThisFrame = null;

        /// <summary>
        /// True when the game has, this frame, rasterised something that lives inside
        /// <paramref name="root"/>'s tree — i.e. it is compositing that page itself.
        /// </summary>
        /// <remarks>
        /// <para><b>This replaces asking whether the page has a <c>GameTimer.Draw</c> subscriber</b>,
        /// which is a proxy and one title defeats outright. Galactic Reign creates its
        /// <c>GameTimer</c> in a process-lifetime <c>RenderManager</c>, subscribes Draw and calls
        /// <c>Start()</c> in the constructor — so the proxy answers "the page draws itself" on
        /// every page of the game forever, while <c>RenderManager.Draw</c> returns immediately
        /// unless its own unrelated <c>IsRunning</c> flag is set. Its menu was therefore never
        /// drawn by anyone. Every other mixed-mode title subscribes from a specific page's
        /// constructor, which is why the proxy held for nine titles out of ten.</para>
        ///
        /// <para><b>Anything inside the tree counts, not just the root.</b> A game that rasterises
        /// a subtree of the live page is compositing that page's Silverlight content into its own
        /// scene at a depth of its choosing; compositing the whole page over the top as well would
        /// draw that subtree twice, and alpha-blending translucent content twice is visible.</para>
        /// </remarks>
        internal static bool GameCompositedThisFrame(UIElement root)
        {
            UIElement rendered = _renderedThisFrame;
            if (rendered == null || root == null) return false;

            for (UIElement node = rendered; node != null; node = node.Parent)
            {
                if (ReferenceEquals(node, root)) return true;
            }

            return false;
        }

        public UIElementRenderer(UIElement element, int width, int height)
        {
            Element = element;
            // Zero or negative would throw inside Texture2D; a game that asks for one before its
            // layout has run should get a usable object rather than an exception at construction,
            // which is the failure this type exists to remove.
            _width = width > 0 ? width : 1;
            _height = height > 0 ? height : 1;

            if (!_announced)
            {
                _announced = true;
                System.Diagnostics.Trace.WriteLine(
                    "[wpr-uirender] Silverlight overlays are rasterised on the CPU: backgrounds, " +
                    "borders, shapes, images and text" +
                    (TextRasteriser.IsAvailable ? "." : " (NO SYSTEM FONT FOUND — text will be blank)."));
            }
        }

        /// <summary>The element rasterised into <see cref="Texture"/>.</summary>
        public UIElement Element { get; private set; }


        /// <summary>
        /// The rasterised surface. Allocated on first read rather than in the constructor: a game
        /// typically builds this from a layout callback, which can run before the device exists.
        /// </summary>
        public Texture2D Texture
        {
            get
            {
                if (_disposed) throw new ObjectDisposedException("UIElementRenderer");
                if (_texture == null)
                {
                    GraphicsDevice device = SharedGraphicsDeviceManager.Current.GraphicsDevice;
                    _texture = new Texture2D(device, _width, _height, false, SurfaceFormat.Color);

                    // Explicitly cleared. A freshly created texture's contents are undefined, and
                    // undefined here would be whatever the GPU last had in that memory — drawn
                    // over the game's scene every frame.
                    _texture.SetData(new Color[_width * _height]);
                }
                return _texture;
            }
        }

        /// <summary>
        /// Rasterises <see cref="Element"/> into <see cref="Texture"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>Called once per frame from a game's draw, so it must do nothing when nothing
        /// changed.</b> Two gates, cheapest first: a hash of everything the rasteriser would look
        /// at (see <see cref="SoftwareVisualRasteriser.Signature"/>), and then a comparison of the
        /// composed pixels against what was last uploaded. A WP7 page is usually static, so in the
        /// steady state this costs a walk of a handful of nodes per frame and no GPU traffic at
        /// all; without the first gate a full-screen clear-and-recomposite would run sixty times a
        /// second for ever.</para>
        ///
        /// <para>Never throws. A game calls this from inside its own draw and does not expect it
        /// to fail, so a rasteriser fault costs the overlay for that frame and nothing else.</para>
        /// </remarks>
        public void Render()
        {
            if (_disposed) return;

            // Recorded on every call, not only when the pixels change: the gates below make most
            // calls early-return, and the host's question is "is the game drawing this page",
            // which a cache hit answers just as affirmatively as a repaint.
            _renderedThisFrame = Element;

            Texture2D texture;
            try { texture = Texture; }
            catch (Exception) { return; }   // no device yet; the game will call again

            try
            {
                int signature = SoftwareVisualRasteriser.Signature(Element);
                if (_everPainted && signature == _signature) return;
                _signature = signature;
                _everPainted = true;

                _pixels ??= new uint[_width * _height];
                SoftwareVisualRasteriser.Paint(Element, _width, _height, _pixels);

                // The signature can change without the pixels changing (a repositioned element
                // that draws nothing, an animation below sub-pixel). Uploading 1.5 MB in that
                // case would be pure waste, and the comparison is vectorised.
                if (_uploaded != null && _pixels.AsSpan().SequenceEqual(_uploaded.AsSpan()))
                    return;

                texture.SetData(_pixels);

                _uploaded ??= new uint[_pixels.Length];
                Array.Copy(_pixels, _uploaded, _pixels.Length);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(
                    "[wpr-uirender] rasterising " + (Element?.GetType().Name ?? "<null>") + " threw " +
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _texture?.Dispose(); }
            catch (Exception) { /* device already gone at teardown; nothing to salvage */ }
            _texture = null;
            _pixels = null;
            _uploaded = null;
            Element = null;
            GC.SuppressFinalize(this);
        }
    }
}
