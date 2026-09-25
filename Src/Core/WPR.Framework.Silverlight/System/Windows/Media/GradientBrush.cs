using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Media.GradientSpreadMethod</c>.</summary>
    public enum GradientSpreadMethod
    {
        /// <summary>Hold the end colours beyond the gradient's extent. Silverlight's default.</summary>
        Pad,

        /// <summary>Mirror the ramp on each repeat.</summary>
        Reflect,

        /// <summary>Restart the ramp on each repeat.</summary>
        Repeat,
    }

    /// <summary>Shim for <c>System.Windows.Media.BrushMappingMode</c>.</summary>
    public enum BrushMappingMode
    {
        /// <summary>Coordinates are absolute, in the painted element's own units.</summary>
        Absolute,

        /// <summary>Coordinates are 0..1 fractions of the painted area. Silverlight's default.</summary>
        RelativeToBoundingBox,
    }

    /// <summary>Shim for <c>System.Windows.Media.GradientBrush</c>.</summary>
    /// <remarks>
    /// Carries the stops and the spread/mapping settings its two concrete subclasses share. Both
    /// are painted by <c>SoftwareVisualRasteriser</c> and by <c>SilverlightRenderer</c>.
    /// <para>This type existed before anything drew a gradient, for a reason worth keeping in
    /// mind: the XAML reader resolves types by name, so a brush it cannot find throws out of the
    /// element declaring it and takes every child of that element with it. A brush that does not
    /// paint costs one fill; a brush that does not exist costs a subtree.</para>
    /// </remarks>
    [ContentProperty(nameof(GradientStops))]
    public abstract class GradientBrush : Brush
    {
        public GradientStopCollection GradientStops { get; } = new GradientStopCollection();

        public GradientSpreadMethod SpreadMethod { get; set; } = GradientSpreadMethod.Pad;

        public BrushMappingMode MappingMode { get; set; } = BrushMappingMode.RelativeToBoundingBox;
    }

    /// <summary>Shim for <c>System.Windows.Media.GradientStop</c>.</summary>
    public class GradientStop : DependencyObject
    {
        public static readonly DependencyProperty ColorProperty =
            DependencyProperty.Register(nameof(Color), typeof(Color), typeof(GradientStop),
                new PropertyMetadata(default(Color)));

        public static readonly DependencyProperty OffsetProperty =
            DependencyProperty.Register(nameof(Offset), typeof(double), typeof(GradientStop),
                new PropertyMetadata((object)0.0));

        public Color Color
        {
            get => (Color)GetValue(ColorProperty)!;
            set => SetValue(ColorProperty, value);
        }

        public double Offset
        {
            get => (double)GetValue(OffsetProperty)!;
            set => SetValue(OffsetProperty, value);
        }
    }

    /// <summary>Shim for <c>System.Windows.Media.GradientStopCollection</c>.</summary>
    public class GradientStopCollection : List<GradientStop> { }

    /// <summary>Shim for <c>System.Windows.Media.LinearGradientBrush</c>.</summary>
    /// <remarks>See <see cref="GradientBrush"/> for why it exists despite painting nothing.</remarks>
    public class LinearGradientBrush : GradientBrush
    {
        public static readonly DependencyProperty StartPointProperty =
            DependencyProperty.Register(nameof(StartPoint), typeof(Point), typeof(LinearGradientBrush),
                new PropertyMetadata(new Point(0, 0)));

        public static readonly DependencyProperty EndPointProperty =
            DependencyProperty.Register(nameof(EndPoint), typeof(Point), typeof(LinearGradientBrush),
                new PropertyMetadata(new Point(1, 1)));

        public Point StartPoint
        {
            get => (Point)GetValue(StartPointProperty)!;
            set => SetValue(StartPointProperty, value);
        }

        public Point EndPoint
        {
            get => (Point)GetValue(EndPointProperty)!;
            set => SetValue(EndPointProperty, value);
        }
    }
}
