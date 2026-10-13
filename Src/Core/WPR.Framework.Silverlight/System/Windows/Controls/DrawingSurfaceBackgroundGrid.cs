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
    /// a test pattern here (retired, Plans/RUNTIME-CONVERGENCE.md 2a), and until 2026-10-13 the
    /// Avalonia Silverlight host drew a branded splash from the app's tile art or a status
    /// placeholder (retired with that host, 2b / Stage D).</para>
    /// </summary>
    public class DrawingSurfaceBackgroundGrid : Grid
    {
        /// <summary>The opaque content-provider object the user app handed us.</summary>
        internal object? AttachedContentProvider { get; private set; }

        /// <summary>The opaque manipulation-handler object the user app handed us.</summary>
        internal object? AttachedManipulationHandler { get; private set; }

        /// <summary>Hand off the WinRT content provider. Remembered, never called.</summary>
        public void SetBackgroundContentProvider(object? contentProvider)
        {
            AttachedContentProvider = contentProvider;
            InvalidateMeasure();
        }

        /// <summary>Hand off the WinRT manipulation handler (raw touch events). Remembered only.</summary>
        public void SetBackgroundManipulationHandler(object? manipulationHandler)
        {
            AttachedManipulationHandler = manipulationHandler;
        }
    }
}
