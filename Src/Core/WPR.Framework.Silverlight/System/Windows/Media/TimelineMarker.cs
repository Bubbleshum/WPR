using System;
using System.Collections.ObjectModel;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.Media.TimelineMarker</c> — a named point in a media file's
    /// timeline, reported through <c>MediaElement.MarkerReached</c>.
    /// </summary>
    public class TimelineMarker : DependencyObject
    {
        public TimeSpan Time { get; set; }
        public string Type { get; set; }
        public string Text { get; set; }
    }

    /// <summary>
    /// Shim for <c>System.Windows.Media.TimelineMarkerCollection</c>.
    /// </summary>
    /// <remarks>
    /// Always empty in practice: WPR's <c>MediaElement</c> never opens a file, so no markers are
    /// ever discovered. It exists because a title that reads <c>MediaElement.Markers</c> needs
    /// the property to resolve and return something enumerable rather than null — one title in
    /// the library does exactly that.
    /// </remarks>
    public class TimelineMarkerCollection : Collection<TimelineMarker>
    {
    }
}
