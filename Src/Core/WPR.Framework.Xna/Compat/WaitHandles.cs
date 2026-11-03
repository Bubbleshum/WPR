using System;
using System.Diagnostics;
using System.Threading;
using WPR.Xna.Rhi;

namespace WPR.Xna.Compat
{
    /// <summary>
    /// Target of every <c>WaitHandle.WaitOne</c> call in game IL (patcher v66,
    /// <c>RedirectWaitOneCalls</c>). The <see cref="Monitor"/> twin for waits on a handle: it waits
    /// on the same handle, so ownership, signalling and abandonment are unchanged.
    ///
    /// <para>The one thing it adds: when the <b>device thread</b> has to wait while
    /// <see cref="DeviceThreadDispatch"/> is on (desktop OpenGL), it services worker threads' GPU
    /// calls between attempts. The thread that holds the handle may be waiting for exactly that.
    /// AE Mobile's framework guards the device with a named <c>Mutex</c>
    /// ("AEGameGraphicsDeviceControl"): <c>AEGame.BeginDraw</c> waits on it, and its texture loader
    /// holds it across <c>Texture2D.GetData</c>, which on desktop OpenGL is handed to the device
    /// thread. Without this AE Roundy POP stopped after its first frame.</para>
    ///
    /// <para>Every other case (another thread, D3D11, Vulkan, a handle already signalled) is the
    /// plain wait after one volatile read.</para>
    /// </summary>
    public static class WaitHandles
    {
        public static bool WaitOne(WaitHandle handle) => WaitOne(handle, Timeout.Infinite);

        public static bool WaitOne(WaitHandle handle, int millisecondsTimeout)
        {
            if (!DeviceThreadDispatch.Enabled || !OffThreadGpuCalls.OnDeviceThread)
            {
                return handle.WaitOne(millisecondsTimeout);
            }
            if (millisecondsTimeout < Timeout.Infinite)
            {
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout));
            }

            long start = Stopwatch.GetTimestamp();
            while (true)
            {
                int slice = 1;
                if (millisecondsTimeout != Timeout.Infinite)
                {
                    long left = millisecondsTimeout - (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    if (left <= 0)
                    {
                        return handle.WaitOne(0);
                    }
                    slice = (int)Math.Min(left, slice);
                }
                if (handle.WaitOne(slice))
                {
                    return true;
                }
                DeviceThreadDispatch.Drain();
            }
        }

        public static bool WaitOne(WaitHandle handle, int millisecondsTimeout, bool exitContext) =>
            WaitOne(handle, millisecondsTimeout);

        public static bool WaitOne(WaitHandle handle, TimeSpan timeout) =>
            WaitOne(handle, ToMilliseconds(timeout));

        public static bool WaitOne(WaitHandle handle, TimeSpan timeout, bool exitContext) =>
            WaitOne(handle, ToMilliseconds(timeout));

        private static int ToMilliseconds(TimeSpan timeout)
        {
            long ms = (long)timeout.TotalMilliseconds;
            if (ms < Timeout.Infinite || ms > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }
            return (int)ms;
        }
    }
}
