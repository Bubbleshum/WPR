using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.DependencyObjectCollection&lt;T&gt;</c>: an observable list that is
    /// itself a <see cref="DependencyObject"/>. The Blend SDK's System.Windows.Interactivity (shipped
    /// in packages, unmodified) derives its behavior and trigger collections from it and attaches
    /// each item from <see cref="CollectionChanged"/>, so the change notifications carry the items,
    /// as Silverlight's did. Arkadium's pages (Microsoft Mahjong, Solitaire, Minesweeper) attach
    /// behaviors in XAML: without this type no page of theirs could be constructed.
    /// </summary>
    public class DependencyObjectCollection<T> : DependencyObject, IList<T>, IList, INotifyCollectionChanged
    {
        private readonly List<T> _items = new();

        public event NotifyCollectionChangedEventHandler? CollectionChanged;

        public T this[int index]
        {
            get => _items[index];
            set
            {
                T old = _items[index];
                _items[index] = value;
                Raise(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Replace, value, old, index));
            }
        }

        public int Count => _items.Count;

        public bool IsReadOnly => false;

        public void Add(T item)
        {
            _items.Add(item);
            Raise(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, item, _items.Count - 1));
        }

        public void Insert(int index, T item)
        {
            _items.Insert(index, item);
            Raise(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, item, index));
        }

        public bool Remove(T item)
        {
            int index = _items.IndexOf(item);
            if (index < 0)
            {
                return false;
            }

            RemoveAt(index);
            return true;
        }

        public void RemoveAt(int index)
        {
            T old = _items[index];
            _items.RemoveAt(index);
            Raise(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, old, index));
        }

        /// <summary>Silverlight raised one Remove per item before clearing, so a listener can detach each.</summary>
        public void Clear()
        {
            while (_items.Count > 0)
            {
                RemoveAt(_items.Count - 1);
            }

            Raise(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        public bool Contains(T item) => _items.Contains(item);

        public int IndexOf(T item) => _items.IndexOf(item);

        public void CopyTo(T[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);

        public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

        bool IList.IsFixedSize => false;

        bool ICollection.IsSynchronized => false;

        object ICollection.SyncRoot => this;

        object? IList.this[int index]
        {
            get => _items[index];
            set => this[index] = (T)value!;
        }

        int IList.Add(object? value)
        {
            Add((T)value!);
            return _items.Count - 1;
        }

        bool IList.Contains(object? value) => value is T t && _items.Contains(t);

        int IList.IndexOf(object? value) => value is T t ? _items.IndexOf(t) : -1;

        void IList.Insert(int index, object? value) => Insert(index, (T)value!);

        void IList.Remove(object? value)
        {
            if (value is T t)
            {
                Remove(t);
            }
        }

        void ICollection.CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);

        private void Raise(NotifyCollectionChangedEventArgs e) => CollectionChanged?.Invoke(this, e);
    }
}
