using WPR.Input.Keyboard;
using WPR.Engine;
using WPR.Engine.Graphics;
using WPR.Platform.Windows.Input;

namespace WPR.Platform.Windows
{
    /// <summary>
    /// What a Windows desktop is, from the engine's point of view. Compare with
    /// <c>WPR.Platform.Android.AndroidPlatform</c> — the two are meant to be read side by side,
    /// which is the whole reason this shape exists. The heads' <c>ServicesSetup.Start()</c> files
    /// were previously near-duplicates that had to be kept in sync by hand.
    /// </summary>
    internal sealed class WindowsPlatform : PlatformDescriptor
    {
        public override string Name => "Windows";

        public override void Describe(IPlatformCapabilities caps) => caps
            // A desktop PC has no motion hardware, so the provider synthesises readings from the
            // keys the Controls page binds. The WP7 Accelerometer shim sees only IAccelerometerProvider
            // and never learns which of the two it got.
            .Accelerometer(new WPR.Input.Keyboard.KeyboardAccelerometerProvider())

            // ...and because the readings are synthetic, this head is also the one that needs the
            // keyboard-tilt input path. Android declares no counterpart: it has a real sensor, so
            // the game host attaches no tilt components there.
            .KeyboardEmulation(new WPR.Input.Keyboard.KeyboardEmulationHost())

            // Deliberately no GraphicsDriver declaration. Windows compiles in D3D11 and OpenGL,
            // FNA3D offers D3D11 first, and it is the right answer — so the lever stays untouched
            // rather than being explicitly set to Automatic, which would clear a hint the desktop
            // never sets in the first place. See GraphicsDriver.Unspecified.

            // FFMpegCore over the ffmpeg.exe this head ships beside the executable. WP7 XNA titles
            // ship .wma soundtracks and the song backend decodes Ogg Vorbis only, so the installer
            // transcodes. No Audio(...) module: FAudio, which the game host installs as the base,
            // is correct on desktop — its once-per-second song stutter only bites on a phone.
            .AudioTranscoder(new Audio.FFMpegCoreAudioTranscoder())

            .Achievements(new WPR.Database.Achievements.EfAchievementStore());
    }
}
