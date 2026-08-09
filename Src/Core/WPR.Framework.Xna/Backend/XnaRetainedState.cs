using System;
using System.Text;
using Microsoft.Xna.Framework.Audio;

namespace WPR.Xna.Rhi
{
	/// <summary>
	/// Reports what the WPR-owned XNA layer is still holding after a game exits — i.e. the static
	/// collections that could ROOT objects from the game's collectible AssemblyLoadContext and stop it
	/// unloading.
	///
	/// <para>Added 2026-08-08 while chasing an ALC that never unloads (teardown logs
	/// <c>alc-unload: FAILED after 12 rounds</c>). The fatal window-leak symptom that shared those
	/// teardown logs is fixed and was a different cause, so this is about the remaining leak: statics
	/// from launch N surviving into launch N+1 of the same WPR session (which the host warns about,
	/// because it can surface later as a duplicate-key error).</para>
	///
	/// <para>Anything listed here that is non-zero after teardown is a candidate root: these live in
	/// the default (non-collectible) load context, so a retained instance whose event handlers or
	/// fields point at game code keeps the game's ALC alive. Public purely so the backend/host can log
	/// it without widening <c>InternalsVisibleTo</c>.</para>
	/// </summary>
	public static class XnaRetainedState
	{
		/// <summary>One log-ready line describing retained XNA state. Never throws.</summary>
		public static string Describe()
		{
			var sb = new StringBuilder();
			try
			{
				// The per-frame dynamic-audio registry. Instances are only removed on Stop(true), and a
				// DynamicSoundEffectInstance typically carries a BufferNeeded handler owned by GAME
				// code — so a leftover entry here roots the game's ALC. This list moved from FNA's
				// FrameworkDispatcher into this assembly in 5c-3a (the "pump inversion"), so if this is
				// non-zero it is the prime suspect and the fix belongs here.
				int streams;
				lock (DynamicSoundEffectInstance.Streams)
				{
					streams = DynamicSoundEffectInstance.Streams.Count;
				}
				sb.Append("dseiStreams=").Append(streams);

				// Capture devices are cached in a static list for the process lifetime; they hold no
				// game references, so this is expected to be harmless — logged to rule it out.
				sb.Append(" microphones=").Append(Microphone.micList?.Count ?? -1);
			}
			catch (Exception ex)
			{
				sb.Append(" (retained-state probe failed: ").Append(ex.GetType().Name).Append(')');
			}
			return sb.ToString();
		}
	}
}
