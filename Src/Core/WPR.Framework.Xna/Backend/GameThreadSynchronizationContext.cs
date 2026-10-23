using System;
using System.Threading;
using Microsoft.Xna.Framework;

namespace WPR.Xna.Rhi
{
	/// <summary>
	/// The XNA game thread's <see cref="SynchronizationContext"/>: a post runs on the game thread
	/// at the start of the next tick (<see cref="WprGameThread"/>).
	/// </summary>
	/// <remarks>
	/// <para>On WP7 an XNA game ran on the Silverlight UI thread, so
	/// <see cref="SynchronizationContext.Current"/> there was never null. Libraries shipped in XNA
	/// titles rely on it: OpenXLive's <c>NavigationService.Initialize(Game)</c> throws
	/// "Invalid cross-thread access" without one, and its leaderboard and achievement pages then
	/// never open (iRunner calls it from its Game constructor). <c>await</c> continuations and
	/// <c>BackgroundWorker</c> completions in game code come back to the game thread through it,
	/// as they did on the phone.</para>
	///
	/// <para><c>WPR.ApplicationLaunch</c> installs it on the game thread of a managed title
	/// and restores the thread's old context at teardown. A Silverlight title's host replaces it
	/// with the dispatcher's own context while it runs. The WP8 native runtime gets none.</para>
	///
	/// <para>WPR code that blocks the game thread on a task must not let an <c>await</c> inside
	/// it capture this context (<c>ConfigureAwait(false)</c>, or start it with <c>Task.Run</c>):
	/// its continuation would be queued for the thread that is waiting on it.</para>
	/// </remarks>
	public sealed class GameThreadSynchronizationContext : SynchronizationContext
	{
		private readonly int _threadId;

		/// <summary>A context for the calling thread, which must be the game thread.</summary>
		public GameThreadSynchronizationContext() : this(Environment.CurrentManagedThreadId)
		{
		}

		private GameThreadSynchronizationContext(int threadId) => _threadId = threadId;

		public override void Post(SendOrPostCallback d, object state) => WprGameThread.Post(() => d(state));

		/// <summary>Runs <paramref name="d"/> at once on the game thread, else queues it and waits.</summary>
		public override void Send(SendOrPostCallback d, object state)
		{
			if (Environment.CurrentManagedThreadId == _threadId)
			{
				d(state);
				return;
			}

			Exception error = null;
			using ManualResetEventSlim done = new ManualResetEventSlim();
			WprGameThread.Post(() =>
			{
				try { d(state); }
				catch (Exception ex) { error = ex; }
				finally { done.Set(); }
			});
			done.Wait();
			if (error != null)
			{
				throw new System.Reflection.TargetInvocationException(error);
			}
		}

		public override SynchronizationContext CreateCopy() => new GameThreadSynchronizationContext(_threadId);
	}
}
