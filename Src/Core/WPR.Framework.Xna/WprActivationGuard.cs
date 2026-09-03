using System;
using System.Threading;

namespace Microsoft.Xna.Framework
{
	/// <summary>
	/// WPR addition. Lets WPR briefly suppress FNA's focus-driven <see cref="Game.IsActive"/>
	/// flips (and therefore <c>OnActivated</c>/<c>OnDeactivated</c>) for transient focus changes
	/// that WPR itself causes — chiefly the Windows achievement-unlock toast, which momentarily
	/// steals the game window's focus. Without this, unlocking an achievement during gameplay
	/// fires <c>OnDeactivated</c>→<c>OnActivated</c> mid-tick; some WP7 ports (e.g. Fruit Ninja
	/// 2013) throw inside their activation overrides, which the game surfaces as a bogus
	/// "memory error" dialog and then exits.
	///
	/// The suppression is a short time window, so a genuine alt-tab a moment later is still
	/// honored. Set from the notification path (a background thread); read on the game thread
	/// in <c>SDL2_FNAPlatform.PollEvents</c> — hence the interlocked access.
	/// </summary>
	public static class WprActivationGuard
	{
		private static long _suppressUntilTicks;

		/// <summary>Ignore focus-driven activation changes for <paramref name="window"/> from now.</summary>
		public static void SuppressFocusActivation(TimeSpan window)
		{
			long until = DateTime.UtcNow.Add(window).Ticks;
			long current = Interlocked.Read(ref _suppressUntilTicks);
			if (until > current)
			{
				Interlocked.Exchange(ref _suppressUntilTicks, until);
			}
		}

		/// <summary>True while a WPR-originated focus change should be ignored.</summary>
		public static bool IsSuppressed =>
			DateTime.UtcNow.Ticks < Interlocked.Read(ref _suppressUntilTicks);
	}
}
