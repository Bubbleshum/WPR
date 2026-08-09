using System;
using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.Media.CompositionTarget</c>. The only WP7 use we
    /// see is subscribing to <see cref="Rendering"/> for per-frame callbacks
    /// (Panorama's entrance animation in design-tool mode). We never raise it.
    /// </summary>
    public static class CompositionTarget
    {
#pragma warning disable CS0067
        public static event EventHandler? Rendering;
#pragma warning restore CS0067

        /// <summary>
        /// Drops subscribers left behind by an exited game. A STATIC event that game code subscribes to
        /// keeps the subscriber (and therefore the game's whole collectible AssemblyLoadContext) alive
        /// forever, so the host clears it during teardown — games do not unsubscribe before exiting.
        /// </summary>
        public static void ResetForNewLaunch()
        {
            Rendering = null;
        }
    }
}
