using System.Threading;
using WPR.Xna.Rhi;

namespace WPR.Xna.Compat
{
    /// <summary>
    /// <c>MemberPatches</c> target for <see cref="System.Threading.Monitor.Enter(object)"/> in game
    /// IL, i.e. every C# <c>lock</c>. Not a replacement for <c>Monitor</c>: it takes the same real
    /// monitor, so the game's own <c>Monitor.Exit</c> (left untouched) releases it.
    ///
    /// <para>The one thing it adds: when the <b>device thread</b> has to wait for a lock while
    /// <see cref="DeviceThreadDispatch"/> is on, it services worker threads' GPU calls between
    /// attempts. The worker that holds the lock may be waiting for exactly that — Plants vs. Zombies'
    /// loader holds <c>ResourceManager.DrawLocker</c> while drawing into a render target, and
    /// <c>Main.Draw</c> takes the same lock — so without this the two threads deadlock.</para>
    ///
    /// <para>Every other case — another thread, D3D11, Vulkan, an uncontended lock — is a single
    /// <c>TryEnter</c> or a plain <c>Monitor.Enter</c>, so the cost to games that never need it is
    /// one volatile read.</para>
    /// </summary>
    public static class Monitor
    {
        public static void Enter(object obj)
        {
            if (!DeviceThreadDispatch.Enabled || !OffThreadGpuCalls.OnDeviceThread)
            {
                System.Threading.Monitor.Enter(obj);
                return;
            }

            while (!System.Threading.Monitor.TryEnter(obj, 1))
            {
                DeviceThreadDispatch.Drain();
            }
        }

        public static void Enter(object obj, ref bool lockTaken)
        {
            if (!DeviceThreadDispatch.Enabled || !OffThreadGpuCalls.OnDeviceThread)
            {
                System.Threading.Monitor.Enter(obj, ref lockTaken);
                return;
            }

            while (true)
            {
                System.Threading.Monitor.TryEnter(obj, 1, ref lockTaken);
                if (lockTaken)
                {
                    return;
                }
                DeviceThreadDispatch.Drain();
            }
        }
    }
}
