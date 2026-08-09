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
    /// Shim for <c>System.Windows.Controls.Border</c>. Single-child decoration
    /// with background / border-brush / thickness / corner-radius. Mirrors the
    /// SL semantics enough for XAML <c>&lt;Border&gt;</c> wrappers to load.
    /// </summary>
    [ContentProperty(nameof(Child))]
    public class Border : FrameworkElement
    {
        // Alias of FrameworkElement.BackgroundProperty — see FrameworkElement.cs.
        public static readonly DependencyProperty BackgroundProperty = FrameworkElement.BackgroundProperty;

        public static readonly DependencyProperty BorderBrushProperty =
            DependencyProperty.Register(nameof(BorderBrush), typeof(Brush), typeof(Border),
                new PropertyMetadata((object?)null));

        public static readonly DependencyProperty BorderThicknessProperty =
            DependencyProperty.Register(nameof(BorderThickness), typeof(Thickness), typeof(Border),
                new PropertyMetadata(new Thickness()));

        public static readonly DependencyProperty CornerRadiusProperty =
            DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius), typeof(Border),
                new PropertyMetadata(new CornerRadius()));

        public static readonly DependencyProperty PaddingProperty =
            DependencyProperty.Register(nameof(Padding), typeof(Thickness), typeof(Border),
                new PropertyMetadata(new Thickness()));

        public static readonly DependencyProperty ChildProperty =
            DependencyProperty.Register(nameof(Child), typeof(UIElement), typeof(Border),
                new PropertyMetadata((object?)null, OnChildChanged));

        // Background is inherited from FrameworkElement.

        public Brush? BorderBrush
        {
            get => (Brush?)GetValue(BorderBrushProperty);
            set => SetValue(BorderBrushProperty, value);
        }

        public Thickness BorderThickness
        {
            get => (Thickness)GetValue(BorderThicknessProperty)!;
            set => SetValue(BorderThicknessProperty, value);
        }

        public CornerRadius CornerRadius
        {
            get => (CornerRadius)GetValue(CornerRadiusProperty)!;
            set => SetValue(CornerRadiusProperty, value);
        }

        public Thickness Padding
        {
            get => (Thickness)GetValue(PaddingProperty)!;
            set => SetValue(PaddingProperty, value);
        }

        public UIElement? Child
        {
            get => (UIElement?)GetValue(ChildProperty);
            set => SetValue(ChildProperty, value);
        }

        private static void OnChildChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is Border b)
            {
                if (e.OldValue is UIElement old) old.SetParent(null);
                if (e.NewValue is UIElement now) now.SetParent(b);
                b.InvalidateMeasure();
            }
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            if (Child == null) return Size.Empty;
            // Subtract border + padding from the available slot.
            double padW = BorderThickness.Left + BorderThickness.Right + Padding.Left + Padding.Right;
            double padH = BorderThickness.Top + BorderThickness.Bottom + Padding.Top + Padding.Bottom;
            var inner = new Size(
                Math.Max(0, availableSize.Width - padW),
                Math.Max(0, availableSize.Height - padH));
            Child.Measure(inner);
            return new Size(Child.DesiredSize.Width + padW, Child.DesiredSize.Height + padH);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (Child == null) return finalSize;
            double left = BorderThickness.Left + Padding.Left;
            double top = BorderThickness.Top + Padding.Top;
            double padW = BorderThickness.Left + BorderThickness.Right + Padding.Left + Padding.Right;
            double padH = BorderThickness.Top + BorderThickness.Bottom + Padding.Top + Padding.Bottom;
            Child.Arrange(new Rect(left, top,
                Math.Max(0, finalSize.Width - padW),
                Math.Max(0, finalSize.Height - padH)));
            return finalSize;
        }
    }
}
