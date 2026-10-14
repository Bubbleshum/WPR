using WPR.Engine.Audio;

namespace WPR.Wp8Native
{
    /// <summary>
    /// The runtime's <see cref="IMediaDecoder"/> over the platform's <see cref="IAudioDecoder"/>
    /// (<see cref="AudioDecoderBackend"/>), read live so a head that composes after the game starts
    /// is still seen. No decoder, or one that is not available, decodes nothing - the media engine
    /// then fails compressed sources as a phone without the codec would.
    /// </summary>
    internal sealed class Wp8NativeMediaDecoder(Action<string>? log) : IMediaDecoder
    {
        private static IAudioDecoder? Platform => AudioDecoderBackend.Decoder is { IsAvailable: true } d ? d : null;

        public string Name => Platform?.Name ?? "none";

        public bool CanDecode(string mimeType) => Platform?.CanDecode(mimeType) == true;

        public DecodedAudio? Decode(byte[] container, string? nameHint)
        {
            if (Platform is not { } decoder)
            {
                return null;
            }

            try
            {
                DecodedPcm? pcm = decoder.Decode(container, nameHint);
                log?.Invoke(pcm is null
                    ? $"[wpr-wp8] media: {decoder.Name} could not decode {nameHint}"
                    : $"[wpr-wp8] media: {decoder.Name} decoded {nameHint}: {pcm.SampleRate} Hz, {pcm.Channels} ch{(pcm.HasVideo ? ", has video" : string.Empty)}");
                return pcm is null ? null : new DecodedAudio(pcm.SampleRate, pcm.Channels, pcm.Pcm16, pcm.HasVideo);
            }
            catch (Exception ex)
            {
                log?.Invoke($"[wpr-wp8] media: {decoder.Name} threw on {nameHint}: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// The runtime's <see cref="IMediaVideoDecoder"/> over the platform's <see cref="IVideoDecoder"/>
    /// (<see cref="VideoDecoderBackend"/>), read live like the audio half. No decoder means no video:
    /// the media engine then refuses a movie as a source it cannot play.
    /// </summary>
    internal sealed class Wp8NativeVideoDecoder(Action<string>? log) : IMediaVideoDecoder
    {
        private static IVideoDecoder? Platform => VideoDecoderBackend.Decoder is { IsAvailable: true } d ? d : null;

        public string Name => Platform?.Name ?? "none";

        public IMediaVideoStream? Open(string path)
        {
            if (Platform is not { } decoder)
            {
                return null;
            }

            try
            {
                IVideoStream? stream = decoder.Open(path);
                log?.Invoke(stream is null
                    ? $"[wpr-wp8] media: {decoder.Name} could not open the movie {Path.GetFileName(path)}"
                    : $"[wpr-wp8] media: {decoder.Name} opened the movie {Path.GetFileName(path)}: {stream.Width}x{stream.Height}, " +
                      $"{stream.Duration.TotalSeconds:0.0} s, audio {stream.AudioSampleRate} Hz x{stream.AudioChannels}");
                return stream is null ? null : new Stream(stream);
            }
            catch (Exception ex)
            {
                log?.Invoke($"[wpr-wp8] media: {decoder.Name} threw opening {Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private sealed class Stream(IVideoStream inner) : IMediaVideoStream
        {
            public int Width => inner.Width;

            public int Height => inner.Height;

            public double DurationSeconds => inner.Duration.TotalSeconds;

            public int AudioChannels => inner.AudioChannels;

            public int AudioSampleRate => inner.AudioSampleRate;

            public bool VideoEnded => inner.VideoEnded;

            public bool TryGetFrame(double seconds, byte[] rgba) => inner.TryGetFrame(TimeSpan.FromSeconds(Math.Max(0, seconds)), rgba);

            public int ReadAudio(byte[] buffer, int offset, int count) => inner.ReadAudio(buffer, offset, count);

            public void Dispose() => inner.Dispose();
        }
    }
}
