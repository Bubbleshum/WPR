namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Media.BezierSegment</c>. A cubic Bezier from the current point.</summary>
    public sealed class BezierSegment : PathSegment
    {
        public Point Point1 { get; set; }
        public Point Point2 { get; set; }
        public Point Point3 { get; set; }
    }
}
