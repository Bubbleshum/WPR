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
        private readonly ItemCollection _items = new ItemCollection();

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
                // Resync from Children. Cheap for the small Panorama/Pivot counts
                // we deal with (typically 2-5 items).
                if (_items.Count != Children.Count || !SameOrder())
                {
                    _items.Clear();
                    foreach (UIElement child in Children) _items.Add(child);
                }
                return _items;
            }
        }

        private bool SameOrder()
        {
            for (int i = 0; i < Children.Count; i++)
            {
                if (!ReferenceEquals(_items[i], Children[i])) return false;
            }
            return true;
        }

        private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ItemsControl ic) ic.Rebuild();
        }

        private static void OnItemTemplateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ItemsControl ic) ic.Rebuild();
        }

        private void Rebuild()
        {
            Children.Clear();
            if (ItemsSource == null) return;
            foreach (object? item in ItemsSource)
                Children.Add(MakeContainer(item));
        }

        protected virtual UIElement MakeContainer(object? item)
        {
            DataTemplate? template = ItemTemplate;
            if (template != null)
            {
                UIElement? produced = template.LoadContent();
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
