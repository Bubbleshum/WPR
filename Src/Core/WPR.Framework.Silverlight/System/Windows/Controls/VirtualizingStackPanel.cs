namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.Controls.VirtualizingStackPanel</c>, ListBox's default items
    /// panel on the phone. It lays out exactly as a <see cref="StackPanel"/>. Every item is
    /// realised; there is no virtualization (WP7 lists are short enough).
    /// </summary>
    public class VirtualizingStackPanel : VirtualizingPanel
    {
        public static readonly DependencyProperty OrientationProperty =
            DependencyProperty.Register(nameof(Orientation), typeof(Orientation), typeof(VirtualizingStackPanel),
                new PropertyMetadata(Orientation.Vertical, (d, _) => (d as UIElement)?.InvalidateMeasure()));

        public Orientation Orientation
        {
            get => (Orientation)GetValue(OrientationProperty)!;
            set => SetValue(OrientationProperty, value);
        }

        internal override Size WprMeasure(Size availableSize) => StackPanel.MeasureStack(Children, Orientation, availableSize);

        internal override Size WprArrange(Size finalSize) => StackPanel.ArrangeStack(Children, Orientation, finalSize);

        public static readonly DependencyProperty VirtualizationModeProperty =
            DependencyProperty.RegisterAttached("VirtualizationMode", typeof(VirtualizationMode),
                typeof(VirtualizingStackPanel), new PropertyMetadata(VirtualizationMode.Standard));

        public static readonly DependencyProperty IsVirtualizingProperty =
            DependencyProperty.RegisterAttached("IsVirtualizing", typeof(bool),
                typeof(VirtualizingStackPanel), new PropertyMetadata(true));

        public static VirtualizationMode GetVirtualizationMode(DependencyObject element)
            => (VirtualizationMode)element.GetValue(VirtualizationModeProperty)!;

        public static void SetVirtualizationMode(DependencyObject element, VirtualizationMode value)
            => element.SetValue(VirtualizationModeProperty, value);

        public static bool GetIsVirtualizing(DependencyObject o)
            => (bool)o.GetValue(IsVirtualizingProperty)!;

        public static void SetIsVirtualizing(DependencyObject o, bool value)
            => o.SetValue(IsVirtualizingProperty, value);
    }
}
