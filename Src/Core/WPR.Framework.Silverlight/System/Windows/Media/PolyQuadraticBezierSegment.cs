namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Media.PolyQuadraticBezierSegment</c>. Points come in pairs.</summary>
    [ContentProperty(nameof(Points))]
    public sealed class PolyQuadraticBezierSegment : PathSegment
    {
        public PointCollection Points { get; set; } = new PointCollection();
    }
}
