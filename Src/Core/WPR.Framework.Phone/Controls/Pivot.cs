using System;
using WPR.SilverlightCompability;

namespace Microsoft.Phone.Controls
{
    /// <summary>
    /// Shim for <c>Microsoft.Phone.Controls.Pivot</c> (moved into Microsoft.Phone on WP8, like
    /// <see cref="Panorama"/>). The renderer lays it out and swipes it; this type holds the
    /// selection, which games may also set, and raises <see cref="SelectionChanged"/> and the
    /// item loading/unloading events when it moves.
    /// </summary>
    public class Pivot : TemplatedItemsControl<PivotItem>
    {
        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(nameof(Title), typeof(object), typeof(Pivot),
                new PropertyMetadata((object?)null));

        public static readonly DependencyProperty TitleTemplateProperty =
            DependencyProperty.Register(nameof(TitleTemplate), typeof(DataTemplate), typeof(Pivot),
                new PropertyMetadata((object?)null));

        public static readonly DependencyProperty HeaderTemplateProperty =
            DependencyProperty.Register(nameof(HeaderTemplate), typeof(DataTemplate), typeof(Pivot),
                new PropertyMetadata((object?)null));

        public static readonly DependencyProperty SelectedItemProperty =
            DependencyProperty.Register(nameof(SelectedItem), typeof(object), typeof(Pivot),
                new PropertyMetadata((object?)null, OnSelectedItemChanged));

        public static readonly DependencyProperty SelectedIndexProperty =
            DependencyProperty.Register(nameof(SelectedIndex), typeof(int), typeof(Pivot),
                new PropertyMetadata((object)(-1), OnSelectedIndexChanged));

        public static readonly DependencyProperty IsLockedProperty =
            DependencyProperty.Register(nameof(IsLocked), typeof(bool), typeof(Pivot),
                new PropertyMetadata((object)false));

        public object? Title
        {
            get => GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        public DataTemplate? TitleTemplate
        {
            get => (DataTemplate?)GetValue(TitleTemplateProperty);
            set => SetValue(TitleTemplateProperty, value);
        }

        public DataTemplate? HeaderTemplate
        {
            get => (DataTemplate?)GetValue(HeaderTemplateProperty);
            set => SetValue(HeaderTemplateProperty, value);
        }

        public object? SelectedItem
        {
            get => GetValue(SelectedItemProperty);
            set => SetValue(SelectedItemProperty, value);
        }

        public int SelectedIndex
        {
            get => (int)GetValue(SelectedIndexProperty)!;
            set => SetValue(SelectedIndexProperty, value);
        }

        /// <summary>Stops the user swiping to another item. Stored; the renderer does not read it yet.</summary>
        public bool IsLocked
        {
            get => (bool)GetValue(IsLockedProperty)!;
            set => SetValue(IsLockedProperty, value);
        }

        protected override DataTemplate? ItemHeaderTemplate => HeaderTemplate;

        public event SelectionChangedEventHandler? SelectionChanged;
        public event EventHandler<PivotItemEventArgs>? LoadingPivotItem;
        public event EventHandler<PivotItemEventArgs>? LoadedPivotItem;
        public event EventHandler<PivotItemEventArgs>? UnloadingPivotItem;
        public event EventHandler<PivotItemEventArgs>? UnloadedPivotItem;

        protected override void OnSelectionChanged(SelectionChangedEventArgs e)
        {
            PivotItem? removed = e.RemovedItems.Count > 0 ? ContainerOf(e.RemovedItems[0]) : null;
            PivotItem? added = e.AddedItems.Count > 0 ? ContainerOf(e.AddedItems[0]) : null;
            if (removed != null) UnloadingPivotItem?.Invoke(this, new PivotItemEventArgs(removed));
            if (added != null) LoadingPivotItem?.Invoke(this, new PivotItemEventArgs(added));
            SelectionChanged?.Invoke(this, e);
            if (removed != null) UnloadedPivotItem?.Invoke(this, new PivotItemEventArgs(removed));
            if (added != null) LoadedPivotItem?.Invoke(this, new PivotItemEventArgs(added));
        }

        private PivotItem? ContainerOf(object? item)
        {
            if (item is PivotItem p) return p;
            int index = IndexOfItem(item);
            return index >= 0 && index < Children.Count ? Children[index] as PivotItem : null;
        }

        private static void OnSelectedItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Pivot p || p.IsSyncingSelection) return;
            int index = p.IndexOfItem(e.NewValue);
            if (index >= 0) PanoramaStateTable.GetOrCreate(p).CurrentIndex = index;
            p.SyncSelection(e.OldValue, e.NewValue, () => p.SelectedIndex = index);
        }

        private static void OnSelectedIndexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Pivot p || p.IsSyncingSelection) return;
            int index = e.NewValue is int i ? i : -1;
            if (index >= 0) PanoramaStateTable.GetOrCreate(p).CurrentIndex = index;
            object? old = p.SelectedItem;
            object? item = p.ItemAt(index);
            p.SyncSelection(old, item, () => p.SelectedItem = item);
        }
    }
}
