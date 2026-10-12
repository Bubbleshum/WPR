using System;
using System.Collections.Generic;
using Xunit;

namespace WPR.SilverlightCompability.Tests
{
    /// <summary>
    /// Shape geometry in the CPU rasteriser: the path mini-language, coverage, fill rules, stroke
    /// width and caps, Stretch, and change detection.
    /// </summary>
    public class ShapeRasterTests
    {
        private const string Ns =
            "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
            "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";

        // ---- helpers ------------------------------------------------------------------------

        private static uint[] Render(UIElement root, int w, int h)
        {
            root.Measure(new Size(w, h));
            root.Arrange(new Rect(0, 0, w, h));
            var px = new uint[w * h];
            SoftwareVisualRasteriser.Paint(root, w, h, px);
            return px;
        }

        private static Canvas CanvasOf(params UIElement[] children)
        {
            var c = new Canvas();
            foreach (UIElement e in children) c.Children.Add(e);
            return c;
        }

        private static double AlphaSum(uint[] px)
        {
            double sum = 0;
            foreach (uint p in px) sum += (p >> 24) / 255.0;
            return sum;
        }

        private static byte Alpha(uint[] px, int w, int x, int y) => (byte)(px[(y * w) + x] >> 24);

        private static SolidColorBrush Red => new SolidColorBrush(Color.FromArgb(255, 255, 0, 0));

        private static Point[] Square(double x0, double y0, double x1, double y1, bool clockwise = true)
            => clockwise
                ? new[] { new Point(x0, y0), new Point(x1, y0), new Point(x1, y1), new Point(x0, y1) }
                : new[] { new Point(x0, y0), new Point(x0, y1), new Point(x1, y1), new Point(x1, y0) };

        // ---- coverage -----------------------------------------------------------------------

        [Fact]
        public void Coverage_FractionalEdges_AreExactHorizontally()
        {
            var mask = SoftwareVisualRasteriser.RasteriseCoverage(
                new List<Point[]> { Square(10.25, 5, 20.75, 15) }, evenOdd: false, 0, 0, 64, 64)!;

            Assert.Equal(0.75, mask.At(10, 8), 3);
            Assert.Equal(1.0, mask.At(15, 8), 3);
            Assert.Equal(0.75, mask.At(20, 8), 3);
            Assert.Equal(0.0, mask.At(21, 8), 3);
            Assert.Equal(0.0, mask.At(15, 15), 3);
        }

        [Fact]
        public void Coverage_FractionalEdges_AreQuantisedVertically()
        {
            var mask = SoftwareVisualRasteriser.RasteriseCoverage(
                new List<Point[]> { Square(0, 2.5, 10, 6) }, evenOdd: false, 0, 0, 64, 64)!;

            Assert.Equal(0.5, mask.At(5, 2), 2);
            Assert.Equal(1.0, mask.At(5, 3), 3);
        }

        [Fact]
        public void FillRule_EvenOdd_PunchesAHole_Nonzero_DoesNot()
        {
            // Both squares wound the same way: Nonzero fills the inner one, EvenOdd leaves it empty.
            var polys = new List<Point[]> { Square(0, 0, 40, 40), Square(10, 10, 30, 30) };

            var evenOdd = SoftwareVisualRasteriser.RasteriseCoverage(polys, true, 0, 0, 64, 64)!;
            var nonzero = SoftwareVisualRasteriser.RasteriseCoverage(polys, false, 0, 0, 64, 64)!;

            Assert.Equal(0.0, evenOdd.At(20, 20), 3);
            Assert.Equal(1.0, evenOdd.At(5, 5), 3);
            Assert.Equal(1.0, nonzero.At(20, 20), 3);
        }

        [Fact]
        public void FillRule_Nonzero_OppositeWinding_IsAHole()
        {
            var polys = new List<Point[]> { Square(0, 0, 40, 40), Square(10, 10, 30, 30, clockwise: false) };
            var nonzero = SoftwareVisualRasteriser.RasteriseCoverage(polys, false, 0, 0, 64, 64)!;
            Assert.Equal(0.0, nonzero.At(20, 20), 3);
        }

        [Fact]
        public void Ellipse_AreaMatchesPiRSquared()
        {
            var e = new Ellipse { Width = 100, Height = 100, Fill = Red };
            Canvas.SetLeft(e, 10.3);
            Canvas.SetTop(e, 7.6);
            uint[] px = Render(CanvasOf(e), 128, 128);

            double expected = Math.PI * 50 * 50;
            Assert.InRange(AlphaSum(px), expected * 0.995, expected * 1.005);
        }

        [Fact]
        public void RoundedRectangle_CornersAreCut_AndCentreIsFull()
        {
            var r = new Rectangle { Width = 80, Height = 60, RadiusX = 20, RadiusY = 20, Fill = Red };
            uint[] px = Render(CanvasOf(r), 100, 100);

            Assert.Equal(0, Alpha(px, 100, 1, 1));
            Assert.Equal(255, Alpha(px, 100, 40, 30));
            Assert.Equal(255, Alpha(px, 100, 40, 0)); // top edge, between the corners
            double expected = (80 * 60) - ((4 - Math.PI) * 20 * 20);
            Assert.InRange(AlphaSum(px), expected * 0.995, expected * 1.005);
        }

