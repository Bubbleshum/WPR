namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Media.ArcSegment</c>. An elliptical arc in SVG endpoint form.</summary>
    public sealed class ArcSegment : PathSegment
    {
        public Point Point { get; set; }
        public Size Size { get; set; }
        public double RotationAngle { get; set; }
        public bool IsLargeArc { get; set; }
        public SweepDirection SweepDirection { get; set; }
    }
}
