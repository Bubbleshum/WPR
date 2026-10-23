using System.Collections.Generic;
using System.Threading;
using WPR.Xna.Rhi;
using Xunit;

namespace WPR.SilverlightCompability.Tests;

/// <summary>
/// <see cref="DeviceThreadDispatch.Post"/>: a worker's resource calls on desktop OpenGL are queued
/// without the worker waiting (it may hold a lock the device thread spins on), keep their order
/// with its waited-for calls, and are run ahead of resource use without pulling a worker's draw
/// into the device thread's frame.
/// </summary>
public class DeviceThreadDispatchTests
{
    [Fact]
    public void Posted_work_never_blocks_the_worker_and_runs_in_order_ahead_of_a_waited_call()
    {
        var ran = new List<string>();
        OffThreadGpuCalls.SetDeviceThread();
        DeviceThreadDispatch.Enable("test");
        try
        {
            // The worker posts two uploads and returns at once: nothing has run yet.
            var worker = new Thread(() =>
            {
                DeviceThreadDispatch.Post(() => ran.Add("create"));
                DeviceThreadDispatch.Post(() => ran.Add("upload"));
            });
            worker.Start();
            Assert.True(worker.Join(5000));
            Assert.Empty(ran);

            // Resource use on the device thread runs them.
            DeviceThreadDispatch.DrainPosted();
            Assert.Equal(new[] { "create", "upload" }, ran);

            // A worker's waited-for draw, then a post queued behind it: DrainPosted must not run
            // the draw (it belongs to the worker's frame), nor anything behind it.
            ran.Clear();
            var drawer = new Thread(() =>
            {
                DeviceThreadDispatch.Invoke(() => ran.Add("draw"));
                DeviceThreadDispatch.Post(() => ran.Add("after"));
            });
            drawer.Start();
            SpinWait.SpinUntil(() => DeviceThreadDispatch.AnyPending, 5000);
            DeviceThreadDispatch.DrainPosted();
            Assert.Empty(ran);

            // The frame boundary drains everything, in order.
            DeviceThreadDispatch.Drain();
            Assert.True(SpinWait.SpinUntil(() =>
            {
                DeviceThreadDispatch.Drain();
                return ran.Count == 2;
            }, 5000));
            Assert.Equal(new[] { "draw", "after" }, ran);
            Assert.True(drawer.Join(5000));
        }
        finally
        {
            DeviceThreadDispatch.Disable();
            OffThreadGpuCalls.ClearDeviceThread();
        }
    }
}
