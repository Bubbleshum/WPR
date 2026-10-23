namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.Documents.TextElement</c>, the base of the rich-text content
    /// model (<see cref="Block"/>, <see cref="Inline"/>). A null value means "the RichTextBox's".
    /// </summary>
    public abstract class TextElement : DependencyObject
    {
        public static readonly DependencyProperty ForegroundProperty =
            DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(TextElement),
                new PropertyMetadata((object?)null));

        public static readonly DependencyProperty FontSizeProperty =
            DependencyProperty.Register(nameof(FontSize), typeof(double), typeof(TextElement),
                new PropertyMetadata(double.NaN));

        public static readonly DependencyProperty FontWeightProperty =
            DependencyProperty.Register(nameof(FontWeight), typeof(FontWeight), typeof(TextElement),
                new PropertyMetadata(FontWeights.Normal));

        public Brush? Foreground
        {
            get => (Brush?)GetValue(ForegroundProperty);
            set => SetValue(ForegroundProperty, value);
        }

        public double FontSize
        {
            get => (double)GetValue(FontSizeProperty)!;
            set => SetValue(FontSizeProperty, value);
        }

        public FontWeight FontWeight
        {
            get => (FontWeight)GetValue(FontWeightProperty)!;
            set => SetValue(FontWeightProperty, value);
        }

        public string? Name { get; set; }
    }
}
