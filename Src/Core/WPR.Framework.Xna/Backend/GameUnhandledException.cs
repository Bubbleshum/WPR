using System;

namespace WPR.Xna.Rhi
{
	/// <summary>
	/// Lets a Silverlight title terminate itself the way WP7 let it: by throwing.
	///
	/// <para>WP7 Silverlight had no public "exit" API, so the standard idiom was to throw a
	/// private exception type, catch it in <c>Application.UnhandledException</c>, do any
	/// save-on-close work, and DELIBERATELY leave <c>e.Handled</c> false — at which point the
	/// shell terminated the app. Cut the Rope is exactly that shape:</para>
	///
	/// <code>
	/// public static void Quit() =&gt; throw new QuitException();
	/// // ... UnhandledException: if (e.ExceptionObject is QuitException) Application_Closing(null, null);
	/// //     (no e.Handled = true, so the app dies here)
	/// </code>
	///
	/// <para><see cref="Microsoft.Xna.Framework.Game"/>'s update loop catches everything and logs
	/// it, and that log is <c>[Conditional("DEBUG")]</c> — so in a Release build the throw was a
	/// silent no-op and the game simply carried on. The symptom is a quit confirmation whose
	/// "Yes" does nothing.</para>
	///
	/// <para><b>Unset means "swallow", which is the behaviour every XNA title already had.</b>
	/// Only <c>WPR.WindowsCompability.Application</c> fills this slot, and only a Silverlight or
	/// mixed-mode game constructs one — so a pure XNA title is completely unaffected. That matters
	/// more than it looks: several titles throw out of <c>Update</c> on EVERY frame and remain
	/// playable or at least diagnosable because WPR swallows it (see the Feed Me Oil and Chickens
	/// Can't Fly notes in CLAUDE.md). Making unhandled exceptions fatal across the board would be
	/// WP7-accurate and would turn those from "frozen but inspectable" into "exits instantly".</para>
	///
	/// <para>The slot lives here rather than in the Silverlight assembly because
	/// <c>WPR.Framework.Silverlight</c> references <c>WPR.Framework.Xna</c> and not the reverse;
	/// the same constraint produced <c>XamlReader.ApplicationResourceLookup</c>, and this follows
	/// it. It names only <see cref="Exception"/>, so nothing XNA-shaped leaks either way.</para>
	/// </summary>
	public static class GameUnhandledException
	{
		/// <summary>
		/// Offers an exception that escaped game code to the title's own
		/// <c>Application.UnhandledException</c> handler. Returns true when the game handled it
		/// (carry on), false when it did not (WP7 would terminate).
		///
		/// <para>Registered by <c>WPR.WindowsCompability.Application</c>'s constructor and cleared
		/// by its <c>ResetCurrent</c>, which <c>ResetWprSingletons</c> calls first at teardown.
		/// Clearing matters: this static lives in the shared framework, not in the game's ALC, so
		/// on the desktop head it would otherwise outlive the launch and hand the NEXT game a
		/// delegate rooted in the previous one's assemblies.</para>
		/// </summary>
		public static Func<Exception, bool>? Reporter;

		/// <summary>
		/// Set once the game has declined an exception, i.e. it wants to die.
		///
		/// <para>A flag rather than an immediate <c>Game.Exit()</c> because the exception is
		/// caught in more than one place and none of them holds the <c>Game</c>: a mixed-mode
		/// title's update runs under <c>GameTimer.RaiseUpdate</c>, which catches first, so the
		/// loop's own catch never sees it. Both report here and <c>Game.Tick</c> acts on the flag
		/// at the end of the tick — one decision point, one <c>Exit</c>, and the game's handler is
		/// never raised twice for the same throw.</para>
		/// </summary>
		public static bool TerminationRequested { get; private set; }

		/// <summary>
		/// True when the game dealt with it, or when nothing is listening — an absent reporter
		/// means "this is not a Silverlight title", never "terminate".
		/// </summary>
		public static bool Report(Exception exception)
		{
			Func<Exception, bool>? reporter = Reporter;
			if (reporter == null)
			{
				return true;
			}

			bool handled;
			try
			{
				handled = reporter(exception);
			}
			catch
			{
				/* A handler that throws is the game's problem, not a reason to kill it — and this
				 * runs from a catch block, where a second throw would escape into the host.
				 * Treat it as handled and let the original log line stand. */
				return true;
			}

			if (!handled)
			{
				/* Logged ONLY on the decision to exit, and through XnaBackend so it survives a
				 * Release build — the per-game log and WprDebugTrace are both DEBUG-only, which is
				 * what made the original defect invisible. Deliberately not logged on the swallow
				 * path: a title that throws out of Update every frame would fill logcat at 60 lines
				 * a second, and swallowing is the long-standing behaviour rather than an event. */
				XnaBackend.LogInfo(
					$"[wpr-quit] the game left {exception.GetType().FullName} unhandled — exiting, "
					+ "which is what WP7 did and how a Silverlight title asks to quit.");
				TerminationRequested = true;
			}

			return handled;
		}

		/// <summary>
		/// Drops the reporter and any pending termination. Called from
		/// <c>Application.ResetCurrent</c>, which <c>ResetWprSingletons</c> runs first at teardown
		/// — these are statics in the shared framework, not in the game's ALC, so on the desktop
		/// head they would otherwise leak into the next launch.
		/// </summary>
		public static void Reset()
		{
			Reporter = null;
			TerminationRequested = false;
		}
	}
}
