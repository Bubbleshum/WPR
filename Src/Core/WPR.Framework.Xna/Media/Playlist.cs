using System;

namespace Microsoft.Xna.Framework.Media
{
    /// <summary>
    /// Shim for <c>Microsoft.Xna.Framework.Media.Playlist</c> — one playlist in the phone's music
    /// library.
    ///
    /// <para>Always empty, for the same reason <see cref="Album"/> and <see cref="Artist"/> are:
    /// WPR has no phone music library to enumerate, and a device with no playlists is a state every
    /// WP7 title had to cope with anyway. The type has to EXIST though — a game that merely asks
    /// how many playlists there are loads it, and until 2026-09-18 nothing in WPR defined it at
    /// all. See <see cref="PlaylistCollection"/> for what that cost.</para>
    /// </summary>
    public sealed class Playlist : IDisposable
    {
        internal Playlist()
        {
            Name = "Unknown";
            Songs = new SongCollection();
            Duration = TimeSpan.Zero;
        }

        public TimeSpan Duration { get; }

        public string Name { get; }

        public SongCollection Songs { get; }

        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }
}
