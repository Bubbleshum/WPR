using System;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.Controls.ListBox</c>. Silverlight's chain,
    /// <c>ItemsControl &lt;- Selector &lt;- ListBox</c>: the selection lives on
    /// <see cref="Selector"/> (see the remarks there for why the base is not optional).
    /// </summary>
    /// <remarks>
    /// <para><b>It has Silverlight's default visual tree, and draws through it</b>: a
    /// <see cref="ScrollViewer"/> named "ScrollViewer", an <see cref="ItemsPresenter"/> in it,
    /// and the items panel (<see cref="ItemsControl.ItemsPanel"/>, a
    /// <see cref="VirtualizingStackPanel"/> by default) in that, holding the containers. Code
    /// that ships with an app and walks that tree runs as it does on the phone: Fox News's
    /// LazyListBox takes the ScrollViewer from <c>GetTemplateChild</c> in
    /// <see cref="FrameworkElement.OnApplyTemplate"/> (raised once, before the first measure)
    /// and finds the panel below it with <c>VisualTreeHelper</c>; any ItemsPanel (the toolkit's
    /// WrapPanel, a Grid) lays the containers out itself.</para>
    ///
    /// <para>A ListBox's own <c>Template</c> is stored, not drawn: this tree is used whatever
    /// template it names. A tap on a row selects it (the input router calls
    /// <see cref="Selector.SelectContainer"/>).</para>
    /// </remarks>
    [ContentProperty(nameof(Items))]
    public class ListBox : Selector
    {
        // The selection properties are declared on Selector; ListBox re-exposes the same objects
        // under its own name because game IL and XAML name them as ListBox.SelectedIndexProperty,
        // and a field reference is resolved on the type it names.
        public static new readonly DependencyProperty SelectedIndexProperty = Selector.SelectedIndexProperty;
        public static new readonly DependencyProperty SelectedItemProperty = Selector.SelectedItemProperty;

        public static readonly DependencyProperty SelectionModeProperty =
            DependencyProperty.Register(nameof(SelectionMode), typeof(SelectionMode), typeof(ListBox),
                new PropertyMetadata(SelectionMode.Single));

        public SelectionMode SelectionMode
        {
            get => (SelectionMode)GetValue(SelectionModeProperty)!;
            set => SetValue(SelectionModeProperty, value);
        }

        private readonly ScrollViewer _scrollViewer;
        private readonly ItemsPresenter _itemsPresenter;

        public ListBox()
        {
            _itemsPresenter = new ItemsPresenter();
            _scrollViewer = new ScrollViewer { Name = "ScrollViewer", Content = _itemsPresenter };
            // The panel first: no container exists yet, so SetItemsHost moves nothing, and the
            // scroll viewer added after it is this control's only child.
            UseItemsPanel(new VirtualizingStackPanel());
            Children.Add(_scrollViewer);
            Control.SetBuiltInTemplateRoot(this, _scrollViewer);
        }

        internal override void ItemsPanelChanged(ItemsPanelTemplate? template) =>
            UseItemsPanel(LoadItemsPanel(template) ?? new VirtualizingStackPanel());

        private void UseItemsPanel(Panel panel)
        {
            SetItemsHost(panel);
            HostItemsPanel(_itemsPresenter, panel);
        }

        /// <summary>Puts <paramref name="panel"/> in the ItemsPresenter. A list with more in its
        /// scrolling area (LongListSelector's header and footer) arranges that here. Called from
        /// the constructor, so an override may run before its own constructor body.</summary>
        internal virtual void HostItemsPanel(ItemsPresenter presenter, Panel panel)
        {
            presenter.Children.Clear();
            presenter.Children.Add(panel);
        }

        /// <summary>The template's ScrollViewer.</summary>
        internal ScrollViewer ScrollViewerPart => _scrollViewer;

        internal override void WprBeforeMeasure()
        {
            if (Control.BeginApplyTemplate(this))
            {
                try { OnApplyTemplate(); }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine(
                        $"[wpr-template] {GetType().Name}.OnApplyTemplate threw {ex.GetType().Name}: {ex.Message}");
                }
            }
            base.WprBeforeMeasure(); // prepares the containers XAML declared
        }

        internal override Size WprMeasure(Size availableSize)
        {
            // What the template's TemplateBindings pass on: ScrollViewer.*ScrollBarVisibility set
            // on the ListBox (attached, often "Disabled" to stop a list scrolling).
            _scrollViewer.HorizontalScrollBarVisibility = (ScrollBarVisibility)GetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty)!;
            _scrollViewer.VerticalScrollBarVisibility = (ScrollBarVisibility)GetValue(ScrollViewer.VerticalScrollBarVisibilityProperty)!;
            _scrollViewer.Measure(availableSize);
            return _scrollViewer.DesiredSize;
        }

        internal override Size WprArrange(Size finalSize)
        {
            _scrollViewer.Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));
            return finalSize;
        }
    }
}
