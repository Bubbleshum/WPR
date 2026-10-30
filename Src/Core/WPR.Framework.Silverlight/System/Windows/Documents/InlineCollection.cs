namespace System.Windows.Documents
{
    /// <summary>Shim for <c>System.Windows.Documents.InlineCollection</c>.</summary>
    public sealed class InlineCollection : PresentationFrameworkCollection<Inline>
    {
        /// <summary>Silverlight's <c>InlineCollection.Add(string)</c>: a Run of that text.</summary>
        public void Add(string text) => Add(new Run { Text = text });

        /// <summary>The owner's re-measure (a TextBlock's), when the inlines change.</summary>
        internal Action? Changed { get; set; }

        protected override void OnItemsChanged() => Changed?.Invoke();
    }
}
