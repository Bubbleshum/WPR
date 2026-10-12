using System;
using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    // Geometry flattening: every Silverlight geometry becomes a list of polylines ("figures") in
    // some output space, which is all the coverage rasteriser and the stroker need to know about.
    internal static partial class SoftwareVisualRasteriser
    {
        /// <summary>One flattened figure: a polyline, plus whether it closes and whether it fills.</summary>
        internal sealed class FlatFigure
        {
            public readonly List<Point> Points = new();
            public bool Closed;
            public bool Filled = true;
        }

        /// <summary>Magic constant for a quarter ellipse as one cubic: 4/3 * (sqrt(2) - 1).</summary>
        private const double Kappa = 0.55228474983079;

        /// <summary>
        /// The geometry's tight bounding box with its own <c>Transform</c> applied; no stroke.
        /// Backs <see cref="Geometry.Bounds"/>.
        /// </summary>
        internal static Rect GeometryBounds(Geometry geometry)
        {
            var figures = new List<FlatFigure>();
            FlattenGeometry(geometry, Affine.Identity, 0.01, figures, 0);
            return BoundsOf(figures) ?? Rect.Empty;
        }

        private static Rect? BoundsOf(List<FlatFigure> figures)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            bool any = false;
            foreach (FlatFigure f in figures)
            {
                foreach (Point p in f.Points)
                {
                    if (double.IsNaN(p.X) || double.IsNaN(p.Y)) continue;
                    any = true;
                    if (p.X < minX) minX = p.X;
                    if (p.Y < minY) minY = p.Y;
                    if (p.X > maxX) maxX = p.X;
                    if (p.Y > maxY) maxY = p.Y;
                }
            }
            return any ? new Rect(minX, minY, maxX - minX, maxY - minY) : null;
        }

        private static Point Apply(in Affine m, Point p)
            => new Point((p.X * m.M11) + (p.Y * m.M21) + m.Dx, (p.X * m.M12) + (p.Y * m.M22) + m.Dy);

        /// <summary>
        /// Flattens <paramref name="geometry"/> into <paramref name="into"/>, mapping every point
        /// through the geometry's own transform and then <paramref name="map"/>.
        /// </summary>
        /// <param name="tolerance">Maximum deviation from the true curve, in OUTPUT units.</param>
        /// <returns>The fill rule the figures are to be filled with.</returns>
        /// <remarks>
        /// Curves are transformed BEFORE they are flattened (an affine map of a Bezier is the Bezier
        /// of the mapped control points), so a large stretch or zoom never shows facets — the
        /// tolerance is honoured in the space the pixels are in. Arcs become cubics first for the
        /// same reason.
        /// </remarks>
        private static FillRule FlattenGeometry(Geometry? geometry, Affine map, double tolerance, List<FlatFigure> into, int depth)
        {
            if (geometry == null || depth > 16) return FillRule.EvenOdd;

            Affine m = geometry.Transform != null ? Compose(geometry.Transform, 0).Then(map) : map;

            switch (geometry)
            {
                case PathGeometry path:
                    foreach (PathFigure figure in path.Figures)
                    {
                        if (figure != null) FlattenFigure(figure, m, tolerance, into);
                    }
                    return path.FillRule;

                case EllipseGeometry ellipse:
                    into.Add(EllipseFigure(ellipse.Center.X, ellipse.Center.Y, ellipse.RadiusX, ellipse.RadiusY, m, tolerance));
                    return FillRule.EvenOdd;

                case RectangleGeometry rect:
                    into.Add(RoundedRectFigure(rect.Rect, rect.RadiusX, rect.RadiusY, m, tolerance));
                    return FillRule.EvenOdd;

                case LineGeometry line:
                {
                    var f = new FlatFigure { Closed = false, Filled = false };
                    f.Points.Add(Apply(m, line.StartPoint));
                    f.Points.Add(Apply(m, line.EndPoint));
                    into.Add(f);
                    return FillRule.EvenOdd;
                }

                case GeometryGroup group:
                    // Children are filled together under the GROUP's rule; their own rules (a
                    // nested PathGeometry's, say) do not apply.
                    foreach (Geometry child in group.Children)
                        FlattenGeometry(child, m, tolerance, into, depth + 1);
                    return group.FillRule;

                default:
                    ReportOnce("geometry:" + geometry.GetType().Name, "not drawn");
                    return FillRule.EvenOdd;
            }
        }

        private static void FlattenFigure(PathFigure figure, Affine m, double tol, List<FlatFigure> into)
        {
            var flat = new FlatFigure { Closed = figure.IsClosed, Filled = figure.IsFilled };
            Point current = figure.StartPoint;
            flat.Points.Add(Apply(m, current));

            foreach (PathSegment segment in figure.Segments)
            {
                switch (segment)
                {
                    case LineSegment line:
                        current = line.Point;
                        flat.Points.Add(Apply(m, current));
                        break;

                    case PolyLineSegment poly:
                        foreach (Point p in poly.Points)
                        {
                            current = p;
                            flat.Points.Add(Apply(m, current));
                        }
                        break;

                    case BezierSegment bezier:
                        Cubic(flat.Points, Apply(m, current), Apply(m, bezier.Point1), Apply(m, bezier.Point2), Apply(m, bezier.Point3), tol);
                        current = bezier.Point3;
                        break;

                    case PolyBezierSegment polyBezier:
                    {
                        PointCollection pts = polyBezier.Points;
                        for (int i = 0; i + 2 < pts.Count; i += 3)
                        {
                            Cubic(flat.Points, Apply(m, current), Apply(m, pts[i]), Apply(m, pts[i + 1]), Apply(m, pts[i + 2]), tol);
                            current = pts[i + 2];
                        }
                        break;
                    }

                    case QuadraticBezierSegment quad:
                        Quadratic(flat.Points, Apply(m, current), Apply(m, quad.Point1), Apply(m, quad.Point2), tol);
                        current = quad.Point2;
                        break;

                    case PolyQuadraticBezierSegment polyQuad:
                    {
                        PointCollection pts = polyQuad.Points;
                        for (int i = 0; i + 1 < pts.Count; i += 2)
                        {
                            Quadratic(flat.Points, Apply(m, current), Apply(m, pts[i]), Apply(m, pts[i + 1]), tol);
                            current = pts[i + 1];
                        }
                        break;
                    }

                    case ArcSegment arc:
                        Arc(flat.Points, current, arc, m, tol);
                        current = arc.Point;
                        break;

                    case null:
                        break;

                    default:
                        ReportOnce("segment:" + segment.GetType().Name, "drawn as a line to nowhere (skipped)");
                        break;
                }
            }

            into.Add(flat);
        }

        /// <summary>
        /// Appends a cubic's points after <paramref name="p0"/> (which the caller already added).
        /// The step count is Wang's formula: the fewest uniform steps that keep the chord within
        /// <paramref name="tol"/> of the curve.
        /// </summary>
        private static void Cubic(List<Point> into, Point p0, Point p1, Point p2, Point p3, double tol)
        {
            double ddx = Math.Max(Math.Abs(p0.X - (2 * p1.X) + p2.X), Math.Abs(p1.X - (2 * p2.X) + p3.X));
            double ddy = Math.Max(Math.Abs(p0.Y - (2 * p1.Y) + p2.Y), Math.Abs(p1.Y - (2 * p2.Y) + p3.Y));
            double dd = Math.Sqrt((ddx * ddx) + (ddy * ddy));
            int n = StepCount(Math.Sqrt(0.75 * dd / tol));

            for (int i = 1; i <= n; i++)
            {
                double t = (double)i / n;
                double u = 1 - t;
                double a = u * u * u, b = 3 * u * u * t, c = 3 * u * t * t, d = t * t * t;
                into.Add(new Point(
                    (a * p0.X) + (b * p1.X) + (c * p2.X) + (d * p3.X),
                    (a * p0.Y) + (b * p1.Y) + (c * p2.Y) + (d * p3.Y)));
            }
        }

        private static void Quadratic(List<Point> into, Point p0, Point p1, Point p2, double tol)
        {
            double ddx = p0.X - (2 * p1.X) + p2.X;
            double ddy = p0.Y - (2 * p1.Y) + p2.Y;
            double dd = Math.Sqrt((ddx * ddx) + (ddy * ddy));
            int n = StepCount(Math.Sqrt(0.25 * dd / tol));

            for (int i = 1; i <= n; i++)
            {
                double t = (double)i / n;
                double u = 1 - t;
                into.Add(new Point(
                    (u * u * p0.X) + (2 * u * t * p1.X) + (t * t * p2.X),
                    (u * u * p0.Y) + (2 * u * t * p1.Y) + (t * t * p2.Y)));
            }
        }

        private static int StepCount(double n)
        {
            if (double.IsNaN(n) || n < 1) return 1;
            return n > 512 ? 512 : (int)Math.Ceiling(n);
        }

        /// <summary>
        /// An SVG/Silverlight endpoint-form elliptical arc from <paramref name="from"/>, as cubics.
        /// The conversion is SVG 1.1's (appendix F.6.5); <c>Clockwise</c> is SVG's sweep-flag 1,
        /// which in y-down screen space is clockwise as seen.
        /// </summary>
        private static void Arc(List<Point> into, Point from, ArcSegment arc, Affine m, double tol)
        {
            Point to = arc.Point;
            double rx = Math.Abs(arc.Size.Width);
            double ry = Math.Abs(arc.Size.Height);

            if ((from.X == to.X && from.Y == to.Y)) return;
            if (rx == 0 || ry == 0)
            {
                into.Add(Apply(m, to)); // degenerate radii: a straight line, per the spec
                return;
            }

            double phi = arc.RotationAngle * Math.PI / 180.0;
            double cos = Math.Cos(phi), sin = Math.Sin(phi);

            double dx2 = (from.X - to.X) / 2, dy2 = (from.Y - to.Y) / 2;
            double x1 = (cos * dx2) + (sin * dy2);
            double y1 = (-sin * dx2) + (cos * dy2);

            double lambda = ((x1 * x1) / (rx * rx)) + ((y1 * y1) / (ry * ry));
            if (lambda > 1)
            {
                double s = Math.Sqrt(lambda);
                rx *= s;
                ry *= s;
            }

            bool sweep = arc.SweepDirection == SweepDirection.Clockwise;
            double num = (rx * rx * ry * ry) - (rx * rx * y1 * y1) - (ry * ry * x1 * x1);
            double den = (rx * rx * y1 * y1) + (ry * ry * x1 * x1);
            double coef = den == 0 ? 0 : Math.Sqrt(Math.Max(0, num / den));
            if (arc.IsLargeArc == sweep) coef = -coef;

            double cxp = coef * (rx * y1 / ry);
            double cyp = coef * -(ry * x1 / rx);

            double cx = (cos * cxp) - (sin * cyp) + ((from.X + to.X) / 2);
            double cy = (sin * cxp) + (cos * cyp) + ((from.Y + to.Y) / 2);

            double theta1 = Math.Atan2((y1 - cyp) / ry, (x1 - cxp) / rx);
            double theta2 = Math.Atan2((-y1 - cyp) / ry, (-x1 - cxp) / rx);
            double delta = theta2 - theta1;
            if (sweep && delta < 0) delta += 2 * Math.PI;
            else if (!sweep && delta > 0) delta -= 2 * Math.PI;

            int pieces = Math.Max(1, (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 2) - 1e-9));
            double step = delta / pieces;
            double alpha = 4.0 / 3.0 * Math.Tan(step / 4);

            Point Map(double ux, double uy) => Apply(m, new Point(
                cx + (rx * ux * cos) - (ry * uy * sin),
                cy + (rx * ux * sin) + (ry * uy * cos)));

            double a = theta1;
            for (int i = 0; i < pieces; i++)
            {
                double b = a + step;
                double ca = Math.Cos(a), sa = Math.Sin(a), cb = Math.Cos(b), sb = Math.Sin(b);
                Point p0 = Map(ca, sa);
                Point c1 = Map(ca - (alpha * sa), sa + (alpha * ca));
                Point c2 = Map(cb + (alpha * sb), sb - (alpha * cb));
                // The last piece ends exactly on the requested point, not on a rounding of it.
                Point p3 = i == pieces - 1 ? Apply(m, to) : Map(cb, sb);
                Cubic(into, p0, c1, c2, p3, tol);
                a = b;
            }
        }

        /// <summary>A full ellipse as four cubics, starting at its rightmost point, clockwise on screen.</summary>
        private static FlatFigure EllipseFigure(double cx, double cy, double rx, double ry, Affine m, double tol)
        {
            var f = new FlatFigure { Closed = true, Filled = true };
            rx = Math.Abs(rx);
            ry = Math.Abs(ry);
            if (rx == 0 && ry == 0) return f;

            double kx = rx * Kappa, ky = ry * Kappa;
            Point P(double x, double y) => Apply(m, new Point(x, y));

            f.Points.Add(P(cx + rx, cy));
            Cubic(f.Points, P(cx + rx, cy), P(cx + rx, cy + ky), P(cx + kx, cy + ry), P(cx, cy + ry), tol);
            Cubic(f.Points, P(cx, cy + ry), P(cx - kx, cy + ry), P(cx - rx, cy + ky), P(cx - rx, cy), tol);
            Cubic(f.Points, P(cx - rx, cy), P(cx - rx, cy - ky), P(cx - kx, cy - ry), P(cx, cy - ry), tol);
            Cubic(f.Points, P(cx, cy - ry), P(cx + kx, cy - ry), P(cx + rx, cy - ky), P(cx + rx, cy), tol);
            return f;
        }

        /// <summary>
        /// A rectangle with elliptical corners. Radii are clamped to half the side they sit on, as
        /// Silverlight does, so <c>RadiusX="8"</c> on a 10-wide rectangle is a capsule.
        /// </summary>
        private static FlatFigure RoundedRectFigure(Rect r, double radiusX, double radiusY, Affine m, double tol)
        {
            var f = new FlatFigure { Closed = true, Filled = true };
            double w = Math.Max(0, r.Width), h = Math.Max(0, r.Height);
            double rx = Math.Min(Math.Abs(radiusX), w / 2);
            double ry = Math.Min(Math.Abs(radiusY), h / 2);
            double x0 = r.X, y0 = r.Y, x1 = r.X + w, y1 = r.Y + h;
            Point P(double x, double y) => Apply(m, new Point(x, y));

            if (rx <= 0 || ry <= 0)
            {
                f.Points.Add(P(x0, y0));
                f.Points.Add(P(x1, y0));
                f.Points.Add(P(x1, y1));
                f.Points.Add(P(x0, y1));
                return f;
            }

            double kx = rx * Kappa, ky = ry * Kappa;
            f.Points.Add(P(x0 + rx, y0));
            f.Points.Add(P(x1 - rx, y0));
            Cubic(f.Points, P(x1 - rx, y0), P(x1 - rx + kx, y0), P(x1, y0 + ry - ky), P(x1, y0 + ry), tol);
            f.Points.Add(P(x1, y1 - ry));
            Cubic(f.Points, P(x1, y1 - ry), P(x1, y1 - ry + ky), P(x1 - rx + kx, y1), P(x1 - rx, y1), tol);
            f.Points.Add(P(x0 + rx, y1));
            Cubic(f.Points, P(x0 + rx, y1), P(x0 + rx - kx, y1), P(x0, y1 - ry + ky), P(x0, y1 - ry), tol);
            f.Points.Add(P(x0, y0 + ry));
            Cubic(f.Points, P(x0, y0 + ry), P(x0, y0 + ry - ky), P(x0 + rx - kx, y0), P(x0 + rx, y0), tol);
            return f;
        }
    }
}
