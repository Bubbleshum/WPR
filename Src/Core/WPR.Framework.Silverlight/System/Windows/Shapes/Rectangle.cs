// Minimal shims for the System.Windows.Shapes namespace.
//
// Real Silverlight defines a Shape base with rich path / geometry support; here
// we only need the type tokens to resolve (so user IL referencing
// System.Windows.Shapes.Rectangle / Shape JITs) and the Fill / Stroke / etc.
// setters to round-trip. The renderer doesn't currently paint primitive shapes.

using System;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.Shapes.Rectangle</c>. UserRank uses it as a
    /// colored badge — code paints the shape via <see cref="Shape.Fill"/> /
    /// <see cref="UIElement.OpacityMask"/>. Renderer doesn't paint shapes yet;
    /// the type exists so JIT'd code that references it loads cleanly.
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
