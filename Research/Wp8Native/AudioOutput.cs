namespace WPR.Wp8Native
{
    /// <summary>
    /// Where XAudio2's source voices are played: the host's own audio stack. Null in the probe,
    /// which keeps the old behaviour of playing into silence.
    /// </summary>
    /// <remarks>
    /// Deliberately the smallest shape a streaming voice needs, so the runtime names no audio
    /// library: <see cref="XAudio2Runtime"/> converts every format the game uses to 16-bit PCM
    /// with one or two channels, and the host only queues and plays it.
    /// </remarks>
    public interface IAudioOutput
    {
        /// <summary>A voice of 16-bit PCM at <paramref name="sampleRate"/>, 1 or 2 channels.</summary>
        IAudioVoiceOutput CreateVoice(int sampleRate, int channels);
    }

    /// <summary>
    /// One streaming voice. Every member is called on the emulator's thread; an implementation
    /// may defer the work to its own.
    /// </summary>
    public interface IAudioVoiceOutput : IDisposable
    {
        /// <summary>Queues interleaved 16-bit PCM behind whatever is already queued.</summary>
        void Submit(byte[] pcm);

        /// <summary>How many submitted buffers have finished playing (or were flushed), ever.</summary>
        int Completed { get; }

        void Play();

        void Pause();

        /// <summary>Drops everything queued; it all counts as completed.</summary>
        void Flush();

        /// <summary>Linear gain, 0 to 1.</summary>
        void SetVolume(float volume);

        /// <summary>Pitch shift in octaves, -1 to 1.</summary>
        void SetPitch(float octaves);
    }
}
