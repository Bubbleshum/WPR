#nullable enable
namespace WPR.Engine
{
    /// <summary>
    /// One platform's declaration of what it has. A head implements this once and the engine
    /// composes everything from it.
    ///
    /// <para>Example - the whole of what makes Android different from Windows:</para>
    /// <code>
    /// public sealed class AndroidPlatform : PlatformDescriptor
    /// {
    ///     public override string Name =&gt; "Android";
    ///     public override void Describe(IPlatformCapabilities caps) =&gt; caps
    ///         .Accelerometer(new AndroidAccelerometerProvider())
    ///         .GraphicsDriver(IsEmulator() ? GraphicsDriver.Automatic : GraphicsDriver.OpenGL, filesDir)
    ///         .Audio(new AndroidMediaPlayerModule())
    ///         .AudioTranscoder(new RemoteAudioTranscoder(context))
    ///         .Notifications(new AndroidNotificationManager(context));
    /// }
    /// </code>
    ///
    /// <para><b>Declare answers, not policies.</b> Note the emulator check above happens in the
    /// head and only its RESULT is declared. Anything requiring a platform API - reading
    /// <c>Android.OS.Build</c>, probing a device - belongs on that side of the line; the engine
    /// only ever sees the conclusion. That keeps the engine free of per-platform conditionals,
    /// which is the thing that made the old arrangement hard to follow.</para>
    ///
    /// <para><b>Describe must be cheap and side-effect-free.</b> It runs at composition time and
    /// may run more than once per process: Android recreates its process straight into any
    /// activity, and <c>GameActivity</c>'s <c>:game</c> process runs the composition root again.
    /// Construct implementations here, but do not start threads, open devices or touch a
    /// registry directly.</para>
    /// </summary>
    public abstract class PlatformDescriptor
    {
        /// <summary>Short name for logs, e.g. <c>Windows</c> or <c>Android</c>.</summary>
        public abstract string Name { get; }

        /// <summary>Declares this platform's capabilities. See the class remarks.</summary>
        public abstract void Describe(IPlatformCapabilities capabilities);

        public override string ToString() => Name;
    }
}
