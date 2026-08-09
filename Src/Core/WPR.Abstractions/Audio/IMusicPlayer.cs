namespace WPR.Abstractions.Audio;

/// <summary>
/// Background music / media playback. Backs the XNA <c>MediaPlayer</c>/<c>Song</c>
/// reimplementation (Stage 5d). Distinct from <see cref="IAudioDevice"/> because WP7
/// media playback has its own lifecycle (and interacts with MediaPlayerLauncher).
/// </summary>
public interface IMusicPlayer
{
    SoundState State { get; }

    /// <summary>Linear volume in [0, 1].</summary>
    float Volume { get; set; }

    /// <summary>Starts playback of the track at the given path/URI.</summary>
    void Play(string trackUri);

    void Stop();
    void Pause();
    void Resume();
}
