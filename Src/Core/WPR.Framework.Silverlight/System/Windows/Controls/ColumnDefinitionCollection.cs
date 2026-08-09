using System;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Backs <see cref="Grid.ColumnDefinitions"/>. Inherits from
    /// <see cref="PresentationFrameworkCollection{T}"/> so user IL that emits
    /// <c>callvirt PresentationFrameworkCollection&lt;ColumnDefinition&gt;::set_Item</c>
    /// dispatches correctly. Hooks layout-invalidation through the protected
    /// <c>OnItemsChanged</c> override.
    /// </summary>
    public class ColumnDefinitionCollection : PresentationFrameworkCollection<ColumnDefinition>
    {
        private readonly Action _onChanged;

        internal ColumnDefinitionCollection(Action onChanged) { _onChanged = onChanged; }

        protected override void OnItemsChanged() => _onChanged?.Invoke();
    }
}
