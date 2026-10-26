using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Net.Sockets.UdpAnySourceMulticastClient</c> (Silverlight 4 / Windows
    /// Phone 7.1): a UDP client that joins an any-source multicast group, the API WP7 titles used
    /// for local Wi-Fi multiplayer. .NET has no such type, so the patcher rescopes it here.
    ///
    /// <para><b>A real implementation over a <see cref="Socket"/></b>, so two WPR instances on one
    /// network (or one machine) find each other the way two phones did. PAC-MAN Kart Rally's
    /// "Host game" / "Connect to game" opens one on <c>224.0.1.1:52295</c> and threw
    /// <see cref="TypeLoadException"/> from its channel constructor without this.</para>
    ///
    /// <para>The Silverlight contract kept here: every operation is a Begin/End pair and completes
    /// on another thread; receiving before <see cref="BeginJoinGroup"/> has completed, or using a
    /// disposed client, fails; <see cref="BlockSource"/> drops a sender's packets. A receive's
    /// callback runs between the game's frames (<see cref="WPR.Xna.Rhi.GameFrameGate"/>), the
    /// same reasoning as <c>SilverlightSockets.AddCompleted</c>: on the single-core phones these
    /// games were written for it never overlapped a frame.</para>
    /// </summary>
    public class UdpAnySourceMulticastClient : IDisposable
    {
        private readonly IPAddress _group;
        private readonly int _localPort;
        private readonly object _lock = new object();
        private readonly HashSet<IPAddress> _blocked = new HashSet<IPAddress>();
        private Socket? _socket;
        private bool _joined;
        private bool _disposed;
        private bool _loopback = true;

        public UdpAnySourceMulticastClient(IPAddress groupAddress, int localPort)
        {
            if (groupAddress == null) throw new ArgumentNullException(nameof(groupAddress));
            if (localPort < 0 || localPort > 65535) throw new ArgumentOutOfRangeException(nameof(localPort));
            if (!IsMulticast(groupAddress)) throw new ArgumentException("The address is not a multicast group address.", nameof(groupAddress));
            _group = groupAddress;
            _localPort = localPort;
        }

        /// <summary>Whether this client receives the packets it sends to the group.</summary>
        public bool MulticastLoopback
        {
            get => _loopback;
            set
            {
                lock (_lock)
                {
                    _loopback = value;
                    ApplyLoopback();
                }
            }
        }

        public int ReceiveBufferSize { get; set; } = 8192;

        public int SendBufferSize { get; set; } = 8192;

        public IAsyncResult BeginJoinGroup(AsyncCallback? callback, object? state) =>
            Run(callback, state, receives: false, () =>
            {
                lock (_lock)
                {
                    ThrowIfDisposed();
                    if (_joined) throw new InvalidOperationException("The group has already been joined.");
                    var socket = new Socket(_group.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                    try
                    {
                        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                        IPAddress any = _group.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
                        socket.Bind(new IPEndPoint(any, _localPort));
                        if (_group.AddressFamily == AddressFamily.InterNetworkV6)
                        {
                            socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership, new IPv6MulticastOption(_group));
                        }
                        else
                        {
                            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(_group));
                        }
                        socket.ReceiveBufferSize = Math.Max(1, ReceiveBufferSize);
                        socket.SendBufferSize = Math.Max(1, SendBufferSize);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                    _socket = socket;
                    _joined = true;
                    ApplyLoopback();
                }
                return Task.FromResult<object?>(null);
            });

        public void EndJoinGroup(IAsyncResult result) => End(result);

        public IAsyncResult BeginReceiveFromGroup(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
        {
            CheckBuffer(buffer, offset, count);
            return Run(callback, state, receives: true, async () =>
            {
                while (true)
                {
                    Socket socket = Joined();
                    EndPoint from = new IPEndPoint(_group.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
                    SocketReceiveFromResult r = await socket.ReceiveFromAsync(new ArraySegment<byte>(buffer, offset, count), SocketFlags.None, from).ConfigureAwait(false);
                    var source = (IPEndPoint)r.RemoteEndPoint;
                    lock (_lock)
                    {
                        if (_blocked.Contains(source.Address)) continue;
                    }
                    return (object?)new ReceiveResult(r.ReceivedBytes, source);
                }
            });
        }

        public int EndReceiveFromGroup(IAsyncResult result, out IPEndPoint source)
        {
            var r = (ReceiveResult)End(result)!;
            source = r.Source;
            return r.Count;
        }

        public IAsyncResult BeginSendToGroup(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
        {
            CheckBuffer(buffer, offset, count);
            return Run(callback, state, receives: false, async () =>
            {
                await Joined().SendToAsync(new ArraySegment<byte>(buffer, offset, count), SocketFlags.None, new IPEndPoint(_group, _localPort)).ConfigureAwait(false);
                return (object?)null;
            });
        }

        public void EndSendToGroup(IAsyncResult result) => End(result);

        /// <summary>Sends to one member of the group (unicast), as Silverlight allowed after joining.</summary>
        public IAsyncResult BeginSendTo(byte[] buffer, int offset, int count, IPEndPoint remoteEndPoint, AsyncCallback? callback, object? state)
        {
            CheckBuffer(buffer, offset, count);
            if (remoteEndPoint == null) throw new ArgumentNullException(nameof(remoteEndPoint));
            return Run(callback, state, receives: false, async () =>
            {
                await Joined().SendToAsync(new ArraySegment<byte>(buffer, offset, count), SocketFlags.None, remoteEndPoint).ConfigureAwait(false);
                return (object?)null;
            });
        }

        public void EndSendTo(IAsyncResult result) => End(result);

        public void BlockSource(IPAddress sourceAddress)
        {
            if (sourceAddress == null) throw new ArgumentNullException(nameof(sourceAddress));
            lock (_lock) { ThrowIfDisposed(); _blocked.Add(sourceAddress); }
        }

        public void UnblockSource(IPAddress sourceAddress)
        {
            if (sourceAddress == null) throw new ArgumentNullException(nameof(sourceAddress));
            lock (_lock) { ThrowIfDisposed(); _blocked.Remove(sourceAddress); }
        }

        public void Dispose()
        {
            Socket? socket;
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                _joined = false;
                socket = _socket;
                _socket = null;
            }
            socket?.Dispose();
        }

        private sealed class ReceiveResult
        {
            public ReceiveResult(int count, IPEndPoint source) { Count = count; Source = source; }
            public int Count { get; }
            public IPEndPoint Source { get; }
        }

        private static bool IsMulticast(IPAddress address) =>
            address.AddressFamily == AddressFamily.InterNetworkV6
                ? address.IsIPv6Multicast
                : address.AddressFamily == AddressFamily.InterNetwork && (address.GetAddressBytes()[0] & 0xF0) == 0xE0;

        private static void CheckBuffer(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || offset > buffer.Length) throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0 || count > buffer.Length - offset) throw new ArgumentOutOfRangeException(nameof(count));
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(UdpAnySourceMulticastClient));
        }

        private Socket Joined()
        {
            lock (_lock)
            {
                ThrowIfDisposed();
                if (!_joined || _socket == null) throw new InvalidOperationException("The multicast group has not been joined.");
                return _socket;
            }
        }

        private void ApplyLoopback()
        {
            if (_socket == null) return;
            try
            {
                if (_group.AddressFamily == AddressFamily.InterNetworkV6)
                    _socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastLoopback, _loopback);
                else
                    _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, _loopback);
            }
            catch (SocketException)
            {
                // Not every stack lets this be changed; the default (on) then stands.
            }
        }

        /// <summary>
        /// Runs one operation as a Silverlight Begin call: never completes synchronously, invokes
        /// the callback once on another thread. A failure (including a disposed socket) is kept and
        /// rethrown by the matching End call, as Silverlight did.
        /// </summary>
        private IAsyncResult Run(AsyncCallback? callback, object? state, bool receives, Func<Task<object?>> operation)
        {
            var tcs = new TaskCompletionSource<object?>(state, TaskCreationOptions.RunContinuationsAsynchronously);
            Task.Run(async () =>
            {
                try
                {
                    tcs.SetResult(await operation().ConfigureAwait(false));
                }
                catch (ObjectDisposedException)
                {
                    tcs.SetException(new ObjectDisposedException(nameof(UdpAnySourceMulticastClient)));
                }
                catch (Exception e)
                {
                    tcs.SetException(e);
                }

                if (callback != null)
                {
                    if (receives)
                        WPR.Xna.Rhi.GameFrameGate.RunBetweenFrames(() => callback(tcs.Task));
                    else
                        callback(tcs.Task);
                }
            });
            return tcs.Task;
        }

        private static object? End(IAsyncResult result)
        {
            if (result is not Task<object?> task) throw new ArgumentException("The IAsyncResult did not come from this client.", nameof(result));
            try
            {
                return task.GetAwaiter().GetResult();
            }
            catch (AggregateException e) when (e.InnerException != null)
            {
                throw e.InnerException;
            }
        }
    }
}
