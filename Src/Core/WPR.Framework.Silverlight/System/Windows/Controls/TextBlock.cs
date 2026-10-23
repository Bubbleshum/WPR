using System;
using System.Globalization;

namespace WPR.SilverlightCompability
{
    public class TextBlock : FrameworkElement
    {
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(nameof(Text), typeof(string), typeof(TextBlock),
                new PropertyMetadata(string.Empty, OnTextRelatedChanged));

        public static readonly DependencyProperty FontSizeProperty =
            DependencyProperty.Register(nameof(FontSize), typeof(double), typeof(TextBlock),
                new PropertyMetadata(14.0, OnTextRelatedChanged));

        public static readonly DependencyProperty FontFamilyProperty =
            DependencyProperty.Register(nameof(FontFamily), typeof(string), typeof(TextBlock),
                new PropertyMetadata("Segoe UI", OnTextRelatedChanged));

        public static readonly DependencyProperty ForegroundProperty =
            DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(TextBlock),
                new PropertyMetadata((object?)null));

        public static readonly DependencyProperty TextAlignmentProperty =
            DependencyProperty.Register(nameof(TextAlignment), typeof(TextAlignment), typeof(TextBlock),
                new PropertyMetadata(TextAlignment.Left));

        public static readonly DependencyProperty TextWrappingProperty =
            DependencyProperty.Register(nameof(TextWrapping), typeof(TextWrapping), typeof(TextBlock),
                new PropertyMetadata(TextWrapping.NoWrap, OnTextRelatedChanged));

        public string Text
        {
            // A binding can store its source value unconverted — Minesweeper binds a TimeSpan here —
            // and Silverlight shows such a value through ToString, as a binding to a string
            // property converts. A hard cast threw from every paint of the page instead.
            get => GetValue(TextProperty) switch
            {
                string s => s,
                null => null!,
                object o => System.Convert.ToString(o, System.Globalization.CultureInfo.CurrentCulture)!,
            };
            set => SetValue(TextProperty, value);
        }

        public double FontSize
        {
            get => (double)GetValue(FontSizeProperty)!;
            set => SetValue(FontSizeProperty, value);
        }

        public string FontFamily
        {
            get => (string)GetValue(FontFamilyProperty)!;
            set => SetValue(FontFamilyProperty, value);
        }

        public Brush? Foreground
        {
            get => (Brush?)GetValue(ForegroundProperty);
            set => SetValue(ForegroundProperty, value);
        }

        public TextAlignment TextAlignment
        {
            get => (TextAlignment)GetValue(TextAlignmentProperty)!;
            set => SetValue(TextAlignmentProperty, value);
        }

        public TextWrapping TextWrapping
        {
            get => (TextWrapping)GetValue(TextWrappingProperty)!;
            set => SetValue(TextWrappingProperty, value);
        }

        private static void OnTextRelatedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TextBlock tb) tb.InvalidateMeasure();
        }

        internal override Size WprMeasure(Size availableSize)
        {
            string text = Text ?? string.Empty;
            if (text.Length == 0) return Size.Empty;

            // WPR's own rasteriser first, because on the mixed-mode host it is what actually draws
            // this text — measuring with anything else guarantees layout and rendering disagree.
            // The heuristic below is only for a machine with no usable system font.
            if (TextRasteriser.IsAvailable)
            {
                double wrapWidth = TextWrapping == TextWrapping.Wrap
                    ? availableSize.Width
                    : double.PositiveInfinity;

                TextRasteriser.Measure(text, FontSize, out double tw, out double th, wrapWidth);
                if (th > 0) return new Size(tw, th);
            }

            // No system font found: a crude heuristic.
            {
                double w = text.Length * FontSize * 0.55;
                double h = FontSize * 1.2;
                if (!double.IsInfinity(availableSize.Width) && w > availableSize.Width && TextWrapping == TextWrapping.Wrap)
                {
                    int linesEst = (int)Math.Ceiling(w / availableSize.Width);
                    w = availableSize.Width;
                    h *= linesEst;
                }
                return new Size(w, h);
            }
        }
    }
}
