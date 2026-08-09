using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.Xna.Framework.Media
{
    public class Genre
    {
        internal Genre()
        {
            Name = "Unknown";
            Songs = new SongCollection();
        }

        public string Name { get; }
        public SongCollection Songs { get; }
    }
}
