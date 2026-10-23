using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace WPR.Backend.FNA
{
	/// <summary>
	/// Stand-in handles for textures and buffers a worker thread creates on desktop OpenGL, where
	/// the real creation is queued for the device thread without the worker waiting for it.
	///
	/// <para><b>Why.</b> On desktop OpenGL a worker's resource calls have to run on the device
	/// thread (<see cref="WPR.Xna.Rhi.DeviceThreadDispatch"/>). Waiting for them deadlocks a loader
	/// that holds a lock the game thread spins on in code that never calls WPR. Need for Speed
	/// Undercover's <c>QuadEffectManager.threadLock</c> is exactly that
	/// (<c>while (m_threadLock) Thread.yield();</c>, and midp's <c>yield()</c> is empty): the loader
	/// takes it, creates a texture, and waits for a game thread that is spinning on it. Android is
	/// not affected (its FNA3D creates textures on worker contexts), and the Windows <c>FNA3D.dll</c>
	/// cannot be rebuilt here to get them, so the worker no longer waits: it gets one of these
	/// handles at once, the creation is posted, and the handle is swapped for the real one wherever
	/// a call reaches FNA3D on the device thread.</para>
	///
	/// <para>A stand-in is a one-byte native allocation, so it can never equal a real FNA3D handle.
	/// It lives until the resource is disposed. <see cref="Resolve"/> is a counter read when none
	/// exist, so D3D11, Vulkan, Android and games that never load on a worker pay nothing.</para>
	/// </summary>
	internal static class DeferredGpuHandles
	{
		private static readonly ConcurrentDictionary<IntPtr, IntPtr> _real = new ConcurrentDictionary<IntPtr, IntPtr>();
		private static int _live;
		private static int _reported;

		/// <summary>A new stand-in, not yet backed by a real handle.</summary>
		public static IntPtr Create()
		{
			IntPtr proxy = Marshal.AllocHGlobal(1);
			_real[proxy] = IntPtr.Zero;
			Interlocked.Increment(ref _live);
			if (Interlocked.Exchange(ref _reported, 1) == 0)
			{
				WPR.Xna.Rhi.XnaBackend.LogInfo(
					"[wpr-gputhread] a worker's texture/buffer creation is queued for the device thread without waiting (desktop OpenGL)");
			}
			return proxy;
		}

		/// <summary>Records the real handle, on the device thread, when the posted creation runs.</summary>
		public static void Complete(IntPtr proxy, IntPtr real) => _real[proxy] = real;

		/// <summary>True when <paramref name="handle"/> is a stand-in.</summary>
		public static bool IsProxy(IntPtr handle) => _live != 0 && handle != IntPtr.Zero && _real.ContainsKey(handle);

		/// <summary>
		/// The handle FNA3D knows for <paramref name="handle"/>: itself unless it is a stand-in.
		/// Device thread only, after <see cref="WPR.Xna.Rhi.DeviceThreadDispatch.DrainPosted"/>.
		/// </summary>
		public static IntPtr Resolve(IntPtr handle)
		{
			if (_live == 0 || handle == IntPtr.Zero)
			{
				return handle;
			}
			return _real.TryGetValue(handle, out IntPtr real) ? real : handle;
		}

		/// <summary>Drops a stand-in once its resource has been disposed.</summary>
		public static void Forget(IntPtr proxy)
		{
			if (_real.TryRemove(proxy, out _))
			{
				Interlocked.Decrement(ref _live);
				Marshal.FreeHGlobal(proxy);
			}
		}

		/// <summary>At device destruction: every stand-in belongs to the device that is going.</summary>
		public static void Clear()
		{
			foreach (IntPtr proxy in _real.Keys)
			{
				Forget(proxy);
			}
		}
	}
}
