using System;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Documents.BlockCollection</c>.</summary>
    public sealed class BlockCollection : PresentationFrameworkCollection<Block>
    {
        private readonly Action? _changed;

        public BlockCollection()
        {
        }

        /// <summary>A RichTextBox's own collection: a change re-lays the box out.</summary>
        internal BlockCollection(Action changed) => _changed = changed;

        protected override void OnItemsChanged() => _changed?.Invoke();
    }
}
