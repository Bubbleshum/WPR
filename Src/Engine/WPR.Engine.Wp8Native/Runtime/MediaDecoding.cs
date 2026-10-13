namespace WPR.Wp8Native
{
    /// <summary>Compressed audio decoded for playback: interleaved 16-bit PCM, one or two channels.</summary>
    /// <param name="HasVideo">The container also carried a video stream, which was not decoded.</param>
    public sealed record DecodedAudio(int SampleRate, int Channels, byte[] Pcm16, bool HasVideo);

    /// <summary>
    /// A host-provided decoder for the compressed media the media engine is fed (MP3 above all; AAC,
    /// MP4 audio and WMA where the platform has a codec). The runtime decodes only uncompressed WAVE
    /// itself; anything else goes through this, which the platform head fills (through the engine
    /// tier's <c>AudioDecoderBackend</c>). Absent - or answering null - the media engine reports
    /// MF_MEDIA_ENGINE_ERR_SRC_NOT_SUPPORTED, as a phone without the codec would.
    /// </summary>
    /// <remarks>
    /// Kept as an interface here so <c>Src/Engine/WPR.Engine.Wp8Native/Runtime</c> carries no platform code: the
    /// Windows head implements it over its bundled ffmpeg, an Android one would over MediaCodec.
    /// Called on the emulator thread, synchronously, while the media engine loads a source.
    /// </remarks>
    public interface IMediaDecoder
    {
        /// <summary>For the report: what decodes (e.g. <c>"ffmpeg"</c>).</summary>
        string Name { get; }

        /// <summary>True when a MIME type (lower case, parameters stripped) is one this decoder handles.</summary>
        bool CanDecode(string mimeType);

        /// <summary>
        /// Decodes the first audio stream of <paramref name="container"/> to 16-bit PCM, keeping at
        /// most two channels. Null when there is no audio stream or no codec for it. Must not throw.
        /// </summary>
        DecodedAudio? Decode(byte[] container, string? nameHint);
    }

    /// <summary>What a media file's first bytes say it is - for the report and the failure records.</summary>
    internal static class MediaContainer
    {
        public static string Sniff(ReadOnlySpan<byte> b)
        {
            if (b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8))
            {
                return b[8..12].SequenceEqual("WAVE"u8) ? "WAVE" : b[8..12].SequenceEqual("XWMA"u8) ? "xWMA" : "RIFF";
            }

            if (b.Length >= 3 && b[..3].SequenceEqual("ID3"u8))
            {
                return "MP3";
            }

            if (b.Length >= 2 && b[0] == 0xFF && (b[1] & 0xE0) == 0xE0)
            {
                return ((b[1] >> 1) & 3) == 0 ? "AAC (ADTS)" : "MP3";
            }

            if (b.Length >= 8 && b[4..8].SequenceEqual("ftyp"u8))
            {
                return "MP4";
            }

            if (b.Length >= 16 && b[..16].SequenceEqual(new byte[] { 0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C }))
            {
                return "ASF (WMA/WMV)";
            }

            if (b.Length >= 4 && b[..4].SequenceEqual("OggS"u8))
            {
                return "Ogg";
            }

            return "unknown";
        }

        /// <summary>A MIME type's base, lower case: <c>"audio/mpeg; codecs=x"</c> is <c>"audio/mpeg"</c>.</summary>
        public static string MimeBase(string type) => type.Split(';')[0].Trim().ToLowerInvariant();

        /// <summary>The MIME types an uncompressed WAVE answers to.</summary>
        public static bool IsWaveMime(string mime) => mime is "audio/wav" or "audio/x-wav" or "audio/wave" or "audio/vnd.wave";
    }
}
