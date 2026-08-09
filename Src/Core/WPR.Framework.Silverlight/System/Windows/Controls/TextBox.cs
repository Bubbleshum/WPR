using System;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Stub for <c>System.Windows.Controls.TextBox</c>. Editable text input. WPR currently
    /// renders this as a TextBlock-like read-only field; full editing requires routing keyboard
    /// input through Avalonia's input pipeline, which we don't yet do for Silverlight pages.
    /// </summary>
    public class TextBox : Control
    {
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(nameof(Text), typeof(string), typeof(TextBox),
                new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty FontSizeProperty =
            DependencyProperty.Register(nameof(FontSize), typeof(double), typeof(TextBox),
                new PropertyMetadata(14.0));

        public static readonly DependencyProperty FontFamilyProperty =
            DependencyProperty.Register(nameof(FontFamily), typeof(string), typeof(TextBox),
                new PropertyMetadata("Segoe UI"));

        public static readonly DependencyProperty ForegroundProperty =
            DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(TextBox),
                new PropertyMetadata((object?)null));

        public static readonly DependencyProperty TextAlignmentProperty =
            DependencyProperty.Register(nameof(TextAlignment), typeof(TextAlignment), typeof(TextBox),
                new PropertyMetadata(TextAlignment.Left));

        public static readonly DependencyProperty TextWrappingProperty =
            DependencyProperty.Register(nameof(TextWrapping), typeof(TextWrapping), typeof(TextBox),
                new PropertyMetadata(TextWrapping.NoWrap));

        public static readonly DependencyProperty MaxLengthProperty =
            DependencyProperty.Register(nameof(MaxLength), typeof(int), typeof(TextBox),
                new PropertyMetadata(0));

        public static readonly DependencyProperty AcceptsReturnProperty =
            DependencyProperty.Register(nameof(AcceptsReturn), typeof(bool), typeof(TextBox),
                new PropertyMetadata(false));

        public static readonly DependencyProperty IsReadOnlyProperty =
            DependencyProperty.Register(nameof(IsReadOnly), typeof(bool), typeof(TextBox),
                new PropertyMetadata(false));

        public string Text
        {
            get => (string)GetValue(TextProperty)!;
            set
            {
                string old = Text;
                SetValue(TextProperty, value ?? string.Empty);
                if (!string.Equals(old, value, StringComparison.Ordinal))
                    TextChanged?.Invoke(this, new TextChangedEventArgs());
            }
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

        public int MaxLength
        {
            get => (int)GetValue(MaxLengthProperty)!;
            set => SetValue(MaxLengthProperty, value);
        }

        public bool AcceptsReturn
        {
            get => (bool)GetValue(AcceptsReturnProperty)!;
            set => SetValue(AcceptsReturnProperty, value);
        }

        public bool IsReadOnly
        {
            get => (bool)GetValue(IsReadOnlyProperty)!;
            set => SetValue(IsReadOnlyProperty, value);
        }

        public int SelectionStart { get; set; }
        public int SelectionLength { get; set; }
        public string SelectedText { get; set; } = string.Empty;

        public event EventHandler<TextChangedEventArgs>? TextChanged;

        public void Select(int start, int length)
        {
            SelectionStart = start;
            SelectionLength = length;
        }

        public void SelectAll()
        {
            SelectionStart = 0;
            SelectionLength = Text?.Length ?? 0;
        }
    }
}
