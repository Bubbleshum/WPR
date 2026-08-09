using System;
using System.Globalization;
using AvFormattedText = Avalonia.Media.FormattedText;
using AvTypeface = Avalonia.Media.Typeface;
using AvFlowDirection = Avalonia.Media.FlowDirection;

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
            get => (string)GetValue(TextProperty)!;
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

        protected override Size MeasureOverride(Size availableSize)
        {
            string text = Text ?? string.Empty;
            if (text.Length == 0) return Size.Empty;

            // Try Avalonia's text measurement; fall back to a heuristic if the font subsystem
            // isn't initialized (e.g. unit tests not running inside an Avalonia app).
            try
            {
                var typeface = new AvTypeface(FontFamily ?? "Segoe UI");
                var ft = new AvFormattedText(
                    text,
                    CultureInfo.CurrentUICulture,
                    AvFlowDirection.LeftToRight,
                    typeface,
                    FontSize,
                    foreground: null);

                if (TextWrapping == TextWrapping.Wrap && !double.IsInfinity(availableSize.Width))
                    ft.MaxTextWidth = availableSize.Width;

                return new Size(ft.Width, ft.Height);
            }
            catch
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
