using WPR.Engine.Audio;
using System;

namespace WPR.Audio.AndroidMediaPlayer
{
    /// <summary>
    /// Android <see cref="IMediaBackend"/> — plays XNA <c>Song</c>s through the platform's own
    /// <see cref="global::Android.Media.MediaPlayer"/> instead of FAudio's <c>XNA_Song</c>.
    ///
    /// <para><b>Why.</b> <c>XNA_SongSubmitBuffer</c> decodes exactly one second of Vorbis per
    /// buffer (<c>sample_rate * channels</c> frames) into a single reusable cache, with a queue
    /// depth of one, refilled from <c>OnBufferEnd</c>. <c>OnBufferEnd</c> fires when the buffer has
    /// already finished, so at every boundary the voice has nothing queued *and* the audio thread
    /// is busy decoding a full second of audio inside the mixer callback. On desktop that decode
    /// fits in the deadline; on a phone it is an audible click once per second — exactly once per
    /// buffer. Rebuilding FAudio with double buffering is the better fix, but
    /// <c>libFAudio.so</c> ships prebuilt and this toolchain has no NDK.</para>
    ///
    /// <para>Android's MediaPlayer does its own buffering on its own thread, decodes Ogg Vorbis
    /// natively, and gives us volume / pause / resume / completion for free — which is most of the
    /// song half of this seam. It is also the semantically right thing: on a real WP7 device
    /// <c>Microsoft.Xna.Framework.Media.MediaPlayer</c> *was* the phone's media player.</para>
    ///
    /// <para><b>Pause/resume keeps its position.</b> <see cref="PauseSong"/> pauses the retained
    /// player rather than tearing it down, so <see cref="ResumeSong"/> continues exactly where it
    /// stopped — MediaPlayer's own Paused → Started contract. The offset is *also* captured on every
    /// pause, because a pause usually means the player left the app and Android may reclaim the audio
    /// track while we are backgrounded; <see cref="ResumeSong"/> then rebuilds and seeks. There is no
    /// seek API on the way in: XNA 4.0 on Windows Phone has no <c>Play(Song, TimeSpan)</c>, so a
    /// game's only route back into the middle of a track is pause/resume.</para>
    ///
    /// <para><b>Video is not reimplemented.</b> Everything below the song region delegates to
    /// the module below this one in the audio stack (Theorafile, under <c>WPR.Audio.FAudio</c>),
    /// which has no equivalent problem and which this class has no business duplicating.</para>
    ///
    /// <para><b>The filename lies about the container</b> — the install-time transcode writes Ogg
    /// Vorbis back under the original <c>.wma</c> name because the <c>.xnb</c> Song stub points
    /// there. That is fine here: MediaPlayer sniffs content through MediaExtractor rather than
    /// trusting the extension. It is the one assumption in this class worth re-checking if songs
    /// silently fail to start.</para>
    /// </summary>
    public sealed class AndroidMediaPlayerBackend : IMediaBackend
    {
        /// <summary>
        /// The backend serving the game in this process, for <c>GameActivity</c> to reach on
        /// pause/resume. Safe as a static because <c>GameActivity</c> runs in its own <c>:game</c>
        /// process and hosts exactly one game for that process's whole life.
        ///
        /// <para>Public rather than internal since the 2026-09-01 split — the activity that pumps it
        /// now lives in a different assembly (the Android head) from the backend.</para>
        /// </summary>
        public static AndroidMediaPlayerBackend? Current { get; private set; }

        /// <summary>
        /// Video half. Theorafile under the FAudio module, but this class never names it: the module
        /// below in the audio stack is handed in by <c>AudioBackendRegistry</c> (see
        /// <see cref="AndroidMediaPlayerModule"/>). That injection is what keeps this project free of
        /// any reference to <c>WPR.Audio.FAudio</c> — before the split this field was a
        /// <c>new WPR.Backend.FNA.FnaMediaBackend()</c> and dragged the whole host backend into the
        /// Android head's audio code.
        ///
        /// <para>Null is legal and means "this composition has no video decoder": the members below
        /// then report an empty video rather than throwing, which is the same thing a game sees for
        /// a file it cannot open.</para>
        /// </summary>
        private readonly IMediaBackend? _video;

        /// <param name="video">The media backend to forward the video half to — the module below
        /// this one in the audio stack. May be null.</param>
        public AndroidMediaPlayerBackend(IMediaBackend? video = null)
        {
            _video = video;
            Current = this;
        }

