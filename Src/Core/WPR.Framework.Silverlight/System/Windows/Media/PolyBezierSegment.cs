namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Media.PolyBezierSegment</c>. Points come in threes, one cubic each.</summary>
    [ContentProperty(nameof(Points))]
    public sealed class PolyBezierSegment : PathSegment
    {
        public PointCollection Points { get; set; } = new PointCollection();
    }
}
