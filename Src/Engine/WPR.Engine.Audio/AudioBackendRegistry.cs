#nullable enable
using System;

using System.Collections.Generic;

namespace WPR.Engine.Audio
{
	/// <summary>
	/// Where audio implementations are plugged in, and where the three audio slots on
	/// this class are composed from them.
	///
	/// <para><b>Two registration kinds, on purpose.</b> <see cref="SetBase"/> is the host's
	/// implementation-of-last-resort — the one that must be present for a game to have audio at all,
	/// installed by the game host itself so that any code path which runs a game (including the
	/// bare-<c>FnaGameHost</c> console harness in CLAUDE.md, which never reaches a head's
	/// <c>ServicesSetup</c>) still gets sound. <see cref="Register"/> is what a platform head calls
	/// at startup to layer a platform implementation over it. The base is always composed first
	/// regardless of call order, which matters because the head registers at launcher startup and
	/// the host sets the base per launch.</para>
	///
	/// <para><b>Lifetimes.</b> Modules are process-lifetime and are deliberately NOT cleared by
	/// teardown — they are registered once by the launcher, and clearing them
	/// on teardown would leave the second game launched without its platform audio, the same trap
	/// documented for <c>SetAchievements</c>. The <em>backends</em> they build are per-launch:
	/// <see cref="Compose"/> runs once per game and produces fresh instances, so a module must keep
	/// no per-game state of its own.</para>
	///
	/// <para>This replaces the earlier <c>WPR.Backend.FNA.MediaBackendOverride</c>, which could plug
	/// only the media seam, only with a single override, and only for heads willing to reference the
	/// FNA host backend to say so.</para>
	/// </summary>
	public static class AudioBackendRegistry
	{
		// ---- The composed backends ----
		//
		// These three slots used to live on XnaBackend beside graphics/input/storage. They moved
		// here when the seams did (2026-09-01): the whole audio subsystem — contracts, registry and
		// composition — is one project now, and the only split left is which *implementation* fills
		// it. The framework's audio types read these directly.

		private static IAudioBackend? _sound;
		private static IXactBackend? _xact;
		private static IMediaBackend? _media;

		/// <summary>True once a module has filled the sound-effect seam.</summary>
		public static bool HasSound => _sound != null;

		/// <summary>The sound-effect backend. Throws if composition produced none.</summary>
		public static IAudioBackend Sound =>
			_sound ?? throw new InvalidOperationException(
				"No IAudioBackend has been composed. The game host must call " +
				"AudioBackendRegistry.SetBase(...) and Compose() before the XNA runtime opens an " +
				"audio device.");

		/// <summary>True once a module has filled the XACT seam.</summary>
		public static bool HasXact => _xact != null;

		/// <summary>The XACT backend. Throws if composition produced none.</summary>
		public static IXactBackend Xact =>
			_xact ?? throw new InvalidOperationException(
				"No IXactBackend has been composed. The game host must call " +
				"AudioBackendRegistry.SetBase(...) and Compose() before the XNA runtime creates an " +
				"AudioEngine.");

		/// <summary>True once a module has filled the media seam.</summary>
		public static bool HasMedia => _media != null;

		/// <summary>The media backend — song playback and video decode. Throws if none.</summary>
		public static IMediaBackend Media =>
			_media ?? throw new InvalidOperationException(
				"No IMediaBackend has been composed. The game host must call " +
				"AudioBackendRegistry.SetBase(...) and Compose() before the XNA runtime plays a " +
				"Song or opens a Video.");

		private static readonly object _gate = new object();
		private static readonly List<IAudioModule> _modules = new List<IAudioModule>();
		private static IAudioModule? _base;

		/// <summary>
		/// Installs the fallback module every other module layers over. Called by the game host
		/// (<c>FnaGameHost</c> installs <c>FAudioModule</c>). Idempotent by assignment.
		/// </summary>
		public static void SetBase(IAudioModule module) =>
			_base = module ?? throw new ArgumentNullException(nameof(module));

		/// <summary>
		/// Adds a module on top of the stack. Called by a platform head in <c>ServicesSetup.Start()</c>.
		///
		/// <para>Re-registering the same <see cref="IAudioModule.Name"/> <b>replaces</b> the previous
		/// entry in place rather than appending — Android recreates its process straight into any
		/// activity and <c>GameActivity</c>'s <c>:game</c> process runs the composition root again, so
		/// accumulating would build a deeper chain on every re-entry.</para>
		/// </summary>
		public static void Register(IAudioModule module)
		{
			if (module == null) throw new ArgumentNullException(nameof(module));
			lock (_gate)
			{
				for (int i = 0; i < _modules.Count; i += 1)
				{
					if (string.Equals(_modules[i].Name, module.Name, StringComparison.OrdinalIgnoreCase))
					{
						_modules[i] = module;
						return;
					}
				}
				_modules.Add(module);
			}
		}

