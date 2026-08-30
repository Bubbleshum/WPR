using System;
using System.Collections.Generic;
using System.Text;

namespace Microsoft.Xna.Framework.GamerServices
{
    public sealed class GamerPresence
    {
        internal GamerPresence()
        {
        }

        // No Xbox LIVE presence to report — store and return what the game set so the
        // property round-trips, which is all WP7 titles ever observed of it.
        public GamerPresenceMode PresenceMode { get; set; } = GamerPresenceMode.None;

        public int PresenceValue { get; set; }
    }
}
