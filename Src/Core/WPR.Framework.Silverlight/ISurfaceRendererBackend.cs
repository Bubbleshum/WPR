using System;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// The rendering seam behind <see cref="DrawingSurfaceBackgroundGrid"/> — Stage 5e of
    /// Plans/ARCHITECTURE-MIGRATION.md.
    ///
    /// <para>WP's <c>DrawingSurfaceBackgroundGrid</c> hosts a native Direct3D surface as a page
    /// background. WPR cannot load the app's native ARM WinRT component, so it substitutes a
    /// managed renderer. Producing those pixels is inherently backend work (D3D11 today, and a
    /// GL/Metal/software equivalent on any other host), so the concrete renderers live in
    /// <c>WPR.Backend.Direct3D11</c> and reach the framework only through this interface.</para>
    ///
    /// <para>Both members may return <see langword="null"/>: a backend that cannot create a device
    /// (no GPU, no debug layer, headless CI) reports that by returning null rather than throwing,
    /// and <see cref="DrawingSurfaceBackgroundGrid"/> falls through to the next candidate. That
    /// mirrors the <c>try { … } catch { /* fall through */ }</c> ladder this replaced.</para>
    ///
    /// <para>Only <b>pull</b> operations belong here — the framework asks for a renderer, the
    /// backend never calls back into the framework. That is the same rule the seven XNA seams in
    /// <c>WPR.Xna.Rhi</c> follow, and it is what keeps the framework compiling with no backend
    /// present at all.</para>
    /// </summary>
    public interface ISurfaceRendererBackend
    {
        /// <summary>
        /// Create a renderer that presents <paramref name="imagePath"/> full-screen (the WP
        /// splash art the app ships). The framework locates the file; the backend only draws it.
        /// Returns null if this backend cannot render the image.
        /// </summary>
        IBackgroundRenderer? CreateImageSplashRenderer(string imagePath);

        /// <summary>
        /// Create the last-resort animated test pattern, which proves GPU pixels are flowing
        /// through the pipeline when there is nothing else to show. Returns null if unavailable.
        /// </summary>
        IBackgroundRenderer? CreateTestPatternRenderer();
    }
}
