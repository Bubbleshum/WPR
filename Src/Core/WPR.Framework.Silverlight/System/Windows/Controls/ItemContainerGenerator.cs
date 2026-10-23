namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Controls.ItemContainerGenerator</c>.</summary>
    /// <remarks>Maps between an <see cref="ItemsControl"/>'s items and the children it made for
    /// them, which are one per item and in item order.</remarks>
    public class ItemContainerGenerator : IItemContainerGenerator
    {
        private readonly ItemsControl? _owner;

        public ItemContainerGenerator() { }

        internal ItemContainerGenerator(ItemsControl owner) { _owner = owner; }

        public DependencyObject? ContainerFromIndex(int index) =>
            _owner != null && index >= 0 && index < _owner.HostChildren.Count ? _owner.HostChildren[index] : null;

        public DependencyObject? ContainerFromItem(object item)
        {
            if (_owner == null) return null;
            for (int i = 0; i < _owner.HostChildren.Count; i++)
            {
                if (Equals(_owner.ItemOfChild(i), item)) return _owner.HostChildren[i];
            }
            return null;
        }

        public int IndexFromContainer(DependencyObject container) =>
            _owner != null && container is UIElement e ? _owner.HostChildren.IndexOf(e) : -1;

        public object? ItemFromContainer(DependencyObject container)
        {
            int index = IndexFromContainer(container);
            return index >= 0 ? _owner!.ItemOfChild(index) : null;
        }
    }
}
