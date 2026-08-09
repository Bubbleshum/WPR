using System;

namespace WPR.Abstractions.Audio;

/// <summary>
/// The audio mixer/output device. The FNA backend implements it over FAudio; the
/// XNA audio reimplementation and Runtime consume it, replacing the reflective
/// FAudio teardown currently in ApplicationLaunch.cs (see STAGE5-SIZING Risk #1 —
/// the ordered shutdown is expressed through <c>IGameHost</c>).
/// </summary>
public interface IAudioDevice
{
    /// <summary>Master output volume in [0, 1].</summary>
    float MasterVolume { get; set; }

    /// <summary>Creates a sound from raw PCM data.</summary>
    ISound CreateSound(ReadOnlySpan<byte> pcm, int sampleRate, int channels);
}
