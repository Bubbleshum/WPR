using System;

namespace Microsoft.Phone.BackgroundAudio
{
    /// <summary>Shim for <c>Microsoft.Phone.BackgroundAudio.BackgroundAudioPlayer</c>.</summary>
    /// <remarks>
    /// WP7.5/8's background audio drives a separate audio-agent process that WPR does not host,
    /// so this player is silent: it keeps the track and the state a caller sets and reports them
    /// back, and nothing plays. Present so a title that merely touches it — "What In The
    /// World?" reads <see cref="PlayerState"/> on its start page — gets past that page, instead
    /// of failing to compile the method and with it the first navigation.
    /// </remarks>
    public sealed class BackgroundAudioPlayer
    {
        private static readonly BackgroundAudioPlayer _instance = new BackgroundAudioPlayer();

        private BackgroundAudioPlayer() { }

        public static BackgroundAudioPlayer Instance => _instance;

        public AudioTrack? Track { get; set; }
        public PlayState PlayerState { get; private set; } = PlayState.Stopped;
        public TimeSpan Position { get; set; }
        public double Volume { get; set; } = 1.0;
        public double BufferingProgress => 0;
        public bool CanPause => false;
        public bool CanSeek => false;
        public Exception? Error => null;

        public event EventHandler? PlayStateChanged;

        public void Play() => SetState(PlayState.Stopped);
        public void Pause() => SetState(PlayState.Paused);
        public void Stop() => SetState(PlayState.Stopped);
        public void SkipNext() { }
        public void SkipPrevious() { }
        public void FastForward() { }
        public void Rewind() { }
        public void Close() => SetState(PlayState.Shutdown);

        private void SetState(PlayState state)
        {
            if (PlayerState == state) return;
            PlayerState = state;
            try { PlayStateChanged?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("[wpr-bgaudio] PlayStateChanged handler threw: " + ex.Message);
            }
        }
    }

    /// <summary>Shim for <c>Microsoft.Phone.BackgroundAudio.PlayState</c>.</summary>
    public enum PlayState
    {
        Unknown = 0,
        Stopped = 1,
        Paused = 2,
        Playing = 3,
        BufferingStarted = 4,
        BufferingStopped = 5,
        TrackReady = 6,
        TrackEnded = 7,
        Rewinding = 8,
        FastForwarding = 9,
        Shutdown = 10,
        Error = 11,
    }
}
