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
}