        // ---- strokes ------------------------------------------------------------------------

        [Fact]
        public void Line_StrokeThickness_IsHonoured_AndFlatCapsStopAtTheEnds()
        {
            var line = new Line { X1 = 10, Y1 = 20, X2 = 50, Y2 = 20, Stroke = Red, StrokeThickness = 4 };
            uint[] px = Render(CanvasOf(line), 64, 64);

            double column = 0;
            for (int y = 0; y < 64; y++) column += Alpha(px, 64, 30, y) / 255.0;
            Assert.Equal(4.0, column, 1);
            Assert.Equal(0, Alpha(px, 64, 8, 20));
            Assert.Equal(0, Alpha(px, 64, 51, 20));
            Assert.Equal(40 * 4, AlphaSum(px), 0);
        }

        [Fact]
        public void Line_SquareCaps_ExtendByHalfTheThickness()
        {
            var line = new Line
            {
                X1 = 10, Y1 = 20, X2 = 50, Y2 = 20, Stroke = Red, StrokeThickness = 4,
                StrokeStartLineCap = PenLineCap.Square, StrokeEndLineCap = PenLineCap.Square,
            };
            uint[] px = Render(CanvasOf(line), 64, 64);

            Assert.Equal(255, Alpha(px, 64, 8, 20));
            Assert.Equal(255, Alpha(px, 64, 51, 20));
            Assert.Equal(44 * 4, AlphaSum(px), 0);
        }

        [Fact]
        public void Polyline_TranslucentStroke_DoesNotDoubleBlendAtJoins()
        {
            var brush = new SolidColorBrush(Color.FromArgb(128, 0, 0, 255));
            var pl = new Polyline { Stroke = brush, StrokeThickness = 10, StrokeLineJoin = PenLineJoin.Round };
            pl.Points.Add(new Point(10, 10));
            pl.Points.Add(new Point(50, 10));
            pl.Points.Add(new Point(50, 50));
            uint[] px = Render(CanvasOf(pl), 64, 64);

            // At the join several pieces overlap; coverage is a union, so alpha stays at the brush's.
            Assert.Equal(128, Alpha(px, 64, 50, 10));
            Assert.Equal(128, Alpha(px, 64, 30, 10));
        }

        [Fact]
        public void MiterJoin_FillsTheCorner_BevelDoesNot()
        {
            Polyline Make(PenLineJoin j)
            {
                var pl = new Polyline { Stroke = Red, StrokeThickness = 10, StrokeLineJoin = j };
                pl.Points.Add(new Point(10, 20));
                pl.Points.Add(new Point(40, 20));
                pl.Points.Add(new Point(40, 50));
                return pl;
            }

            uint[] miter = Render(CanvasOf(Make(PenLineJoin.Miter)), 64, 64);
            uint[] bevel = Render(CanvasOf(Make(PenLineJoin.Bevel)), 64, 64);

            // The outer corner of the turn is at (45, 15).
            Assert.Equal(255, Alpha(miter, 64, 44, 15));
            Assert.Equal(0, Alpha(bevel, 64, 44, 15));
        }

        // ---- paths, XAML and stretch -------------------------------------------------------

        [Fact]
        public void PathMarkup_ParsesFillRuleImplicitCommandsAndPackedNumbers()
        {
            PathGeometry g = PathMarkupParser.Parse("F1 M0,0 L10,0 10,10Z m5-5.5.5.5 h2 v-1 c1,1 2,2 3,3 A5,5 0 0110,10 z");

            Assert.Equal(FillRule.Nonzero, g.FillRule);
            Assert.Equal(2, g.Figures.Count);

            PathFigure first = g.Figures[0];
            Assert.True(first.IsClosed);
            Assert.Equal(2, first.Segments.Count);
            Assert.Equal(new Point(10, 10), ((LineSegment)first.Segments[1]).Point);

            PathFigure second = g.Figures[1];
            // Relative move from the closed figure's start (0,0), then an implicit relative line-to.
            Assert.Equal(new Point(5, -5.5), second.StartPoint);
            Assert.Equal(new Point(5.5, -5), ((LineSegment)second.Segments[0]).Point);
            var arc = Assert.IsType<ArcSegment>(second.Segments[4]);
            Assert.False(arc.IsLargeArc);
            Assert.Equal(SweepDirection.Clockwise, arc.SweepDirection);
            Assert.Equal(new Point(10, 10), arc.Point);
            Assert.True(second.IsClosed);
        }

        [Fact]
        public void PathMarkup_DefaultsToEvenOdd()
        {
            Assert.Equal(FillRule.EvenOdd, PathMarkupParser.Parse("M0,0 L1,1").FillRule);
        }

