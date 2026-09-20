using WPR.Engine;
using WPR.Engine.Graphics;

namespace WPR.Platform.Android
{
    /// <summary>
    /// What an Android device is, from the engine's point of view. Read it against
    /// <c>WPR.Platform.Windows.WindowsPlatform</c>: the differences between the two files are
    /// exactly the differences between the two platforms, which is the property the old pair of
    /// hand-synchronised <c>ServicesSetup.Start()</c> bodies could not give.
    /// </summary>
    internal sealed class AndroidPlatform : PlatformDescriptor
    {
        private readonly global::Android.Content.Context? _context;
        private readonly string? _externalFilesDirectory;

        /// <param name="context">Application context, never an activity — the things built here
        /// outlive any one screen, and this descriptor is applied again in GameActivity's
        /// <c>:game</c> process, where holding an activity would pin it for the whole game run.</param>
        /// <param name="externalFilesDirectory">Where the graphics driver override file lives.</param>
        internal AndroidPlatform(global::Android.Content.Context? context, string? externalFilesDirectory)
        {
            _context = context;
            _externalFilesDirectory = externalFilesDirectory;
        }

        public override string Name => "Android";

        public override void Describe(IPlatformCapabilities caps)
        {
            // Point the crash breadcrumb at its storage before anything asks it a question —
            // ChosenGraphicsDriver() below reads it. Keyed on Build.Fingerprint, which changes with
            // any system image update, so a driver condemned by a firmware that is no longer
            // installed gets another chance instead of inheriting the verdict for ever.
            WPR.Engine.Graphics.GraphicsDriverProbe.Configure(
                _externalFilesDirectory, global::Android.OS.Build.Fingerprint);

            // Same directory, same cross-process reason: a device can only be measured in the
            // :game process and the diagnostics screen that shows it lives in the launcher.
            WPR.Engine.Graphics.GraphicsCapabilitiesStore.Configure(_externalFilesDirectory);

            // The device's real accelerometer, read from SensorManager by the WPR.Input.AndroidSensor
            // module. The WP7 Accelerometer shim sees only IAccelerometerProvider and never learns
            // which implementation it got. No KeyboardEmulation counterpart — a phone does not need
            // one, and the game host therefore attaches no tilt components here.
            caps.Accelerometer(new WPR.Input.AndroidSensor.AndroidAccelerometerProvider());

            // THE graphics decision, declared as an answer rather than a policy — and since
            // 2026-09-07 it is the SAME answer on the emulator and on hardware, which is the
            // point of it. Emulator results are only worth anything if both run one driver.
            //
            // Vulkan, because FNA3D's OpenGL driver marshals every off-thread GPU call onto the
            // device thread and blocks the caller until the next SwapBuffers drains the queue
            // (ForceToMainThread — 19 call sites; Vulkan and D3D11 have none). A game that loads
            // content on a worker while its game thread waits on a lock that worker holds
            // deadlocks with no crash and no CPU. Fable: Coin Golf did exactly that.
            //
            // This REVERSES the old force of OpenGL, which existed because Vulkan T-posed every
            // skinned character. That was never a shader-translation bug: BlendIndices is Byte4,
            // which maps to VK_FORMAT_R8G8B8A8_USCALED, and Adreno does not support the optional
            // *_SCALED vertex formats — so the attribute delivered nothing and every vertex
            // resolved to bone 0, which is identity. VertexFormatExpansion now rewrites those
            // formats to float above the driver. If that is ever reverted, revert this too.
            //
            // By name rather than Automatic: automatic order offers OpenGL first on Android and
            // would land straight back on the deadlocking driver. A per-device fna3d_driver.txt
            // still overrides this for testing a regression.
            //
            // ChosenGraphicsDriver, not a bare Vulkan, because Vulkan is not universally usable:
            // FNA3D's Vulkan driver is unfinished, and the ladder in
            // SDL2_FNAPlatform.PrepareWindowAttributesWithFallback can only rescue a driver that
            // declines at PrepareWindowAttributes — one that prepares and then fails inside
            // FNA3D_CreateDevice reaches the player as a black screen, an error dialog and a dead
            // game process, with no way back. The override file is the triage tool for that and
            // needs a PC; this is the same lever for someone holding only the phone.
            caps.GraphicsDriver(ChosenGraphicsDriver(), _externalFilesDirectory);

            // THE content decision, and the mirror image of the Windows head's. Here '\' is an
            // ordinary character in a filename, so a WP7 title's hardcoded "Content\Credits.xml"
            // names one file in the install root instead of Credits.xml inside Content. Games
            // swallow the resulting exception, so the symptom is never a file error — Battlewagon
            // drew its animated background for ever and never built its menu, throwing an NRE on
            // every frame from a field the failed load left null.
            //
            // A fact, not an instruction: WPR.Engine.Content.ContentPaths decides what to do about
            // it, and the framework shims the patcher points games at are one call each into that.
            caps.ContentPaths(new WPR.Engine.Content.ContentPathRules
            {
                WindowsSeparatorsAreNative = false,
                ProbeInstallFolderForRelativePaths = true,
            });

            // Replace FAudio's song player with the platform's own. FAudio's XNA_Song decodes a
            // full second of Vorbis per buffer with a queue depth of one, refilled from
            // OnBufferEnd, so once per second the voice starves while the audio thread decodes —
            // audible on a phone as a click exactly once per second. The module claims the song
            // half only; sound effects, XACT and video stay on FAudio.
            caps.Audio(new WPR.Audio.AndroidMediaPlayer.AndroidMediaPlayerModule());

            if (_context != null)
            {
                // Install-time transcoding of .wma soundtracks. NOT FFmpegKitAudioTranscoder
                // directly: running ffmpeg-kit leaves that process unable to complete another Mono
                // stop-the-world, which surfaced as "installing a second game in one launch hangs".
                // RemoteAudioTranscoder forwards each file to TranscodeService in the :transcode
                // process, which throws itself away when the batch goes quiet — the same answer
                // GameActivity gives for a game run, for the same reason.
                caps.AudioTranscoder(new Audio.RemoteAudioTranscoder(_context));

                // Where achievement-unlock toasts go. Nothing in this head assigned this for a
                // long time, so every unlock NullReferenced into BeginAwardAchievement's own catch
                // and no notification ever appeared — the achievement was still awarded and
                // persisted, it just went unseen.
                caps.Notifications(
                    new WPR.Notifications.AndroidChannel.AndroidNotificationManager(
                        _context, Resource.Drawable.ic_stat_wpr));

                // The handset's vibration motor. Microsoft.Devices.VibrateController was an empty
                // method body until this landed, so every WP7 title that buzzed did nothing at all.
                // Windows declares no counterpart — a desktop PC has no motor, and the seam degrades
                // to silence rather than throwing.
                caps.Vibration(new WPR.Vibration.AndroidVibrator.AndroidVibratorProvider(_context));
            }

            caps.Achievements(new WPR.Database.Achievements.EfAchievementStore());
        }

