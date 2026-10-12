namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Media.PathFigure</c>.</summary>
    [ContentProperty(nameof(Segments))]
    public sealed class PathFigure : DependencyObject
    {
        public Point StartPoint { get; set; }
        public PathSegmentCollection Segments { get; set; } = new PathSegmentCollection();
        public bool IsClosed { get; set; }

        /// <summary>Defaults to true, as in Silverlight: an unfilled figure is stroked only.</summary>
        public bool IsFilled { get; set; } = true;
    }
}
