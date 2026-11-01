namespace System.Windows.Controls
{
    /// <summary>
    /// Shim for WP8's <c>System.Windows.Controls.DrawingSurface</c>: an element whose content a
    /// native Direct3D component draws into a texture of its own (Mandreel's Fruit Ninja and
    /// Jetpack Joyride, Gameloft's WCP titles). The page hands it the component's provider; the
    /// host the launch sets (<see cref="ContentProviderSet"/>) runs it. Unlike
    /// <see cref="DrawingSurfaceBackgroundGrid"/> it is an element of the page, so on a landscape
    /// page its texture and its pointer positions are landscape too.
    /// </summary>
    public class DrawingSurface : FrameworkElement
    {
        /// <summary>The opaque content-provider object the page handed over.</summary>
        internal object? AttachedContentProvider { get; private set; }

        /// <summary>The opaque manipulation-handler object the page handed over.</summary>
        internal object? AttachedManipulationHandler { get; private set; }

        /// <summary>Set by the host for the launch: called with each provider a page hands over.</summary>
        public static Action<object?>? ContentProviderSet { get; set; }

        /// <summary>Set by the host for the launch: called with each manipulation handler a page hands over.</summary>
        public static Action<object?>? ManipulationHandlerSet { get; set; }

        /// <summary>Hand off the WinRT content provider, to <see cref="ContentProviderSet"/>.</summary>
        public void SetContentProvider(object? contentProvider)
        {
            AttachedContentProvider = contentProvider;
            InvalidateMeasure();
            ContentProviderSet?.Invoke(contentProvider);
        }

        /// <summary>Hand off the WinRT manipulation handler (raw touch events), to <see cref="ManipulationHandlerSet"/>.</summary>
        public void SetManipulationHandler(object? manipulationHandler)
        {
            AttachedManipulationHandler = manipulationHandler;
            ManipulationHandlerSet?.Invoke(manipulationHandler);
        }
    }
}
