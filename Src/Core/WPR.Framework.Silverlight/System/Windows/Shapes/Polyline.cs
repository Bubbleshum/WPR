using System;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Shapes.Polyline</c> (open; a Fill still fills it as if closed).</summary>
    public class Polyline : Shape
    {
        public static readonly DependencyProperty PointsProperty =
            DependencyProperty.Register(nameof(Points), typeof(PointCollection), typeof(Polyline),
                new PropertyMetadata((object?)null));

        public static readonly DependencyProperty FillRuleProperty =
            DependencyProperty.Register(nameof(FillRule), typeof(FillRule), typeof(Polyline),
                new PropertyMetadata(FillRule.EvenOdd));

        public PointCollection Points
        {
            get
            {
                if (GetValue(PointsProperty) is PointCollection points) return points;
                var created = new PointCollection();
                SetValue(PointsProperty, created);
                return created;
            }
            set => SetValue(PointsProperty, value);
        }

        public FillRule FillRule
        {
            get => (FillRule)GetValue(FillRuleProperty)!;
            set => SetValue(FillRuleProperty, value);
        }
    }
}
