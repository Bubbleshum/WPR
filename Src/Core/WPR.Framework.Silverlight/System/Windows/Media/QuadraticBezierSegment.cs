namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Media.QuadraticBezierSegment</c>.</summary>
    public sealed class QuadraticBezierSegment : PathSegment
    {
        public Point Point1 { get; set; }
        public Point Point2 { get; set; }
    }
}
