using System.Collections.Generic;

namespace Microsoft.Xna.Framework.Media
{
    public class MediaSource
    {
        public MediaSourceType MediaSourceType { get; set; }
        public string Name => "WPR Media Source";

        internal MediaSource(MediaSourceType sourceType)
        {
            this.MediaSourceType = sourceType;
        }

        public static IList<MediaSource> GetAvailableMediaSources()
        {
            return new List<MediaSource>{ new MediaSource(MediaSourceType.LocalDevice) };
        }
    }
}
