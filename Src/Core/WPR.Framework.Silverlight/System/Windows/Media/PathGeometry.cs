namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Media.PathGeometry</c>.</summary>
    /// <remarks>
    /// <c>FillRule</c> defaults to <see cref="FillRule.EvenOdd"/>, as in Silverlight. The path
    /// mini-language (<c>Data="F1 M 0,0 L ..."</c>) is parsed into one of these by
    /// <see cref="PathMarkupParser"/>, so everything downstream sees a single representation.
    /// </remarks>
    [ContentProperty(nameof(Figures))]
    public sealed class PathGeometry : Geometry
    {
        public PathFigureCollection Figures { get; set; } = new PathFigureCollection();
        public FillRule FillRule { get; set; } = FillRule.EvenOdd;
    }
}
