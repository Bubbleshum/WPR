namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Media.LineGeometry</c>. Stroked only; it encloses no area.</summary>
    public sealed class LineGeometry : Geometry
    {
        public Point StartPoint { get; set; }
        public Point EndPoint { get; set; }
    }
}
