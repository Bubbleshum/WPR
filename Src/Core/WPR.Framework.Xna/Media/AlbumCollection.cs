using System.Collections;
using System.Collections.Generic;

namespace Microsoft.Xna.Framework.Media
{
    public sealed class AlbumCollection : IEnumerable<Album>, IEnumerable
    {
        private List<Album> _Albums;

        internal AlbumCollection()
        {
            _Albums = new List<Album>();
        }

        public IEnumerator<Album> GetEnumerator() => _Albums.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => _Albums.GetEnumerator();

        public int Count => _Albums.Count;

        /// <summary>
        /// XNA has this and WPR did not, which made walking the collection by index — the obvious
        /// way to pair it with <see cref="Count"/>, and what Fast and the Furious: Adrenaline does
        /// — fail to resolve. <see cref="PictureCollection"/> already had its indexer; this and
        /// <see cref="ArtistCollection"/> were the two that did not.
        /// </summary>
        public Album this[int index] => _Albums[index];
    }
}
