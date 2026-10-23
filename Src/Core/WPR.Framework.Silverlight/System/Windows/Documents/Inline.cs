namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Documents.Inline</c>.</summary>
    public abstract class Inline : TextElement
    {
        /// <summary>The text this inline contributes when it is drawn as plain text.</summary>
        internal abstract string PlainText { get; }
    }
}
