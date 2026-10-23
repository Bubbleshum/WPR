namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Documents.Run</c>: a stretch of text.</summary>
    [ContentProperty(nameof(Text))]
    public sealed class Run : Inline
    {
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(nameof(Text), typeof(string), typeof(Run),
                new PropertyMetadata(string.Empty));

        public string Text
        {
            get => (string?)GetValue(TextProperty) ?? string.Empty;
            set => SetValue(TextProperty, value);
        }

        internal override string PlainText => Text;
    }
}
