namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Media.PolyLineSegment</c>.</summary>
    [ContentProperty(nameof(Points))]
    public sealed class PolyLineSegment : PathSegment
    {
        public PointCollection Points { get; set; } = new PointCollection();
    }
}
