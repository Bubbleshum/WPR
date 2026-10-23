using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using WPR.SilverlightCompability;

namespace Microsoft.Phone.Controls
{
    /// <summary>
    /// Shim for WP8's <c>Microsoft.Phone.Controls.LongListSelector</c> (Microsoft.Phone 8.0, not the
    /// WP7 toolkit's control of the same name): the phone's long, virtualised list.
    /// </summary>
    /// <remarks>
    /// <para>Built on <see cref="ListBox"/>'s real visual tree, so it scrolls and selects the same
    /// way: ScrollViewer &gt; ItemsPresenter &gt; [list header, items, list footer]. Each row is a
    /// <see cref="ContentPresenter"/> holding the item, drawn through <see cref="ItemsControl.ItemTemplate"/>.</para>
    ///
    /// <para><b><see cref="ItemRealized"/> / <see cref="ItemUnrealized"/> follow the viewport</b>
    /// (one screen either side of it), as the phone's virtualisation makes them: apps use them as
    /// their "load the next page" trigger (YouTube's lists do, in every listing control), so
    /// realising every row at once would make them fetch page after page.</para>
    ///
    /// <para>Not implemented: grouping (<see cref="IsGroupingEnabled"/> lists the items flat),
    /// <see cref="LongListSelectorLayoutMode.Grid"/> (laid out as a list), the jump list
    /// (<see cref="JumpListOpening"/> never fires), and <see cref="ManipulationState"/> stays Idle.</para>
    /// </remarks>
    public class LongListSelector : ListBox, INotifyPropertyChanged
    {
        // Field initialisers run before ListBox's constructor, which calls HostItemsPanel.
        private readonly ContentPresenter _listHeader = new();
        private readonly ContentPresenter _listFooter = new();
        private readonly StackPanel _scrollArea = new();
        private readonly HashSet<UIElement> _realized = new();

        public LongListSelector()
        {
            ScrollViewerPart.WprOffsetChanged += UpdateRealization;
        }

        internal override void HostItemsPanel(ItemsPresenter presenter, Panel panel)
        {
            _scrollArea.Children.Clear();
            _scrollArea.Children.Add(_listHeader);
            _scrollArea.Children.Add(panel);
            _scrollArea.Children.Add(_listFooter);
            presenter.Children.Clear();
            presenter.Children.Add(_scrollArea);
        }

        // A LongListSelector has no ItemsPanel; it lays its rows out itself.
        internal override void ItemsPanelChanged(ItemsPanelTemplate? template) { }

        // ---- ItemsSource: an IList on the phone ----

        public static new readonly DependencyProperty ItemsSourceProperty =
            DependencyProperty.Register(nameof(ItemsSource), typeof(IList), typeof(LongListSelector),
                new PropertyMetadata((object?)null, (d, e) => ((LongListSelector)d).OnItemsSourceChanged((IList?)e.NewValue)));

        public new IList? ItemsSource
        {
            get => (IList?)GetValue(ItemsSourceProperty);
            set => SetValue(ItemsSourceProperty, value);
        }

        private void OnItemsSourceChanged(IList? value)
        {
            SetValue(ItemsControl.ItemsSourceProperty, value);
            OnPropertyChanged(nameof(ItemsSource));
        }

        // ---- the rest of the phone's surface ----

        public static readonly DependencyProperty LayoutModeProperty = Register(nameof(LayoutMode), typeof(LongListSelectorLayoutMode), LongListSelectorLayoutMode.List);
        public static readonly DependencyProperty GridCellSizeProperty = Register(nameof(GridCellSize), typeof(Size), new Size(0, 0));
        public static readonly DependencyProperty IsGroupingEnabledProperty = Register(nameof(IsGroupingEnabled), typeof(bool), false);
        public static readonly DependencyProperty HideEmptyGroupsProperty = Register(nameof(HideEmptyGroups), typeof(bool), false);
        public static readonly DependencyProperty GroupHeaderTemplateProperty = Register(nameof(GroupHeaderTemplate), typeof(DataTemplate), null);
        public static readonly DependencyProperty GroupFooterTemplateProperty = Register(nameof(GroupFooterTemplate), typeof(DataTemplate), null);
        public static readonly DependencyProperty JumpListStyleProperty = Register(nameof(JumpListStyle), typeof(Style), null);
        public static readonly DependencyProperty ListHeaderProperty = Register(nameof(ListHeader), typeof(object), null, HeaderFooterChanged);
        public static readonly DependencyProperty ListHeaderTemplateProperty = Register(nameof(ListHeaderTemplate), typeof(DataTemplate), null, HeaderFooterChanged);
        public static readonly DependencyProperty ListFooterProperty = Register(nameof(ListFooter), typeof(object), null, HeaderFooterChanged);
        public static readonly DependencyProperty ListFooterTemplateProperty = Register(nameof(ListFooterTemplate), typeof(DataTemplate), null, HeaderFooterChanged);

        private static DependencyProperty Register(string name, Type type, object? defaultValue, PropertyChangedCallback? changed = null) =>
            DependencyProperty.Register(name, type, typeof(LongListSelector), new PropertyMetadata(defaultValue, (d, e) =>
            {
                changed?.Invoke(d, e);
                ((LongListSelector)d).OnPropertyChanged(name);
            }));

