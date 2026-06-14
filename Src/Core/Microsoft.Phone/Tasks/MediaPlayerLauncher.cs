using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.Phone.Tasks
{
    public class MediaPlayerLauncher
    {
        public MediaLocationType Location { get; set; }

        public Uri Media { get; set; }

        public MediaPlaybackControls Controls { get; set; }

        public MediaPlayerOrientation Orientation { get; set; }

        /// <summary>
        /// Raised when <see cref="Show"/> is called. On real WP7 the launcher hands off
        /// to the system media player and the app is reactivated when it returns; the
        /// host (WPR.ApplicationLaunch) subscribes here to emulate that round-trip.
        /// This assembly is a dependency-free facade, so it can't drive the game/lifecycle
        /// directly — it just signals, and the host bridges to the game thread.
        /// </summary>
        public static event Action<MediaPlayerLauncher>? Launched;

        public void Show()
        {
            // We don't actually play the video (no cross-platform .wmv backend, and the
            // clips are skippable cutscenes). Signal "launched" so the host can emulate
            // the launcher returning, which fires the app-reactivation the game waits on
            // to advance (e.g. Hoth's CVideoPlayer -> OnGameActivated -> LoadLevel).
            Launched?.Invoke(this);
        }
    }
}