        /// <summary>
        /// Guards every transition. MediaPlayer is not documented as thread-safe, and the calls
        /// arrive from two places: the game thread (Play/Pause/Stop via the XNA MediaPlayer) and
        /// MediaPlayer's own completion callback thread.
        /// </summary>
        private readonly object _gate = new();

        private global::Android.Media.MediaPlayer? _player;

        /// <summary>
        /// Set by the completion callback, cleared when a new song starts. This is what
        /// <c>MediaPlayer.Update</c> polls through <see cref="GetSongEnded"/> to advance the queue,
        /// so it must latch rather than reflect instantaneous state.
        /// </summary>
        private bool _ended;

        /// <summary>Retained so a volume set before/between songs still applies to the next one.</summary>
        private float _volume = 1.0f;

        /// <summary>
        /// Path of the song currently loaded, held so a paused song can be *rebuilt* if the platform
        /// invalidates the player underneath us. Cleared by <see cref="StopSong"/> — a stopped song
        /// has nothing to restore. See <see cref="ResumeSong"/> for why this is needed.
        /// </summary>
        private string? _fileName;

        /// <summary>
        /// Playback offset in milliseconds captured at <see cref="PauseSong"/>, and the seek target
        /// for the rebuild path. -1 means "not paused, nothing to restore", which is what makes the
        /// rebuild in <see cref="ResumeSong"/> fire only for a genuine pause/resume.
        /// </summary>
        private int _pausedPositionMs = -1;

        #region Song playback

        public void SongInit()
        {
            /* Nothing to do — the player is created per song in PlaySong. MediaPlayer instances are
             * cheap and reusing one across files means Reset() plus a state machine to get wrong. */
        }

        public void SongQuit()
        {
            lock (_gate)
            {
                DisposePlayerLocked();
                _fileName = null;
                _pausedPositionMs = -1;
            }
        }

        /// <returns>Song duration in seconds, matching <c>XNA_PlaySong</c>'s contract. 0 on failure,
        /// which is what the FAudio path returns for a file it cannot open.</returns>
        public float PlaySong(string fileName)
        {
            lock (_gate)
            {
                DisposePlayerLocked();
                _ended = false;
                _fileName = fileName;
                _pausedPositionMs = -1;

                /* A new song is a fresh state: whatever the host suspended is gone. Note
                 * StartPlayerLocked sets it back to true if we are currently backgrounded — the
                 * song is then held silent and owed a start, not a resume. */
                _suspendedByHost = false;

                try
                {
                    /* Always from the beginning. XNA 4.0 on Windows Phone has no
                     * Play(Song, TimeSpan) overload, so Play means "start this song", full stop.
                     *
                     * Deliberately NOT clever here: a WP7 title stops its own music from its
                     * Deactivated handler and replays it on reactivation, so carrying the old
                     * offset across that stop/replay would make backgrounding resume mid-track.
                     * Nicer to listen to, but it is not what the game asked for, and a game that
                     * restarts a track on purpose would silently get the wrong behaviour. Position
                     * is preserved for the case XNA actually defines it — Pause then Resume. */
                    return StartPlayerLocked(fileName, 0) / 1000.0f;
                }
                catch (Exception ex)
                {
                    WPR.Common.Log.Warn(WPR.Common.LogCategory.AppAudioConverter,
                        $"Android MediaPlayer could not play '{fileName}': {ex.GetType().Name}: {ex.Message}");
                    DisposePlayerLocked();
                    _fileName = null;

                    /* Report the song as finished so the queue advances instead of waiting forever
                     * on a track that will never end. */
                    _ended = true;
                    return 0.0f;
                }
            }
        }