		/// <summary>Removes a module by name. Returns false if it was not registered.</summary>
		public static bool Unregister(string name)
		{
			lock (_gate)
			{
				for (int i = 0; i < _modules.Count; i += 1)
				{
					if (string.Equals(_modules[i].Name, name, StringComparison.OrdinalIgnoreCase))
					{
						_modules.RemoveAt(i);
						return true;
					}
				}
			}
			return false;
		}

		/// <summary>The stack as it will be composed, base first. Diagnostics only.</summary>
		public static IReadOnlyList<string> ModuleNames
		{
			get
			{
				List<string> names = new List<string>();
				foreach (IAudioModule m in Snapshot()) names.Add(m.Name);
				return names;
			}
		}

		/// <summary>
		/// Builds the three audio backends and publishes them on the slots above. Called by
		/// the host once per game launch, before the game touches audio.
		///
		/// <para>A seam nobody filled is left unset rather than being given a null: the
		/// accessor's own "no backend composed" message is a better
		/// diagnostic than a NullReferenceException from inside a game.</para>
		///
		/// <para>A factory that throws must never take a launch down over sound — the module is
		/// skipped, the stack below it stands, and the failure is logged. That is the same
		/// degradation the media override had, generalised to all three seams.</para>
		/// </summary>
		/// <returns>A one-line summary of what was composed, for the launch log.</returns>
		public static string Compose()
		{
			IAudioBackend? audio = null;
			IXactBackend? xact = null;
			IMediaBackend? media = null;
			string audioFrom = "none", xactFrom = "none", mediaFrom = "none";

			foreach (IAudioModule module in Snapshot())
			{
				bool available;
				try { available = module.IsAvailable; }
				catch (Exception ex) { Warn(module, "IsAvailable", ex); continue; }
				if (!available) continue;

				try
				{
					IAudioBackend? next = module.CreateAudio(audio);
					if (next != null && !ReferenceEquals(next, audio)) { audio = next; audioFrom = module.Name; }
				}
				catch (Exception ex) { Warn(module, nameof(IAudioModule.CreateAudio), ex); }

				try
				{
					IXactBackend? next = module.CreateXact(xact);
					if (next != null && !ReferenceEquals(next, xact)) { xact = next; xactFrom = module.Name; }
				}
				catch (Exception ex) { Warn(module, nameof(IAudioModule.CreateXact), ex); }

				try
				{
					IMediaBackend? next = module.CreateMedia(media);
					if (next != null && !ReferenceEquals(next, media)) { media = next; mediaFrom = module.Name; }
				}
				catch (Exception ex) { Warn(module, nameof(IAudioModule.CreateMedia), ex); }
			}

			/* Assigned, not cleared-then-assigned: a seam nobody filled keeps whatever the previous
			 * launch left, which is deliberate. XNA resources are finalizable and their finalizers
			 * reach these backends (~SoundEffectInstance -> Stop -> DestroyVoice) on the GC's
			 * schedule, i.e. after teardown. An accessor that threw there would take the process
			 * down from a finalizer with no diagnostics. Same reasoning as XnaBackend.Clear. */
			if (audio != null) _sound = audio;
			if (xact != null) _xact = xact;
			if (media != null) _media = media;

			LastComposition = "sound=" + audioFrom + " xact=" + xactFrom + " media=" + mediaFrom;
			return LastComposition;
		}

		/// <summary>
		/// What the last <see cref="Compose"/> produced, as the same one-line summary it returns —
		/// or "(not composed)" if it has not run this process.
		///
		/// <para>Kept because composition happens in the host <em>before</em> the per-game trace
		/// listener exists, so the summary cannot simply be logged where it is produced. The launch
		/// path re-reads it once the log file is open. It is also the fastest answer to "which
		/// module is actually serving songs on this device" — read it, do not infer it from which
		/// projects are referenced.</para>
		/// </summary>
		public static string LastComposition { get; private set; } = "(not composed)";

		/// <summary>Base first, then registrations in the order they were added.</summary>
		private static List<IAudioModule> Snapshot()
		{
			List<IAudioModule> stack = new List<IAudioModule>();
			IAudioModule? baseModule = _base;
			if (baseModule != null) stack.Add(baseModule);
			lock (_gate)
			{
				foreach (IAudioModule m in _modules)
				{
					if (!ReferenceEquals(m, baseModule)) stack.Add(m);
				}
			}
			return stack;
		}

		private static void Warn(IAudioModule module, string what, Exception ex) =>
			WPR.Common.Log.Warn(
				WPR.Common.LogCategory.AppList,
				"Audio module '" + module.Name + "' failed in " + what +
				"; falling back to the module below it. " + ex);
	}
}
