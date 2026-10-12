using System;

namespace WPR.Engine.Audio
{
    /// <summary>
    /// Decodes a game's movie file (WMV or MP4, as WP7 titles ship them) into RGBA frames and
    /// interleaved 16-bit PCM, for the two WP7 APIs that play video: <c>MediaPlayerLauncher</c>
    /// (a full-screen system player) and Silverlight's <c>MediaElement</c>.
    ///
    /// <para><b>Why not <see cref="IMediaBackend"/>'s video half.</b> That one is XNA's
    /// <c>VideoPlayer</c> over Theorafile, which reads Ogg Theora only — no WP7 title ships it, and
    /// Theorafile is not even shipped on Windows. A scan of the library found 87 <c>.wmv</c> and
    /// 41 <c>.mp4</c> files across ~55 titles, all of which WPR used to skip as though they had
    /// ended at once.</para>
    ///
    /// <para><b>Per platform</b>, because the codecs are: the desktop pipes the file through the
    /// bundled <c>ffmpeg.exe</c>, which decodes WMV/VC-1 and H.264 directly; Android has no WMV
    /// decoder in <c>MediaCodec</c>, so its installer writes MP4/Ogg sidecars that the platform
    /// codecs and NVorbis can read. Vocabulary is paths, bytes and ints only, like
    /// <see cref="IAudioDecoder"/>.</para>
    /// </summary>
    public interface IVideoDecoder
    {
        /// <summary>For logs: what decodes (e.g. <c>"ffmpeg.exe"</c>, <c>"MediaCodec"</c>).</summary>
        string Name { get; }

        /// <summary>True when the decoder can actually run.</summary>
        bool IsAvailable { get; }

        /// <summary>
        /// Opens a movie. Null when it cannot be played on this platform (no codec, no sidecar,
        /// a corrupt file); the caller then behaves as WPR always did and reports the movie as
        /// finished. Must not throw.
        /// </summary>
        IVideoStream? Open(string path);
    }

    /// <summary>
    /// One open movie. Frames and audio are pulled independently, each from a single thread;
    /// the stream decodes ahead on threads of its own, so neither read blocks for long.
    /// </summary>
    public interface IVideoStream : IDisposable
    {
        int Width { get; }
        int Height { get; }

        /// <summary>Total length, or <see cref="TimeSpan.Zero"/> when the container does not say.</summary>
        TimeSpan Duration { get; }

        /// <summary>0 when the movie has no sound.</summary>
        int AudioChannels { get; }
        int AudioSampleRate { get; }

        /// <summary>
        /// Returns the newest decoded frame whose presentation time is at or before
        /// <paramref name="position"/>, as <c>Width * Height * 4</c> RGBA bytes copied into
        /// <paramref name="rgba"/>. False when no newer frame than the last one returned is due yet.
        /// </summary>
        bool TryGetFrame(TimeSpan position, byte[] rgba);

        /// <summary>True once the last frame has been handed out (or the video track failed).</summary>
        bool VideoEnded { get; }

        /// <summary>
        /// Copies up to <paramref name="count"/> bytes of interleaved little-endian 16-bit PCM.
        /// Returns 0 when nothing is buffered yet, and -1 at the end of the audio.
        /// </summary>
        int ReadAudio(byte[] buffer, int offset, int count);
    }

    /// <summary>
    /// The registry for <see cref="IVideoDecoder"/>, filled by <c>caps.VideoDecoder(...)</c>.
    /// Launcher-lifetime, not cleared at teardown. Null means "this platform plays no video", and
    /// every movie is then reported as finished at once — the behaviour before this existed.
    /// </summary>
    public static class VideoDecoderBackend
    {
        public static IVideoDecoder? Decoder { get; private set; }

        /// <summary>Idempotent by assignment, like every registry a head may compose twice.</summary>
        public static void SetDecoder(IVideoDecoder? decoder) => Decoder = decoder;
    }
}
