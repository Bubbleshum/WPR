using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using WPR.WindowsCompability;
using Xunit;

namespace WPR.SilverlightCompability.Tests;

/// <summary>
/// Silverlight's Socket *Async methods always completed asynchronously and raised Completed;
/// WP7 games (Pool Pro Online 3's TCPManager) wait on that event with no timeout.
/// </summary>
public class SilverlightSocketsTests
{
    [Fact]
    public void Connect_send_and_receive_always_raise_Completed()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var done = new AutoResetEvent(false);
        SocketAsyncOperation? last = null;
        SocketError error = SocketError.SocketError;
        var e = new SocketAsyncEventArgs { RemoteEndPoint = listener.LocalEndPoint };
        e.Completed += (_, args) =>
        {
            last = args.LastOperation;
            error = args.SocketError;
            done.Set();
        };

        Assert.True(SilverlightSockets.ConnectAsync(client, e));
        Assert.True(done.WaitOne(5000), "Connect never raised Completed");
        Assert.Equal(SocketAsyncOperation.Connect, last);
        Assert.Equal(SocketError.Success, error);
        using Socket server = listener.Accept();

        // A tiny send on a connected loopback socket is exactly the case .NET finishes at once.
        for (int i = 0; i < 20; i++)
        {
            e.SetBuffer(new byte[] { 1, 2, 3, 4 }, 0, 4);
            Assert.True(SilverlightSockets.SendAsync(client, e));
            Assert.True(done.WaitOne(5000), $"send {i} never raised Completed");
            Assert.Equal(SocketAsyncOperation.Send, last);
            Assert.Equal(SocketError.Success, error);
        }

        // Data already waiting (the 80 bytes just sent): the receive also finishes at once.
        Thread.Sleep(100);
        var r = new SocketAsyncEventArgs();
        r.SetBuffer(new byte[1024], 0, 1024);
        int received = 0;
        r.Completed += (_, args) =>
        {
            received = args.BytesTransferred;
            done.Set();
        };
        Assert.True(SilverlightSockets.ReceiveAsync(server, r));
        Assert.True(done.WaitOne(5000), "receive never raised Completed");
        Assert.Equal(80, received);
    }

    /// <summary>
    /// A receive's callback waits for the game's frame (GameFrameGate) so it can't race the game's
    /// own use of what it received; a send's callback doesn't, because a frame may be waiting for it.
    /// Pool Pro Online 3 lost packets when its receive callback ran in the middle of its frame.
    /// </summary>
    [Fact]
    public void Receive_callbacks_wait_for_the_frame_and_sends_do_not()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        client.Connect(listener.LocalEndPoint!);
        using Socket server = listener.Accept();

        using var frameHeld = new ManualResetEventSlim();
        using var endFrame = new ManualResetEventSlim();
        var frame = new Thread(() =>
        {
            WPR.Xna.Rhi.GameFrameGate.EnterFrame();
            frameHeld.Set();
            endFrame.Wait();
            WPR.Xna.Rhi.GameFrameGate.ExitFrame();
        });
        frame.Start();
        frameHeld.Wait();

        using var received = new ManualResetEventSlim();
        using var sent = new ManualResetEventSlim();
        var r = new SocketAsyncEventArgs();
        r.SetBuffer(new byte[64], 0, 64);
        SilverlightSockets.AddCompleted(r, (_, _) => received.Set());
        var s = new SocketAsyncEventArgs();
        s.SetBuffer(new byte[] { 7 }, 0, 1);
        SilverlightSockets.AddCompleted(s, (_, _) => sent.Set());

        SilverlightSockets.ReceiveAsync(client, r);
        server.Send(new byte[] { 1, 2, 3 });
        SilverlightSockets.SendAsync(client, s);

        Assert.True(sent.Wait(2000), "a send callback must not wait for the frame");
        Assert.False(received.Wait(150), "a receive callback ran in the middle of the frame");
        endFrame.Set();
        Assert.True(received.Wait(2000), "the receive callback never ran after the frame");
        frame.Join();
    }

    [Fact]
    public void A_receive_runs_anyway_if_the_frame_never_ends()
    {
        using var frameHeld = new ManualResetEventSlim();
        using var endFrame = new ManualResetEventSlim();
        var frame = new Thread(() =>
        {
            WPR.Xna.Rhi.GameFrameGate.EnterFrame();
            frameHeld.Set();
            endFrame.Wait();
            WPR.Xna.Rhi.GameFrameGate.ExitFrame();
        });
        frame.Start();
        frameHeld.Wait();
        var ran = Task.Run(() => WPR.Xna.Rhi.GameFrameGate.RunBetweenFrames(() => { }));
        Assert.True(ran.Wait(WPR.Xna.Rhi.GameFrameGate.MaxWait + TimeSpan.FromSeconds(2)), "a callback waited for ever");
        endFrame.Set();
        frame.Join();
    }
}
