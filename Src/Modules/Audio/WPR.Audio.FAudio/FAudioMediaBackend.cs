using WPR.Engine.Audio;
using System;
using WPR.Xna.Rhi;

namespace WPR.Audio.FAudio
{
    /// <summary>
    /// FAudio/Theorafile implementation of <see cref="IMediaBackend"/> — Stage 5c-3c (Plans/STAGE5C-SCOPE.md).
    /// Streams songs through <see cref="StreamingSongPlayer"/> (FAudio voice + Theorafile Vorbis decode) and decodes video with Theorafile.
    ///
    /// <para>Both binding classes live in the <b>global namespace</b> inside FNA.dll, which matters:
    /// FNA registers its <c>DllImport</c> resolver (<c>FNADllMap</c>) only for P/Invokes whose
    /// <em>declaring assembly</em> is FNA, so this adapter must call through them rather than
    /// re-declaring the natives here — the same constraint that shaped
    /// <see cref="FAudioSoundBackend"/> and <see cref="FAudioXactBackend"/>.</para>
    ///
    /// <para>Per launch: <c>AudioBackendRegistry.Compose()</c> builds a fresh instance for every
    /// game, and the song player it owns is torn down by <see cref="SongQuit"/> in the host's
    /// teardown ladder. The video half is stateless — the Theorafile handle belongs to the caller.</para>
    /// </summary>
    public sealed class FAudioMediaBackend : IMediaBackend, IBackgroundAwareAudio
    {
        #region Song playback (managed streaming over an FAudio voice)

        // Not FAudio's native XNA_* song player: that one keeps a single one-second buffer queued
        // and clicks once a second on a phone. See StreamingSongPlayer for the whole story.
        private readonly StreamingSongPlayer _songs = new StreamingSongPlayer();

        public void SongInit() => _songs.Init();

        public void SongQuit() => _songs.Quit();

        public float PlaySong(string fileName) => _songs.Play(fileName);

        public void PauseSong() => _songs.Pause();

        public void ResumeSong() => _songs.Resume();

        public void StopSong() => _songs.Stop();

        public void SetSongVolume(float volume) => _songs.SetVolume(volume);

        public bool GetSongEnded() => _songs.Ended;

        public void SuspendForBackground() => _songs.SuspendForBackground();

        public void RestoreFromForeground() => _songs.RestoreFromForeground();

        public void EnableSongVisualization(bool enable) => _songs.VisualizationEnabled = enable;

        public bool IsSongVisualizationEnabled() => _songs.VisualizationEnabled;

        public void GetSongVisualizationData(float[] frequencies, float[] samples, int count) =>
            _songs.GetVisualizationData(frequencies, samples, count);

        #endregion

        #region Video decode (Theorafile)

        /// <summary>
        /// Opens the file. Theorafile allocates the decoder struct before it even tries the open, and
        /// hands it back regardless of the result; the open result is discarded here to keep the
        /// pre-move behaviour — a failed open surfaces as "no video stream and no audio stream",
        /// and the handle still has to be closed to free that allocation.
        /// </summary>
        public IntPtr OpenVideo(string fileName)
        {
            IntPtr video;
            Theorafile.tf_fopen(fileName, out video);
            return video;
        }

        public void CloseVideo(ref IntPtr video)
        {
            if (video != IntPtr.Zero)
            {
                Theorafile.tf_close(ref video);
            }
        }

        public void GetVideoInfo(
            IntPtr video,
            out int width,
            out int height,
            out double framesPerSecond,
            out VideoPixelFormat format
        ) {
            Theorafile.th_pixel_fmt fmt;
            Theorafile.tf_videoinfo(video, out width, out height, out framesPerSecond, out fmt);
            switch (fmt)
            {
                case Theorafile.th_pixel_fmt.TH_PF_420:
                    format = VideoPixelFormat.Yuv420;
                    break;
                case Theorafile.th_pixel_fmt.TH_PF_422:
                    format = VideoPixelFormat.Yuv422;
                    break;
                case Theorafile.th_pixel_fmt.TH_PF_444:
                    format = VideoPixelFormat.Yuv444;
                    break;
                default:
                    format = VideoPixelFormat.Unknown;
                    break;
            }
        }

        public void GetVideoAudioInfo(IntPtr video, out int channels, out int sampleRate) =>
            Theorafile.tf_audioinfo(video, out channels, out sampleRate);

        public bool HasVideoStream(IntPtr video) => Theorafile.tf_hasvideo(video) == 1;

        public bool HasAudioStream(IntPtr video) => Theorafile.tf_hasaudio(video) == 1;

        public void SetAudioTrack(IntPtr video, int track) =>
            Theorafile.tf_setaudiotrack(video, track);

        public void SetVideoTrack(IntPtr video, int track) =>
            Theorafile.tf_setvideotrack(video, track);

        public bool IsEndOfVideo(IntPtr video) => Theorafile.tf_eos(video) == 1;

        public void ResetVideo(IntPtr video) => Theorafile.tf_reset(video);

        public bool ReadVideoFrames(IntPtr video, IntPtr yuvBuffer, int frameCount) =>
            Theorafile.tf_readvideo(video, yuvBuffer, frameCount) == 1;

        public int ReadVideoAudio(IntPtr video, IntPtr buffer, int length) =>
            Theorafile.tf_readaudio(video, buffer, length);

        #endregion
    }
}
