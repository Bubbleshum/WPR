namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.Documents.InlineUIContainer</c>: an element inside text.
    /// Not drawn: <see cref="RichTextBox"/> presents plain text, and leaves a gap where it sits.
    /// </summary>
    [ContentProperty(nameof(Child))]
    public sealed class InlineUIContainer : Inline
    {
        public UIElement? Child { get; set; }

        internal override string PlainText => "   ";
    }
}
