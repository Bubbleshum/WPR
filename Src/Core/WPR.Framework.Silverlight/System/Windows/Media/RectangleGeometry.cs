using System;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Media.RectangleGeometry</c>.</summary>
    /// <remarks>
    /// Commonly used as a <c>UIElement.Clip</c>, which is why <see cref="Bounds"/> stays the raw
    /// <see cref="Rect"/> rather than going through the flattener.
    /// </remarks>
    public class RectangleGeometry : Geometry
    {
        public Rect Rect { get; set; }

        public double RadiusX { get; set; }

        public double RadiusY { get; set; }

        public override Rect Bounds => Transform == null ? Rect : SoftwareVisualRasteriser.GeometryBounds(this);
    }
}
