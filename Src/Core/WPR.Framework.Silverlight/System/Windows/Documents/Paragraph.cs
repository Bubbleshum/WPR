namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Documents.Paragraph</c>.</summary>
    [ContentProperty(nameof(Inlines))]
    public sealed class Paragraph : Block
    {
        public InlineCollection Inlines { get; } = new InlineCollection();
    }
}
