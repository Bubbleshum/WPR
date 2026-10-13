#if __ANDROID__
using System;
using System.IO;
using WPR.Common;
using WPR.Xna.Rhi;

namespace WPR.Audio.FAudio
{
	/// <summary>
	/// Which output FAudio uses on Android, and the per-device way back to the old one.
	/// </summary>
	/// <remarks>
	/// <para>
	/// FAudio opens its own low-latency AAudio stream on Android 8.1+ (the vendored
	/// <c>FAudio_platform_aaudio.inl</c>, built into <c>libFAudio.so</c>), not SDL's audio
	/// device. SDL's Android drivers open a plain track with a large buffer that they keep full,
	/// so FAudio mixed 100+ ms ahead of what was heard: that much latency on every sound, and a
	/// voice refilled about once a frame ran dry inside it (WP8 titles fed through FAudio,
	/// measured on the emulator). Below 8.1, or if AAudio will not open, FAudio falls back to SDL
	/// by itself.
	/// </para>
	/// <para>
	/// <c>faudio_output.txt</c> in the app's external files, containing <c>sdl</c>, forces the
	/// SDL path, the same kind of escape hatch as <c>fna3d_driver.txt</c>. It is read before
	/// the first engine is created in the process; a game process is new for every launch.
	/// </para>
	/// </remarks>
	internal static class AndroidAudioOutput
	{
		public const string OverrideFileName = "faudio_output.txt";

		private static bool _applied;

		/// <summary>Call before every <c>FAudioCreate</c>; only the first call does anything.</summary>
		public static void ApplyOverride()
		{
			if (_applied)
			{
				return;
			}
			_applied = true;

			try
			{
				string? path = Configuration.Current?.DataPath(OverrideFileName);
				if (path is not null && File.Exists(path) &&
					File.ReadAllText(path).Trim().Equals("sdl", StringComparison.OrdinalIgnoreCase))
				{
					// Read by FAudio_platform_sdl2.c when an engine opens its output.
					SDL2.SDL.SDL_SetHint("FAUDIO_ANDROID_OUTPUT", "sdl");
					XnaBackend.LogInfo("[wpr-audio] " + OverrideFileName + " says sdl: FAudio uses SDL's audio device, not AAudio.");
				}
			}
			catch (Exception ex)
			{
				XnaBackend.LogWarn("[wpr-audio] could not read " + OverrideFileName + ": " + ex.Message);
			}
		}
	}
}
#endif
