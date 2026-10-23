using System;
using System.Collections;
using WPR.SilverlightCompability;

namespace Microsoft.Phone.Controls
{
    /// <summary>
    /// Shim for <c>Microsoft.Phone.Controls.TemplatedItemsControl&lt;T&gt;</c>, the base of
    /// <see cref="Panorama"/> and <see cref="Pivot"/>. WPR's <see cref="ItemsControl"/> is a
    /// <see cref="StackPanel"/>; the renderer lays Panorama and Pivot out by type name
    /// (<c>WpChrome.ArrangePanorama</c>) and moves the selection through
    /// <c>PanoramaSelectedItemSync</c>, which writes the concrete control's
    /// <c>SelectedItemProperty</c>. This class holds what both controls do with that write: keep
    /// item and index in step and report the change once.
    /// </summary>
    public class TemplatedItemsControl<T> : ItemsControl where T : FrameworkElement, new()
    {
        private bool _syncing;

        private IEnumerable SelectableItems => (IEnumerable?)ItemsSource ?? Children;

        /// <summary>Whether a selection write is being mirrored onto its partner property.</summary>
        protected bool IsSyncingSelection => _syncing;

        /// <summary>The index of <paramref name="item"/> among the items, or -1.</summary>
        protected int IndexOfItem(object? item)
        {
            if (item == null) return -1;
            int j = 0;
            foreach (object? candidate in SelectableItems)
            {
                if (Equals(candidate, item)) return j;
                j++;
            }
            // The renderer selects the displayed container; with an ItemsSource that is the
            // child, not the data item.
            return item is UIElement e ? Children.IndexOf(e) : -1;
        }

        /// <summary>The item at <paramref name="index"/>, or null.</summary>
        protected object? ItemAt(int index)
        {
            if (index < 0) return null;
            int j = 0;
            foreach (object? item in SelectableItems)
            {
                if (j == index) return item;
                j++;
            }
            return null;
        }

        /// <summary>
        /// Applies a change of one selection property to the other and reports it.
        /// <paramref name="update"/> writes the partner property; the change it causes is ignored.
        /// </summary>
        protected void SyncSelection(object? oldItem, object? newItem, Action update)
        {
            if (_syncing) return;
            _syncing = true;
            try { update(); }
            finally { _syncing = false; }
            if (Equals(oldItem, newItem)) return;
            var args = new SelectionChangedEventArgs(
                oldItem == null ? Array.Empty<object>() : new[] { oldItem },
                newItem == null ? Array.Empty<object>() : new[] { newItem })
            {
                OriginalSource = this,
            };
            try { OnSelectionChanged(args); }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"[wpr-select] {GetType().Name}.SelectionChanged handler threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Raises the concrete control's SelectionChanged event.</summary>
        protected virtual void OnSelectionChanged(SelectionChangedEventArgs e) { }

        // Bound to an ItemsSource, each item gets a T (PivotItem / PanoramaItem) holding it, as
        // the toolkit's own container generation does; the renderer lays out and titles Ts.
        protected override bool IsItemItsOwnContainerOverride(object item) => item is T;

        protected override DependencyObject GetContainerForItemOverride() => new T();

        /// <summary>The control's HeaderTemplate, which titles each generated container.</summary>
        protected virtual DataTemplate? ItemHeaderTemplate => null;

        protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
        {
            base.PrepareContainerForItemOverride(element, item);
            if (ReferenceEquals(element, item) || element is not FrameworkElement container) return;

            // The header: the HeaderTemplate built over the item, read as its text (the renderer
            // draws a header as text), else the item itself.
            object? header = item;
            if (ItemHeaderTemplate?.LoadContent() is FrameworkElement templated)
            {
                templated.DataContext = item;
                header = FirstText(templated) ?? header;
            }

            System.Reflection.PropertyInfo? property = container.GetType().GetProperty("Header");
            if (property?.CanWrite == true && property.GetValue(container) == null)
            {
                property.SetValue(container, header);
            }
        }

        private static string? FirstText(DependencyObject node)
        {
            if (node is TextBlock text && !string.IsNullOrEmpty(text.Text)) return text.Text;
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                if (VisualTreeHelper.GetChild(node, i) is DependencyObject child && FirstText(child) is string found)
                    return found;
            }

            return null;
        }
    }
}
