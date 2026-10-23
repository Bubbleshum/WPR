using System.Threading;

namespace Microsoft.Xna.Framework
{
	/// <summary>
	/// WPR addition. Lets a host queue a WP7 hardware-Back press that did not reach SDL as a
	/// key event, so it still surfaces as one frame of <c>GamePad.Buttons.Back</c>.
	///
	/// Needed on Android: the system Back key is dispatched to the activity (which by default
	/// finishes it) rather than to SDL's surface, so <c>SDL2_FNAPlatform.PollEvents</c> never
	/// sees the <c>SDLK_AC_BACK</c> keydown the desktop head produces — there, Esc and
	/// SDLK_AC_BACK come straight off the SDL event queue. <c>GameActivity.OnBackPressed</c>
	/// calls <see cref="Press"/> instead and PollEvents drains it at the top of the next tick,
	/// giving the same one-frame, edge-triggered contract as the keyboard path. That edge
	/// matters: WP7 games sample Buttons.Back level-triggered every Update, so a press held
	/// across frames reads as many Backs.
	///
	/// Queued from the Android UI thread, drained on the game thread — hence the interlocked
	/// access. Presses arriving faster than one per tick collapse into one.
	/// </summary>
	public static class WprPhoneBackButton
	{
		private static int _pending;

		/// <summary>
		/// Game thread: true while <see cref="Game.Tick"/> runs the second and later fixed-timestep
		/// Updates of one tick. The press is polled once per tick, and a tick that catches up runs
		/// several Updates; a game that reads Back level-triggered in Update would then take one press
		/// as several. iRunner closes OpenXLive's page on the first and quits the game on the second.
		/// On the phone a press reached one Update. Draw still sees it, as it always did.
		/// </summary>
		internal static bool HiddenFromCatchUpUpdates;

		/// <summary>Queue one Back press for the next tick. Safe from any thread.</summary>
		public static void Press()
		{
			Interlocked.Exchange(ref _pending, 1);
		}

		/// <summary>Game thread: take the queued press, if any, clearing it.</summary>
		internal static bool ConsumePending()
		{
			return Interlocked.Exchange(ref _pending, 0) == 1;
		}
	}
}
