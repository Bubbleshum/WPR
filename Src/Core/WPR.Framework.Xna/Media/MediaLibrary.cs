using System;
using System.Collections.Generic;

namespace Microsoft.Xna.Framework.Media
{
    public class MediaLibrary : IDisposable
    {
        private SongCollection _Songs;
        private ArtistCollection _Artists;
        private AlbumCollection _Albums;
        private PictureCollection _Pictures;
        private PlaylistCollection _Playlists;

        public MediaLibrary(MediaSource source)
        {
            _Songs = new SongCollection();
            _Artists = new ArtistCollection();
            _Albums = new AlbumCollection();
            _Pictures = new PictureCollection();
            _Playlists = new PlaylistCollection();
        }

        public MediaLibrary() : this(new MediaSource(MediaSourceType.LocalDevice)) { }

        public SongCollection Songs => _Songs;
        public ArtistCollection Artists => _Artists;
        public AlbumCollection Albums => _Albums;
        public PlaylistCollection Playlists => _Playlists;
        public PictureCollection Pictures => _Pictures;
        public PictureCollection SavedPictures => _Pictures;

        public bool IsDisposed { get; private set; }

        /// <summary>
        /// XNA's <c>MediaLibrary</c> is <see cref="IDisposable"/>, and games call this. There is
        /// nothing here to release — every collection is a permanently empty list — but the method
        /// has to exist: a game that opens a library, queries it and disposes it would otherwise
        /// fail to resolve <c>Dispose</c> when its method is compiled, not when the call runs.
        /// </summary>
        public void Dispose()
        {
            IsDisposed = true;
        }

        /// <summary>
        /// Save an image into the phone's Saved Pictures album. WPR has no gallery to write into,
        /// so the stream is consumed and a <see cref="Picture"/> describing it is returned without
        /// anything being persisted. Returning a real object rather than null matters: callers
        /// dereference the result to report success — Kinectimals'
        /// <c>MediaUtils.PhotoLibraryAccess.SaveToLibrary</c> does — and would NRE on null.
        /// The picture is deliberately not added to <see cref="Pictures"/>, which stays empty and
        /// consistent with "a phone with no photos".
        /// </summary>
        public Picture SavePicture(string name, System.IO.Stream source)
        {
            // Drain the stream so a caller that hands us a seekable buffer and then checks
            // Position sees what a real save would have left behind.
            if (source != null && source.CanRead)
            {
                try { source.CopyTo(System.IO.Stream.Null); } catch { /* best-effort */ }
            }

            return new Picture(string.IsNullOrEmpty(name) ? "Untitled" : name);
        }

        /// <summary>Byte-array overload of <see cref="SavePicture(string, System.IO.Stream)"/>.</summary>
        public Picture SavePicture(string name, byte[] source)
        {
            return new Picture(string.IsNullOrEmpty(name) ? "Untitled" : name);
        }
    }
}