        /// <summary>
        /// Builds, prepares and starts a player for <paramref name="fileName"/>, optionally seeking
        /// to <paramref name="seekMs"/> first. Caller must hold <see cref="_gate"/>; throws on a
        /// file the platform cannot open.
        /// </summary>
        /// <returns>Duration in milliseconds.</returns>
        private int StartPlayerLocked(string fileName, int seekMs)
        {
            var player = new global::Android.Media.MediaPlayer();
            player.SetDataSource(fileName);

            /* Synchronous Prepare, not PrepareAsync: the caller needs the duration as a return
             * value, and this is a local file so there is no network stall to fear. A malformed
             * file throws here rather than failing silently later. */
            player.Prepare();
            player.SetVolume(_volume, _volume);

            /* Completion latches _ended. Deliberately NOT MediaPlayer.Looping: repeat is decided a
             * layer up by the XNA MediaPlayer's queue logic, which polls GetSongEnded and re-issues
             * PlaySong. Setting Looping here would strand it.
             *
             * Error latches _ended too — see OnError. Both callbacks are delivered on the Looper of
             * the thread that created the player; the FNA game thread has none, so they arrive on
             * the main thread. That is why every critical section under _gate stays short. */
            player.Completion += OnCompletion;
            player.Error += OnError;

            /* Valid from the Prepared state, and cheaper than starting then seeking (which would
             * emit a burst of audio from the wrong offset first). */
            if (seekMs > 0)
            {
                player.SeekTo(seekMs);
            }

            if (_hostBackgrounded)
            {
                /* The app is not in the foreground, so leave the player Prepared-but-not-Started.
                 * A song started here would be audible over the home screen or another app with
                 * nothing left to stop it: SuspendForBackground has already run for this
                 * background, and it will not run again until we have been foregrounded first.
                 *
                 * This is not hypothetical. The game thread keeps running either side of our
                 * suspend — between base.OnPause() and the suspend, and again between the SDL
                 * thread unblocking on resume and RestoreFromForeground — and a WP7 title reacting
                 * to Game.Deactivated / Game.Activated routinely stops and replays its track right
                 * there. Prepared-but-not-Started rather than started-then-paused because Start()
                 * emits a burst of audio before a Pause() could land.
                 *
                 * Claimed as ours to restore, so RestoreFromForeground starts it when we return —
                 * from this offset, which is where the caller asked to begin. */
                _pausedPositionMs = seekMs;
                _suspendedByHost = true;

                WPR.Common.Log.Info(WPR.Common.LogCategory.AppAudioConverter,
                    $"[wpr-media] song prepared but held silent at {seekMs} ms — app is backgrounded");
            }
            else
            {
                player.Start();
            }

            _player = player;

            return player.Duration;
        }

        private void OnCompletion(object? sender, EventArgs e)
        {
            lock (_gate)
            {
                _ended = true;

                /* Logged because a Completion arriving while we are suspended is NOT a song
                 * finishing — it is Android tearing the track down behind a paused player — and it
                 * is otherwise indistinguishable from the game stopping its own music: both end up
                 * as StopSong from the XNA queue. See SuspendForBackground. */
                WPR.Common.Log.Info(WPR.Common.LogCategory.AppAudioConverter,
                    $"[wpr-media] song reported ended (suspended={_suspendedByHost})");
            }
        }

        /// <summary>
        /// An errored MediaPlayer is in a terminal state — it will never raise Completion, so
        /// without this the XNA queue would poll <see cref="GetSongEnded"/> forever and the game
        /// would lose all music for the rest of the session. Latch the song as finished instead and
        /// let the queue move on.
        /// </summary>
        private void OnError(object? sender, global::Android.Media.MediaPlayer.ErrorEventArgs e)
        {
            WPR.Common.Log.Warn(WPR.Common.LogCategory.AppAudioConverter,
                $"Android MediaPlayer error (what={e.What}, extra={e.Extra}) on '{_fileName}'; " +
                "reporting the song as ended so the queue advances");

            lock (_gate)
            {
                _ended = true;
            }

            /* Handled, so the platform does not additionally synthesise a Completion for the same
             * failure — _ended is already set either way, but one signal is easier to reason about. */
            e.Handled = true;
        }

