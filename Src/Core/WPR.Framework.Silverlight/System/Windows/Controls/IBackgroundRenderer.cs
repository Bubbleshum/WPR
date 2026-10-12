using System;
using System.Collections.Generic;
using Avalonia.Threading;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Managed stand-in for the native D3D background an app's WinRT component would draw behind
    /// a <see cref="DrawingSurfaceBackgroundGrid"/>. The only implementation left is
    /// <see cref="BrandedSplashRenderer"/>; the D3D11 renderers and the per-app
    /// <c>RegisterRenderer</c> table were retired on 2026-10-12.
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
