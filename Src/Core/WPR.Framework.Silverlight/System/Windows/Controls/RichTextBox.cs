using System.Linq;
using System.Text;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.Controls.RichTextBox</c>, read-only presentation. Each paragraph
    /// of <see cref="Blocks"/> is an <see cref="InlineFlowPanel"/>: its words in their run's colour,
    /// size and weight, and its <see cref="InlineUIContainer"/> elements in line (Microsoft
    /// Minesweeper's tutorial puts gesture and tile pictures in its sentences), wrapped to the
    /// width offered and aligned by TextAlignment. Editing is not implemented.
    /// </summary>
    /// <remarks>
    /// Silverlight derives it from Control; this one is a ContentControl because that is how an
    /// element here presents children of its own. Its Content is WPR's, rebuilt whenever what it
    /// shows changed (checked at measure, since Blocks and their runs are plain collections).
    /// </remarks>
    [ContentProperty(nameof(Blocks))]
    public class RichTextBox : ContentControl
    {
        public static readonly DependencyProperty TextWrappingProperty =
            DependencyProperty.Register(nameof(TextWrapping), typeof(TextWrapping), typeof(RichTextBox),
                new PropertyMetadata(TextWrapping.Wrap, OnPresentationChanged));

        public static readonly DependencyProperty TextAlignmentProperty =
            DependencyProperty.Register(nameof(TextAlignment), typeof(TextAlignment), typeof(RichTextBox),
                new PropertyMetadata(TextAlignment.Left, OnPresentationChanged));

        public static readonly DependencyProperty IsReadOnlyProperty =
            DependencyProperty.Register(nameof(IsReadOnly), typeof(bool), typeof(RichTextBox),
                new PropertyMetadata((object)false));

        private string? _shown;

        public RichTextBox() => Blocks = new BlockCollection(InvalidateMeasure);

        public BlockCollection Blocks { get; }

        public TextWrapping TextWrapping
        {
            get => (TextWrapping)GetValue(TextWrappingProperty)!;
            set => SetValue(TextWrappingProperty, value);
        }

        public TextAlignment TextAlignment
        {
            get => (TextAlignment)GetValue(TextAlignmentProperty)!;
            set => SetValue(TextAlignmentProperty, value);
        }

        public bool IsReadOnly
        {
            get => (bool)GetValue(IsReadOnlyProperty)!;
            set => SetValue(IsReadOnlyProperty, value);
        }

        public void SelectAll()
        {
        }

        private static void OnPresentationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is RichTextBox box)
            {
                box._shown = null;
                box.InvalidateMeasure();
            }
        }

        internal override Size WprMeasure(Size availableSize)
        {
            Refresh();
            return base.WprMeasure(availableSize);
        }

        /// <summary>Rebuilds the presented paragraphs when the content or the box's text properties changed.</summary>
        private void Refresh()
        {
            var key = new StringBuilder();
            key.Append(FontSize).Append('|').Append(TextAlignment).Append('|').Append(TextWrapping).Append('|')
               .Append(Foreground?.GetHashCode()).Append('|');
            foreach (Block block in Blocks)
            {
                key.Append(block.FontSize).Append(block.ExplicitTextAlignment).Append(block.Foreground?.GetHashCode()).Append('{');
                if (block is Paragraph p) AppendKey(key, p.Inlines);
                key.Append('}');
            }

            string shown = key.ToString();
            if (shown == _shown) return;
            _shown = shown;

            // An InlineUIContainer's element moves to the new panel; take it out of the old one.
            if (Content is StackPanel old)
            {
                foreach (UIElement paragraph in old.Children)
                {
                    if (paragraph is Panel flow) flow.Children.Clear();
                }
            }

            var panel = new StackPanel();
            foreach (Block block in Blocks)
            {
                double size = double.IsNaN(block.FontSize) ? FontSize : block.FontSize;
                var flow = new InlineFlowPanel(block.ExplicitTextAlignment ?? TextAlignment, size)
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                if (block is Paragraph p)
                {
                    AddInlines(flow, p.Inlines, block.Foreground ?? Foreground, size, block.FontWeight);
                }

                panel.Children.Add(flow);
            }

            Content = panel;
        }

        private static void AppendKey(StringBuilder key, InlineCollection inlines)
        {
            foreach (Inline inline in inlines)
            {
                key.Append(inline.GetType().Name).Append(':').Append(inline.FontSize).Append(':')
                   .Append(inline.Foreground?.GetHashCode()).Append(':');
                switch (inline)
                {
                    case Run run: key.Append(run.Text); break;
                    case InlineUIContainer ui: key.Append(ui.Child?.GetHashCode()); break;
                    case Span span: AppendKey(key, span.Inlines); break;
                }

                key.Append('\u0001');
            }
        }

        /// <summary>Words, spaces, breaks and inline elements, each run in its own (or inherited) colour, size and weight.</summary>
        private static void AddInlines(InlineFlowPanel flow, InlineCollection inlines, Brush? foreground, double size, FontWeight weight)
        {
            foreach (Inline inline in inlines)
            {
                Brush? brush = inline.Foreground ?? foreground;
                double fontSize = double.IsNaN(inline.FontSize) ? size : inline.FontSize;
                FontWeight fontWeight = inline.FontWeight == FontWeights.Normal ? weight : inline.FontWeight;
                switch (inline)
                {
                    case Run run:
                        AddWords(flow, run.Text, brush, fontSize, fontWeight);
                        break;
                    case LineBreak:
                        flow.Children.Add(new InlineFlowPanel.Break());
                        break;
                    case InlineUIContainer { Child: { } element }:
                        flow.Children.Add(element);
                        break;
                    case Span span:
                        AddInlines(flow, span.Inlines, brush, fontSize, fontWeight);
                        break;
                }
            }
        }

        private static void AddWords(InlineFlowPanel flow, string text, Brush? brush, double size, FontWeight weight)
        {
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '\n')
                {
                    flow.Children.Add(new InlineFlowPanel.Break());
                    i++;
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    flow.Children.Add(new InlineFlowPanel.Space());
                    while (i < text.Length && char.IsWhiteSpace(text[i]) && text[i] != '\n') i++;
                    continue;
                }

                int start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
                var word = new TextBlock { Text = text.Substring(start, i - start), FontSize = size, FontWeight = weight };
                if (brush != null) word.Foreground = brush;
                flow.Children.Add(word);
            }
        }
    }
}