        public void PauseSong()
        {
            lock (_gate)
            {
                global::Android.Media.MediaPlayer? player = _player;
                if (player == null)
                {
                    return;
                }

                try
                {
                    /* Capture the offset while the player is certainly still valid. Pause() itself
                     * preserves the position — this is purely the restore point for the rebuild in
                     * ResumeSong, for the case where the player does NOT survive. */
                    _pausedPositionMs = player.CurrentPosition;

                    if (player.IsPlaying)
                    {
                        player.Pause();
                    }
                }
                catch (Exception ex)
                {
                    WPR.Common.Log.Warn(WPR.Common.LogCategory.AppAudioConverter,
                        $"Android MediaPlayer pause failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Resumes a paused song from where it stopped.
        ///
        /// <para>The normal path is just <c>Start()</c> on the retained player, which continues at
        /// the preserved position — that is MediaPlayer's own Paused → Started contract, and it is
        /// why <see cref="PauseSong"/> does not tear the player down.</para>
        ///
        /// <para>The fallback exists because a game is typically paused by the player leaving the
        /// app, and Android is entitled to take the audio track away while we are backgrounded (on
        /// API 36 it both mutes background playback and freezes the process). If the player has been
        /// invalidated, <c>Start()</c> throws; swallowing that would silently kill music for the
        /// rest of the session, because nothing upstream ever re-issues PlaySong for a song it
        /// believes is merely paused. So rebuild the player and seek back to the captured offset —
        /// the audible result is the same either way.</para>
        /// </summary>
        public void ResumeSong()
        {
            lock (_gate)
            {
                global::Android.Media.MediaPlayer? player = _player;
                if (player != null)
                {
                    if (_hostBackgrounded)
                    {
                        /* Same reasoning as StartPlayerLocked: a game resuming its own music while
                         * we are backgrounded — its Activated handler can run before
                         * RestoreFromForeground clears the latch — must not become audible. Claim
                         * it instead, so the restore is what starts it. */
                        _suspendedByHost = true;
                        return;
                    }

                    try
                    {
                        if (!player.IsPlaying)
                        {
                            player.Start();
                        }

                        return;
                    }
                    catch (Exception ex)
                    {
                        WPR.Common.Log.Warn(WPR.Common.LogCategory.AppAudioConverter,
                            $"Android MediaPlayer resume failed ({ex.GetType().Name}: {ex.Message}); " +
                            "rebuilding the player at the paused offset");
                        DisposePlayerLocked();
                    }
                }

                /* Only rebuild for a genuine pause/resume: _pausedPositionMs is -1 unless PauseSong
                 * captured an offset, and StopSong clears both fields. Without that guard a Resume
                 * arriving after a Stop would restart music the game had deliberately ended. */
                string? fileName = _fileName;
                if (fileName == null || _pausedPositionMs < 0 || _ended)
                {
                    return;
                }

                try
                {
                    StartPlayerLocked(fileName, _pausedPositionMs);
                }
                catch (Exception ex)
                {
                    WPR.Common.Log.Warn(WPR.Common.LogCategory.AppAudioConverter,
                        $"Android MediaPlayer could not rebuild '{fileName}' at {_pausedPositionMs} ms: " +
                        $"{ex.GetType().Name}: {ex.Message}");
                    DisposePlayerLocked();
                    _ended = true;
                }
            }
        }

        public void StopSong()
        {
            lock (_gate)
            {
                /* Tear the player down rather than just Stop(): a stopped MediaPlayer has to be
                 * Prepare()d again before it can play, and PlaySong builds a fresh one anyway.
                 * _ended is NOT set here — an explicit stop is not a song finishing, and claiming
                 * otherwise would make the queue advance on the caller's own Stop(). */
                /* Logged with the suspend/ended flags because those two bits are what distinguish a
                 * game stopping its own music (suspended=True, ended=False — a WP7 Deactivated
                 * handler, which is what Mirror's Edge does 103 ms after backgrounding) from the
                 * XNA queue advancing on a finished song (ended=True). They look identical from
                 * here otherwise, and the difference decided the design of PlaySong. */
                WPR.Common.Log.Info(WPR.Common.LogCategory.AppAudioConverter,
                    $"[wpr-media] StopSong (suspended={_suspendedByHost}, ended={_ended})");

                DisposePlayerLocked();

                /* Drop the restore point: a stopped song must not be resurrected by ResumeSong,
                 * nor by RestoreFromForeground when the app returns. */
                _fileName = null;
                _pausedPositionMs = -1;
                _suspendedByHost = false;
            }
        }

        public void SetSongVolume(float volume)
        {
            lock (_gate)
            {
                _volume = volume < 0.0f ? 0.0f : (volume > 1.0f ? 1.0f : volume);
                try
                {
                    _player?.SetVolume(_volume, _volume);
                }
                catch (Exception)
                {
                    /* Raced with teardown; the value is retained for the next song regardless. */
                }
            }
        }

        public bool GetSongEnded()
        {
            lock (_gate)
            {
                return _ended;
            }
        }

        #endregion

        #region Activity lifecycle

        /// <summary>
        /// True when <see cref="SuspendForBackground"/> paused a song that was playing, so
        /// <see cref="RestoreFromForeground"/> knows the pause was ours to undo.
        /// </summary>
        private bool _suspendedByHost;

        /// <summary>
        /// True from <see cref="SuspendForBackground"/> until <see cref="RestoreFromForeground"/> —
        /// for as long as the app is not in the foreground.
        ///
        /// <para>Deliberately separate from <see cref="_suspendedByHost"/>, which means "we paused
        /// a song and owe it a resume" and is therefore cleared by <see cref="StopSong"/> and
        /// re-decided per song. This one is a property of the <em>activity</em>, not of any song,
        /// so it survives the game stopping and starting tracks while backgrounded — which is
        /// exactly the case it exists for. Without it the first song the game starts after our
        /// suspend has already run plays out loud over the home screen, and nothing is left to stop
        /// it until the app has been foregrounded and backgrounded again.</para>
        /// </summary>
        private bool _hostBackgrounded;

        /// <summary>
        /// Silences the song because the app is leaving the foreground.
        ///
        /// <para><b>Why this is needed at all.</b> Sound effects stop by themselves: they go through
        /// FAudio, whose SDL audio device SDL pauses as part of the Android activity lifecycle. The
        /// song does not — a platform MediaPlayer is ours alone and keeps playing happily behind the
        /// launcher, the home screen or another app, which is both wrong and a battery drain. On
        /// API 36 the platform mutes background playback itself ("AudioHardening"), which hides the
        /// problem on a new emulator image but not on real devices.</para>
        ///
        /// <para>Only a song that is actually <em>playing</em> is claimed. If the game paused or
        /// stopped its own music first — its Deactivated handler may already have run — this does
        /// nothing and <see cref="RestoreFromForeground"/> will not override the game's intent by
        /// restarting music the game meant to leave silent.</para>
        ///
        /// <para>Pausing what is playing right now is only half the job, though — this also latches
        /// <see cref="_hostBackgrounded"/>, which is what keeps a song the game starts <em>after</em>
        /// this point silent. Without it the game's own deactivation handling could replay its track
        /// moments later and nothing would stop it, because this method has already run for this
        /// background and will not run again until the app has been foregrounded.</para>
        /// </summary>
        public void SuspendForBackground()
        {
            lock (_gate)
            {
                /* Set FIRST and unconditionally, ahead of every early return below: this latch, not
                 * the pause, is what keeps a song started *later* — while we are still
                 * backgrounded — silent. See StartPlayerLocked. */
                _hostBackgrounded = true;

                global::Android.Media.MediaPlayer? player = _player;
                if (player == null || _suspendedByHost)
                {
                    WPR.Common.Log.Info(WPR.Common.LogCategory.AppAudioConverter,
                        $"[wpr-media] background suspend: nothing to pause (player={player != null}, " +
                        $"alreadySuspended={_suspendedByHost})");
                    return;
                }

                try
                {
                    if (!player.IsPlaying)
                    {
                        WPR.Common.Log.Info(WPR.Common.LogCategory.AppAudioConverter,
                            "[wpr-media] background suspend: player was not playing");
                        return;
                    }

                    _pausedPositionMs = player.CurrentPosition;
                    player.Pause();
                    _suspendedByHost = true;

                    WPR.Common.Log.Info(WPR.Common.LogCategory.AppAudioConverter,
                        $"[wpr-media] song paused for background at {_pausedPositionMs} ms");
                }
                catch (Exception ex)
                {
                    WPR.Common.Log.Warn(WPR.Common.LogCategory.AppAudioConverter,
                        $"Android MediaPlayer background pause failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Undoes <see cref="SuspendForBackground"/> when the app returns to the foreground —
        /// including the case where Android reclaimed the audio track while we were away, which
        /// <see cref="ResumeSong"/> handles by rebuilding at the captured offset.
        /// </summary>
        public void RestoreFromForeground()
        {
            bool restore;
            lock (_gate)
            {
                /* Cleared unconditionally and before anything else: from here a PlaySong /
                 * ResumeSong arriving from the game thread starts audibly again, which is what we
                 * want now that we are back in the foreground. */
                _hostBackgrounded = false;

                restore = _suspendedByHost;
                _suspendedByHost = false;

                /* The game stopped its music while we were backgrounded, so there is nothing of ours
                 * left to restore — StopSong cleared the restore point. */
                if (restore && _fileName == null)
                {
                    restore = false;
                }
            }

            if (!restore)
            {
                return;
            }

            WPR.Common.Log.Info(WPR.Common.LogCategory.AppAudioConverter,
                "[wpr-media] restoring song after returning to the foreground");

            ResumeSong();
        }

        #endregion

        #region Song visualization — not supported

        /* FAudio's XNA_* visualization reads its own mix buffer. There is no equivalent here:
         * Android's Visualizer API needs the RECORD_AUDIO permission, which is not a trade worth
         * making for a WP7 API almost nothing used. Report it as disabled and hand back silence —
         * the same shape as an unseeded product reporting no achievements, rather than throwing. */

        public void EnableSongVisualization(bool enable)
        {
        }

        public bool IsSongVisualizationEnabled() => false;

        public void GetSongVisualizationData(float[] frequencies, float[] samples, int count)
        {
            if (frequencies != null)
            {
                Array.Clear(frequencies, 0, Math.Min(count, frequencies.Length));
            }
            if (samples != null)
            {
                Array.Clear(samples, 0, Math.Min(count, samples.Length));
            }
        }

        #endregion

        #region Video decode — delegated to the module below

        /* Nothing here is Android-specific. Theora video has no equivalent of the once-per-second
         * XNA_Song starvation this class exists to work around, so reimplementing it over
         * MediaPlayer would be duplication with a real regression risk (VideoPlayer wants raw YUV
         * planes and interleaved float audio, neither of which the platform player hands out).
         *
         * With no module below, `_video` is null and every member reports "no video". That is the
         * same shape VideoPlayer already handles for an unopenable file — see IMediaBackend.OpenVideo,
         * which returns a handle even on failure and lets the info query decide. */

        public IntPtr OpenVideo(string fileName) =>
            _video != null ? _video.OpenVideo(fileName) : IntPtr.Zero;

        public void CloseVideo(ref IntPtr video)
        {
            if (_video != null) _video.CloseVideo(ref video);
            else video = IntPtr.Zero;
        }

        public void GetVideoInfo(
            IntPtr video,
            out int width,
            out int height,
            out double framesPerSecond,
            out VideoPixelFormat format
        )
        {
            if (_video != null)
            {
                _video.GetVideoInfo(video, out width, out height, out framesPerSecond, out format);
                return;
            }
            width = 0;
            height = 0;
            framesPerSecond = 0.0;
            format = VideoPixelFormat.Unknown;
        }

        public void GetVideoAudioInfo(IntPtr video, out int channels, out int sampleRate)
        {
            if (_video != null)
            {
                _video.GetVideoAudioInfo(video, out channels, out sampleRate);
                return;
            }
            channels = 0;
            sampleRate = 0;
        }

        public bool HasVideoStream(IntPtr video) => _video != null && _video.HasVideoStream(video);

        public bool HasAudioStream(IntPtr video) => _video != null && _video.HasAudioStream(video);

        public void SetAudioTrack(IntPtr video, int track) => _video?.SetAudioTrack(video, track);

        public void SetVideoTrack(IntPtr video, int track) => _video?.SetVideoTrack(video, track);

        /// <summary>True with no decoder — an absent video has already ended, which is what stops
        /// <c>VideoPlayer</c> spinning on it.</summary>
        public bool IsEndOfVideo(IntPtr video) => _video == null || _video.IsEndOfVideo(video);

        public void ResetVideo(IntPtr video) => _video?.ResetVideo(video);

        public bool ReadVideoFrames(IntPtr video, IntPtr yuvBuffer, int frameCount) =>
            _video != null && _video.ReadVideoFrames(video, yuvBuffer, frameCount);

        public int ReadVideoAudio(IntPtr video, IntPtr buffer, int length) =>
            _video != null ? _video.ReadVideoAudio(video, buffer, length) : 0;

        #endregion

        #region Helpers

        private void DisposePlayerLocked()
        {
            global::Android.Media.MediaPlayer? player = _player;
            _player = null;
            if (player == null)
            {
                return;
            }

            try
            {
                player.Completion -= OnCompletion;
                player.Error -= OnError;
                if (player.IsPlaying)
                {
                    player.Stop();
                }
            }
            catch (Exception)
            {
                /* Already in a terminal state — Release below is still required. */
            }

            try
            {
                /* Release, not just Dispose: MediaPlayer holds a native codec and an audio track,
                 * and leaking those across game launches exhausts a global pool on some devices. */
                player.Release();
                player.Dispose();
            }
            catch (Exception)
            {
            }
        }

        #endregion
    }
}
