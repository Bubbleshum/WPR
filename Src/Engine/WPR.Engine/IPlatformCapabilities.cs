#nullable enable
using WPR.Engine.Audio;
using WPR.Engine.Notifications;
using WPR.Engine.Sensors;
using WPR.Engine.Graphics;
using WPR.Xna.Rhi;

namespace WPR.Engine
{
    /// <summary>
    /// What a platform tells the engine it has. A head declares capabilities; the engine works out
    /// which registries that implies.
    ///
    /// <para><b>Why this exists.</b> Before it, a head filled seven registries by hand -
    /// <c>XnaBackend</c> (twelve slots), <c>SensorBackend</c>, <c>AudioTranscoderBackend</c>,
    /// <c>AudioBackendRegistry</c>, <c>SilverlightBackend</c>, the graphics driver lever, and a
    /// bare static field for notifications - each invented at a different time, in a different
    /// assembly, with a different lifetime. The two heads' ServicesSetup.cs files were listed in
    /// CLAUDE.md as duplicated code that must be hand-kept-in-sync because "nothing enforces it".
    /// This is that enforcement: one interface, so the two platforms are diffable against each
    /// other, and a new platform is a descriptor rather than an archaeology exercise.</para>
    ///
    /// <para><b>Everything is optional.</b> A capability nobody declares is simply absent, and
    /// absent means "this platform does not have it", never an error. Android declares no tilt
    /// emulator because it has a real accelerometer; Windows declares no platform song player
    /// because FAudio's is fine there. The registries whose accessors DO throw when unset are the
    /// per-launch RHI seams, and those are filled by the game host, not from here.</para>
    ///
    /// <para>Calls chain, so a descriptor reads as a list of facts about the device.</para>
    /// </summary>
    public interface IPlatformCapabilities
    {
        /// <summary>
        /// This platform can report device motion. Android passes its hardware sensor; Windows
        /// passes the keyboard emulator that synthesises one.
        /// </summary>
        IPlatformCapabilities Accelerometer(IAccelerometerProvider provider);

        /// <summary>
        /// This platform wants a specific FNA3D driver. Omit it (or pass
        /// <see cref="WPR.Engine.Graphics.GraphicsDriver.Unspecified"/>) to leave the lever
        /// untouched - which is what the desktop wants, and what keeps Android's fna3d.env force
        /// in place.
        /// </summary>
        /// <param name="overrideDirectory">Where to look for the runtime override file, or null to
        /// disable that escape hatch.</param>
        IPlatformCapabilities GraphicsDriver(GraphicsDriver driver, string? overrideDirectory = null);

        /// <summary>
        /// This platform brings its own audio implementation, layered over the host's default.
        /// May be called more than once; modules compose as a stack.
        /// </summary>
        IPlatformCapabilities Audio(IAudioModule module);

        /// <summary>Install-time audio transcoding (WMA soundtracks to Ogg Vorbis).</summary>
        IPlatformCapabilities AudioTranscoder(IAudioTranscoder transcoder);

        /// <summary>Where achievements are persisted.</summary>
        IPlatformCapabilities Achievements(WPR.Xna.Achievements.IAchievementStore store);

        /// <summary>How to show a notification - in practice the achievement-unlock toast.</summary>
        IPlatformCapabilities Notifications(INotificationManager manager);

        /// <summary>
        /// This platform emulates tilt from the keyboard. Declared only by heads that have a
        /// keyboard and no real sensor; the game host attaches the XNA components when it sees one.
        /// </summary>
        IPlatformCapabilities KeyboardEmulation(IKeyboardEmulationHost host);
    }
}
