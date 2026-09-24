namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Media.RadialGradientBrush</c>.</summary>
    /// <remarks>
    /// <para>Holds its geometry and paints nothing, like the other gradient brushes —
    /// the software rasteriser does not draw gradients and the Avalonia renderer converts only
    /// the brushes it knows.</para>
    ///
    /// <para><b>It exists because a missing brush costs a whole subtree, not one fill.</b> The
    /// XAML reader resolves types by name, so a <c>RadialGradientBrush</c> it cannot find throws
    /// out of the element that declares it and every child of that element is skipped. Sid
    /// Meier's Pirates! sets one as a Button's background, which cost the button rather than its
    /// colour.</para>
    /// </remarks>
    public class RadialGradientBrush : GradientBrush
    {
        public static readonly DependencyProperty CenterProperty =
            DependencyProperty.Register(nameof(Center), typeof(Point), typeof(RadialGradientBrush),
                new PropertyMetadata(new Point(0.5, 0.5)));

        public static readonly DependencyProperty GradientOriginProperty =
            DependencyProperty.Register(nameof(GradientOrigin), typeof(Point), typeof(RadialGradientBrush),
                new PropertyMetadata(new Point(0.5, 0.5)));

        public static readonly DependencyProperty RadiusXProperty =
            DependencyProperty.Register(nameof(RadiusX), typeof(double), typeof(RadialGradientBrush),
                new PropertyMetadata((object)0.5));

        public static readonly DependencyProperty RadiusYProperty =
            DependencyProperty.Register(nameof(RadiusY), typeof(double), typeof(RadialGradientBrush),
                new PropertyMetadata((object)0.5));

        public Point Center
        {
            get => (Point)GetValue(CenterProperty)!;
            set => SetValue(CenterProperty, value);
        }

        public Point GradientOrigin
        {
            get => (Point)GetValue(GradientOriginProperty)!;
            set => SetValue(GradientOriginProperty, value);
        }

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
