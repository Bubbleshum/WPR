namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Documents.LineBreak</c>.</summary>
    public sealed class LineBreak : Inline
    {
        internal override string PlainText => "\n";
    }
}
