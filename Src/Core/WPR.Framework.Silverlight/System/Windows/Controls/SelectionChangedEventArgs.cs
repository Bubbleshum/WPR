using System;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.Controls.SelectionChangedEventArgs</c>.
    /// <see cref="AddedItems"/> / <see cref="RemovedItems"/> mirror SL semantics;
    /// the lists are populated when our renderer eventually raises the event.
    /// </summary>
    public class SelectionChangedEventArgs : RoutedEventArgs
    {
        public System.Collections.IList AddedItems { get; }
        public System.Collections.IList RemovedItems { get; }

        public SelectionChangedEventArgs(System.Collections.IList removedItems, System.Collections.IList addedItems)
        {
            RemovedItems = removedItems;
            AddedItems = addedItems;
        }

        public SelectionChangedEventArgs()
            : this(Array.Empty<object>(), Array.Empty<object>())
        {
        }
    }
}
