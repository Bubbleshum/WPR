using System;
using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Stub for <c>System.Windows.Input.Touch</c>. WP/Silverlight apps subscribe to
    /// <see cref="FrameReported"/> to receive raw multi-touch frames. WPR currently routes
    /// touch through Avalonia/FNA's pointer pipeline rather than this static, so the event
    /// is never fired — but the type must exist so the user's hookup IL JITs and runs.
    /// </summary>
    public static class Touch
    {
        public static event TouchFrameEventHandler? FrameReported;

        // Suppress "never used" warning while the event isn't raised yet — we want the
        // event member around so user code that subscribes to it works.
        internal static void RaiseFrameReportedForFutureUse(object? sender, TouchFrameEventArgs e)
        {
            FrameReported?.Invoke(sender, e);
        }

        /// <summary>
        /// Drops subscribers left behind by an exited game. A STATIC event that game code subscribes to
        /// keeps the subscriber (and therefore the game's whole collectible AssemblyLoadContext) alive
        /// forever, so the host clears it during teardown — games do not unsubscribe before exiting.
        /// </summary>
        public static void ResetForNewLaunch()
        {
            FrameReported = null;
        }
    }
}
