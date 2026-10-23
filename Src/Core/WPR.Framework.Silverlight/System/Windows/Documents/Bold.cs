namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Documents.Bold</c>. Drawn as plain text.</summary>
    public sealed class Bold : Span
    {
        public Bold() => FontWeight = FontWeights.Bold;
    }
}
