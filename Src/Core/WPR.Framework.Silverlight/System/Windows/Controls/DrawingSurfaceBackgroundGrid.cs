using System;
using Avalonia.Threading;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Stub for <c>System.Windows.Controls.DrawingSurfaceBackgroundGrid</c>. WP-only Silverlight
    /// control used by hybrid Silverlight + WinRT apps to host a Direct3D surface as the page
    /// background.
    ///
    /// In real WP, the bound <c>IDrawingSurfaceBackgroundContentProvider</c> is implemented in
    /// native ARM C++ inside the app's bundled WinRT component (e.g.
    /// <c>WinPhoneRunnerAppComponent.dll</c>) and renders directly to a shared Direct3D11
    /// surface. WPR can't load that native code on net8.0 / x64, so by default we paint a
    /// clear status placeholder, or a branded splash built from the app's own tile art.
    ///
    /// <para>Until 2026-10-12 a Direct3D11 backend (<c>WPR.Backend.Direct3D11</c>, behind an
    /// <c>ISurfaceRendererBackend</c> seam) could also draw the app's splash image or a test
    /// pattern here, and a per-ProductId <c>RegisterRenderer</c> table existed for managed
    /// re-implementations. Neither had a real consumer, so both were retired
    /// (Plans/RUNTIME-CONVERGENCE.md item 2a).</para>
    /// </summary>
    public class DrawingSurfaceBackgroundGrid : Grid
    {
        /// <summary>The renderer attached to this control instance (if any).</summary>
        internal IBackgroundRenderer? AttachedRenderer { get; private set; }

        /// <summary>The opaque content-provider object the user app handed us.</summary>
        internal object? AttachedContentProvider { get; private set; }

        /// <summary>The opaque manipulation-handler object the user app handed us.</summary>
        internal object? AttachedManipulationHandler { get; private set; }

        private DispatcherTimer? _renderTimer;

        /// <summary>
        /// Hand off the WinRT content provider. Real WP wires this directly to a native D3D11
        /// surface; we attach the branded splash renderer when the app ships tile art, and
        /// otherwise just remember the object so the SL paint path can show a status
        /// placeholder.
        /// </summary>
        public void SetBackgroundContentProvider(object? contentProvider)
        {
            AttachedContentProvider = contentProvider;
            AttachedRenderer = LookupRenderer();
            AttachedRenderer?.OnContentProviderAttached(contentProvider);
            EnsureRenderTimer();
            InvalidateMeasure();
        }

        /// <summary>
        /// Drive ~60Hz repaints whenever there's an active renderer attached. Without this,
        /// an animated renderer would only advance when something else (touch input,
        /// layout change) invalidated the page. We ride Avalonia's main UI dispatcher so the
        /// timer dies along with the host window.
        /// </summary>
        private void EnsureRenderTimer()
        {
            if (_renderTimer != null) return;
            if (AttachedRenderer == null) return;

            _renderTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(16),
            };
            _renderTimer.Tick += (_, _) =>
            {
                if (AttachedRenderer == null) { _renderTimer.Stop(); return; }
                InvalidateMeasure();
            };
            _renderTimer.Start();
        }

        /// <summary>
        /// Hand off the WinRT manipulation handler (raw touch events). Forwarded to the
        /// attached renderer if any; otherwise stored.
        /// </summary>
        public void SetBackgroundManipulationHandler(object? manipulationHandler)
        {
            AttachedManipulationHandler = manipulationHandler;
            AttachedRenderer?.OnManipulationHandlerAttached(manipulationHandler);
        }

        private static IBackgroundRenderer? LookupRenderer()
        {
            // A branded splash built from the app's own tile/icon art -- nicer than a bare
            // placeholder for titles whose engine we can't host (e.g. AC Pirates' native ARM
            // Spark2). Pure Avalonia. Returning null makes the SL paint path draw its status
            // placeholder instead.
            string? installFolder = HostContext.CurrentInstallFolder;
            if (installFolder != null && BrandedSplashRenderer.HasArt(installFolder))
            {
                try { return new BrandedSplashRenderer(installFolder); }
                catch { /* fall through */ }
            }

            return null;
        }
    }
}
