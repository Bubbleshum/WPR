using System;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Backend registry for the Silverlight framework — the direct counterpart of
    /// <c>WPR.Framework.Xna</c>'s <c>XnaBackend</c>, and registered the same way: the launcher
    /// (the composition root) sets the implementation before any app is loaded.
    ///
    /// <para>A settable static rather than constructor injection because the consumers are WP
    /// control shims (<see cref="DrawingSurfaceBackgroundGrid"/>) that games construct through
    /// the XAML parser — WPR never gets to pass them anything. That is the same constraint which
    /// produced <c>XnaBackend</c>.</para>
    ///
    /// <para><b>Unlike <c>XnaBackend</c>, this one does not need clearing at teardown.</b> The
    /// registered object is a stateless factory holding no device and no native handle, and both
    /// it and this class load into the launcher's default ALC — not the per-game ALC that
    /// <c>ApplicationLaunch</c> unloads. Nothing here can pin a game's ALC. The GPU resources live
    /// in the individual <see cref="IBackgroundRenderer"/> instances the factory returns, which are
    /// owned by the <see cref="DrawingSurfaceBackgroundGrid"/> that requested them. If a future
    /// backend wants to cache a device on the registered instance, that changes and this note
    /// stops being true.</para>
    ///
    /// <para>Null is the supported default. Every consumer treats "no backend registered" as
    /// "fall through to the pure-Avalonia path", which is exactly what the non-Windows legs of
    /// this framework have always done — they never had a D3D renderer to begin with.</para>
    /// </summary>
    public static class SilverlightBackend
    {
        /// <summary>
        /// The registered surface-rendering backend, or null when none is composed in.
        /// Set by the launcher; see <c>WPR.Platform.Windows.ServicesSetup</c>.
        /// </summary>
        public static ISurfaceRendererBackend? SurfaceRenderer { get; set; }
    }
}
