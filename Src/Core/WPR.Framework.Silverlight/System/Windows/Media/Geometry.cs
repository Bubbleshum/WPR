using System;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Media.Geometry</c>.</summary>
    /// <remarks>
    /// <see cref="Bounds"/> is computed from the flattened outline (<c>SoftwareVisualRasteriser.GeometryBounds</c>)
    /// with <see cref="Transform"/> applied, which is what Silverlight reports: the tight box of
    /// the fill geometry, without any stroke.
    /// </remarks>
    public abstract class Geometry : DependencyObject
    {
        public Transform? Transform { get; set; }

        public virtual Rect Bounds => SoftwareVisualRasteriser.GeometryBounds(this);
    }
}
