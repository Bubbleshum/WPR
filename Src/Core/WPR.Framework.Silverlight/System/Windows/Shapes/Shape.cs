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
    /// Shim for <c>System.Windows.Shapes.Shape</c>. Common base for primitive
    /// vector shapes (<see cref="Rectangle"/>, etc.). Surface area limited to the
    /// brushes WP7 code routinely sets — <see cref="Fill"/>, <see cref="Stroke"/>,
    /// <see cref="StrokeThickness"/> — plus a no-op <c>Stretch</c> that XAML often
    /// specifies.
    /// </summary>
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
    }
}
