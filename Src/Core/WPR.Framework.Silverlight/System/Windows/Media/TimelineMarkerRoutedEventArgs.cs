namespace System.Windows.Media
{
    /// <summary>
    /// Shim for <c>System.Windows.Media.TimelineMarkerRoutedEventHandler</c>.
    /// </summary>
    public delegate void TimelineMarkerRoutedEventHandler(object sender, TimelineMarkerRoutedEventArgs e);

    /// <summary>
    /// Shim for <c>System.Windows.Media.TimelineMarkerRoutedEventArgs</c>.
    /// </summary>
    public class TimelineMarkerRoutedEventArgs : RoutedEventArgs
    {
        public TimelineMarker? Marker { get; set; }
    }
}
