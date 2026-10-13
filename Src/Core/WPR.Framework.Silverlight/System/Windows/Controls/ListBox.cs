using System;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.Controls.ListBox</c>. Silverlight's chain,
    /// <c>ItemsControl &lt;- Selector &lt;- ListBox</c>: the selection lives on
    /// <see cref="Selector"/> (see the remarks there for why the base is not optional). The visual
    /// is the inherited <see cref="ItemsControl"/> StackPanel over
    /// <see cref="ItemsControl.ItemsSource"/>, and a tap on a row selects it (the input router
    /// calls <see cref="Selector.SelectContainer"/>).
    /// </summary>
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
    }
}
