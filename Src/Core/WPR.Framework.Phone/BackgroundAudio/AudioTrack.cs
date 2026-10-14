using System;

namespace Microsoft.Phone.BackgroundAudio
{
    /// <summary>Shim for <c>Microsoft.Phone.BackgroundAudio.AudioTrack</c>: the track description
    /// <see cref="BackgroundAudioPlayer"/> holds. Data only; nothing here plays.</summary>
    public class AudioTrack : IDisposable
    {
        public AudioTrack() { }

        public AudioTrack(Uri source, string title, string artist, string album, Uri albumArt)
        {
            Source = source;
            Title = title;
            Artist = artist;
            Album = album;
            AlbumArt = albumArt;
        }

        public AudioTrack(Uri source, string title, string artist, string album, Uri albumArt, string tag, EnabledPlayerControls enabledControls)
            : this(source, title, artist, album, albumArt)
        {
            Tag = tag;
            PlayerControls = enabledControls;
        }

        public Uri? Source { get; set; }
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? Album { get; set; }
        public Uri? AlbumArt { get; set; }
        public string? Tag { get; set; }
        public TimeSpan Duration { get; set; }
        public EnabledPlayerControls PlayerControls { get; set; } = EnabledPlayerControls.All;

        public void BeginEdit() { }
        public void EndEdit() { }
        public void Dispose() { }
    }

    /// <summary>Shim for <c>Microsoft.Phone.BackgroundAudio.EnabledPlayerControls</c>.</summary>
    [Flags]
    public enum EnabledPlayerControls : long
    {
        None = 0,
        SkipNext = 1,
        SkipPrevious = 2,
        FastForward = 4,
        Rewind = 8,
        Pause = 16,
        All = 31,
    }
}
