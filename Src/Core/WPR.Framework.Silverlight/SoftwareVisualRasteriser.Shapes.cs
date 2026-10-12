using System;
using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Real shape rasterisation: <see cref="Rectangle"/> (rounded corners), <see cref="Ellipse"/>,
    /// <see cref="Line"/>, <see cref="Polyline"/>, <see cref="Polygon"/> and <see cref="Path"/>
    /// over every <see cref="Geometry"/> the shim defines.
    /// </summary>
    /// <remarks>
    /// <para><b>Pipeline.</b> Geometry is flattened to polylines in the element's own LAYOUT space
    /// (after <c>Stretch</c>), the stroke is turned into outline polygons in that same space — so a
    /// non-uniform ancestor scale stretches the pen exactly as it stretches the shape — and only
    /// then is everything mapped to device pixels and filled by one coverage rasteriser.</para>
    ///
    /// <para><b>Anti-aliasing</b> is <see cref="CoverageSubsamples"/> sub-scanlines per pixel row,
    /// each contributing EXACT horizontal coverage of its spans. Both fill rules are evaluated per
    /// sub-scanline from sorted crossings, which is what makes EvenOdd exact rather than the
    /// approximation a signed-area accumulator gives it.</para>
    ///
    /// <para><b>The stroke is a union of positively-oriented pieces</b> — one quad per segment,
    /// plus join and cap pieces — filled Nonzero. Normalising every piece's orientation is what
    /// makes the union work: two overlapping pieces of opposite winding would cancel to a hole.
    /// Coverage is clamped per sub-sample, so overlapping pieces of a translucent stroke do not
    /// double-blend.</para>
    ///
    /// <para><b>Shapes are not clipped to their own slot</b>, unlike every other element here: a
    /// <c>Stretch="None"</c> path draws at its geometry's own coordinates, which routinely lie
    /// outside the box layout gave it, and Silverlight shows them. Only an ancestor's clip applies.
    /// <c>UniformToFill</c> is the exception — that stretch is defined as overflowing and cropping.</para>
    ///
    /// <para><b>A plain rectangle keeps the exact pixel path it always had</b> (no radius, no dashes,
    /// <c>Stretch</c> None or Fill): WP7 chrome is overwhelmingly such rectangles, and anti-aliasing
    /// their fractional edges would soften every mixed-mode title's UI for no gain.</para>
    /// </remarks>
    internal static partial class SoftwareVisualRasteriser
    {
        /// <summary>Vertical sub-samples per pixel row. 16 gives 17 coverage levels on a horizontal-ish edge.</summary>
        internal const int CoverageSubsamples = 16;

        /// <summary>Curve flattening tolerance, in device pixels.</summary>
        private const double DeviceTolerance = 0.15;

        /// <summary>A rasterised coverage mask: <see cref="A"/> is row-major over (X, Y, W, H), 0..1.</summary>
        internal sealed class CoverageMask
        {
            public int X, Y, W, H;
            public float[] A = Array.Empty<float>();

            public float At(int x, int y)
            {
                x -= X; y -= Y;
                if (x < 0 || y < 0 || x >= W || y >= H) return 0;
                return A[(y * W) + x];
            }
        }

        // ---------------------------------------------------------------------------------
        // Dispatch target
        // ---------------------------------------------------------------------------------

        /// <param name="bounds">The shape's slot in device space.</param>
        /// <param name="clip">The ANCESTOR clip — deliberately not the slot; see the class remarks.</param>
        private static void PaintShape(
            Surface surface, Shape shape, Rect bounds, Rect clip, double opacity, double scaleX, double scaleY)
        {
            DoubleCollection? dashes = shape.StrokeDashArray;
            bool dashed = dashes != null && dashes.Count > 0;

            if (shape is Rectangle plain && plain.RadiusX <= 0 && plain.RadiusY <= 0 && !dashed &&
                (plain.Stretch == Stretch.None || plain.Stretch == Stretch.Fill))
            {
                Rect slotClip = Intersect(bounds, clip);
                FillBrush(surface, plain.Fill, bounds, slotClip, opacity);
                StrokeEdges(surface, plain.Stroke, bounds, plain.StrokeThickness, opacity, slotClip);
                return;
            }

            if (scaleX == 0 || scaleY == 0) return;

            double layoutW = bounds.Width / scaleX;
            double layoutH = bounds.Height / scaleY;
            bool stroked = shape.Stroke != null && shape.StrokeThickness > 0;
            double t = stroked ? shape.StrokeThickness : 0;

            // Flatten in LAYOUT space at a tolerance that is DeviceTolerance once scaled up.
            double maxScale = Math.Max(Math.Abs(scaleX), Math.Abs(scaleY));
            double tol = DeviceTolerance / maxScale;

            var figures = new List<FlatFigure>();
            FillRule rule = FillRule.EvenOdd;
            Rect? shapeBox; // the shape's outline bounds in layout space: what brushes are relative to

            switch (shape)
            {
                case Rectangle rect:
                {
                    Rect r = StretchedBox(rect.Stretch, layoutW, layoutH, t);
                    figures.Add(RoundedRectFigure(r, rect.RadiusX, rect.RadiusY, Affine.Identity, tol));
                    shapeBox = r;
                    break;
                }

                case Ellipse ellipse:
                {
                    Rect r = StretchedBox(ellipse.Stretch, layoutW, layoutH, t);
                    figures.Add(EllipseFigure(r.X + (r.Width / 2), r.Y + (r.Height / 2), r.Width / 2, r.Height / 2, Affine.Identity, tol));
                    shapeBox = r;
                    break;
                }

                default:
                {
                    Geometry? geometry = ShapeGeometry.GetDefiningGeometry(shape);
                    if (geometry == null)
                    {
                        if (shape is not Path)
                            ReportOnce("shape:" + shape.GetType().Name, "not drawn");
                        return;
                    }

                    Affine stretch = Affine.Identity;
                    if (shape.Stretch != Stretch.None)
                    {
                        Rect raw = GeometryBounds(geometry);
                        stretch = StretchMatrix(raw, shape.Stretch, layoutW, layoutH, t);
                    }

                    rule = FlattenGeometry(geometry, stretch, tol, figures, 0);
                    shapeBox = BoundsOf(figures);
                    break;
                }
            }

            if (shapeBox == null) return;

            if (dashed && stroked)
                ReportOnceDashes();

            Rect drawClip = shape.Stretch == Stretch.UniformToFill ? Intersect(bounds, clip) : clip;

            // Layout -> device.
            var device = new Affine { M11 = scaleX, M22 = scaleY, Dx = bounds.X, Dy = bounds.Y };
            Rect brushArea = MapRect(shapeBox.Value, device);

            // Fill: every filled figure with at least three points, implicitly closed.
            if (shape.Fill != null && !(shape is Line))
            {
                var polys = new List<Point[]>();
                foreach (FlatFigure f in figures)
                {
                    if (!f.Filled || f.Points.Count < 3) continue;
                    polys.Add(MapPoints(f.Points, device));
                }

                if (polys.Count > 0)
                {
                    CoverageMask? mask = RasteriseToClip(polys, rule == FillRule.EvenOdd, surface, drawClip);
                    if (mask != null) CompositeCoverage(surface, mask, shape.Fill, brushArea, opacity);
                }
            }

            if (stroked)
            {
                List<FlatFigure> strokeFigures = dashed
                    ? Dash(figures, dashes!, shape.StrokeDashOffset, t)
                    : figures;

                PenLineCap startCap = dashed ? shape.StrokeDashCap : shape.StrokeStartLineCap;
                PenLineCap endCap = dashed ? shape.StrokeDashCap : shape.StrokeEndLineCap;

                var pieces = new List<Point[]>();
                foreach (FlatFigure f in strokeFigures)
                {
                    // The outer caps of a dashed open figure are still the line's own caps.
                    StrokeFigure(f, t / 2, shape.StrokeLineJoin, Math.Max(1, shape.StrokeMiterLimit),
                        startCap, endCap, tol, pieces);
                }

                if (pieces.Count > 0)
                {
                    for (int i = 0; i < pieces.Count; i++) pieces[i] = MapPoints(pieces[i], device);
                    CoverageMask? mask = RasteriseToClip(pieces, evenOdd: false, surface, drawClip);
                    if (mask != null)
                    {
                        Rect strokeArea = brushArea;
                        CompositeCoverage(surface, mask, shape.Stroke!, strokeArea, opacity);
                    }
                }
            }
        }

        private static void ReportOnceDashes()
        {
            // Dashes ARE drawn; this only records that a title uses them, which is worth knowing
            // when a dashed outline looks different from Silverlight's.
            ReportOnce("shape:StrokeDashArray", "dashed (dash phase approximate at figure joins)");
        }

        /// <summary>
        /// Where a <see cref="Rectangle"/> or <see cref="Ellipse"/> sits inside its render size:
        /// inset by half the stroke so the stroke lies inside the slot, squared for Uniform.
        /// </summary>
        private static Rect StretchedBox(Stretch stretch, double w, double h, double t)
        {
            switch (stretch)
            {
                case Stretch.Uniform:
                    w = h = Math.Min(w, h);
                    break;
                case Stretch.UniformToFill:
                    w = h = Math.Max(w, h);
                    break;
            }
            return new Rect(t / 2, t / 2, Math.Max(0, w - t), Math.Max(0, h - t));
        }

        /// <summary>
        /// The map a geometry shape's <c>Stretch</c> applies: the geometry's bounds onto the render
        /// size less the stroke, offset by half the stroke. The pen itself is not stretched.
        /// </summary>
        private static Affine StretchMatrix(Rect raw, Stretch stretch, double w, double h, double t)
        {
            double availW = Math.Max(0, w - t);
            double availH = Math.Max(0, h - t);
            double sx = raw.Width > 0 ? availW / raw.Width : 1;
            double sy = raw.Height > 0 ? availH / raw.Height : 1;

            switch (stretch)
            {
                case Stretch.Uniform:
                    sx = sy = Math.Min(raw.Width > 0 ? sx : sy, raw.Height > 0 ? sy : sx);
                    break;
                case Stretch.UniformToFill:
                    sx = sy = Math.Max(raw.Width > 0 ? sx : sy, raw.Height > 0 ? sy : sx);
                    break;
            }

            return new Affine
            {
                M11 = sx,
                M22 = sy,
                Dx = (t / 2) - (raw.X * sx),
                Dy = (t / 2) - (raw.Y * sy),
            };
        }

        private static Point[] MapPoints(List<Point> points, Affine m)
        {
            var result = new Point[points.Count];
            for (int i = 0; i < result.Length; i++) result[i] = Apply(m, points[i]);
            return result;
        }

        private static Point[] MapPoints(Point[] points, Affine m)
        {
            for (int i = 0; i < points.Length; i++) points[i] = Apply(m, points[i]);
            return points;
        }

        private static Rect MapRect(Rect r, Affine m)
        {
            Point a = Apply(m, new Point(r.X, r.Y));
            Point b = Apply(m, new Point(r.Right, r.Bottom));
            return new Rect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
        }

        // ---------------------------------------------------------------------------------
        // Stroking
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Turns one flattened figure into stroke pieces (each positively oriented) appended to
        /// <paramref name="into"/>.
        /// </summary>
        internal static void StrokeFigure(
            FlatFigure figure, double half, PenLineJoin join, double miterLimit,
            PenLineCap startCap, PenLineCap endCap, double tol, List<Point[]> into)
        {
            if (half <= 0) return;

            // Drop repeated points; a zero-length segment has no direction.
            var pts = new List<Point>(figure.Points.Count);
            foreach (Point p in figure.Points)
            {
                if (pts.Count == 0 || Dist(pts[pts.Count - 1], p) > 1e-9) pts.Add(p);
            }

            bool closed = figure.Closed;
            if (closed && pts.Count > 1 && Dist(pts[0], pts[pts.Count - 1]) <= 1e-9) pts.RemoveAt(pts.Count - 1);

            int n = pts.Count;
            if (n == 0) return;

            if (n == 1)
            {
                // A dot: only caps give it a size.
                Point p = pts[0];
                if (startCap == PenLineCap.Round) AddPiece(into, Circle(p, half, tol));
                else if (startCap == PenLineCap.Square)
                    AddPiece(into, new[] { new Point(p.X - half, p.Y - half), new Point(p.X + half, p.Y - half), new Point(p.X + half, p.Y + half), new Point(p.X - half, p.Y + half) });
                return;
            }

            if (n == 2) closed = false; // a two-point "closed" figure is a line drawn twice

            int segments = closed ? n : n - 1;
            for (int i = 0; i < segments; i++)
            {
                Point a = pts[i], b = pts[(i + 1) % n];
                Point nrm = Normal(a, b);
                AddPiece(into, new[]
                {
                    new Point(a.X + (nrm.X * half), a.Y + (nrm.Y * half)),
                    new Point(b.X + (nrm.X * half), b.Y + (nrm.Y * half)),
                    new Point(b.X - (nrm.X * half), b.Y - (nrm.Y * half)),
                    new Point(a.X - (nrm.X * half), a.Y - (nrm.Y * half)),
                });
            }

            int firstJoin = closed ? 0 : 1;
            int lastJoin = closed ? n - 1 : n - 2;
            for (int j = firstJoin; j <= lastJoin; j++)
            {
                Point prev = pts[(j - 1 + n) % n], at = pts[j], next = pts[(j + 1) % n];
                AddJoin(into, prev, at, next, half, join, miterLimit, tol);
            }

            if (!closed)
            {
                AddCap(into, pts[0], pts[1], half, startCap, tol);
                AddCap(into, pts[n - 1], pts[n - 2], half, endCap, tol);
            }
        }

        private static void AddJoin(
            List<Point[]> into, Point prev, Point at, Point next, double half, PenLineJoin join, double miterLimit, double tol)
        {
            Point n0 = Normal(prev, at), n1 = Normal(at, next);
            double d0x = at.X - prev.X, d0y = at.Y - prev.Y, d1x = next.X - at.X, d1y = next.Y - at.Y;
            double cross = (d0x * d1y) - (d0y * d1x);
            double dot = (n0.X * n1.X) + (n0.Y * n1.Y);
            if (Math.Abs(cross) < 1e-12 && dot > 0) return; // straight on: the segment quads already meet

            if (join == PenLineJoin.Round)
            {
                AddPiece(into, Circle(at, half, tol));
                return;
            }

            // The outer side is the one the offset lines diverge on.
            double s = cross > 0 ? -1 : 1;
            var a = new Point(at.X + (s * half * n0.X), at.Y + (s * half * n0.Y));
            var b = new Point(at.X + (s * half * n1.X), at.Y + (s * half * n1.Y));

            if (join == PenLineJoin.Miter && 1 + dot > 1e-9)
            {
                double k = half / (1 + dot);
                var miter = new Point(at.X + (s * k * (n0.X + n1.X)), at.Y + (s * k * (n0.Y + n1.Y)));
                if (Dist(miter, at) / half <= miterLimit)
                {
                    AddPiece(into, new[] { at, a, miter, b });
                    return;
                }
            }

            AddPiece(into, new[] { at, a, b }); // bevel, and a miter past its limit
        }

        /// <param name="end">The figure's end point.</param>
        /// <param name="inward">The neighbouring point; the cap extends away from it.</param>
        private static void AddCap(List<Point[]> into, Point end, Point inward, double half, PenLineCap cap, double tol)
        {
            if (cap == PenLineCap.Flat) return;

            double len = Dist(end, inward);
            if (len <= 0) return;
            double dx = (end.X - inward.X) / len, dy = (end.Y - inward.Y) / len; // outward
            double nx = -dy, ny = dx;

            switch (cap)
            {
                case PenLineCap.Round:
                    AddPiece(into, Circle(end, half, tol));
                    break;

                case PenLineCap.Square:
                    AddPiece(into, new[]
                    {
                        new Point(end.X + (nx * half), end.Y + (ny * half)),
                        new Point(end.X + (nx * half) + (dx * half), end.Y + (ny * half) + (dy * half)),
                        new Point(end.X - (nx * half) + (dx * half), end.Y - (ny * half) + (dy * half)),
                        new Point(end.X - (nx * half), end.Y - (ny * half)),
                    });
                    break;

                case PenLineCap.Triangle:
                    AddPiece(into, new[]
                    {
                        new Point(end.X + (nx * half), end.Y + (ny * half)),
                        new Point(end.X + (dx * half), end.Y + (dy * half)),
                        new Point(end.X - (nx * half), end.Y - (ny * half)),
                    });
                    break;
            }
        }

        private static Point[] Circle(Point c, double r, double tol)
        {
            // Segment count from the sagitta: the chord stays within tol of the arc.
            double step = r > tol ? 2 * Math.Acos(1 - (tol / r)) : Math.PI / 2;
            int n = Math.Clamp((int)Math.Ceiling(2 * Math.PI / step), 8, 256);
            var pts = new Point[n];
            for (int i = 0; i < n; i++)
            {
                double a = 2 * Math.PI * i / n;
                pts[i] = new Point(c.X + (r * Math.Cos(a)), c.Y + (r * Math.Sin(a)));
            }
            return pts;
        }

        /// <summary>Adds a piece with positive signed area; drops degenerate ones.</summary>
        private static void AddPiece(List<Point[]> into, Point[] piece)
        {
            double area = 0;
            for (int i = 0, j = piece.Length - 1; i < piece.Length; j = i++)
                area += (piece[j].X * piece[i].Y) - (piece[i].X * piece[j].Y);
            if (Math.Abs(area) < 1e-12) return;
            if (area < 0) Array.Reverse(piece);
            into.Add(piece);
        }

        private static Point Normal(Point a, Point b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len = Math.Sqrt((dx * dx) + (dy * dy));
            return len <= 0 ? new Point(0, 0) : new Point(-dy / len, dx / len);
        }

        private static double Dist(Point a, Point b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            return Math.Sqrt((dx * dx) + (dy * dy));
        }

        /// <summary>
        /// Splits figures into dashes. Lengths in <paramref name="pattern"/> are multiples of the
        /// stroke thickness, as in Silverlight; <paramref name="offset"/> likewise.
        /// </summary>
        private static List<FlatFigure> Dash(List<FlatFigure> figures, DoubleCollection pattern, double offset, double thickness)
        {
            var lengths = new List<double>();
            double total = 0;
            foreach (double d in pattern)
            {
                double len = Math.Max(0, d) * thickness;
                lengths.Add(len);
                total += len;
            }
            if (lengths.Count % 2 == 1) lengths.AddRange(lengths.ToArray()); // odd patterns repeat
            if (total <= 0) return figures;
            total = 0;
            foreach (double l in lengths) total += l;

            var result = new List<FlatFigure>();
            foreach (FlatFigure f in figures)
            {
                var pts = new List<Point>(f.Points);
                if (f.Closed && pts.Count > 1) pts.Add(pts[0]);
                if (pts.Count < 2) continue;

                // Phase: where in the pattern the figure starts.
                double phase = ((offset * thickness) % total + total) % total;
                int index = 0;
                while (phase >= lengths[index]) { phase -= lengths[index]; index = (index + 1) % lengths.Count; }
                double remaining = lengths[index] - phase;
                bool on = index % 2 == 0;

                FlatFigure? current = null;
                if (on)
                {
                    current = new FlatFigure { Closed = false, Filled = false };
                    current.Points.Add(pts[0]);
                }

                for (int i = 0; i + 1 < pts.Count; i++)
                {
                    Point a = pts[i], b = pts[i + 1];
                    double segLen = Dist(a, b);
                    double pos = 0;
                    int guard = 0;
                    while (segLen - pos > remaining && guard++ < 100000)
                    {
                        pos += remaining;
                        double tt = pos / segLen;
                        var p = new Point(a.X + ((b.X - a.X) * tt), a.Y + ((b.Y - a.Y) * tt));
                        if (on)
                        {
                            current!.Points.Add(p);
                            result.Add(current);
                            current = null;
                        }
                        else
                        {
                            current = new FlatFigure { Closed = false, Filled = false };
                            current.Points.Add(p);
                        }
                        on = !on;
                        index = (index + 1) % lengths.Count;
                        remaining = lengths[index];
                    }
                    remaining -= segLen - pos;
                    if (on) current!.Points.Add(b);
                }

                if (on && current != null && current.Points.Count > 1) result.Add(current);
            }
            return result;
        }

        // ---------------------------------------------------------------------------------
        // Coverage
        // ---------------------------------------------------------------------------------

        private static CoverageMask? RasteriseToClip(List<Point[]> polygons, bool evenOdd, Surface surface, Rect clip)
        {
            int cx0 = Math.Max(0, (int)Math.Round(clip.X));
            int cy0 = Math.Max(0, (int)Math.Round(clip.Y));
            int cx1 = Math.Min(surface.Width, (int)Math.Round(clip.Right));
            int cy1 = Math.Min(surface.Height, (int)Math.Round(clip.Bottom));
            return RasteriseCoverage(polygons, evenOdd, cx0, cy0, cx1, cy1);
        }

        private struct Edge
        {
            public double X0, Y0, Y1, Dxdy;
            public int Dir;
        }

        /// <summary>
        /// Anti-aliased coverage of <paramref name="polygons"/> (implicitly closed) inside the pixel
        /// rectangle [<paramref name="cx0"/>, <paramref name="cx1"/>) x [<paramref name="cy0"/>,
        /// <paramref name="cy1"/>). Null when nothing is covered.
        /// </summary>
        internal static CoverageMask? RasteriseCoverage(List<Point[]> polygons, bool evenOdd, int cx0, int cy0, int cx1, int cy1)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            var edges = new List<Edge>();

            foreach (Point[] poly in polygons)
            {
                if (poly.Length < 2) continue;
                for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
                {
                    Point a = poly[j], b = poly[i];
                    if (double.IsNaN(a.X) || double.IsNaN(a.Y) || double.IsNaN(b.X) || double.IsNaN(b.Y)) continue;
                    if (a.X < minX) minX = a.X;
                    if (a.X > maxX) maxX = a.X;
                    if (a.Y < minY) minY = a.Y;
                    if (a.Y > maxY) maxY = a.Y;
                    if (a.Y == b.Y) continue;

                    var e = new Edge();
                    if (a.Y < b.Y) { e.X0 = a.X; e.Y0 = a.Y; e.Y1 = b.Y; e.Dir = 1; e.Dxdy = (b.X - a.X) / (b.Y - a.Y); }
                    else { e.X0 = b.X; e.Y0 = b.Y; e.Y1 = a.Y; e.Dir = -1; e.Dxdy = (a.X - b.X) / (a.Y - b.Y); }
                    edges.Add(e);
                }
            }

            if (edges.Count == 0) return null;

            int x0 = Math.Max(cx0, (int)Math.Floor(minX));
            int x1 = Math.Min(cx1, (int)Math.Ceiling(maxX));
            int y0 = Math.Max(cy0, (int)Math.Floor(minY));
            int y1 = Math.Min(cy1, (int)Math.Ceiling(maxY));
            if (x1 <= x0 || y1 <= y0) return null;

            int w = x1 - x0, h = y1 - y0;
            var mask = new CoverageMask { X = x0, Y = y0, W = w, H = h, A = new float[w * h] };

            edges.Sort((p, q) => p.Y0.CompareTo(q.Y0));

            var active = new List<int>();
            var xs = new List<(double X, int Dir)>();
            var row = new float[w + 2];
            var full = new float[w + 2];
            int nextEdge = 0;
            const float weight = 1f / CoverageSubsamples;
            bool any = false;

            for (int py = y0; py < y1; py++)
            {
                Array.Clear(row, 0, row.Length);
                Array.Clear(full, 0, full.Length);
                bool rowAny = false;

                for (int s = 0; s < CoverageSubsamples; s++)
                {
                    double sy = py + ((s + 0.5) / CoverageSubsamples);

                    while (nextEdge < edges.Count && edges[nextEdge].Y0 <= sy) active.Add(nextEdge++);

                    xs.Clear();
                    for (int k = active.Count - 1; k >= 0; k--)
                    {
                        Edge e = edges[active[k]];
                        if (e.Y1 <= sy) { active.RemoveAt(k); continue; }
                        if (e.Y0 > sy) continue;
                        xs.Add((e.X0 + ((sy - e.Y0) * e.Dxdy), e.Dir));
                    }
                    if (xs.Count < 2) continue;
                    xs.Sort((p, q) => p.X.CompareTo(q.X));

                    int winding = 0;
                    double spanStart = 0;
                    for (int k = 0; k < xs.Count; k++)
                    {
                        bool wasIn = evenOdd ? (winding & 1) != 0 : winding != 0;
                        winding += xs[k].Dir;
                        bool isIn = evenOdd ? (winding & 1) != 0 : winding != 0;
                        if (!wasIn && isIn) spanStart = xs[k].X;
                        else if (wasIn && !isIn)
                        {
                            if (AddSpan(row, full, spanStart - x0, xs[k].X - x0, w, weight)) rowAny = true;
                        }
                    }
                }

                if (!rowAny) continue;

                float acc = 0;
                int baseIndex = (py - y0) * w;
                for (int x = 0; x < w; x++)
                {
                    acc += full[x];
                    float v = row[x] + acc;
                    if (v > 1f) v = 1f;
                    if (v > 0.0005f) { mask.A[baseIndex + x] = v; any = true; }
                }
            }

            return any ? mask : null;
        }

        /// <summary>Adds the exact horizontal coverage of [a, b) (mask-relative) to a row.</summary>
        private static bool AddSpan(float[] row, float[] full, double a, double b, int w, float weight)
        {
            if (a < 0) a = 0;
            if (b > w) b = w;
            if (b <= a) return false;

            int ia = (int)Math.Floor(a), ib = (int)Math.Floor(b);
            if (ia == ib)
            {
                row[ia] += (float)((b - a) * weight);
                return true;
            }

            row[ia] += (float)((ia + 1 - a) * weight);
            // Pixels strictly between are fully covered: a difference array keeps that O(1).
            full[ia + 1] += weight;
            full[ib] -= weight;
            if (ib < w) row[ib] += (float)((b - ib) * weight);
            return true;
        }

        /// <summary>
        /// Composites <paramref name="brush"/> through <paramref name="mask"/>. Solid colours go
        /// straight through; every other brush is painted by <see cref="FillBrush"/> into a scratch
        /// buffer the size of the mask, so gradients and image brushes come for free and stay
        /// relative to <paramref name="brushArea"/>.
        /// </summary>
        private static void CompositeCoverage(Surface surface, CoverageMask mask, Brush brush, Rect brushArea, double opacity)
        {
            uint[] px = surface.Pixels;
            int sw = surface.Width;

            uint solid = 0;
            uint[]? scratch = null;

            if (brush is SolidColorBrush scb)
            {
                double bo = brush.Opacity;
                if (bo <= 0) return;
                solid = Premultiply(scb.Color, opacity * Math.Min(1, bo));
                if ((solid >> 24) == 0) return;
            }
            else
            {
                scratch = new uint[mask.W * mask.H];
                var temp = new Surface(scratch, mask.W, mask.H);
                var shifted = new Rect(brushArea.X - mask.X, brushArea.Y - mask.Y, brushArea.Width, brushArea.Height);
                FillBrush(temp, brush, shifted, new Rect(0, 0, mask.W, mask.H), opacity);
                if (!temp.Painted) return;
            }

            for (int y = 0; y < mask.H; y++)
            {
                int dstRow = (mask.Y + y) * sw + mask.X;
                int srcRow = y * mask.W;
                for (int x = 0; x < mask.W; x++)
                {
                    float a = mask.A[srcRow + x];
                    if (a <= 0) continue;
                    uint src = scratch == null ? solid : scratch[srcRow + x];
                    if (src == 0) continue;

                    uint c = (uint)(a * 255f + 0.5f);
                    if (c < 255) src = ScaleColour(src, c);
                    px[dstRow + x] = Over(src, px[dstRow + x]);
                }
            }

            surface.Painted = true;
        }

        private static uint ScaleColour(uint premultiplied, uint scale)
            => Pack(
                Mul(premultiplied & 0xFF, scale),
                Mul((premultiplied >> 8) & 0xFF, scale),
                Mul((premultiplied >> 16) & 0xFF, scale),
                Mul(premultiplied >> 24, scale));

        // ---------------------------------------------------------------------------------
        // Change detection
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Everything about a shape that changes its pixels. Geometry is walked point by point: a
        /// storyboard can animate a segment or a figure's start point, and nothing else in the tree
        /// moves when it does.
        /// </summary>
        private static void SignShape(Shape shape, ref HashCode hash)
        {
            SignBrush(shape.Fill, ref hash);
            SignBrush(shape.Stroke, ref hash);
            hash.Add(shape.StrokeThickness);
            hash.Add((int)shape.Stretch);
            hash.Add((int)shape.StrokeLineJoin);
            hash.Add((int)shape.StrokeStartLineCap);
            hash.Add((int)shape.StrokeEndLineCap);
            hash.Add(shape.StrokeMiterLimit);
            DoubleCollection? dashes = shape.StrokeDashArray;
            if (dashes != null)
            {
                hash.Add(dashes.Count);
                foreach (double d in dashes) hash.Add(d);
                hash.Add(shape.StrokeDashOffset);
                hash.Add((int)shape.StrokeDashCap);
            }

            switch (shape)
            {
                case Rectangle rect:
                    hash.Add(rect.RadiusX);
                    hash.Add(rect.RadiusY);
                    break;
                case Line line:
                    hash.Add(line.X1); hash.Add(line.Y1); hash.Add(line.X2); hash.Add(line.Y2);
                    break;
                case Polyline polyline:
                    hash.Add((int)polyline.FillRule);
                    SignPoints(polyline.Points, ref hash);
                    break;
                case Polygon polygon:
                    hash.Add((int)polygon.FillRule);
                    SignPoints(polygon.Points, ref hash);
                    break;
                case Path path:
                    SignGeometry(path.Data, ref hash, 0);
                    break;
            }
        }

        private static void SignPoints(PointCollection? points, ref HashCode hash)
        {
            if (points == null) { hash.Add(-1); return; }
            hash.Add(points.Count);
            foreach (Point p in points) { hash.Add(p.X); hash.Add(p.Y); }
        }

        private static void SignGeometry(Geometry? geometry, ref HashCode hash, int depth)
        {
            if (geometry == null || depth > 16) { hash.Add(0); return; }
            hash.Add(geometry.GetType());

            if (geometry.Transform != null)
            {
                Affine m = Compose(geometry.Transform, 0);
                hash.Add(m.M11); hash.Add(m.M12); hash.Add(m.M21); hash.Add(m.M22); hash.Add(m.Dx); hash.Add(m.Dy);
            }

            switch (geometry)
            {
                case PathGeometry path:
                    hash.Add((int)path.FillRule);
                    foreach (PathFigure f in path.Figures)
                    {
                        if (f == null) continue;
                        hash.Add(f.StartPoint.X); hash.Add(f.StartPoint.Y);
                        hash.Add(f.IsClosed); hash.Add(f.IsFilled);
                        foreach (PathSegment s in f.Segments) SignSegment(s, ref hash);
                    }
                    break;
                case EllipseGeometry e:
                    hash.Add(e.Center.X); hash.Add(e.Center.Y); hash.Add(e.RadiusX); hash.Add(e.RadiusY);
                    break;
                case RectangleGeometry r:
                    hash.Add(r.Rect.X); hash.Add(r.Rect.Y); hash.Add(r.Rect.Width); hash.Add(r.Rect.Height);
                    hash.Add(r.RadiusX); hash.Add(r.RadiusY);
                    break;
                case LineGeometry l:
                    hash.Add(l.StartPoint.X); hash.Add(l.StartPoint.Y); hash.Add(l.EndPoint.X); hash.Add(l.EndPoint.Y);
                    break;
                case GeometryGroup g:
                    hash.Add((int)g.FillRule);
                    foreach (Geometry child in g.Children) SignGeometry(child, ref hash, depth + 1);
                    break;
            }
        }

        private static void SignSegment(PathSegment? segment, ref HashCode hash)
        {
            switch (segment)
            {
                case LineSegment l: hash.Add(1); hash.Add(l.Point.X); hash.Add(l.Point.Y); break;
                case BezierSegment b:
                    hash.Add(2);
                    hash.Add(b.Point1.X); hash.Add(b.Point1.Y); hash.Add(b.Point2.X); hash.Add(b.Point2.Y); hash.Add(b.Point3.X); hash.Add(b.Point3.Y);
                    break;
                case QuadraticBezierSegment q:
                    hash.Add(3); hash.Add(q.Point1.X); hash.Add(q.Point1.Y); hash.Add(q.Point2.X); hash.Add(q.Point2.Y);
                    break;
                case ArcSegment a:
                    hash.Add(4); hash.Add(a.Point.X); hash.Add(a.Point.Y); hash.Add(a.Size.Width); hash.Add(a.Size.Height);
                    hash.Add(a.RotationAngle); hash.Add(a.IsLargeArc); hash.Add((int)a.SweepDirection);
                    break;
                case PolyLineSegment pl: hash.Add(5); SignPoints(pl.Points, ref hash); break;
                case PolyBezierSegment pb: hash.Add(6); SignPoints(pb.Points, ref hash); break;
                case PolyQuadraticBezierSegment pq: hash.Add(7); SignPoints(pq.Points, ref hash); break;
                default: hash.Add(0); break;
            }
        }
    }
}
