namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Media.EllipseGeometry</c>.</summary>
    public sealed class EllipseGeometry : Geometry
    {
        public Point Center { get; set; }
        public double RadiusX { get; set; }
        public double RadiusY { get; set; }
    }
}
