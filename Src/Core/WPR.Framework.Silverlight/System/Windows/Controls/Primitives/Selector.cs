using System;
using System.Collections;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Controls.Primitives.Selector</c>.</summary>
    /// <remarks>
    /// <para>The base of <see cref="ListBox"/>, as in Silverlight (<c>ItemsControl &lt;- Selector
    /// &lt;- ListBox</c>). It used to be a separate leaf with ListBox deriving straight from
    /// ItemsControl, and that is a real defect rather than a simplification: game code that
    /// reads a list's selection through the base — Minesweeper's mode list does
    /// <c>((Selector)sender).SelectedItem</c> in its Tap handler — compiles to a
    /// <c>castclass Selector</c> on a ListBox, which then failed.</para>
    /// <para>Selection is kept on the item (not the container): <see cref="SelectedIndex"/>
    /// indexes <see cref="ItemsControl.ItemsSource"/> when one is set and the direct children
    /// otherwise.</para>
    /// </remarks>
    public class Selector : ItemsControl
    {
        public static readonly DependencyProperty SelectedIndexProperty =
            DependencyProperty.Register(nameof(SelectedIndex), typeof(int), typeof(Selector),
                new PropertyMetadata((object)(-1), OnSelectedIndexChanged));

        public static readonly DependencyProperty SelectedItemProperty =
            DependencyProperty.Register(nameof(SelectedItem), typeof(object), typeof(Selector),
                new PropertyMetadata((object?)null, OnSelectedItemChanged));

        public int SelectedIndex
        {
            get => (int)GetValue(SelectedIndexProperty)!;
            set => SetValue(SelectedIndexProperty, value);
        }

        public object? SelectedItem
        {
            get => GetValue(SelectedItemProperty);
            set => SetValue(SelectedItemProperty, value);
        }

        public event SelectionChangedEventHandler? SelectionChanged;

        // Prevent re-entrancy when one of SelectedIndex / SelectedItem is set in response to the
        // other changing.
        private bool _syncing;

        private IEnumerable SelectableItems => (IEnumerable?)ItemsSource ?? Children;

        private static void OnSelectedIndexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Selector s || s._syncing) return;
            s._syncing = true;
            object? old = s.SelectedItem;
            try
            {
                int idx = e.NewValue is int i ? i : -1;
                object? found = null;
                if (idx >= 0)
                {
                    int j = 0;
                    foreach (object? item in s.SelectableItems)
                    {
                        if (j == idx) { found = item; break; }
                        j++;
                    }
                }
                s.SelectedItem = found;
            }
            finally { s._syncing = false; }
            s.RaiseSelectionChanged(old, s.SelectedItem);
        }

        private static void OnSelectedItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Selector s || s._syncing) return;
            s._syncing = true;
            try
            {
                object? target = e.NewValue;
                int found = -1;
                if (target != null)
                {
                    int j = 0;
                    foreach (object? item in s.SelectableItems)
                    {
                        if (Equals(item, target)) { found = j; break; }
                        j++;
                    }
                }
                s.SelectedIndex = found;
            }
            finally { s._syncing = false; }
            s.RaiseSelectionChanged(e.OldValue, e.NewValue);
        }

        private void RaiseSelectionChanged(object? removed, object? added)
        {
            if (Equals(removed, added)) return;
            SelectionChangedEventHandler? handler = SelectionChanged;
            if (handler == null) return;
            var args = new SelectionChangedEventArgs(
                removed == null ? Array.Empty<object>() : new[] { removed },
                added == null ? Array.Empty<object>() : new[] { added })
            {
                OriginalSource = this,
            };
            try { handler(this, args); }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"[wpr-select] SelectionChanged handler threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Selects the item whose container is the direct child <paramref name="container"/> — what
        /// a tap on a list row does. Returns false when it is not one of this selector's rows.
        /// </summary>
        internal bool SelectContainer(UIElement container)
        {
            int index = Children.IndexOf(container);
            if (index < 0) return false;
            SelectedIndex = index;
            return true;
        }
    }
}
