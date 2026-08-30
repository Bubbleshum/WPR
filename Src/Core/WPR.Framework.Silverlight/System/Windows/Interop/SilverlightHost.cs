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
        public double ActualWidth { get; internal set; }
        public double ActualHeight { get; internal set; }
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
