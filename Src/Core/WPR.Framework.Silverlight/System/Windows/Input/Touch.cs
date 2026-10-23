using System;
using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.Input.Touch</c>. WP/Silverlight apps subscribe to
    /// <see cref="FrameReported"/> to receive raw multi-touch frames; the Silverlight host raises it
    /// once a frame while a finger is down, and once more when the last one lifts, through
    /// <see cref="TouchFrameReporter"/>.
    /// </summary>
    /// <remarks>
    /// The WP Toolkit's GestureListener is built on this event (plus <c>TouchPanel.ReadGesture</c>),
    /// so a game's own Microsoft.Phone.Controls.Toolkit.dll raises its Tap/Hold/Drag/Pinch events
    /// from here. It was declared and never raised until patcher version 47.
    /// </remarks>
    public static class Touch
    {
        public static event TouchFrameEventHandler? FrameReported;

        internal static bool HasSubscribers => FrameReported != null;

        internal static void Raise(object? sender, TouchFrameEventArgs e)
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
            TouchFrameReporter.ResetForNewLaunch();
        }
    }
}
