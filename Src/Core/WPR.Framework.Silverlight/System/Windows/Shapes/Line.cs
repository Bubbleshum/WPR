using System;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Shapes.Line</c>.</summary>
    public class Line : Shape
    {
        public static readonly DependencyProperty X1Property =
            DependencyProperty.Register(nameof(X1), typeof(double), typeof(Line),
                new PropertyMetadata((object)0.0));

        public static readonly DependencyProperty Y1Property =
            DependencyProperty.Register(nameof(Y1), typeof(double), typeof(Line),
                new PropertyMetadata((object)0.0));

        public static readonly DependencyProperty X2Property =
            DependencyProperty.Register(nameof(X2), typeof(double), typeof(Line),
                new PropertyMetadata((object)0.0));

        public static readonly DependencyProperty Y2Property =
            DependencyProperty.Register(nameof(Y2), typeof(double), typeof(Line),
                new PropertyMetadata((object)0.0));

        public double X1
        {
            get => (double)GetValue(X1Property)!;
            set => SetValue(X1Property, value);
        }

        public double Y1
        {
            get => (double)GetValue(Y1Property)!;
            set => SetValue(Y1Property, value);
        }

        public double X2
        {
            get => (double)GetValue(X2Property)!;
            set => SetValue(X2Property, value);
        }

        public double Y2
        {
            get => (double)GetValue(Y2Property)!;
            set => SetValue(Y2Property, value);
        }
    }
}
