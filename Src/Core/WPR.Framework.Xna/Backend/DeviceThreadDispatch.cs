using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace WPR.Xna.Rhi
{
	/// <summary>
	/// Runs a GPU call issued from a thread other than the device thread ON the device thread,
	/// synchronously, when the backend cannot take it where it was issued. Today that is exactly
	/// FNA3D's OpenGL driver.
	///
	/// <para><b>Why this exists.</b> A GL context is current on one thread. FNA3D's OpenGL driver
	/// defers the resource calls (<c>ForceToMainThread</c>, see <see cref="OffThreadGpuCalls"/>)
	/// but does nothing at all for the drawing ones — <c>SetRenderTargets</c>, <c>Clear</c>,
	/// <c>ApplyEffect</c>, the draws. Issued from a worker they run against no context: GL calls
	/// are dropped (<c>libEGL: call to OpenGL ES API with no current context</c>), and
	/// MojoShader's GL context pointer is <em>thread-local</em>, so <c>MOJOSHADER_effectCommitChanges</c>
	/// maps the uniform registers of a NULL context and <c>memcpy</c>s into address 0x18. That is a
	/// SIGSEGV with no managed frame worth reading.</para>
	///
	/// <para><b>Plants vs. Zombies is the reference case</b> (realme RMX3938, Mali-G57, OpenGL,
	/// 2026-10-05). Its loading thread premultiplies every image by drawing it into a
	/// <c>RenderTarget2D</c> with a <c>SpriteBatch</c>, under <c>ResourceManager.DrawLocker</c>, which
	/// <c>Main.Draw</c> also takes. D3D11 and Vulkan accept drawing from any thread, which is why it
	/// plays there.</para>
	///
	/// <para><b>The deadlock this must not create.</b> A worker waiting here for the device thread
	/// may be holding a game lock the device thread is about to take — PvZ's loader holds
	/// <c>DrawLocker</c> for the whole draw, and <c>Main.Draw</c> blocks on it. A plain
	/// "queue and wait for the next frame" would hang for ever, which is what the OpenGL driver's
	/// own resource queue did to this same game. So the device thread also services the queue
	/// <em>while it waits for a game lock</em>: every <c>Monitor.Enter</c> in game code is rewritten
	/// by the patcher to <see cref="WPR.Xna.Compat.Monitor.Enter(object)"/>, which spins on
	/// <c>TryEnter</c> and calls <see cref="Drain"/> between attempts. Since the worker is blocked
	/// synchronously, its calls run in the order it issued them and see exactly the state it set,
	/// as if they had run on its own thread under a device-wide lock — which is what D3D11 does.</para>
	///
	/// <para>Off by default, and a no-op on the device thread, so D3D11 and Vulkan never see it.</para>
	/// </summary>
	public static class DeviceThreadDispatch
	{
		private sealed class WorkItem
		{
			public Action Work;
			public Exception Error;
			public readonly ManualResetEventSlim Done;

			/// <summary>True for <see cref="Post"/>: nobody waits, a failure is only logged.</summary>
			public readonly bool Posted;

			public WorkItem() => Done = new ManualResetEventSlim(false);
			public WorkItem(Action work) { Work = work; Posted = true; }
		}

		private static readonly object _lock = new object();
		private static readonly Queue<WorkItem> _queue = new Queue<WorkItem>();
		private static volatile int _pending;
		private static volatile bool _enabled;
		private static int _reported;
		private static int _postFailuresReported;

		/// <summary>
		/// Set by the device while it presents a turned backbuffer (PresentationTurn): waiting
		/// calls are held until the next frame boundary instead of running mid-present, where they
		/// would meet the host backbuffer bound and the device would re-bind the game's backbuffer
		/// over whatever target they set. Posted resource work still runs.
		/// </summary>
		internal static bool Suspended;

		[ThreadStatic] private static WorkItem _threadItem;

		/// <summary>True when off-thread GPU calls must be run on the device thread.</summary>
		public static bool Enabled => _enabled;

		/// <summary>True when the calling thread must hand its GPU call to the device thread.</summary>
		public static bool MustMarshal => _enabled && !OffThreadGpuCalls.OnDeviceThread;

		/// <summary>True when a worker is waiting for the device thread.</summary>
		public static bool AnyPending => _pending > 0;

		/// <summary>Turn marshalling on for this device. Called from the backend's <c>CreateDevice</c>.</summary>
		public static void Enable(string reason)
		{
			_enabled = true;
			Interlocked.Exchange(ref _reported, 0);
			XnaBackend.LogInfo(
				"[wpr-gputhread] off-thread draw calls run on the device thread (" + reason + ")");
		}

		/// <summary>
		/// Turn marshalling off and fail anything still waiting. Called from <c>DestroyDevice</c>:
		/// a call queued against a destroyed device must not run, and its caller must not wait for
		/// ever either.
		/// </summary>
		public static void Disable()
		{
			_enabled = false;
			Suspended = false;
			WorkItem[] stranded;
			lock (_lock)
			{
				stranded = _queue.ToArray();
				_queue.Clear();
				_pending = 0;
			}
			foreach (WorkItem item in stranded)
			{
				if (item.Posted)
				{
					continue;
				}
				item.Error = new ObjectDisposedException("GraphicsDevice",
					"The graphics device was destroyed before this call reached the device thread.");
				item.Done.Set();
			}
		}

		/// <summary>
		/// Run <paramref name="work"/> on the device thread and wait for it. The caller has already
		/// checked <see cref="MustMarshal"/>. An exception thrown by the work is rethrown here, on
		/// the thread that issued the call.
		/// </summary>
		public static void Invoke(Action work)
		{
			WorkItem item = _threadItem ??= new WorkItem();
			item.Work = work;
			item.Error = null;
			item.Done.Reset();

			lock (_lock)
			{
				if (!_enabled)
				{
					item.Work = null;
					work();
					return;
				}
				_queue.Enqueue(item);
				_pending = _queue.Count;
			}

			if (Interlocked.Exchange(ref _reported, 1) == 0)
			{
				XnaBackend.LogInfo(
					"[wpr-gputhread] first draw-family call from thread " +
					Environment.CurrentManagedThreadId + " handed to the device thread");
			}

			OffThreadGpuCalls.Scope scope = OffThreadGpuCalls.Enter();
			try
			{
				item.Done.Wait();
			}
			finally
			{
				scope.Dispose();
			}

			item.Work = null;
			if (item.Error != null)
			{
				ExceptionDispatchInfo.Capture(item.Error).Throw();
			}
		}

		/// <summary>
		/// Queue <paramref name="work"/> for the device thread and return at once. For resource
		/// creation and uploads on desktop OpenGL, where waiting is what deadlocks: a loader thread
		/// that waits here while holding a lock the device thread spins on in game code that never
		/// calls WPR (Need for Speed Undercover's <c>QuadEffectManager.threadLock</c>) can never be
		/// served. Queued in the same FIFO as <see cref="Invoke(Action)"/>, so a worker's own calls
		/// still run in the order it made them. The work must own everything it touches (copy the
		/// caller's data first).
		/// </summary>
		public static void Post(Action work)
		{
			lock (_lock)
			{
				if (!_enabled)
				{
					work();
					return;
				}
				_queue.Enqueue(new WorkItem(work));
				_pending = _queue.Count;
			}
		}

		/// <summary>
		/// On the device thread, before it uses a resource: run the posted work at the head of the
		/// queue, so a texture or buffer a worker created or filled exists and holds its data. Stops
		/// at the first waiting call, because that one is a worker's draw and must not be mixed
		/// into the frame the device thread is building.
		/// </summary>
		public static void DrainPosted()
		{
			if (_pending == 0 || !OffThreadGpuCalls.OnDeviceThread)
			{
				return;
			}
			while (true)
			{
				WorkItem item;
				lock (_lock)
				{
					if (_queue.Count == 0 || !_queue.Peek().Posted)
					{
						return;
					}
					item = _queue.Dequeue();
					_pending = _queue.Count;
				}
				Run(item);
			}
		}

		private static void Run(WorkItem item)
		{
			try
			{
				item.Work();
			}
			catch (Exception ex)
			{
				if (!item.Posted)
				{
					item.Error = ex;
				}
				else if (Interlocked.Increment(ref _postFailuresReported) <= 5)
				{
					XnaBackend.LogWarn("[wpr-gputhread] queued resource call failed on the device thread: " + ex.Message);
				}
			}
			if (!item.Posted)
			{
				item.Done.Set();
			}
		}

		/// <summary>Run <paramref name="work"/> on the device thread, wait, and return its result.</summary>
		public static T Invoke<T>(Func<T> work)
		{
			T result = default;
			Invoke(() => { result = work(); });
			return result;
		}

		/// <summary>
		/// Run every queued call. Device thread only; a no-op anywhere else and when nothing is
		/// waiting, so it is cheap enough to call from every frame and from a lock spin.
		/// </summary>
		public static void Drain()
		{
			if (Suspended)
			{
				DrainPosted();
				return;
			}
			if (_pending == 0 || !OffThreadGpuCalls.OnDeviceThread)
			{
				return;
			}
			while (true)
			{
				WorkItem item;
				lock (_lock)
				{
					if (_queue.Count == 0)
					{
						_pending = 0;
						return;
					}
					item = _queue.Dequeue();
					_pending = _queue.Count;
				}
				Run(item);
			}
		}
	}
}
