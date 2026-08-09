using System;
using System.Collections.Generic;
using Avalonia.Threading;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Pluggable per-app D3D-background renderer. Implement this in a managed re-implementation
    /// of an app's WinRT component, then register it via
    /// <see cref="DrawingSurfaceBackgroundGrid.RegisterRenderer"/>.
    /// </summary>
    public interface IBackgroundRenderer
    {
        /// <summary>Called once when the user app hands its content provider to us.</summary>
        void OnContentProviderAttached(object? contentProvider);

        /// <summary>Called once when the user app hands its manipulation handler to us.</summary>
        void OnManipulationHandlerAttached(object? manipulationHandler);

        /// <summary>
        /// Asked to render the background into <paramref name="ctx"/> (an Avalonia DrawingContext)
        /// inside <paramref name="bounds"/> (the surface rect in window coordinates). Return
        /// true if the renderer painted; false to fall back to the default placeholder.
        /// </summary>
        bool Render(global::Avalonia.Media.DrawingContext ctx, global::Avalonia.Rect bounds);
    }
}
