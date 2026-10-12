using System;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.Shapes.Shape</c>. Common base for the primitive vector shapes
    /// (<see cref="Rectangle"/>, <see cref="Ellipse"/>, <see cref="Line"/>, <see cref="Polyline"/>,
    /// <see cref="Polygon"/>, <see cref="Path"/>).
    /// </summary>
    /// <remarks>
    /// <para>Painted by <c>SoftwareVisualRasteriser</c> (Shapes partial), which builds the outline
    /// through <see cref="ShapeGeometry"/>. The Avalonia <c>SilverlightRenderer</c> still draws only
    /// rectangles; every other shape is its bounding box there.</para>
    ///
    /// <para><b>Measuring.</b> <see cref="Rectangle"/> and <see cref="Ellipse"/> have no natural
    /// size (they fill the slot they are given), so they measure as zero exactly as they did before
    /// shapes were drawn. The geometry shapes measure as their geometry: with <c>Stretch=None</c>
    /// as the box from the origin to the geometry's far corner, otherwise as the geometry's own
    /// size scaled into the available space — the WPF/Silverlight rule.</para>
    /// </remarks>
    public abstract class Shape : FrameworkElement
    {
        public static readonly DependencyProperty FillProperty =
            DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(Shape),
                new PropertyMetadata((object?)null));

        public static readonly DependencyProperty StrokeProperty =
            DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(Shape),
                new PropertyMetadata((object?)null));

        public static readonly DependencyProperty StrokeThicknessProperty =
            DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(Shape),
                new PropertyMetadata((object)1.0));

        public static readonly DependencyProperty StretchProperty =
            DependencyProperty.Register(nameof(Stretch), typeof(Stretch), typeof(Shape),
                new PropertyMetadata(Stretch.None));

        public static readonly DependencyProperty StrokeLineJoinProperty =
            DependencyProperty.Register(nameof(StrokeLineJoin), typeof(PenLineJoin), typeof(Shape),
                new PropertyMetadata(PenLineJoin.Miter));

        public static readonly DependencyProperty StrokeStartLineCapProperty =
            DependencyProperty.Register(nameof(StrokeStartLineCap), typeof(PenLineCap), typeof(Shape),
                new PropertyMetadata(PenLineCap.Flat));

        public static readonly DependencyProperty StrokeEndLineCapProperty =
            DependencyProperty.Register(nameof(StrokeEndLineCap), typeof(PenLineCap), typeof(Shape),
                new PropertyMetadata(PenLineCap.Flat));

        public static readonly DependencyProperty StrokeDashCapProperty =
            DependencyProperty.Register(nameof(StrokeDashCap), typeof(PenLineCap), typeof(Shape),
                new PropertyMetadata(PenLineCap.Flat));

        public static readonly DependencyProperty StrokeMiterLimitProperty =
            DependencyProperty.Register(nameof(StrokeMiterLimit), typeof(double), typeof(Shape),
                new PropertyMetadata((object)10.0));

        public static readonly DependencyProperty StrokeDashOffsetProperty =
            DependencyProperty.Register(nameof(StrokeDashOffset), typeof(double), typeof(Shape),
                new PropertyMetadata((object)0.0));

        public static readonly DependencyProperty StrokeDashArrayProperty =
            DependencyProperty.Register(nameof(StrokeDashArray), typeof(DoubleCollection), typeof(Shape),
                new PropertyMetadata((object?)null));

        public Brush? Fill
        {
            get => (Brush?)GetValue(FillProperty);
            set => SetValue(FillProperty, value);
        }

        public Brush? Stroke
        {
            get => (Brush?)GetValue(StrokeProperty);
            set => SetValue(StrokeProperty, value);
        }

        public double StrokeThickness
        {
            get => (double)GetValue(StrokeThicknessProperty)!;
            set => SetValue(StrokeThicknessProperty, value);
        }

        public Stretch Stretch
        {
            get => (Stretch)GetValue(StretchProperty)!;
            set => SetValue(StretchProperty, value);
        }

        public PenLineJoin StrokeLineJoin
        {
            get => (PenLineJoin)GetValue(StrokeLineJoinProperty)!;
            set => SetValue(StrokeLineJoinProperty, value);
        }

        public PenLineCap StrokeStartLineCap
        {
            get => (PenLineCap)GetValue(StrokeStartLineCapProperty)!;
            set => SetValue(StrokeStartLineCapProperty, value);
        }

        public PenLineCap StrokeEndLineCap
        {
            get => (PenLineCap)GetValue(StrokeEndLineCapProperty)!;
            set => SetValue(StrokeEndLineCapProperty, value);
        }

        public PenLineCap StrokeDashCap
        {
            get => (PenLineCap)GetValue(StrokeDashCapProperty)!;
            set => SetValue(StrokeDashCapProperty, value);
        }

        public double StrokeMiterLimit
        {
            get => (double)GetValue(StrokeMiterLimitProperty)!;
            set => SetValue(StrokeMiterLimitProperty, value);
        }

        public double StrokeDashOffset
        {
            get => (double)GetValue(StrokeDashOffsetProperty)!;
            set => SetValue(StrokeDashOffsetProperty, value);
        }

        public DoubleCollection? StrokeDashArray
        {
            get => (DoubleCollection?)GetValue(StrokeDashArrayProperty);
            set => SetValue(StrokeDashArrayProperty, value);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            Geometry? geometry = ShapeGeometry.GetDefiningGeometry(this);
            if (geometry == null) return Size.Empty; // Rectangle / Ellipse: no natural size

            Rect b = geometry.Bounds;
            double t = Stroke != null ? Math.Max(0, StrokeThickness) : 0;

            if (Stretch == Stretch.None)
            {
                return new Size(Math.Max(0, b.Right + (t / 2)), Math.Max(0, b.Bottom + (t / 2)));
            }

            double nw = b.Width + t;
            double nh = b.Height + t;
            bool fw = !double.IsInfinity(availableSize.Width) && !double.IsNaN(availableSize.Width);
            bool fh = !double.IsInfinity(availableSize.Height) && !double.IsNaN(availableSize.Height);

            switch (Stretch)
            {
                case Stretch.Fill:
                    return new Size(fw ? availableSize.Width : nw, fh ? availableSize.Height : nh);

                default:
                {
                    double sx = fw && nw > 0 ? availableSize.Width / nw : double.NaN;
                    double sy = fh && nh > 0 ? availableSize.Height / nh : double.NaN;
                    double s;
                    if (double.IsNaN(sx) && double.IsNaN(sy)) s = 1;
                    else if (double.IsNaN(sx)) s = sy;
                    else if (double.IsNaN(sy)) s = sx;
                    else s = Stretch == Stretch.Uniform ? Math.Min(sx, sy) : Math.Max(sx, sy);
                    return new Size(nw * s, nh * s);
                }
            }
        }
    }
}
