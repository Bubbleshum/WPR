using System;
using WPR.SilverlightCompability;

namespace Microsoft.Phone.Controls
{
    /// <summary>
    /// Shim for <c>Microsoft.Phone.Controls.Panorama</c>. WP8 moved it from the toolkit's
    /// Microsoft.Phone.Controls.dll into the system Microsoft.Phone.dll, and the WP8 copy of
    /// Microsoft.Phone.Controls.dll only forwards it here. Layout, swiping and the title parallax
    /// are the renderer's (<c>WpChrome.ArrangePanorama</c>, which reads <see cref="Title"/> by
    /// name); this type holds the state games read and raises <see cref="SelectionChanged"/>
    /// when the renderer moves the selection.
    /// </summary>
    public class Panorama : TemplatedItemsControl<PanoramaItem>
    {
        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(nameof(Title), typeof(object), typeof(Panorama),
                new PropertyMetadata((object?)null));

        public static readonly DependencyProperty TitleTemplateProperty =
            DependencyProperty.Register(nameof(TitleTemplate), typeof(DataTemplate), typeof(Panorama),
                new PropertyMetadata((object?)null));

        public static readonly DependencyProperty HeaderTemplateProperty =
            DependencyProperty.Register(nameof(HeaderTemplate), typeof(DataTemplate), typeof(Panorama),
                new PropertyMetadata((object?)null));

        public static readonly DependencyProperty DefaultItemProperty =
            DependencyProperty.Register(nameof(DefaultItem), typeof(object), typeof(Panorama),
                new PropertyMetadata((object?)null, OnDefaultItemChanged));

        public static readonly DependencyProperty SelectedItemProperty =
            DependencyProperty.Register(nameof(SelectedItem), typeof(object), typeof(Panorama),
                new PropertyMetadata((object?)null, OnSelectedItemChanged));

        public static readonly DependencyProperty SelectedIndexProperty =
            DependencyProperty.Register(nameof(SelectedIndex), typeof(int), typeof(Panorama),
                new PropertyMetadata((object)(-1), OnSelectedIndexChanged));

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

        /// <summary>The item shown first. Setting it moves the display to that item.</summary>
        public object? DefaultItem
        {
            get => GetValue(DefaultItemProperty);
            set => SetValue(DefaultItemProperty, value);
        }

        /// <summary>The item on screen. Read-only on WP, as here; the renderer writes the DP.</summary>
        public object? SelectedItem => GetValue(SelectedItemProperty);

        public int SelectedIndex => (int)GetValue(SelectedIndexProperty)!;

        protected override DataTemplate? ItemHeaderTemplate => HeaderTemplate;

        public event EventHandler<SelectionChangedEventArgs>? SelectionChanged;

        protected override void OnSelectionChanged(SelectionChangedEventArgs e) =>
            SelectionChanged?.Invoke(this, e);

        private static void OnDefaultItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Panorama p) return;
            int index = p.IndexOfItem(e.NewValue);
            if (index < 0) return;
            PanoramaStateTable.GetOrCreate(p).CurrentIndex = index;
            p.SetValue(SelectedItemProperty, e.NewValue);
        }

        private static void OnSelectedItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Panorama p || p.IsSyncingSelection) return;
            p.SyncSelection(e.OldValue, e.NewValue,
                () => p.SetValue(SelectedIndexProperty, p.IndexOfItem(e.NewValue)));
        }

        private static void OnSelectedIndexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Panorama p || p.IsSyncingSelection) return;
            object? old = p.SelectedItem;
            object? item = p.ItemAt(e.NewValue is int i ? i : -1);
            p.SyncSelection(old, item, () => p.SetValue(SelectedItemProperty, item));
        }
    }
}