        /// <summary>
        /// The driver to declare: whatever the user picked in Settings, then whatever the crash
        /// breadcrumb has learned, and Vulkan when neither says otherwise — which is every install
        /// that has not gone looking for this and has never had a launch die on it.
        ///
        /// <para><b>An unrecognised name falls back to Vulkan rather than being passed through.</b>
        /// FNA3D matches the force hint with <c>strcmp</c> against a driver's own <c>Name</c>, so a
        /// typo'd or stale value would match nothing at all and turn into a launch failure — the
        /// exact thing this setting exists to get someone out of. The names here are the two
        /// drivers Android actually compiles in; <c>fna3d_driver.txt</c> stays the unvalidated
        /// escape hatch for anything else.</para>
        ///
        /// <para><b>An explicit choice outranks the breadcrumb, and that ordering is the whole
        /// point of both.</b> The probe exists so someone holding only a phone does not have to
        /// discover the setting; the setting exists so someone whose device the probe got wrong can
        /// overrule it. Letting the probe win would collapse the second case into the first and
        /// leave that person with no way out again.</para>
        /// </summary>
        private static GraphicsDriver ChosenGraphicsDriver()
        {
            string? chosen = WPR.Common.Configuration.Current?.GraphicsDriver;

            if (!string.IsNullOrEmpty(chosen))
            {
                return string.Equals(chosen, "OpenGL", System.StringComparison.OrdinalIgnoreCase)
                    ? GraphicsDriver.OpenGL
                    : GraphicsDriver.Vulkan;
            }

            // Nothing chosen. If the last launch asked for Vulkan and never got a frame onto the
            // screen, take the other driver this platform has rather than repeating it — the
            // failure that leaves this mark behind is a dead game process every single launch, and
            // FNA3D's own ladder cannot rescue it (it only retries a driver that DECLINES, not one
            // that initialises and then dies, still less one that SIGSEGVs).
            //
            // Only Vulkan is demoted. A pending mark against OpenGL means the fallback itself
            // failed, and sending the device back to Vulkan on that evidence would flip it between
            // two broken drivers for ever; leaving it on OpenGL at least keeps the state readable
            // and lets the Settings picker mean something.
            if (string.Equals(
                    WPR.Engine.Graphics.GraphicsDriverProbe.FailedDriver,
                    "Vulkan",
                    System.StringComparison.OrdinalIgnoreCase))
            {
                return GraphicsDriver.OpenGL;
            }

            return GraphicsDriver.Vulkan;
        }
    }
}
