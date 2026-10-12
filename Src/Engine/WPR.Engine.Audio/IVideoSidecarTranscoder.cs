using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WPR.Engine.Audio
{
    /// <summary>
    /// Optional on an <see cref="IAudioTranscoder"/>: at install time, rewrites a game's movie into
    /// files this platform's <see cref="IVideoDecoder"/> can play, written beside the original
    /// (see <see cref="VideoSidecars"/>).
    ///
    /// <para>Only a platform whose run-time codecs cannot read WP7's movies needs it. Android's
    /// <c>MediaCodec</c> has no WMV/VC-1 decoder, and ffmpeg-kit cannot run in the game process,
    /// so the <c>:transcode</c> process does the conversion once, at install. The desktop decodes
    /// the originals with ffmpeg.exe at run time and implements nothing here.</para>
    /// </summary>
    public interface IVideoSidecarTranscoder
    {
        /// <summary>
        /// Writes the picture to <paramref name="videoOutput"/> and the sound, when there is any, to
        /// <paramref name="audioOutput"/>. Fails through the result rather than throwing, like
        /// <see cref="IAudioTranscoder.TranscodeToOggVorbisAsync"/>; a movie without sound is a
        /// success with no audio file written.
        /// </summary>
        Task<AudioTranscodeResult> TranscodeVideoAsync(
            string inputPath,
            string videoOutput,
            string audioOutput,
            CancellationToken cancellationToken);
    }

    /// <summary>Where a movie's sidecars live: beside it, named after it, so they are copied,
    /// repatched and uninstalled with the install folder and need no record anywhere.</summary>
    public static class VideoSidecars
    {
        /// <summary>The file extensions WP7 titles ship movies in.</summary>
        public static bool IsMovie(string path)
        {
            if (IsSidecar(path)) return false;
            string ext = Path.GetExtension(path);
            return ext.Equals(".wmv", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".m4v", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".3gp", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsSidecar(string path) =>
            path.EndsWith(".wpr.mp4", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".wpr.ogg", StringComparison.OrdinalIgnoreCase);

        /// <summary>The picture: MP4 the platform codecs read (H.264 kept, anything else MPEG-4).</summary>
        public static string VideoPath(string movie) => movie + ".wpr.mp4";

        /// <summary>The sound: Ogg Vorbis, decoded in managed code. Absent for a silent movie.</summary>
        public static string AudioPath(string movie) => movie + ".wpr.ogg";
    }
}
