using System.Linq;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Documents.Span</c>: a group of inlines.</summary>
    [ContentProperty(nameof(Inlines))]
    public class Span : Inline
    {
        public InlineCollection Inlines { get; } = new InlineCollection();

        internal override string PlainText => string.Concat(Inlines.Select(i => i.PlainText));
    }
}
