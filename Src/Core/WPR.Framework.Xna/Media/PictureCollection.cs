using System.Collections;
using System.Collections.Generic;

namespace Microsoft.Xna.Framework.Media
{
    /// <summary>
    /// WP7's photo-gallery collection. Always empty — see <see cref="Picture"/> for the
    /// rationale (desktop has no phone photo gallery; an empty collection is the safest fake).
    /// </summary>
    public sealed class PictureCollection : IEnumerable<Picture>, IEnumerable
    {
        private readonly List<Picture> _Pictures;

        internal PictureCollection()
        {
            _Pictures = new List<Picture>();
        }

        public IEnumerator<Picture> GetEnumerator() => _Pictures.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => _Pictures.GetEnumerator();

        public int Count => _Pictures.Count;

        public Picture this[int index] => _Pictures[index];
    }
}
