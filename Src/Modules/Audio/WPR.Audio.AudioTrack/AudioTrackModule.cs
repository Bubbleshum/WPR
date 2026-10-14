#nullable enable
using WPR.Engine.Audio;

namespace WPR.Audio.AudioTrack
{
    /// <summary>Which streaming voices <see cref="AudioTrackModule"/> takes from the module below.</summary>
    public enum AudioTrackScope
    {
        /// <summary>Every 16-bit PCM <c>DynamicSoundEffectInstance</c>, XNA titles included.</summary>
        AllDynamicVoices,

        /// <summary>
        /// Only while a host has asked for low-latency streaming
        /// (<see cref="AudioBackendRegistry.LowLatencyStreaming"/>, set by the WP8 native host for
        /// its launch). XNA titles keep FAudio.
        /// </summary>
        LowLatencyRequests,
    }

    /// <summary>
    /// Plugs Android's <c>AudioTrack</c> into the sound-effect seam for streaming voices only.
    /// Everything else in <see cref="IAudioBackend"/> - the device, static sound effects, 3D maths,
    /// the microphone, and float or non-16-bit voices - is forwarded to the module below (FAudio).
    /// </summary>
    /// <remarks>
    /// Registered by the Android head through <c>caps.Audio(new AudioTrackModule(...))</c>. The
    /// launch log then reads <c>sound=AudioTrack</c>, which means "streaming voices on AudioTrack,
    /// the rest on whatever is below": the composition line names the top of the chain.
    /// </remarks>
    public sealed class AudioTrackModule : AudioModule
    {
        private readonly AudioTrackScope _scope;

        public AudioTrackModule(AudioTrackScope scope) => _scope = scope;

        public override string Name => "AudioTrack";

        public override IAudioBackend? CreateAudio(IAudioBackend? next) =>
            next is null ? null : new AudioTrackSoundBackend(next, _scope);
    }
}
