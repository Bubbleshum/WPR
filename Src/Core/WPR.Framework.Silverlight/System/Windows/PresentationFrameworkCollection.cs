using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.PresentationFrameworkCollection&lt;T&gt;</c>.
    /// SL's typed observable-ish list used as the public collection type for many
    /// XAML collection-valued properties (RowDefinitions, ColumnDefinitions, etc.).
    /// Mutators are <c>virtual</c> so derived collections (RowDefinitionCollection,
    /// ColumnDefinitionCollection) can hook layout-invalidation callbacks — List&lt;T&gt;'s
    /// own methods are non-virtual, so we cannot use it as a base for that.
    /// Also: user IL emits <c>callvirt PresentationFrameworkCollection&lt;T&gt;::set_Item</c>
    /// on values returned by <c>Grid.RowDefinitions</c> etc., so the concrete
    /// collections MUST actually inherit from this type for the dispatch to land
    /// in the right slot.</summary>
    public class PresentationFrameworkCollection<T> : IList<T>, IList
    {
        private readonly List<T> _items = new List<T>();

        public virtual T this[int index]
        {
            get => _items[index];
            set { _items[index] = value; OnItemsChanged(); }
        }

        public int Count => _items.Count;
        public bool IsReadOnly => false;

        public virtual void Add(T item) { _items.Add(item); OnItemsChanged(); }
        public virtual void Clear() { _items.Clear(); OnItemsChanged(); }
        public bool Contains(T item) => _items.Contains(item);
        public void CopyTo(T[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
        public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
        public int IndexOf(T item) => _items.IndexOf(item);
        public virtual void Insert(int index, T item) { _items.Insert(index, item); OnItemsChanged(); }
        public virtual bool Remove(T item)
        {
            bool r = _items.Remove(item);
            if (r) OnItemsChanged();
            return r;
        }
        public virtual void RemoveAt(int index) { _items.RemoveAt(index); OnItemsChanged(); }
        IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

        // Non-generic IList facade — XAML/XamlReader's TryAddToCollection path
        // grabs the IList interface for collection-valued properties; expose so
        // both work without ambiguity.
        bool IList.IsFixedSize => false;
        bool ICollection.IsSynchronized => false;
        object ICollection.SyncRoot => this;
        object? IList.this[int index]
        {
            get => _items[index];
            set { _items[index] = (T)value!; OnItemsChanged(); }
        }
        int IList.Add(object? value) { Add((T)value!); return _items.Count - 1; }
        bool IList.Contains(object? value) => value is T t && _items.Contains(t);
        int IList.IndexOf(object? value) => value is T t ? _items.IndexOf(t) : -1;
        void IList.Insert(int index, object? value) => Insert(index, (T)value!);
        void IList.Remove(object? value) { if (value is T t) Remove(t); }
        void ICollection.CopyTo(Array array, int index)
            => ((ICollection)_items).CopyTo(array, index);

        /// <summary>Override to react to list mutations — RowDefinitionCollection and
        /// ColumnDefinitionCollection use this to call Grid.InvalidateMeasure.</summary>
        protected virtual void OnItemsChanged() { }
    }
}
