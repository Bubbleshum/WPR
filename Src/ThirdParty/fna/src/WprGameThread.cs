using System;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Microsoft.Xna.Framework
{
	/// <summary>
	/// WPR: a minimal "run this on the game thread, next frame" queue.
	///
	/// Some WP7 shims need to hand work back to the game's Update/Draw thread —
	/// e.g. <c>Microsoft.Phone.Tasks.MediaPlayerLauncher</c> finishing a video has
	/// to drive the title's reactivation callback, and that callback ends up calling
	/// <c>LoadLevel</c>, which touches the GraphicsDevice and so must run on the
	/// game thread (not a thread-pool continuation). The actions are drained at the
	/// top of <see cref="Game.Tick"/>, so an item posted during frame N runs at the
	/// start of frame N+1 — late enough that whatever set it up has finished, early
	/// enough to be before that frame's Update/Draw.
	///
	/// This exists because the games that need it (e.g. Star Wars: The Battle for
	/// Hoth) don't pump <c>GamerServicesDispatcher.Update</c> /
	/// <c>FrameworkDispatcher.Update</c>, so there's no other per-frame game-thread
	/// hook to piggyback on.
	/// </summary>
	public static class WprGameThread
	{
		private static readonly ConcurrentQueue<Action> _pending = new ConcurrentQueue<Action>();

		/// <summary>Queue an action to run on the game thread at the start of the next tick.</summary>
		public static void Post(Action action)
		{
			if (action != null)
			{
				_pending.Enqueue(action);
			}
		}

		/// <summary>Drain and run all queued actions. Called from <see cref="Game.Tick"/> on the game thread.</summary>
		internal static void DrainPending()
		{
			while (_pending.TryDequeue(out Action action))
			{
				try
				{
					action();
				}
				catch (Exception ex)
				{
					WprDebugTrace.WriteLine("[wpr-ex] WprGameThread action threw: " + ex);
					_ = ex;
				}
			}
		}
	}
}
