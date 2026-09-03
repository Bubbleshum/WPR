#nullable enable
using WPR.Engine.Notifications;
using WPR.Engine.Audio;
using System;
using System.Collections.Generic;
using WPR.Engine.Notifications;
using WPR.Engine.Audio;
using WPR.Engine.Sensors;
using WPR.Common;
using WPR.Engine.Graphics;
using WPR.Xna.Rhi;

namespace WPR.Engine
{
    /// <summary>
    /// The composition root: takes a <see cref="PlatformDescriptor"/> and fills every registry it
    /// implies.
    ///
    /// <para>This is the only place in the tree that knows the full set of registries. A platform
    /// head knows none of them.</para>
    /// </summary>
    public static class PlatformComposition
    {
        /// <summary>The platform composed most recently, or null before the first call.</summary>
        public static string? ComposedPlatform { get; private set; }

        /// <summary>
        /// Applies a platform's declaration.
        ///
        /// <para><b>Idempotent by design.</b> Every registry underneath is set-by-assignment, and
        /// this runs more than once per process on Android - the launcher composes at startup and
        /// <c>GameActivity</c>'s <c>:game</c> process composes again. Re-applying the same
        /// descriptor must therefore land in the same state, not accumulate. The one registry that
        /// could accumulate, the audio module stack, de-duplicates by module name for exactly this
        /// reason.</para>
        ///
        /// <para>Returns a one-line summary of what was declared, e.g.
        /// <c>Android: accelerometer=AndroidAccelerometerProvider driver=OpenGL audio=[AndroidMediaPlayer]
        /// transcoder=RemoteAudioTranscoder notifications=AndroidNotificationManager</c>. Log it -
        /// it replaces having to grep several subsystems to find out how a device was set up.</para>
        /// </summary>
        public static string Apply(PlatformDescriptor descriptor)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));

            Recorder recorder = new Recorder();
            descriptor.Describe(recorder);
            recorder.Commit();

            ComposedPlatform = descriptor.Name;
            string summary = descriptor.Name + ": " + recorder.Summarise();
            Log.Info(LogCategory.AppList, "[wpr-platform] " + summary);
            return summary;
        }

        /// <summary>
        /// Collects the declaration first and applies it second.
        ///
        /// <para>Two-phase on purpose: a descriptor that throws half-way through leaves NOTHING
        /// registered rather than a half-configured platform, which is far easier to diagnose than
        /// a device with an accelerometer but no audio. It also means the summary describes what
        /// was actually committed.</para>
        /// </summary>
        private sealed class Recorder : IPlatformCapabilities
        {
            private IAccelerometerProvider? _accelerometer;
            /* Fully qualified: the interface member below is also called GraphicsDriver, and a
             * method name shadows a type name inside the class that declares it. */
            private WPR.Engine.Graphics.GraphicsDriver _driver = WPR.Engine.Graphics.GraphicsDriver.Unspecified;
            private string? _driverOverrideDirectory;
            private readonly List<IAudioModule> _audio = new List<IAudioModule>();
            private IAudioTranscoder? _transcoder;
            private WPR.Xna.Achievements.IAchievementStore? _achievements;
            private INotificationManager? _notifications;
            private IKeyboardEmulationHost? _tilt;

            public IPlatformCapabilities Accelerometer(IAccelerometerProvider provider)
            {
                _accelerometer = provider ?? throw new ArgumentNullException(nameof(provider));
                return this;
            }

            public IPlatformCapabilities GraphicsDriver(WPR.Engine.Graphics.GraphicsDriver driver, string? overrideDirectory = null)
            {
                _driver = driver;
                _driverOverrideDirectory = overrideDirectory;
                return this;
            }

            public IPlatformCapabilities Audio(IAudioModule module)
            {
                _audio.Add(module ?? throw new ArgumentNullException(nameof(module)));
                return this;
            }

            public IPlatformCapabilities AudioTranscoder(IAudioTranscoder transcoder)
            {
                _transcoder = transcoder ?? throw new ArgumentNullException(nameof(transcoder));
                return this;
            }

            public IPlatformCapabilities Achievements(WPR.Xna.Achievements.IAchievementStore store)
            {
                _achievements = store ?? throw new ArgumentNullException(nameof(store));
                return this;
            }

            public IPlatformCapabilities Notifications(INotificationManager manager)
            {
                _notifications = manager ?? throw new ArgumentNullException(nameof(manager));
                return this;
            }

            public IPlatformCapabilities KeyboardEmulation(IKeyboardEmulationHost host)
            {
                _tilt = host ?? throw new ArgumentNullException(nameof(host));
                return this;
            }

            internal void Commit()
            {
                if (_accelerometer != null) WPR.Engine.Sensors.SensorBackend.SetAccelerometer(_accelerometer);

                /* Declared even when Unspecified: the preference registry treats that as "leave the
                 * lever alone", which is a different instruction from "clear it", and recording it
                 * keeps the resolved answer available to the launch log either way. */
                GraphicsDriverPreference.Declare(_driver, _driverOverrideDirectory);

                foreach (IAudioModule module in _audio) AudioBackendRegistry.Register(module);

                if (_transcoder != null) AudioTranscoderBackend.SetTranscoder(_transcoder);
                if (_achievements != null) XnaBackend.SetAchievements(_achievements);
                if (_notifications != null) NotificationBackend.SetManager(_notifications);
                if (_tilt != null) XnaBackend.SetKeyboardEmulation(_tilt);
            }

            internal string Summarise()
            {
                List<string> parts = new List<string>();
                parts.Add("accelerometer=" + Name(_accelerometer));
                parts.Add(GraphicsDriverPreference.Describe());
                parts.Add("audio=[" + string.Join(", ", _audio.ConvertAll(m => m.Name)) + "]");
                parts.Add("transcoder=" + Name(_transcoder));
                parts.Add("achievements=" + Name(_achievements));
                parts.Add("notifications=" + Name(_notifications));
                parts.Add("tilt=" + Name(_tilt));
                return string.Join(" ", parts);
            }

            private static string Name(object? o) => o == null ? "none" : o.GetType().Name;
        }
    }
}
