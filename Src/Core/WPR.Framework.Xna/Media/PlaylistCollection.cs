using System;
using System.Collections;
using System.Collections.Generic;

namespace Microsoft.Xna.Framework.Media
{
    /// <summary>
    /// Shim for <c>Microsoft.Xna.Framework.Media.PlaylistCollection</c>. Always empty — see
    /// <see cref="Playlist"/>.
    ///
    /// <para><b>Why this type earned its own note.</b> It and <see cref="Playlist"/> were the only
    /// two <c>Microsoft.Xna.Framework.Media</c> types WPR did not define, and the failure that
    /// caused is nothing like "playlists don't work". Fast and the Furious: Adrenaline
    /// (<c>{ad67744a-…}</c>) asks for the playlist count from <c>FF7Base.setNumberOfPlaylists()</c>,
    /// which its constructor calls — and that constructor is the root of a chain fourteen objects
    /// deep ending at <c>FF73DGame.createApplication</c>. The <c>TypeLoadException</c> unwound all
    /// of it, so the Oberon SDK's <c>PlatformStub</c> was left holding no application, and its
    /// <c>Draw</c> then threw a NullReferenceException on EVERY frame with
    /// <c>drawCallsThisFrame=0</c>. The game reported as a white screen on load; nothing in the
    /// symptom pointed at the music library.</para>
    /// </summary>
    public sealed class PlaylistCollection : IEnumerable<Playlist>, IEnumerable, IDisposable
    {
        private readonly List<Playlist> _Playlists;

        internal PlaylistCollection()
        {
            _Playlists = new List<Playlist>();
        }

        public IEnumerator<Playlist> GetEnumerator() => _Playlists.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => _Playlists.GetEnumerator();

        public int Count => _Playlists.Count;

        public Playlist this[int index] => _Playlists[index];

        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            _Playlists.Clear();
            IsDisposed = true;
        }
    }
}
