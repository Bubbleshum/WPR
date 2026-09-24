using System;

namespace WPR.WindowsCompability
{
    /// <summary>
    /// Stub for Silverlight's <c>System.Windows.Interop.SilverlightHost</c>. Exposes the
    /// most-commonly-touched properties so user-code constructors that assume the host
    /// exists can complete. Most members return safe defaults.
    /// </summary>
    public sealed class SilverlightHost
    {
        public SilverlightHostContent Content { get; } = new();
        public SilverlightHostSettings Settings { get; } = new();
        public Uri? Source { get; internal set; }

        /// <summary>True if the SL plugin is loaded; we report true since user code is running.</summary>
        public bool IsLoaded => true;

        public bool IsVersionSupported(string versionStr) => true;

        public string NavigationState { get; set; } = string.Empty;
        public event EventHandler<EventArgs>? NavigationStateChanged;
    }

    /// <summary>Stub for Silverlight's <c>System.Windows.Interop.Content</c>.</summary>
    public sealed class SilverlightHostContent
    {
        /// <summary>
        /// The WP7 screen: a fixed 480x800 WVGA panel, in portrait.
        /// </summary>
        /// <remarks>
        /// <b>These default to the screen size, not to zero.</b> This is the Silverlight way of
        /// asking how big the screen is — the counterpart of <c>GameWindow.ClientBounds</c>,
        /// which returns the same 480x800 for the same reason (see its own remarks: on WP7 the
        /// window IS the screen, and it never rotates). They were 0 until 2026-09-21, and a
        /// game that sizes its own layout from them divided by, scaled against, or laid out
        /// inside nothing. <b>Little Acorns</b> is the measured case: its menu rendered, at the
        /// wrong scale, with the edges off-screen.
        ///
        /// <para>Zero is never a plausible answer to "how big is the display", so there is no
        /// reading under which the old default was the safe one.</para>
        /// </remarks>
        public double ActualWidth { get; internal set; } = 480;

        /// <inheritdoc cref="ActualWidth"/>
        public double ActualHeight { get; internal set; } = 800;
        public double ZoomFactor { get; internal set; } = 1.0;

        /// <summary>WP devices report a per-resolution scale factor (e.g. 100 for WVGA).</summary>
        public int ScaleFactor { get; internal set; } = 100;

        public bool IsFullScreen { get; internal set; } = true;

        public event EventHandler? Resized;
        public event EventHandler? FullScreenChanged;
        public event EventHandler? Zoomed;

        protected internal void RaiseResized() => Resized?.Invoke(this, EventArgs.Empty);
        protected internal void RaiseFullScreenChanged() => FullScreenChanged?.Invoke(this, EventArgs.Empty);
        protected internal void RaiseZoomed() => Zoomed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stub for Silverlight's <c>System.Windows.Interop.Settings</c>.</summary>
    public sealed class SilverlightHostSettings
    {
        public bool EnableFrameRateCounter { get; set; }
        public bool EnableRedrawRegions { get; set; }
        public bool EnableCacheVisualization { get; set; }
        public bool EnableHTMLAccess => false;
        public bool Windowless => false;
        public int MaxFrameRate { get; set; } = 60;
    }
}
