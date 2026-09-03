using System;
using System.Threading;

namespace WPR.Xna.Rhi
{
	/// <summary>
	/// Counts GPU calls that are in flight on a thread other than the one that created the
	/// graphics device, so the game loop knows it must keep presenting even on a frame the
	/// game asked to skip.
	///
	/// <para><b>Why this exists.</b> FNA3D's OpenGL driver can only touch GL from the thread
	/// that owns the context. Every resource call made from any other thread
	/// (<c>FNA3D_CreateTexture2D</c>, <c>FNA3D_SetTextureData2D</c>, the buffer and effect
	/// equivalents — see <c>ForceToMainThread</c> in <c>FNA3D_Driver_OpenGL.c</c>) is appended to
	/// a command list and the caller <em>blocks on a semaphore</em> until that list is drained.
	/// The list is drained in exactly one place: <c>ExecuteCommands</c>, called from
	/// <c>OPENGL_SwapBuffers</c>. No swap, no drain.</para>
	///
	/// <para>That collides with <see cref="Microsoft.Xna.Framework.Game.SuppressDraw"/>. A game
	/// that loads content on a worker thread while a static screen suppresses its draws — a very
	/// common WP7 shape, since the phone's battery rewarded it — deadlocks outright: the worker
	/// waits for a swap that the suppressed game loop will never perform, and the loop keeps
	/// suppressing because the worker never finishes producing anything to draw. Game Room:
	/// Pitfall! is the reference case; it hangs forever on the second (Krome) splash logo with the
	/// loader thread parked inside <c>SetTextureData2D</c> loading <c>Fonts/Title</c>.</para>
	///
	/// <para>Only the OpenGL driver defers this way — D3D11 (Windows) and Vulkan take off-thread
	/// calls directly — which is why the hang is Android-only and only since the OpenGL driver was
	/// forced there. The counter is driver-agnostic all the same: "another thread is inside a GPU
	/// call" is a sound reason to present a frame on any backend, and it costs nothing when no
	/// such call is outstanding.</para>
	///
	/// <para>This does not make off-thread loading fast. Each deferred call still costs one frame
	/// of latency, because one swap drains one worker's one queued command. It makes it finish.</para>
	/// </summary>
	public static class OffThreadGpuCalls
	{
		private static int _deviceThreadId;
		private static int _inFlight;

		/// <summary>
		/// Record the calling thread as the device thread. Called from the backend's
		/// <c>CreateDevice</c>, which is the same call that makes FNA3D latch its own
		/// <c>renderer-&gt;threadID</c>, so the two notions of "main thread" cannot drift.
		/// </summary>
		public static void SetDeviceThread()
		{
			Volatile.Write(ref _deviceThreadId, Environment.CurrentManagedThreadId);
			Volatile.Write(ref _inFlight, 0);
		}

		/// <summary>Forget the device thread and drop the count. Called from <c>DestroyDevice</c>.</summary>
		public static void ClearDeviceThread()
		{
			Volatile.Write(ref _deviceThreadId, 0);
			Volatile.Write(ref _inFlight, 0);
		}

		/// <summary>True when the caller is the thread that created the device.</summary>
		public static bool OnDeviceThread
		{
			get
			{
				int owner = Volatile.Read(ref _deviceThreadId);
				return owner == 0 || owner == Environment.CurrentManagedThreadId;
			}
		}

		/// <summary>
		/// True while at least one GPU call from a non-device thread has not yet returned. The
		/// game loop must present this frame rather than skip it — that call may be blocked
		/// waiting for the swap.
		/// </summary>
		public static bool AnyInFlight => Volatile.Read(ref _inFlight) > 0;

		/// <summary>
		/// Bracket a GPU call that FNA3D's OpenGL driver would defer to the device thread.
		/// A no-op (and uncounted) when already on the device thread.
		/// </summary>
		public static Scope Enter()
		{
			if (OnDeviceThread)
			{
				return default;
			}
			Interlocked.Increment(ref _inFlight);
			return new Scope(true);
		}

		/// <summary>Disposable returned by <see cref="Enter"/>. A struct, so bracketing allocates nothing.</summary>
		public readonly struct Scope : IDisposable
		{
			private readonly bool _counted;

			internal Scope(bool counted)
			{
				_counted = counted;
			}

			public void Dispose()
			{
				if (_counted)
				{
					Interlocked.Decrement(ref _inFlight);
				}
			}
		}
	}
}
