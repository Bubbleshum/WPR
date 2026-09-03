using WPR.Engine.Audio;
using System;
using WPR.Xna.Rhi;

namespace WPR.Audio.FAudio
{
    /// <summary>
    /// FAudio/Theorafile implementation of <see cref="IMediaBackend"/> — Stage 5c-3c (Plans/STAGE5C-SCOPE.md).
    /// Forwards song playback to FAudio's <c>XNA_*</c> entry points and video decode to Theorafile.
    ///
    /// <para>Both binding classes live in the <b>global namespace</b> inside FNA.dll, which matters:
    /// FNA registers its <c>DllImport</c> resolver (<c>FNADllMap</c>) only for P/Invokes whose
    /// <em>declaring assembly</em> is FNA, so this adapter must call through them rather than
    /// re-declaring the natives here — the same constraint that shaped
    /// <see cref="FAudioSoundBackend"/> and <see cref="FAudioXactBackend"/>.</para>
    ///
    /// <para>Stateless. Everything the media subsystem owns is either a native singleton (the song
    /// player) or an opaque handle the caller holds (the Theorafile decoder), so there is nothing
    /// per-launch to reset here.</para>
    /// </summary>
    public sealed class FAudioMediaBackend : IMediaBackend
    {
        #region Song playback (FAudio XNA_*)

        public void SongInit() => global::FAudio.XNA_SongInit();

        public void SongQuit() => global::FAudio.XNA_SongQuit();

        public float PlaySong(string fileName) => global::FAudio.XNA_PlaySong(fileName);

        public void PauseSong() => global::FAudio.XNA_PauseSong();

        public void ResumeSong() => global::FAudio.XNA_ResumeSong();

        public void StopSong() => global::FAudio.XNA_StopSong();

        public void SetSongVolume(float volume) => global::FAudio.XNA_SetSongVolume(volume);

        public bool GetSongEnded() => global::FAudio.XNA_GetSongEnded() != 0;

        public void EnableSongVisualization(bool enable) =>
            global::FAudio.XNA_EnableVisualization(enable ? 1u : 0u);

        public bool IsSongVisualizationEnabled() => global::FAudio.XNA_VisualizationEnabled() == 1;

        public void GetSongVisualizationData(float[] frequencies, float[] samples, int count) =>
            global::FAudio.XNA_GetSongVisualizationData(frequencies, samples, (uint) count);

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
