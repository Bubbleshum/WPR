using System;
using System.Collections;
using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Children collection for Panel. Mutations call back the owning panel to invalidate
    /// layout. This is a deliberately thin shim — no INotifyCollectionChanged in 1.5a.
    /// </summary>
    public class UIElementCollection : IList<UIElement>
    {
        private readonly List<UIElement> _items = new();
        private readonly UIElement _owner;
        private readonly Action _onChanged;

        internal UIElementCollection(UIElement owner, Action onChanged)
        {
            _owner = owner;
            _onChanged = onChanged;
        }

        public UIElement this[int index]
        {
            get => _items[index];
            set
            {
                _items[index].SetParent(null);
                value.SetParent(_owner);
                _items[index] = value;
                _onChanged();
            }
        }

        public int Count => _items.Count;
        public bool IsReadOnly => false;

        public void Add(UIElement item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            item.SetParent(_owner);
            _items.Add(item);
            _onChanged();
        }

        public void Clear()
        {
            if (_items.Count == 0) return;
            foreach (var item in _items) item.SetParent(null);
            _items.Clear();
            _onChanged();
        }

        public bool Contains(UIElement item) => _items.Contains(item);
        public void CopyTo(UIElement[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
        public IEnumerator<UIElement> GetEnumerator() => _items.GetEnumerator();
        public int IndexOf(UIElement item) => _items.IndexOf(item);

        public void Insert(int index, UIElement item)
        {
            item.SetParent(_owner);
            _items.Insert(index, item);
            _onChanged();
        }

        public bool Remove(UIElement item)
        {
            bool removed = _items.Remove(item);
            if (removed) { item.SetParent(null); _onChanged(); }
            return removed;
        }

        public void RemoveAt(int index)
        {
            var item = _items[index];
            item.SetParent(null);
            _items.RemoveAt(index);
            _onChanged();
        }

        IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
    }
}
