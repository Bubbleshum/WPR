using System;
using System.Collections;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Items host. Inherits from StackPanel so its visual is just its Children stacked.
    /// Setting <see cref="ItemsSource"/> rebuilds Children. If <see cref="ItemTemplate"/>
    /// is set, each item's container is the template materialized with the item as
    /// DataContext (this is what enables {Binding} inside an item template).
    /// </summary>
    public class ItemsControl : StackPanel
    {
        public static readonly DependencyProperty ItemsSourceProperty =
            DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(ItemsControl),
                new PropertyMetadata((object?)null, OnItemsSourceChanged));

        public static readonly DependencyProperty ItemTemplateProperty =
            DependencyProperty.Register(nameof(ItemTemplate), typeof(DataTemplate), typeof(ItemsControl),
                new PropertyMetadata((object?)null, OnItemTemplateChanged));

        // Backing ItemCollection — keeps a parallel record of items added via the
        // Items property (or auto-populated from Children for XAML-direct cases).
        // Real Silverlight's ItemsControl uses a single Items collection; container
        // hosting happens through templating. Our renderer reads Children directly,
        // so we mirror items into Children too.
        private readonly ItemCollection _items;

        // The child drawn for each entry of _items, index for index.
        private readonly System.Collections.Generic.List<UIElement> _containers = new();

        public ItemsControl()
        {
            _items = new ItemCollection(this);
        }

        // ---- Where the containers live ----
        // An ItemsControl here is its own panel, so by default its containers are its Children. A
        // control with Silverlight's real visual tree (ListBox: ScrollViewer > ItemsPresenter >
        // items panel) puts them in that panel instead. Everything that means "the containers"
        // goes through HostChildren; Children is only the control's own visual.

        private Panel? _itemsHost;

        /// <summary>The panel the containers are children of: this control, or its items panel.</summary>
        internal Panel ItemsHost => _itemsHost ?? this;

        internal UIElementCollection HostChildren => ItemsHost.Children;

        /// <summary>Moves the containers (in order) into <paramref name="host"/>, which becomes the items host.</summary>
        internal void SetItemsHost(Panel host)
        {
            Panel old = ItemsHost;
            if (ReferenceEquals(old, host)) return;
            var moving = new System.Collections.Generic.List<UIElement>();
            foreach (UIElement child in old.Children) moving.Add(child);
            if (!ReferenceEquals(old, this)) { old.Children.Clear(); old.ItemsOwner = null; }
            else foreach (UIElement child in moving) Children.Remove(child);
            _itemsHost = ReferenceEquals(host, this) ? null : host;
            host.ItemsOwner = this;
            foreach (UIElement child in moving) host.Children.Add(child);
        }

        /// <summary>Silverlight's <c>ItemsControl.GetItemsOwner</c>: the control whose items panel
        /// <paramref name="element"/> is, or null.</summary>
        public static ItemsControl? GetItemsOwner(DependencyObject element) =>
            element is Panel p ? p.ItemsOwner : null;

        public IEnumerable? ItemsSource
        {
            get => (IEnumerable?)GetValue(ItemsSourceProperty);
            set => SetValue(ItemsSourceProperty, value);
        }

        public DataTemplate? ItemTemplate
        {
            get => (DataTemplate?)GetValue(ItemTemplateProperty);
            set => SetValue(ItemTemplateProperty, value);
        }

        /// <summary>
        /// The runtime items collection. Toolkit code (Panorama's OnSelectionChanged
        /// in particular) accesses this to find the index of a selected value and
        /// raise SelectionChanged events. If we returned null or didn't expose this
        /// at all, those callbacks would throw <c>MissingMethodException</c> /
        /// <c>NullReferenceException</c> mid-SetValue, breaking selection sync.
        ///
        /// We lazily mirror <see cref="Panel.Children"/> into the underlying
        /// <see cref="ItemCollection"/> on each read, so XAML that adds items
        /// directly as children (the common case — <c>&lt;Panorama&gt;&lt;PanoramaItem/&gt;...&lt;/Panorama&gt;</c>)
        /// still produces a sensible Items collection without the toolkit having
        /// to call <c>Items.Add</c> explicitly.
        /// </summary>
        public ItemCollection Items
        {
            get
            {
                // Resync from Children (or ItemsSource) when something other than Items changed
                // them. Cheap for the small Panorama/Pivot counts we deal with (typically 2-5 items).
                if (_items.Count != HostChildren.Count || !SameOrder())
                {
                    _items.Syncing = true;
                    try
                    {
                        _items.Clear();
                        _containers.Clear();
                        for (int i = 0; i < HostChildren.Count; i++)
                        {
                            _items.Add(ItemOfChild(i)!);
                            _containers.Add(HostChildren[i]);
                        }
                    }
                    finally { _items.Syncing = false; }
                }
                return _items;
            }
        }

        private bool SameOrder()
        {
            if (_containers.Count != HostChildren.Count) return false;
            for (int i = 0; i < HostChildren.Count; i++)
            {
                if (!ReferenceEquals(_containers[i], HostChildren[i])) return false;
            }
            return true;
        }

        // ---- Items written in code: one container per item, at the same index ----

        internal void ItemInserted(int index, object item)
        {
            UIElement container = ContainerFor(item);
            int at = System.Math.Min(index, HostChildren.Count);
            HostChildren.Insert(at, container);
            _containers.Insert(System.Math.Min(index, _containers.Count), container);
            ApplyContainerStyle(container);
            Prepare(container, item);
            RaiseItemsChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(
                System.Collections.Specialized.NotifyCollectionChangedAction.Add, item, index));
        }

        internal void ItemRemoved(int index, object item)
        {
            if (index < _containers.Count)
            {
                UIElement container = _containers[index];
                Unprepare(container, item);
                HostChildren.Remove(container);
                _containers.RemoveAt(index);
            }
            RaiseItemsChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(
                System.Collections.Specialized.NotifyCollectionChangedAction.Remove, item, index));
        }

        internal void ItemReplaced(int index, object item)
        {
            if (index < _containers.Count)
            {
                HostChildren.Remove(_containers[index]);
                _containers.RemoveAt(index);
            }
            UIElement container = ContainerFor(item);
            HostChildren.Insert(System.Math.Min(index, HostChildren.Count), container);
            _containers.Insert(System.Math.Min(index, _containers.Count), container);
            ApplyContainerStyle(container);
            Prepare(container, item);
            RaiseItemsChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(
                System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
        }

        // ---- Container preparation ----
        // Silverlight's generator calls PrepareContainerForItemOverride for every item, including
        // one that is its own container (a PivotItem written in XAML), and the toolkit's
        // TemplatedItemsControl maps item to container only there: Pivot.GetContainer answered
        // null for every XAML item, so LoadedPivotItem was raised with no item (Fox News's handler
        // then failed). Containers made here are prepared when made; the ones XAML put in the
        // panel are prepared before the first measure or Loaded.

        private readonly System.Collections.Generic.HashSet<UIElement> _prepared = new();

        private void Prepare(DependencyObject container, object item)
        {
            if (container is UIElement element) _prepared.Add(element);
            PrepareContainerForItemOverride(container, item);
        }

        private void Unprepare(DependencyObject container, object item)
        {
            if (container is UIElement element) _prepared.Remove(element);
            ClearContainerForItemOverride(container, item);
        }

        internal override void WprBeforeMeasure()
        {
            base.WprBeforeMeasure();
            for (int i = 0; i < HostChildren.Count; i++)
            {
                UIElement child = HostChildren[i];
                if (_prepared.Contains(child)) continue;
                object? item = ItemOfChild(i);
                if (item == null) continue;
                try { Prepare(child, item); }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine(
                        $"[wpr-items] {GetType().Name}.PrepareContainerForItemOverride threw {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        internal void ItemsCleared()
        {
            _prepared.Clear();
            foreach (UIElement container in _containers) HostChildren.Remove(container);
            _containers.Clear();
            RaiseItemsChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(
                System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
        }

        private UIElement ContainerFor(object item)
        {
            if (item != null && !IsItemItsOwnContainerOverride(item) && HasCustomContainers()
                && GetContainerForItemOverride() is ContentControl custom)
            {
                // As Silverlight's ItemsControl prepares one: the container holds the ITEM and
                // draws it through the item template. A handler reading the container's Content
                // (Arkadium's UniformGrid raises Select with it) gets the data, not the visuals.
                custom.ContentTemplate = ItemTemplate;
                custom.Content = item;
                return custom;
            }

            return MakeContainer(item);
        }

        private bool HasCustomContainers() =>
            GetType().GetMethod(nameof(GetContainerForItemOverride),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.DeclaringType
            != typeof(ItemsControl);

        private void RaiseItemsChanged(System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            try { OnItemsChanged(e); }
            catch (System.Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"[wpr-items] {GetType().Name}.OnItemsChanged threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not ItemsControl ic) return;
            if (e.OldValue is System.Collections.Specialized.INotifyCollectionChanged oldSource)
                oldSource.CollectionChanged -= ic.OnItemsSourceCollectionChanged;
            if (e.NewValue is System.Collections.Specialized.INotifyCollectionChanged newSource)
                newSource.CollectionChanged += ic.OnItemsSourceCollectionChanged;
            ic.Rebuild();
        }

        /// <summary>
        /// Silverlight follows an observable ItemsSource. WPR used to build the rows once, when the
        /// source was assigned, so a list bound to an ObservableCollection that fills in later (the
        /// normal shape: bind, then download) stayed empty for ever. Adds and removes touch only the
        /// rows concerned, so the others keep their containers; anything else rebuilds.
        /// </summary>
        private void OnItemsSourceCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (!ReferenceEquals(sender, ItemsSource)) return;
            System.Collections.Generic.List<object?> now = SourceNow();
            try
            {
                switch (e.Action)
                {
                    // The rows already show the source (an event that arrived after a rebuild).
                    case var _ when _shown != null && SameItems(_shown, now):
                        break;

                    case System.Collections.Specialized.NotifyCollectionChangedAction.Add
                        when e.NewItems != null && e.NewStartingIndex >= 0 && e.NewStartingIndex <= HostChildren.Count
                             && AddMatches(now, e.NewItems, e.NewStartingIndex):
                        for (int i = 0; i < e.NewItems.Count; i++)
                        {
                            object? item = e.NewItems[i];
                            UIElement container = ContainerFor(item!);
                            HostChildren.Insert(e.NewStartingIndex + i, container);
                            ApplyContainerStyle(container);
                            Prepare(container, item!);
                        }
                        _shown = now;
                        RaiseItemsChanged(e);
                        break;

                    case System.Collections.Specialized.NotifyCollectionChangedAction.Remove
                        when e.OldItems != null && e.OldStartingIndex >= 0 && e.OldStartingIndex + e.OldItems.Count <= HostChildren.Count:
                        for (int i = 0; i < e.OldItems.Count; i++)
                        {
                            UIElement container = HostChildren[e.OldStartingIndex];
                            Unprepare(container, e.OldItems[i]!);
                            HostChildren.RemoveAt(e.OldStartingIndex);
                        }
                        _shown = now;
                        RaiseItemsChanged(e);
                        break;

                    default:
                        Rebuild();
                        break;
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"[wpr-items] {GetType().Name}: following ItemsSource ({e.Action}) failed: {ex.GetType().Name}: {ex.Message}; rebuilding");
                Rebuild();
            }
        }

        // The source's items as the rows show them, by position; null when not following one.
        private System.Collections.Generic.List<object?>? _shown;

        private System.Collections.Generic.List<object?> SourceNow() =>
            ItemsSource == null ? new() : new System.Collections.Generic.List<object?>(SourceItems(ItemsSource));

        private static bool SameItems(System.Collections.Generic.List<object?> a, System.Collections.Generic.List<object?> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!ReferenceEquals(a[i], b[i]) && !Equals(a[i], b[i])) return false;
            return true;
        }

        /// <summary>
        /// Whether an Add of <paramref name="added"/> at <paramref name="index"/> turns the rows
        /// into the source as it is now. A source may raise its events late (through the
        /// dispatcher) or with a wrong index; Fox News reports each Add one past its index, after
        /// rows built from the source already held the item, so items came out twice. Such an
        /// event is checked against the source instead.
        /// </summary>
        private bool AddMatches(System.Collections.Generic.List<object?> now, IList added, int index)
        {
            if (_shown == null || now.Count != _shown.Count + added.Count) return false;
            var expected = new System.Collections.Generic.List<object?>(_shown);
            for (int i = 0; i < added.Count; i++) expected.Insert(index + i, added[i]);
            return SameItems(expected, now);
        }

        private static void OnItemTemplateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ItemsControl ic) ic.Rebuild();
        }

        public static readonly DependencyProperty ItemsPanelProperty =
            DependencyProperty.Register(nameof(ItemsPanel), typeof(ItemsPanelTemplate), typeof(ItemsControl),
                new PropertyMetadata((object?)null, OnItemsPanelChanged));

        /// <summary>
        /// The panel the items are laid out in. This control is its own panel, a StackPanel, so a
        /// StackPanel template (VirtualizingStackPanel included) is honoured by taking its
        /// <see cref="StackPanel.Orientation"/>, and a Grid by the uniform-grid layout below. Any
        /// other panel (the toolkit's WrapPanel) is not hosted: the items stay stacked.
        /// </summary>
        public ItemsPanelTemplate? ItemsPanel
        {
            get => (ItemsPanelTemplate?)GetValue(ItemsPanelProperty);
            set => SetValue(ItemsPanelProperty, value);
        }

        private static void OnItemsPanelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ItemsControl ic) ic.ItemsPanelChanged(e.NewValue as ItemsPanelTemplate);
        }

        /// <summary>Builds the panel <paramref name="template"/> describes, or null (traced) if it cannot.</summary>
        internal static Panel? LoadItemsPanel(ItemsPanelTemplate? template)
        {
            if (template?.VisualTree == null) return null;
            try { return template.LoadContent() as Panel; }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[wpr-uirender] ItemsPanel could not be built: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>This control is its own panel: a stack template lends it its orientation (a Grid
        /// has the layout below). A control with a real items panel (ListBox) hosts it instead.</summary>
        internal virtual void ItemsPanelChanged(ItemsPanelTemplate? template)
        {
            if (template?.VisualTree?.Name.LocalName is not ("StackPanel" or "VirtualizingStackPanel")) return;
            switch (LoadItemsPanel(template))
            {
                case StackPanel sp: Orientation = sp.Orientation; break;
                case VirtualizingStackPanel vsp: Orientation = vsp.Orientation; break;
            }
        }

        public static readonly DependencyProperty ItemContainerStyleProperty =
            DependencyProperty.Register(nameof(ItemContainerStyle), typeof(Style), typeof(ItemsControl),
                new PropertyMetadata((object?)null, OnItemTemplateChanged));

        /// <summary>
        /// Style given to each item's container. Declared here (where Silverlight 5 has it) so IL
        /// naming <c>ListBox::set_ItemContainerStyle</c> resolves through the base class too.
        /// Applied to the container this control makes when the style's TargetType fits it, and
        /// never over a Style the container already has.
        /// </summary>
        public Style? ItemContainerStyle
        {
            get => (Style?)GetValue(ItemContainerStyleProperty);
            set => SetValue(ItemContainerStyleProperty, value);
        }

        private void ApplyContainerStyle(UIElement container)
        {
            Style? style = ItemContainerStyle;
            if (style != null && container is FrameworkElement fe && fe.Style == null &&
                (style.TargetType == null || style.TargetType.IsInstanceOfType(fe)))
                fe.Style = style;
        }

        // ---- An ItemsPanel that is a Grid ----
        // WPR's ItemsControl is its own panel and stacks its containers. A control that names a
        // Grid as its items panel positions each container with Grid.Row / Grid.Column instead
        // (Arkadium's UniformGrid: a two-column grid of game tiles), so that case is laid out as
        // a uniform grid of the largest container's size.

        private bool UsesGridPanel(out int rows, out int columns)
        {
            rows = columns = 0;
            if (ItemsPanel?.VisualTree?.Name.LocalName != "Grid") return false;
            foreach (UIElement child in Children)
            {
                rows = Math.Max(rows, Grid.GetRow(child) + 1);
                columns = Math.Max(columns, Grid.GetColumn(child) + 1);
            }
            return Children.Count > 0 && (rows > 1 || columns > 1);
        }

        private Size _gridCell;

        internal override Size WprMeasure(Size availableSize)
        {
            if (!UsesGridPanel(out int rows, out int columns)) return base.WprMeasure(availableSize);
            double w = 0, h = 0;
            foreach (UIElement child in Children)
            {
                child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                w = Math.Max(w, child.DesiredSize.Width);
                h = Math.Max(h, child.DesiredSize.Height);
            }
            _gridCell = new Size(w, h);
            return new Size(w * columns, h * rows);
        }

        internal override Size WprArrange(Size finalSize)
        {
            if (!UsesGridPanel(out int rows, out int columns)) return base.WprArrange(finalSize);
            // Share the width out as the Grid's star columns would.
            double cellW = columns > 0 ? Math.Max(_gridCell.Width, finalSize.Width / columns) : _gridCell.Width;
            foreach (UIElement child in Children)
            {
                child.Arrange(new Rect(Grid.GetColumn(child) * cellW, Grid.GetRow(child) * _gridCell.Height,
                    cellW, _gridCell.Height));
            }
            return finalSize;
        }

        private ItemContainerGenerator? _generator;

        /// <summary>Maps items to the children made for them.</summary>
        public ItemContainerGenerator ItemContainerGenerator => _generator ??= new ItemContainerGenerator(this);

        /// <summary>The item each child was made for, in child order (the child itself when it is its own container).</summary>
        internal object? ItemOfChild(int index)
        {
            if (ItemsSource == null) return index >= 0 && index < HostChildren.Count ? HostChildren[index] : null;
            int j = 0;
            foreach (object? item in SourceItems(ItemsSource))
            {
                if (j == index) return item;
                j++;
            }
            return null;
        }

        /// <summary>
        /// The items of <paramref name=source/>, through its generic enumerator when it has one
        /// over a reference type. Fox News's RssItemObservableCollection implements only that one
        /// (its non-generic GetEnumerator throws NotImplementedException).
        /// </summary>
        private static System.Collections.Generic.IEnumerable<object?> SourceItems(IEnumerable source) =>
            source as System.Collections.Generic.IEnumerable<object?> ?? System.Linq.Enumerable.Cast<object?>(source);

        private void Rebuild()
        {
            for (int i = 0; i < HostChildren.Count; i++)
                Unprepare(HostChildren[i], ItemOfChild(i)!);
            HostChildren.Clear();
            if (ItemsSource == null) { _shown = null; return; }
            _shown = SourceNow();
            foreach (object? item in _shown)
            {
                // A subclass with its own container type (a selectable grid's item, say) gets it,
                // holding what this control would have shown, so its Prepare override finds the
                // container it expects.
                UIElement container = ContainerFor(item!);
                HostChildren.Add(container);
                ApplyContainerStyle(container);
                Prepare(container, item!);
            }
            RaiseItemsChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(
                System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
        }

        /// <summary>Whether <paramref name="item"/> can stand in the panel as it is.</summary>
        protected virtual bool IsItemItsOwnContainerOverride(object item) => item is UIElement;

        /// <summary>A new container for an item that is not its own.</summary>
        protected virtual DependencyObject GetContainerForItemOverride() => new ContentPresenter();

        /// <summary>Called once a container holds its item. The base does nothing more than <see cref="MakeContainer"/> did.</summary>
        protected virtual void PrepareContainerForItemOverride(DependencyObject element, object item) { }

        /// <summary>Called before a container loses its item.</summary>
        protected virtual void ClearContainerForItemOverride(DependencyObject element, object item) { }

        /// <summary>Called when the items change. Raised for ItemsSource replacement only.</summary>
        protected virtual void OnItemsChanged(System.Collections.Specialized.NotifyCollectionChangedEventArgs e) { }

        protected virtual UIElement MakeContainer(object? item)
        {
            DataTemplate? template = ItemTemplate;
            if (template != null)
            {
                UIElement? produced = template.LoadContent() as UIElement;
                if (produced is FrameworkElement fe)
                {
                    fe.DataContext = item;
                    return fe;
                }
                if (produced != null) return produced;
            }

            if (item is UIElement el) return el;
            return new TextBlock { Text = item?.ToString() ?? string.Empty };
        }
    }
}
