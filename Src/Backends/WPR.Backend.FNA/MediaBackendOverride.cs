using System;
using WPR.Xna.Rhi;

namespace WPR.Backend.FNA
{
    /// <summary>
    /// Lets a platform head replace the <see cref="IMediaBackend"/> that
    /// <see cref="FnaGameHost"/> would otherwise install.
    ///
    /// <para><b>Why an override rather than the head just calling
    /// <c>XnaBackend.SetMedia(...)</c>.</b> Every other seam the heads fill —
    /// <c>SensorBackend</c>, <c>AudioTranscoderBackend</c>, the achievement store — is registered
    /// once at launcher startup and lives for the process. The media backend is not: it is one of
    /// the slots <see cref="FnaGameHost.RunAsync"/> installs *per game launch* and clears in its
    /// <c>finally</c> (a backend registry must not outlive a run — ADR Risk #1). A head that
    /// registered directly would be overwritten by the next launch, so instead it registers a
    /// factory here and the host asks for it at the moment it composes the slot.</para>
    ///
    /// <para><b>Why Android needs it.</b> FAudio's song player decodes one full second of Vorbis
    /// per buffer with a queue depth of one, refilled from <c>OnBufferEnd</c> — so at every buffer
    /// boundary the voice has nothing queued *and* the audio thread is busy decoding. On desktop
    /// the decode fits inside the deadline and it goes unnoticed; on a phone it is an audible click
    /// once per second. The clean fix is to rebuild FAudio with a double-buffered
    /// <c>XNA_SongSubmitBuffer</c>, but <c>libFAudio.so</c> ships prebuilt and there is no NDK in
    /// this toolchain, so Android instead swaps the whole song path for the platform's own
    /// media player.</para>
    ///
    /// <para>Null (the default) means "use <see cref="FnaMediaBackend"/>", which is what Windows
    /// does and what the behaviour was before this existed.</para>
    /// </summary>
    public static class MediaBackendOverride
    {
        /// <summary>
        /// Factory for the replacement backend, or null to use the FNA/FAudio default.
        /// Set by the platform head in <c>ServicesSetup.Start()</c>.
        /// </summary>
        public static Func<IMediaBackend>? Factory { get; private set; }

        /// <summary>
        /// Installs the factory. Idempotent by assignment — a head that re-runs
        /// <c>ServicesSetup.Start()</c> (Android recreates the process straight into any activity,
        /// and <c>GameActivity</c>'s <c>:game</c> process runs it again) replaces rather than
        /// accumulates. Pass null to go back to the default.
        /// </summary>
        public static void SetFactory(Func<IMediaBackend>? factory) => Factory = factory;

        /// <summary>
        /// Produces the backend for one game launch. Called by <see cref="FnaGameHost"/>; a new
        /// instance per launch, deliberately, because that is the lifetime the slot has.
        ///
        /// <para>A factory that throws must not take the launch down over background music, so it
        /// falls back to the FNA default and logs.</para>
        /// </summary>
        internal static IMediaBackend Create()
        {
            Func<IMediaBackend>? factory = Factory;
            if (factory == null)
            {
                return new FnaMediaBackend();
            }

            try
            {
                return factory() ?? new FnaMediaBackend();
            }
            catch (Exception ex)
            {
                Microsoft.Xna.Framework.FNALoggerEXT.LogWarn?.Invoke(
                    "Media backend override threw, falling back to FAudio's song player: " + ex);
                return new FnaMediaBackend();
            }
        }
    }
}
