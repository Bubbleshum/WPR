using System;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// The geometry a <see cref="Shape"/> stands for, before its <c>Stretch</c> is applied — shared
    /// by layout (<see cref="Shape"/>'s measure) and painting (<c>SoftwareVisualRasteriser</c>), so
    /// the two cannot disagree about where a shape is.
    /// </summary>
    /// <remarks>
    /// Null for <see cref="Rectangle"/> and <see cref="Ellipse"/>: their outline is a function of
    /// the size they are ARRANGED to, not of any property, so it is built at paint time.
    /// </remarks>
    internal static class ShapeGeometry
    {
        public static Geometry? GetDefiningGeometry(Shape shape)
        {
            switch (shape)
            {
                case Path path:
                    return path.Data;

                case Line line:
                    return new LineGeometry
                    {
                        StartPoint = new Point(line.X1, line.Y1),
                        EndPoint = new Point(line.X2, line.Y2),
                    };

                case Polyline polyline:
                    return FromPoints(polyline.Points, closed: false, polyline.FillRule);

                case Polygon polygon:
                    return FromPoints(polygon.Points, closed: true, polygon.FillRule);

                default:
                    return null;
            }
        }

        private static Geometry? FromPoints(PointCollection? points, bool closed, FillRule rule)
        {
            if (points == null || points.Count == 0) return null;

            var figure = new PathFigure { StartPoint = points[0], IsClosed = closed, IsFilled = true };
            var segment = new PolyLineSegment();
            for (int i = 1; i < points.Count; i++) segment.Points.Add(points[i]);
            figure.Segments.Add(segment);

            var geometry = new PathGeometry { FillRule = rule };
            geometry.Figures.Add(figure);
            return geometry;
        }
    }
}
