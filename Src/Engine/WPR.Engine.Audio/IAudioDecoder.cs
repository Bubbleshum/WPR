namespace WPR.Engine.Audio
{
    /// <summary>Compressed audio decoded to interleaved 16-bit PCM, one or two channels.</summary>
    /// <param name="HasVideo">The container also carried a video stream (which is not decoded).</param>
    public sealed record DecodedPcm(int SampleRate, int Channels, byte[] Pcm16, bool HasVideo);

    /// <summary>
    /// Decodes compressed media to PCM at run time, in memory: the platform's codecs, for audio a
    /// game hands to an API that expects the OS to decode it.
    ///
    /// <para><b>Why this seam exists.</b> WP8 native titles play music through the Media
    /// Foundation media engine (<c>IMFMediaEngine</c>), which the WP8 runtime in
    /// <c>Src/Engine/WPR.Engine.Wp8Native/Runtime</c> implements. A scan of the 102 ARM titles in the library found its
    /// users (Assassin's Creed Pirates, Doodle God Blitz, FIFA 14/15, the GameMaker and Unity
    /// runners) feeding it MP3 - not the WAVE the runtime decodes itself. Install-time transcoding,
    /// the WP7 answer (<see cref="IAudioTranscoder"/>), does not fit: the same titles ship MP3s
    /// their own code decodes (Angry Birds alone has hundreds), and the media engine is also fed
    /// bytes from memory through <c>IMFByteStream</c>, which no install step sees. So the runtime
    /// asks the platform, once per source, as the phone's media engine asked its codecs.</para>
    ///
    /// <para>Its vocabulary is bytes and PCM only, like the transcoder's paths: nothing about a
    /// decode needs an XNA or WinRT type.</para>
    /// </summary>
    public interface IAudioDecoder
    {
        /// <summary>For logs: what decodes (e.g. <c>"ffmpeg.exe"</c>).</summary>
        string Name { get; }

        /// <summary>True when the decoder can actually run (its tool or library is present).</summary>
        bool IsAvailable { get; }

        /// <summary>True for a MIME type (lower case, no parameters) this decoder handles - what <c>CanPlayType</c> answers from.</summary>
        bool CanDecode(string mimeType);

        /// <summary>
        /// Decodes the first audio stream of <paramref name="data"/>, keeping at most two channels;
        /// null when there is none or no codec for it. Must not throw; may block (a source is
        /// decoded once, as it is loaded).
        /// </summary>
        DecodedPcm? Decode(byte[] data, string? nameHint);
    }

    /// <summary>
    /// The registry for <see cref="IAudioDecoder"/>, filled by <c>caps.AudioDecoder(...)</c>.
    /// Launcher-lifetime, stateless, not cleared at teardown. <b>Null is a supported state</b>:
    /// a platform without a decoder plays only uncompressed audio, and every compressed source
    /// fails the way a phone without the codec fails it (the media engine's
    /// MF_MEDIA_ENGINE_ERR_SRC_NOT_SUPPORTED), which games handle.
    /// </summary>
    public static class AudioDecoderBackend
    {
        public static IAudioDecoder? Decoder { get; private set; }

        /// <summary>Idempotent by assignment, like every registry a head may compose twice.</summary>
        public static void SetDecoder(IAudioDecoder? decoder) => Decoder = decoder;
    }
}
