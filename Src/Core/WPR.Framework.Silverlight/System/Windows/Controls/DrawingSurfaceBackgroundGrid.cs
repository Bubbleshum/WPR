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
    /// surface. A title whose whole game is that native component runs as a WP8 native app
    /// (<c>WPR.Wp8Native.XamlInterop</c>), not through this type.
    ///
    /// <para>Here it is an ordinary <see cref="Grid"/>: the CPU rasteriser paints its own
    /// <c>Background</c> and children, and nothing in place of the native surface. The provider
    /// and manipulation handler are only remembered.</para>
    ///
    /// <para>History: until 2026-10-12 a Direct3D11 backend could draw the app's splash image or
    /// a test pattern here (retired, the runtime convergence plan 2a), and until 2026-10-13 the
    /// Avalonia Silverlight host drew a branded splash from the app's tile art or a status
    /// placeholder (retired with that host, 2b / Stage D).</para>
    /// </summary>
    public class DrawingSurfaceBackgroundGrid : Grid
    {
        /// <summary>The opaque content-provider object the user app handed us.</summary>
        internal object? AttachedContentProvider { get; private set; }

        /// <summary>The opaque manipulation-handler object the user app handed us.</summary>
        internal object? AttachedManipulationHandler { get; private set; }

        /// <summary>
        /// Set by the host for the launch: called with each provider a page hands over. A MANAGED
        /// provider (the SharpDX Toolkit's GameWindowWP8) is driven by WPR.Engine.HostDirect3D's
        /// DrawingSurfaceHost through it; with no host, or a provider it cannot drive, the
        /// provider is only remembered.
        /// </summary>
        public static System.Action<object?>? ContentProviderSet { get; set; }

        // The grid whose provider the host is driving. The host has one embedded loop, so a page
        // clearing a grid it is leaving (Arkadium's renderer moves its provider between pages'
        // grids) must not stop the provider another grid now holds.
        private static DrawingSurfaceBackgroundGrid? _active;

        /// <summary>Hand off the WinRT content provider, to <see cref="ContentProviderSet"/>.</summary>
        public void SetBackgroundContentProvider(object? contentProvider)
        {
            AttachedContentProvider = contentProvider;
            InvalidateMeasure();
            if (contentProvider is null && !ReferenceEquals(_active, this))
            {
                return;
            }

            _active = contentProvider is null ? null : this;
            ContentProviderSet?.Invoke(contentProvider);
        }

        /// <summary>The launch is over: no grid holds the host's provider.</summary>
        public static void ResetActive() => _active = null;

        /// <summary>
        /// Set by the host for the launch: called with each manipulation handler a page hands over,
        /// which it gives a <c>DrawingSurfaceManipulationHost</c> raising the grid's pointer events
        /// (the Arkadium engine's only touch input).
        /// </summary>
        public static System.Action<object?>? ManipulationHandlerSet { get; set; }

        /// <summary>Hand off the WinRT manipulation handler (raw touch events), to <see cref="ManipulationHandlerSet"/>.</summary>
        public void SetBackgroundManipulationHandler(object? manipulationHandler)
        {
            AttachedManipulationHandler = manipulationHandler;
            ManipulationHandlerSet?.Invoke(manipulationHandler);
        }
    }
}
