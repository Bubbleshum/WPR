using System;
using System.Collections.Generic;
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
    /// clear status placeholder.
    ///
    /// Per-app re-implementations can register a managed <see cref="IBackgroundRenderer"/>
    /// via <see cref="RegisterRenderer"/> keyed by ProductId; <see cref="SetBackgroundContentProvider"/>
    /// then routes the WinRT activation into that renderer instead of the default placeholder.
    /// </summary>
    public class DrawingSurfaceBackgroundGrid : Grid
    {
        private static readonly Dictionary<string, Func<IBackgroundRenderer>> _renderersByProductId =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The renderer attached to this control instance (if any).</summary>
        internal IBackgroundRenderer? AttachedRenderer { get; private set; }

        /// <summary>The opaque content-provider object the user app handed us.</summary>
        internal object? AttachedContentProvider { get; private set; }

        /// <summary>The opaque manipulation-handler object the user app handed us.</summary>
        internal object? AttachedManipulationHandler { get; private set; }

        private DispatcherTimer? _renderTimer;

        /// <summary>
        /// Register a managed renderer for a specific app, keyed by the WMAppManifest ProductId
        /// (the GUID-shaped folder name under AppData). When that app's MainPage calls
        /// <see cref="SetBackgroundContentProvider"/>, we instantiate the renderer instead of
        /// painting the default placeholder.
        ///
        /// Call this from a host plugin or from app-init code before the user app is loaded.
        /// </summary>
        public static void RegisterRenderer(string productId, Func<IBackgroundRenderer> factory)
        {
            if (string.IsNullOrEmpty(productId)) throw new ArgumentNullException(nameof(productId));
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            _renderersByProductId[productId] = factory;
        }

        /// <summary>
        /// Hand off the WinRT content provider. Real WP wires this directly to a native D3D11
        /// surface; we look up a registered managed renderer by current ProductId and route
        /// to it, otherwise just remember the object so the SL paint path can show a status
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
        /// the test-pattern animation would only advance when something else (touch input,
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
        /// registered renderer if any; otherwise stored.
        /// </summary>
        public void SetBackgroundManipulationHandler(object? manipulationHandler)
        {
            AttachedManipulationHandler = manipulationHandler;
            AttachedRenderer?.OnManipulationHandlerAttached(manipulationHandler);
        }

        private static IBackgroundRenderer? LookupRenderer()
        {
            // Candidate order is load-bearing and unchanged from the pre-Stage-5e inline version:
            // app-specific renderer, then the app's own splash art on the GPU, then the branded
            // Avalonia splash, then the test pattern. Each step falls through on failure.
            //
            // The GPU steps go through SilverlightBackend.SurfaceRenderer, which is null unless a
            // launcher composed a backend in (WPR.Backend.Direct3D11 today). Null is a supported
            // state, not an error: the net8.0 / net8.0-android legs of this framework never had a
            // D3D path, and they take the same fall-through they always did.

            // App-specific renderer first.
            string? pid = HostContext.CurrentProductId;
            if (pid != null && _renderersByProductId.TryGetValue(pid, out var factory))
            {
                try { return factory(); } catch { /* fall through */ }
            }

            ISurfaceRendererBackend? backend = SilverlightBackend.SurfaceRenderer;

            // A dedicated full-screen splash image (Ken Burns) if the app ships one. Finding the
            // file is a framework concern (it is the app's install folder); drawing it is the
            // backend's.
            if (backend != null)
            {
                string? splash = TryFindSplashImage();
                if (splash != null)
                {
                    var renderer = backend.CreateImageSplashRenderer(splash);
                    if (renderer != null) return renderer;
                }
            }

            // Otherwise present a branded splash built from the app's own tile/icon art — much
            // nicer than a bare test pattern for titles whose engine we can't host (e.g. AC
            // Pirates' native ARM Spark2). Pure Avalonia, so it works on every backend.
            string? installFolder = HostContext.CurrentInstallFolder;
            if (installFolder != null && BrandedSplashRenderer.HasArt(installFolder))
            {
                try { return new BrandedSplashRenderer(installFolder); }
                catch { /* fall through */ }
            }

            // Last resort: animated test pattern proves the pipeline is wired even when
            // there's nothing else to show.
            return backend?.CreateTestPatternRenderer();
        }

        /// <summary>
        /// Look for a WP-style splash image in the running app's install folder. Real WP apps
        /// always ship one (it's how the OS shows something while the app boots).
        /// Pure file lookup — no graphics dependency — so it is not backend-conditional.
        /// </summary>
        private static string? TryFindSplashImage()
        {
            string? folder = HostContext.CurrentInstallFolder;
            if (folder == null || !System.IO.Directory.Exists(folder)) return null;

            // Prefer the highest-resolution splash. Real XAPs typically ship 480 + 720 + base.
            foreach (var name in new[]
            {
                "SplashScreenImage.screen-WVGA.jpg",
                "SplashScreenImage.screen-720p.jpg",
                "SplashScreenImage720.jpg",
                "SplashScreenImage480.jpg",
                "SplashScreenImage.jpg",
                "SplashScreen.jpg",
                "SplashScreen.png",
            })
            {
                string p = System.IO.Path.Combine(folder, name);
                if (System.IO.File.Exists(p)) return p;
            }
            return null;
        }
    }
}
