using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Controls.ItemContainerGenerator</c>.
    /// User code rarely touches this directly; needs to exist for typerefs.</summary>
    public class ItemContainerGenerator
    {
        public DependencyObject? ContainerFromIndex(int index) => null;
        public DependencyObject? ContainerFromItem(object item) => null;
        public int IndexFromContainer(DependencyObject container) => -1;
        public object? ItemFromContainer(DependencyObject container) => null;
    }
}
