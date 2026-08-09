using System;
using System.Collections.Generic;

namespace Microsoft.Xna.Framework.Media
{
    public class MediaLibrary
    {
        private SongCollection _Songs;
        private ArtistCollection _Artists;
        private AlbumCollection _Albums;
        private PictureCollection _Pictures;

        public MediaLibrary(MediaSource source)
        {
            _Songs = new SongCollection();
            _Artists = new ArtistCollection();
            _Albums = new AlbumCollection();
            _Pictures = new PictureCollection();
        }

        public MediaLibrary() : this(new MediaSource(MediaSourceType.LocalDevice)) { }

        public SongCollection Songs => _Songs;
        public ArtistCollection Artists => _Artists;
        public AlbumCollection Albums => _Albums;
        public PictureCollection Pictures => _Pictures;
        public PictureCollection SavedPictures => _Pictures;
    }
}
