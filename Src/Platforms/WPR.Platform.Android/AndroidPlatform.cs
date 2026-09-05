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
            // The device's real accelerometer. The WP7 Accelerometer shim sees only
            // IAccelerometerProvider, so the hardware code (and its Xamarin.Essentials dependency) stays
            // in this head. No KeyboardEmulation counterpart — a phone does not need one, and the game
            // host therefore attaches no tilt components here.
            caps.Accelerometer(new WPR.Input.XamarinEssentials.EssentialsAccelerometerProvider());

            // THE graphics decision, declared as an answer rather than a policy.
            //
            // fna3d.env forces OpenGL process-wide because FNA3D's Vulkan driver mistranslates
            // SkinnedEffect's relative-addressed bone array and T-posed every animated character on
            // real hardware. But the emulator cannot run the OpenGL path at all — it leaves the
            // game's own clear colour on screen and draws nothing. So the answer differs per
            // device and has to be decided at runtime.
            //
            // Detection is biased to FALSE NEGATIVES on purpose and must stay that way: a missed
            // emulator only means the emulator renders nothing, whereas a false positive puts a
            // real phone back on the T-posing driver. Never invert this into "force OpenGL only
            // when we detect hardware".
            caps.GraphicsDriver(
                AndroidDeviceKind.IsEmulator()
                    /* Automatic, not "Vulkan" by name: FNA3D already offers OpenGL first and falls
                     * through there, so this stays correct if an emulator image ever gains a
                     * working GL translator, and it does not hard-fail on an image built without
                     * the Vulkan driver. */
                    ? GraphicsDriver.Automatic
                    /* Physical device: re-declaring OpenGL is equivalent to leaving fna3d.env's
                     * force alone, and says so explicitly rather than relying on the env file. */
                    : GraphicsDriver.OpenGL,
                _externalFilesDirectory);

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
    }
}
