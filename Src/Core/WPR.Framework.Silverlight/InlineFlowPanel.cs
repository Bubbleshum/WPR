using System;
using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// How <see cref="RichTextBox"/> presents one paragraph: its words (one TextBlock each, in their
    /// run's colour and size) and its InlineUIContainer elements laid out left to right, wrapped at
    /// the width offered, each line aligned by <see cref="TextAlignment"/> and its items centred on
    /// the line. Not a Silverlight type.
    /// </summary>
    /// <remarks>
    /// Microsoft Minesweeper's tutorial sentences carry gesture and tile pictures in the middle of
    /// the text ("A [tile] means exactly one mine is touching this square"); drawn as plain text
    /// they were gaps.
    /// </remarks>
    internal sealed class InlineFlowPanel : Panel
    {
        /// <summary>Whitespace in the source: a space's width between the items either side.</summary>
        internal sealed class Space : FrameworkElement
        {
        }

        /// <summary>A forced line break (LineBreak, or a newline in a run).</summary>
        internal sealed class Break : FrameworkElement
        {
        }

        public InlineFlowPanel(TextAlignment alignment, double fontSize)
        {
            Alignment = alignment;
            SpaceWidth = MeasureSpace(fontSize);
            LineHeight = fontSize * 1.33;
        }

        public TextAlignment Alignment { get; }

        /// <summary>The gap a space leaves.</summary>
        public double SpaceWidth { get; }

        /// <summary>The height of an empty line (a break with nothing before it).</summary>
        public double LineHeight { get; }

        // Lines from the last measure: each item with its x within the line, and the line's size.
        private readonly List<(List<(UIElement Item, double X)> Items, double Width, double Height)> _lines = new();

        internal override Size WprMeasure(Size availableSize)
        {
            _lines.Clear();
            double limit = availableSize.Width;

            var line = new List<(UIElement, double)>();
            double lineWidth = 0, lineHeight = 0, widest = 0, total = 0;
            bool pendingSpace = false;

            void EndLine()
            {
                _lines.Add((line, lineWidth, line.Count == 0 ? LineHeight : lineHeight));
                widest = Math.Max(widest, lineWidth);
                total += line.Count == 0 ? LineHeight : lineHeight;
                line = new List<(UIElement, double)>();
                lineWidth = 0;
                lineHeight = 0;
                pendingSpace = false;
            }

            foreach (UIElement child in Children)
            {
                switch (child)
                {
                    case Break:
                        EndLine();
                        continue;
                    case Space:
                        pendingSpace = line.Count > 0;
                        continue;
                }

                child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Size size = child.DesiredSize;
                double gap = pendingSpace ? SpaceWidth : 0;
                if (line.Count > 0 && lineWidth + gap + size.Width > limit)
                {
                    EndLine();
                    gap = 0;
                }

                line.Add((child, lineWidth + gap));
                lineWidth += gap + size.Width;
                lineHeight = Math.Max(lineHeight, size.Height);
                pendingSpace = false;
            }

            if (line.Count > 0) EndLine();
            return new Size(double.IsInfinity(limit) ? widest : Math.Min(widest, limit), total);
        }

        internal override Size WprArrange(Size finalSize)
        {
            double y = 0;
            foreach ((List<(UIElement Item, double X)> items, double width, double height) in _lines)
            {
                double offset = Alignment switch
                {
                    TextAlignment.Center => (finalSize.Width - width) / 2,
                    TextAlignment.Right => finalSize.Width - width,
                    _ => 0,
                };

                foreach ((UIElement item, double x) in items)
                {
                    Size size = item.DesiredSize;
                    item.Arrange(new Rect(offset + x, y + ((height - size.Height) / 2), size.Width, size.Height));
                }

                y += height;
            }

            return finalSize;
        }

        private static double MeasureSpace(double fontSize)
        {
            if (TextRasteriser.IsAvailable)
            {
                TextRasteriser.Measure("x x", fontSize, out double spaced, out _);
                TextRasteriser.Measure("xx", fontSize, out double tight, out _);
                if (spaced > tight) return spaced - tight;
            }

            return fontSize * 0.28;
        }
    }
}