        [Fact]
        public void Xaml_PathData_AndPolygonPoints_Load()
        {
            var canvas = (Canvas)XamlReader.Load(
                $"<Canvas {Ns}>" +
                "<Path Fill='Red' Data='M 0,0 L 20,0 L 20,20 Z'/>" +
                "<Polygon Fill='Red' Points='30,0 50,0 50,20'/>" +
                "<Path Stroke='Red'><Path.Data><EllipseGeometry Center='10,40' RadiusX='5' RadiusY='5'/></Path.Data></Path>" +
                "</Canvas>");

            var path = Assert.IsType<Path>(canvas.Children[0]);
            Assert.IsType<PathGeometry>(path.Data);
            var polygon = Assert.IsType<Polygon>(canvas.Children[1]);
            Assert.Equal(3, polygon.Points.Count);
            Assert.IsType<EllipseGeometry>(((Path)canvas.Children[2]).Data);

            uint[] px = Render(canvas, 64, 64);
            // Two 200-pixel triangles plus a 1-px ring of radius 5 (about 31 pixels).
            Assert.InRange(AlphaSum(px), 425, 440);
        }

        [Fact]
        public void Arc_Semicircle_HasHalfTheDiscArea()
        {
            var path = new Path { Fill = Red, Data = PathMarkupParser.Parse("M 10,30 A 20,20 0 0 1 50,30 Z") };
            uint[] px = Render(CanvasOf(path), 64, 64);
            double expected = Math.PI * 20 * 20 / 2;
            Assert.InRange(AlphaSum(px), expected * 0.99, expected * 1.01);
            Assert.Equal(255, Alpha(px, 64, 30, 20)); // clockwise from left to right: the upper half
            Assert.Equal(0, Alpha(px, 64, 30, 40));
        }

        [Fact]
        public void Stretch_Fill_MapsGeometryOntoTheSlot()
        {
            var path = new Path
            {
                Width = 40, Height = 20, Stretch = Stretch.Fill, Fill = Red,
                Data = PathMarkupParser.Parse("M 100,100 L 200,100 L 200,200 L 100,200 Z"),
            };
            Canvas.SetLeft(path, 5);
            Canvas.SetTop(path, 5);
            uint[] px = Render(CanvasOf(path), 64, 64);

            Assert.Equal(40 * 20, AlphaSum(px), 0);
            Assert.Equal(255, Alpha(px, 64, 5, 5));
            Assert.Equal(0, Alpha(px, 64, 45, 5));
        }

        [Fact]
        public void Stretch_None_PathMeasuresToItsFarCorner()
        {
            var path = new Path { Data = PathMarkupParser.Parse("M 10,10 L 30,10 L 30,25 Z"), Fill = Red };
            path.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.Equal(new Size(30, 25), path.DesiredSize);
        }

        [Fact]
        public void GeometryGroup_EvenOdd_MakesARing()
        {
            var group = new GeometryGroup();
            group.Children.Add(new EllipseGeometry { Center = new Point(32, 32), RadiusX = 30, RadiusY = 30 });
            group.Children.Add(new EllipseGeometry { Center = new Point(32, 32), RadiusX = 15, RadiusY = 15 });
            uint[] px = Render(CanvasOf(new Path { Fill = Red, Data = group }), 64, 64);

            Assert.Equal(0, Alpha(px, 64, 32, 32));
            Assert.Equal(255, Alpha(px, 64, 32, 10));
            double expected = Math.PI * ((30 * 30) - (15 * 15));
            Assert.InRange(AlphaSum(px), expected * 0.99, expected * 1.01);
        }

        [Fact]
        public void Geometry_Bounds_AreTight_AndIncludeTheTransform()
        {
            var e = new EllipseGeometry
            {
                Center = new Point(10, 10), RadiusX = 5, RadiusY = 3,
                Transform = new TranslateTransform { X = 100 },
            };
            Rect b = e.Bounds;
            Assert.Equal(105, b.X, 3);
            Assert.Equal(7, b.Y, 3);
            Assert.Equal(10, b.Width, 3);
            Assert.Equal(6, b.Height, 3);
        }

        // ---- change detection ---------------------------------------------------------------

        [Fact]
        public void Signature_ChangesWhenGeometryMoves()
        {
            var segment = new LineSegment { Point = new Point(20, 0) };
            var figure = new PathFigure { StartPoint = new Point(0, 0), IsClosed = true };
            figure.Segments.Add(segment);
            figure.Segments.Add(new LineSegment { Point = new Point(20, 20) });
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            var canvas = CanvasOf(new Path { Fill = Red, Data = geometry });
            Render(canvas, 64, 64);

            int before = SoftwareVisualRasteriser.Signature(canvas);
            segment.Point = new Point(30, 0);
            Assert.NotEqual(before, SoftwareVisualRasteriser.Signature(canvas));
        }

        [Fact]
        public void Signature_ChangesWithStrokeStyle()
        {
            var line = new Line { X2 = 20, Stroke = Red, StrokeThickness = 3 };
            var canvas = CanvasOf(line);
            Render(canvas, 32, 32);
            int before = SoftwareVisualRasteriser.Signature(canvas);
            line.StrokeEndLineCap = PenLineCap.Round;
            Assert.NotEqual(before, SoftwareVisualRasteriser.Signature(canvas));
        }
    }
}
