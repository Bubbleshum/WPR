using System;
using WPR.SilverlightCompability;

namespace Microsoft.Phone.Controls
{
    /// <summary>Shim for WP8's <c>Microsoft.Phone.Controls.ItemRealizationEventArgs</c>.</summary>
    public class ItemRealizationEventArgs : EventArgs
    {
        internal ItemRealizationEventArgs(ContentPresenter container, LongListSelectorItemKind kind)
        {
            Container = container;
            ItemKind = kind;
        }

        /// <summary>The container that came into (or left) the realised window; its Content is the item.</summary>
        public ContentPresenter Container { get; }

        public LongListSelectorItemKind ItemKind { get; }
    }
}
