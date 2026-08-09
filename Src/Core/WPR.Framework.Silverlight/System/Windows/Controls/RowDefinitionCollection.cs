using System;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Backs <see cref="Grid.RowDefinitions"/>. Inherits from
    /// <see cref="PresentationFrameworkCollection{T}"/> so user IL that emits
    /// <c>callvirt PresentationFrameworkCollection&lt;RowDefinition&gt;::set_Item</c>
    /// dispatches correctly. Hooks layout-invalidation through the protected
    /// <c>OnItemsChanged</c> override.
    /// </summary>
    public class RowDefinitionCollection : PresentationFrameworkCollection<RowDefinition>
    {
        private readonly Action _onChanged;

        internal RowDefinitionCollection(Action onChanged) { _onChanged = onChanged; }

        protected override void OnItemsChanged() => _onChanged?.Invoke();
    }
}
