namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Controls.ItemCollection</c>.
    /// In SL this is the runtime collection backing ItemsControl.Items.
    /// Inherits from <see cref="PresentationFrameworkCollection{T}"/> because
    /// user / toolkit IL emits <c>callvirt PresentationFrameworkCollection&lt;object&gt;::IndexOf</c>
    /// against it — if we used <c>List&lt;object&gt;</c> as the base, that virtual call
    /// would land in the wrong slot (or fail outright on a missing override).
    /// </summary>
    /// <remarks>
    /// Writes go through to the owner's children: a game that fills a panorama in code
    /// (<c>panorama.Items.Add(new PanoramaItem …)</c>, Arkadium's GamePanoramaControl) gets one
    /// container per item, which is what the renderer draws.
    /// </remarks>
    public class ItemCollection : PresentationFrameworkCollection<object>
    {
        private readonly ItemsControl? _owner;

        /// <summary>Set while the owner copies its children in, so that copy is not written back.</summary>
        internal bool Syncing { get; set; }

        public ItemCollection() { }

        internal ItemCollection(ItemsControl owner) { _owner = owner; }

        public override object this[int index]
        {
            get => base[index];
            set
            {
                base[index] = value;
                if (!Syncing) _owner?.ItemReplaced(index, value);
            }
        }

        public override void Add(object item) => Insert(Count, item);

        public override void Insert(int index, object item)
        {
            base.Insert(index, item);
            if (!Syncing) _owner?.ItemInserted(index, item);
        }

        public override void RemoveAt(int index)
        {
            object item = base[index];
            base.RemoveAt(index);
            if (!Syncing) _owner?.ItemRemoved(index, item);
        }

        public override bool Remove(object item)
        {
            int index = IndexOf(item);
            if (index < 0) return false;
            RemoveAt(index);
            return true;
        }

        public override void Clear()
        {
            base.Clear();
            if (!Syncing) _owner?.ItemsCleared();
        }
    }
}
