#nullable enable
using WPR.Engine.Audio;

namespace WPR.Audio.FAudio
{
    /// <summary>
    /// Plugs this project into the audio seams: the full set — sound effects (FAudio), XACT (FACT)
    /// and media (FAudio's <c>XNA_Song</c> player + Theorafile).
    ///
    /// <para>This is the <b>base</b> module. The game host installs it with
    /// <c>AudioBackendRegistry.SetBase(...)</c> rather than <c>Register</c>, so it is always composed
    /// first and every platform module layers over it — including one that replaces only part of a
    /// seam and forwards the rest back here, which is what Android does with video.</para>
    ///
    /// <para>It ignores the <c>next</c> argument on all three factories by design: nothing sensibly
    /// sits below the implementation of last resort.</para>
    /// </summary>
    public sealed class FAudioModule : AudioModule
    {
        public override string Name => "FAudio";

        public override IAudioBackend CreateAudio(IAudioBackend? next) => new FAudioSoundBackend();

        public override IXactBackend CreateXact(IXactBackend? next) => new FAudioXactBackend();

        public override IMediaBackend CreateMedia(IMediaBackend? next) => new FAudioMediaBackend();
    }
}
