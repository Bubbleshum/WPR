using System;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Children collection for Panel. Mutations call back the owning panel to invalidate
    /// layout. This is a deliberately thin shim — no INotifyCollectionChanged in 1.5a.
    /// </summary>
    /// <remarks>
    /// <b>The base class is load-bearing.</b> Silverlight declares <c>Add</c>, <c>Clear</c> and
    /// the rest on <c>PresentationFrameworkCollection&lt;UIElement&gt;</c> and
    /// <c>UIElementCollection</c> redeclares none of them, so ordinary game code —
    /// <c>LayoutRoot.Children.Add(x)</c> — compiles to
    /// <c>callvirt PresentationFrameworkCollection`1&lt;UIElement&gt;::Add</c>. This type was a
    /// bare <c>IList&lt;UIElement&gt;</c> until 2026-09-22, so that call had nowhere to land; the
    /// remarks on the base class already warned that concrete collections must really inherit
    /// from it. Found through Rabbids Go Phone, whose loading screen parks a progress bar in the
    /// page's Canvas.
    /// </remarks>
    public class UIElementCollection : PresentationFrameworkCollection<UIElement>
    {
        private readonly UIElement _owner;
        private readonly Action _onChanged;

        internal UIElementCollection(UIElement owner, Action onChanged)
        {
            _owner = owner;
            _onChanged = onChanged;
        }

        public override UIElement this[int index]
        {
            get => base[index];
            set
            {
                base[index]?.SetParent(null);
                value?.SetParent(_owner);
                base[index] = value;
            }
        }

        public override void Add(UIElement item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            item.SetParent(_owner);
            base.Add(item);
        }

        public override void Clear()
        {
            if (Count == 0) return;
            foreach (UIElement item in this) item?.SetParent(null);
            base.Clear();
        }

        public override void Insert(int index, UIElement item)
        {
            item?.SetParent(_owner);
            base.Insert(index, item);
        }

        public override bool Remove(UIElement item)
        {
            bool removed = base.Remove(item);
            if (removed) item?.SetParent(null);
            return removed;
        }

        public override void RemoveAt(int index)
        {
            base[index]?.SetParent(null);
            base.RemoveAt(index);
        }

        /// <summary>
        /// The parent panel's layout invalidation. Routed through the base's hook so it fires for
        /// mutations that arrive via the non-generic <c>IList</c> facade too — which is the path
        /// the XAML reader uses for collection-valued properties.
        /// </summary>
        protected override void OnItemsChanged() => _onChanged?.Invoke();
    }
}
