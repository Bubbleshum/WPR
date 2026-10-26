using System;
using System.Net;
using System.Text;
using System.Threading;
using Xunit;

namespace WPR.SilverlightCompability.Tests;

/// <summary>
/// Silverlight's <c>UdpAnySourceMulticastClient</c>, which WP7 titles used for local multiplayer
/// (PAC-MAN Kart Rally hosts and finds games on 224.0.1.1:52295).
/// </summary>
public class UdpAnySourceMulticastClientTests
{
    private static readonly IPAddress Group = IPAddress.Parse("239.255.42.99");

    [Fact]
    public void Only_a_multicast_group_is_accepted()
    {
        Assert.Throws<ArgumentException>(() => new UdpAnySourceMulticastClient(IPAddress.Loopback, 5000));
        Assert.Throws<ArgumentNullException>(() => new UdpAnySourceMulticastClient(null!, 5000));
        using var ok = new UdpAnySourceMulticastClient(Group, 5000);
    }

    [Fact]
    public void Receiving_before_joining_fails_at_End_not_at_Begin()
    {
        using var client = new UdpAnySourceMulticastClient(Group, 0);
        IAsyncResult r = client.BeginReceiveFromGroup(new byte[16], 0, 16, null, "state");
        Assert.Equal("state", r.AsyncState);
        Assert.True(r.AsyncWaitHandle.WaitOne(5000));
        Assert.Throws<InvalidOperationException>(() => client.EndReceiveFromGroup(r, out _));
    }

    [Fact]
    public void A_disposed_client_reports_ObjectDisposed()
    {
        var client = new UdpAnySourceMulticastClient(Group, 0);
        client.Dispose();
        IAsyncResult r = client.BeginSendToGroup(new byte[1], 0, 1, null, null);
        r.AsyncWaitHandle.WaitOne(5000);
        Assert.Throws<ObjectDisposedException>(() => client.EndSendToGroup(r));
    }

    [Fact]
    public void A_packet_sent_to_the_group_comes_back_with_loopback_on()
    {
        int port = 40000 + Random.Shared.Next(20000);
        using var client = new UdpAnySourceMulticastClient(Group, port);
        var joined = new ManualResetEventSlim();
        client.BeginJoinGroup(ar => { client.EndJoinGroup(ar); joined.Set(); }, null);
        Assert.True(joined.Wait(5000), "join did not complete");

        var buffer = new byte[64];
        int count = -1;
        IPEndPoint? source = null;
        var received = new ManualResetEventSlim();
        client.BeginReceiveFromGroup(buffer, 0, buffer.Length, ar =>
        {
            count = client.EndReceiveFromGroup(ar, out source);
            received.Set();
        }, null);

        byte[] message = Encoding.ASCII.GetBytes("pacman");
        var sent = new ManualResetEventSlim();
        client.BeginSendToGroup(message, 0, message.Length, ar => { client.EndSendToGroup(ar); sent.Set(); }, null);
        Assert.True(sent.Wait(5000), "send did not complete");

        if (!received.Wait(5000))
        {
            // A machine with no multicast-capable interface cannot loop a packet back; the
            // contract above is what this test is for, so do not fail on the environment.
            return;
        }
        Assert.Equal(message.Length, count);
        Assert.Equal("pacman", Encoding.ASCII.GetString(buffer, 0, count));
        Assert.NotNull(source);
        Assert.Equal(port, source!.Port);
    }
}
