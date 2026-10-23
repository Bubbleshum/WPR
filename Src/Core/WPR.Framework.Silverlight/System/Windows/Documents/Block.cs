namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Documents.Block</c>.</summary>
    public abstract class Block : TextElement
    {
        private TextAlignment? _textAlignment;

        /// <summary>Unset means the RichTextBox's.</summary>
        public TextAlignment TextAlignment
        {
            get => _textAlignment ?? TextAlignment.Left;
            set => _textAlignment = value;
        }

        internal TextAlignment? ExplicitTextAlignment => _textAlignment;
    }
}
