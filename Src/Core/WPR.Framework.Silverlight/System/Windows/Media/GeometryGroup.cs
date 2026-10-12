namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Media.GeometryGroup</c>.</summary>
    /// <remarks>
    /// The children are filled together under the GROUP's <see cref="FillRule"/> (default
    /// EvenOdd), not each under its own, which is what makes two concentric circles in an EvenOdd
    /// group a ring.
    /// </remarks>
    [ContentProperty(nameof(Children))]
    public sealed class GeometryGroup : Geometry
    {
        public GeometryCollection Children { get; set; } = new GeometryCollection();
        public FillRule FillRule { get; set; } = FillRule.EvenOdd;
    }
}