        public LongListSelectorLayoutMode LayoutMode { get => (LongListSelectorLayoutMode)GetValue(LayoutModeProperty)!; set => SetValue(LayoutModeProperty, value); }
        public Size GridCellSize { get => (Size)GetValue(GridCellSizeProperty)!; set => SetValue(GridCellSizeProperty, value); }
        public bool IsGroupingEnabled { get => (bool)GetValue(IsGroupingEnabledProperty)!; set => SetValue(IsGroupingEnabledProperty, value); }
        public bool HideEmptyGroups { get => (bool)GetValue(HideEmptyGroupsProperty)!; set => SetValue(HideEmptyGroupsProperty, value); }
        public DataTemplate? GroupHeaderTemplate { get => (DataTemplate?)GetValue(GroupHeaderTemplateProperty); set => SetValue(GroupHeaderTemplateProperty, value); }
        public DataTemplate? GroupFooterTemplate { get => (DataTemplate?)GetValue(GroupFooterTemplateProperty); set => SetValue(GroupFooterTemplateProperty, value); }
        public Style? JumpListStyle { get => (Style?)GetValue(JumpListStyleProperty); set => SetValue(JumpListStyleProperty, value); }
        public object? ListHeader { get => GetValue(ListHeaderProperty); set => SetValue(ListHeaderProperty, value); }
        public DataTemplate? ListHeaderTemplate { get => (DataTemplate?)GetValue(ListHeaderTemplateProperty); set => SetValue(ListHeaderTemplateProperty, value); }
        public object? ListFooter { get => GetValue(ListFooterProperty); set => SetValue(ListFooterProperty, value); }
        public DataTemplate? ListFooterTemplate { get => (DataTemplate?)GetValue(ListFooterTemplateProperty); set => SetValue(ListFooterTemplateProperty, value); }

        private static void HeaderFooterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var lls = (LongListSelector)d;
            Fill(lls._listHeader, lls.ListHeader, lls.ListHeaderTemplate);
            Fill(lls._listFooter, lls.ListFooter, lls.ListFooterTemplate);
            lls.InvalidateMeasure();
        }

        private static void Fill(ContentPresenter presenter, object? content, DataTemplate? template)
        {
            presenter.ContentTemplate = template;
            presenter.Content = content;
            presenter.Visibility = content != null || template != null ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Always Idle: WPR's ScrollViewer does not report manipulation states.</summary>
        public ManipulationState ManipulationState => ManipulationState.Idle;

        public event EventHandler<ItemRealizationEventArgs>? ItemRealized;
        public event EventHandler<ItemRealizationEventArgs>? ItemUnrealized;
        public event EventHandler? JumpListOpening;
        public event EventHandler? JumpListClosed;
        public event EventHandler? ManipulationStateChanged;
        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        /// <summary>Scrolls so that <paramref name="item"/>'s row is at the top.</summary>
        public void ScrollTo(object item)
        {
            if (ItemContainerGenerator.ContainerFromItem(item) is not UIElement row) return;
            double top = 0;
            for (UIElement? e = row; e != null && !ReferenceEquals(e, ScrollViewerPart); e = e.Parent)
                top += e.ArrangedRect.Y;
            ScrollViewerPart.ScrollToVerticalOffset(top);
        }

        // ---- rows ----

        protected override DependencyObject GetContainerForItemOverride() => new ContentPresenter();

        protected override bool IsItemItsOwnContainerOverride(object item) => false;

        protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
        {
            base.PrepareContainerForItemOverride(element, item);
            InvalidateArrange();
        }

        protected override void ClearContainerForItemOverride(DependencyObject element, object item)
        {
            if (element is UIElement row && _realized.Remove(row) && row is ContentPresenter presenter)
                Raise(ItemUnrealized, presenter, LongListSelectorItemKind.Item);
            base.ClearContainerForItemOverride(element, item);
        }

        internal override Size WprArrange(Size finalSize)
        {
            Size arranged = base.WprArrange(finalSize);
            UpdateRealization();
            return arranged;
        }

        /// <summary>Raises ItemRealized for rows within a screen of the viewport and ItemUnrealized
        /// for rows that left it.</summary>
        private void UpdateRealization()
        {
            ScrollViewer scroller = ScrollViewerPart;
            double viewport = scroller.ViewportHeight > 0 ? scroller.ViewportHeight : 800;
            double windowTop = scroller.VerticalOffset - viewport;
            double windowBottom = scroller.VerticalOffset + 2 * viewport;
            double panelTop = _listHeader.Visibility == Visibility.Visible ? _listHeader.RenderSize.Height : 0;

            var changes = new List<(ContentPresenter, bool)>();
            foreach (UIElement row in HostChildren)
            {
                if (row is not ContentPresenter presenter) continue;
                double top = panelTop + row.ArrangedRect.Y;
                bool inWindow = row.RenderSize.Height > 0 && top + row.RenderSize.Height >= windowTop && top <= windowBottom;
                if (inWindow && _realized.Add(row)) changes.Add((presenter, true));
                else if (!inWindow && _realized.Remove(row)) changes.Add((presenter, false));
            }
            foreach ((ContentPresenter presenter, bool realized) in changes)
                Raise(realized ? ItemRealized : ItemUnrealized, presenter, LongListSelectorItemKind.Item);
        }

        private void Raise(EventHandler<ItemRealizationEventArgs>? handler, ContentPresenter container, LongListSelectorItemKind kind)
        {
            if (handler == null) return;
            try { handler(this, new ItemRealizationEventArgs(container, kind)); }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[wpr-items] LongListSelector item realization handler threw {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
