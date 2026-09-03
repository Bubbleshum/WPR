#nullable enable
using WPR.Engine.Audio;

namespace WPR.Audio.AndroidMediaPlayer
{
    /// <summary>
    /// Plugs this project into the audio seams. It claims <b>only the song half of the media
    /// seam</b>: sound effects stay on FAudio, XACT stays on FACT, and video is handed straight back
    /// to whatever module sits below.
    ///
    /// <para>Registered by the Android head in <c>ServicesSetup.Start()</c> with
    /// <c>AudioBackendRegistry.Register(new AndroidMediaPlayerModule())</c>. Because the FAudio
    /// module is the registry's <em>base</em>, this one is always composed after it and receives its
    /// media backend as <c>next</c> — which is exactly the video decoder
    /// <see cref="AndroidMediaPlayerBackend"/> forwards to.</para>
    ///
    /// <para>This partial-seam case is the reason <c>IAudioModule</c>'s factories take the module
    /// below rather than being plain parameterless constructors. Before the 2026-09-01 split the
    /// same delegation was a hardcoded <c>new WPR.Backend.FNA.FnaMediaBackend()</c> inside the
    /// backend, which is what forced the Android head's audio code to reference the FNA host
    /// backend at all.</para>
    /// </summary>
    public sealed class AndroidMediaPlayerModule : AudioModule
    {
        public override string Name => "AndroidMediaPlayer";

        public override IMediaBackend CreateMedia(IMediaBackend? next) =>
            new AndroidMediaPlayerBackend(next);
    }
}
