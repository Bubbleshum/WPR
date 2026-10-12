using System;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.Shapes.Rectangle</c>. Fills its slot, inset by half the
    /// stroke; <see cref="RadiusX"/>/<see cref="RadiusY"/> round the corners (clamped to half a
    /// side). Painted by <c>SoftwareVisualRasteriser</c>: a square-cornered one on the exact
    /// pixel path it always had, a rounded one through the anti-aliased shape path.
    /// </summary>
    public class Rectangle : Shape
    {
        public static readonly DependencyProperty RadiusXProperty =
            DependencyProperty.Register(nameof(RadiusX), typeof(double), typeof(Rectangle),
                new PropertyMetadata((object)0.0));

        public static readonly DependencyProperty RadiusYProperty =
            DependencyProperty.Register(nameof(RadiusY), typeof(double), typeof(Rectangle),
                new PropertyMetadata((object)0.0));

        public double RadiusX
        {
            get => (double)GetValue(RadiusXProperty)!;
            set => SetValue(RadiusXProperty, value);
        }

        public double RadiusY
        {
            get => (double)GetValue(RadiusYProperty)!;
            set => SetValue(RadiusYProperty, value);
        }
    }
}
