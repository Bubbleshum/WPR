namespace WPR.Abstractions.Audio;

/// <summary>Playback state of a sound or music track.</summary>
public enum SoundState
{
    Stopped,
    Playing,
    Paused,
}

/// <summary>
/// A playable sound instance owned by an <see cref="IAudioDevice"/>. Backs the XNA
/// <c>SoundEffect</c>/<c>SoundEffectInstance</c> reimplementation (Stage 5c/5d).
/// </summary>
public interface ISound : IDisposable
{
    SoundState State { get; }

    /// <summary>Linear volume in [0, 1].</summary>
    float Volume { get; set; }

    void Play();
    void Stop();
    void Pause();
    void Resume();
}
