#nullable enable
using System;
using System.Threading;

namespace WPR.Xna.Rhi
{
	/// <summary>
	/// Held by the game thread while it runs a frame (input, Update, Draw), so that callbacks a
	/// game wrote assuming they never overlap its frame don't.
	/// </summary>
	/// <remarks>
	/// <para><b>Why.</b> WP7 titles were written on single-core phones, where a socket's
	/// <c>Completed</c> callback (on a worker thread) and the game's frame practically never ran
	/// at the same moment. Many games call straight into their game logic from that callback
	/// without a lock. Pool Pro Online 3 is the reference case: its receive callback appends to the
	/// same message buffer the frame reads every tick, and on a multi-core machine the two collide
	/// and a packet is lost (its "ready to break" vanished and the match hung on "waiting for
	/// player"). WPR.WindowsCompability.SilverlightSockets runs a game's RECEIVE callbacks under
	/// this gate, which gives back the order a single core gave.</para>
	///
	/// <para><b>Never a deadlock.</b> Only what a game's frame doesn't wait for goes through the
	/// gate (receives; not connects or sends, which Pool Pro's frame blocks on). And a callback that
	/// can't get in within <see cref="MaxWait"/> runs anyway, as it did before, so a game whose frame
	/// waits for a receive slows by that much once, instead of hanging.</para>
	///
	/// <para>The frame is held only from input polling to the end of Draw, not while the loop
	/// sleeps for its frame time, so callbacks normally get in straight away.</para>
	/// </remarks>
	public static class GameFrameGate
	{
		private static readonly object Gate = new object();

		/// <summary>How long a callback waits for the frame to finish before running anyway.</summary>
		public static readonly TimeSpan MaxWait = TimeSpan.FromMilliseconds(250);

		/// <summary>The game thread, at the start of its frame's work.</summary>
		public static void EnterFrame() => Monitor.Enter(Gate);

		/// <summary>The game thread, at the end of its frame.</summary>
		public static void ExitFrame()
		{
			if (Monitor.IsEntered(Gate)) Monitor.Exit(Gate);
		}

		/// <summary>Runs <paramref name="callback"/> between frames (or after <see cref="MaxWait"/> regardless).</summary>
		public static void RunBetweenFrames(Action callback)
		{
			bool entered = false;
			try
			{
				Monitor.TryEnter(Gate, MaxWait, ref entered);
				callback();
			}
			finally
			{
				if (entered) Monitor.Exit(Gate);
			}
		}
	}
}
