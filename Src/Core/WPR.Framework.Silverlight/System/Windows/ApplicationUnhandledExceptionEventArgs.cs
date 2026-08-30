using System;

namespace WPR.WindowsCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.ApplicationUnhandledExceptionEventArgs</c>.
    ///
    /// The two members below are the whole point of the type: a game's
    /// <c>Application_UnhandledException</c> handler reads <see cref="ExceptionObject"/> to log or
    /// display the failure and sets <see cref="Handled"/> to stop the platform tearing the app
    /// down. Without them the handler itself fails to bind, so a game that would have reported a
    /// recoverable error instead dies inside its own error path — Kinectimals
    /// (Main.MainGame.OnUnhandledException) and Skulls of the Shogun
    /// (Microsoft.Internal.GamesTest.Beacon.Csi.CsiHandler) both do this.
    /// </summary>
    public class ApplicationUnhandledExceptionEventArgs : EventArgs
    {
        public ApplicationUnhandledExceptionEventArgs()
        {
        }

        public ApplicationUnhandledExceptionEventArgs(Exception exception, bool handled)
        {
            ExceptionObject = exception;
            Handled = handled;
        }

        /// <summary>The exception that went unhandled. Never null in practice on the real platform.</summary>
        public Exception ExceptionObject { get; internal set; }

        /// <summary>
        /// Set by the handler to say it dealt with the failure. WPR does not currently consult
        /// this — the host's own teardown path decides what happens next — but games both read
        /// and write it, so it has to round-trip.
        /// </summary>
        public bool Handled { get; set; }
    }
}
