using System.Diagnostics;
using System.Text;
using WPR.Wp8Native;

namespace Wp8ContractTests.Suites;

/// <summary>
/// WS2_32 to the Winsock 2 contract, over loopback only: start-up, byte order and address text,
/// TCP listen/connect/accept/send/recv (blocking and non-blocking), select/WSAPoll, options,
/// shutdown, UDP, name resolution of "localhost" and literals, event selection, and the ordinal
/// imports the import table actually uses.
/// </summary>
public sealed class WinsockSuite : IContractSuite
{
    public string Name => "winsock";

    private const long SocketError = 0xFFFFFFFF;

    public void Run(Harness h)
    {
        ArmEmulator e = h.E;
        HostStubs s = h.S;
        void Check(string what, bool ok, string detail = "") => h.Check(what, ok, detail);
        long Call(string fn, params long[] a) => h.Call(fn, a);

        // Ordinal imports go through Dispatch exactly as the trap names them.
        long Ordinal(int n, params long[] a)
        {
            int[] registers = [Harness.R0, Harness.R1, Harness.R2, Harness.R3];
            for (int i = 0; i < a.Length && i < 4; i++) e.WriteRegister(registers[i], a[i]);
            for (int i = 4; i < a.Length; i++) e.WriteUInt32(h.Stack + ((i - 4) * 4), (uint)a[i]);
            e.WriteRegister(Harness.SP, h.Stack);
            s.Dispatch($"WS2_32.dll!#{n}");
            return e.ReadRegister(Harness.R0) & 0xFFFFFFFFL;
        }

        long LastError() => Call("WSAGetLastError");
        long Int(long address) => e.ReadUInt32(address);
        long IntBox(long value)
        {
            long p = h.Buffer(4);
            e.WriteUInt32(p, (uint)value);
            return p;
        }

        long SockAddrIn(uint ipNetworkOrder, int port)
        {
            long p = h.Buffer(16);
            e.WriteMemory(p, [2, 0, (byte)(port >> 8), (byte)port]);
            e.WriteUInt32(p + 4, ipNetworkOrder);
            return p;
        }

        int PortOf(long sockaddr) => (e.ReadMemory(sockaddr + 2, 1)[0] << 8) | e.ReadMemory(sockaddr + 3, 1)[0];
        const uint loopback = 0x0100007F;

        // ---- before WSAStartup ----
        Check("socket before WSAStartup fails WSANOTINITIALISED", Call("socket", 2, 1, 6) == SocketError && LastError() == 10093);
        Check("WSACleanup before WSAStartup fails WSANOTINITIALISED", Call("WSACleanup") == SocketError && LastError() == 10093);

        // ---- byte order needs no start-up ----
        Check("htons swaps the low 16 bits (#9)", Ordinal(9, 0x1234) == 0x3412);
        Check("ntohs (#15)", Ordinal(15, 0xABCD) == 0xCDAB);
        Check("htonl swaps 32 bits (#8)", Ordinal(8, 0x11223344) == 0x44332211);
        Check("ntohl (#14)", Ordinal(14, 0x0100007F) == 0x7F000001);

        // ---- WSAStartup ----
        long wsadata = h.Buffer(400);
        Check("WSAStartup(0.0) is WSAVERNOTSUPPORTED", Ordinal(115, 0x0000, wsadata) == 10092);
        Check("WSAStartup with a NULL WSADATA is WSAEFAULT", Ordinal(115, 0x0202, 0) == 10014);
        Check("WSAStartup(2.2) succeeds (#115)", Ordinal(115, 0x0202, wsadata) == 0);
        Check("WSADATA wVersion/wHighVersion are 2.2", h.U16(wsadata) == 0x0202 && h.U16(wsadata + 2) == 0x0202);
        Check("WSADATA szDescription", h.ReadNarrow(wsadata + 4).StartsWith("WinSock 2", StringComparison.Ordinal), h.ReadNarrow(wsadata + 4));
        Check("WSAStartup(1.1) negotiates 1.1", Ordinal(115, 0x0101, wsadata) == 0 && h.U16(wsadata) == 0x0101);
        Check("WSAStartup(3.0) negotiates 2.2", Ordinal(115, 0x0003, wsadata) == 0 && h.U16(wsadata) == 0x0202);
        Check("WSACleanup balances one start-up", Ordinal(116) == 0);
        Check("WSACleanup balances another", Ordinal(116) == 0);
        Check("WSACleanup balances the third", Ordinal(116) == 0);
        Check("...and a fourth is WSANOTINITIALISED", Ordinal(116) == SocketError && LastError() == 10093);

        // ---- last error ----
        Ordinal(112, 10061);
        Check("WSASetLastError/WSAGetLastError round-trip (#112/#111)", Ordinal(111) == 10061);
        Check("WSAGetLastError is GetLastError", Call("GetLastError") == 10061);

        // ---- address text ----
        Check("inet_addr dotted quad (#11)", Ordinal(11, h.Narrow("127.0.0.1")) == loopback);
        Check("inet_addr a.b form", Call("inet_addr", h.Narrow("10.65536")) == 0x0000010A, $"0x{Call("inet_addr", h.Narrow("10.65536")):X}");
        Check("inet_addr hex and octal parts", Call("inet_addr", h.Narrow("0x7f.0.0.01")) == loopback);
        Check("inet_addr rejects junk with INADDR_NONE", Call("inet_addr", h.Narrow("1.2.3.x")) == 0xFFFFFFFF);
        Check("inet_addr rejects an overflowing part", Call("inet_addr", h.Narrow("1.2.3.256")) == 0xFFFFFFFF);
        long text = Ordinal(12, 0x0403020A);
        Check("inet_ntoa (#12)", text != 0 && h.ReadNarrow(text) == "10.2.3.4", text == 0 ? "null" : h.ReadNarrow(text));
        long v6 = h.Buffer(16);
        Check("inet_pton IPv6", Call("inet_pton", 23, h.Narrow("::1"), v6) == 1 && e.ReadMemory(v6, 16)[15] == 1);
        Check("inet_pton strict IPv4 rejects shorthand", Call("inet_pton", 2, h.Narrow("127.1"), v6) == 0);
        Check("inet_pton bad family is -1 with WSAEAFNOSUPPORT", Call("inet_pton", 99, h.Narrow("1.2.3.4"), v6) == SocketError && LastError() == 10047);
        long ntop = h.Buffer(64);
        Check("inet_ntop IPv6", Call("inet_ntop", 23, v6, ntop, 64) == ntop && h.ReadNarrow(ntop) == "::1", h.ReadNarrow(ntop));
        long wide = h.Buffer(128);
        Check("InetNtopW IPv4", Call("InetNtopW", 2, IntBox(loopback), wide, 64) == wide && h.ReadWide(wide) == "127.0.0.1");
        Check("inet_ntop with a short buffer fails", Call("inet_ntop", 2, IntBox(loopback), ntop, 4) == 0);

        // ---- the real thing ----
        Check("WSAStartup again", Call("WSAStartup", 0x0202, wsadata) == 0);
        Check("socket with a bad family is WSAEAFNOSUPPORT", Call("socket", 99, 1, 0) == SocketError && LastError() == 10047);
        Check("socket with a bad type is WSAESOCKTNOSUPPORT", Call("socket", 2, 9, 0) == SocketError && LastError() == 10044);
        Check("socket(stream, udp) is WSAEPROTOTYPE", Call("socket", 2, 1, 17) == SocketError && LastError() == 10041);
        Check("closesocket on a non-socket is WSAENOTSOCK", Call("closesocket", 0x12345) == SocketError && LastError() == 10038);

        long server = Ordinal(23, 2, 1, 6);
        Check("socket(AF_INET, SOCK_STREAM, TCP) (#23)", server != SocketError && server != 0 && (server & 3) == 0, $"0x{server:X}");
        long name = h.Buffer(16), nameLength = IntBox(16);
        Check("getsockname on an unbound socket is WSAEINVAL", Call("getsockname", server, name, nameLength) == SocketError && LastError() == 10022);
        Check("listen on an unbound socket is WSAEINVAL", Call("listen", server, 5) == SocketError && LastError() == 10022);
        Check("bind with a short length is WSAEFAULT", Ordinal(2, server, SockAddrIn(loopback, 0), 8) == SocketError && LastError() == 10014);
        Check("bind 127.0.0.1:0 (#2)", Ordinal(2, server, SockAddrIn(loopback, 0), 16) == 0);
        Check("bind twice is WSAEINVAL", Call("bind", server, SockAddrIn(loopback, 0), 16) == SocketError && LastError() == 10022);
        Check("listen (#13)", Ordinal(13, server, 0x7FFFFFFF) == 0);
        Check("getsockname (#6)", Ordinal(6, server, name, nameLength) == 0 && Int(nameLength) == 16);
        int port = PortOf(name);
        Check("getsockname reports the bound port and family", port != 0 && h.U16(name) == 2 && Int(name + 4) == loopback, $"port {port}");
        long option = h.Buffer(4), optionLength = IntBox(4);
        Check("SO_ACCEPTCONN is TRUE on a listener", Call("getsockopt", server, 0xFFFF, 2, option, optionLength) == 0 && Int(option) == 1);

        // A non-blocking connect.
        long client = Call("socket", 2, 1, 0);
        Check("FIONBIO puts a socket in non-blocking mode (#10)", Ordinal(10, client, 0x8004667E, IntBox(1)) == 0);
        Check("recv on an unconnected socket is WSAENOTCONN", Call("recv", client, h.Buffer(8), 8, 0) == SocketError && LastError() == 10057);
        long c = Ordinal(4, client, SockAddrIn(loopback, port), 16);
        long err = LastError();
        Check("non-blocking connect returns WSAEWOULDBLOCK (#4)", c == SocketError && err == 10035, $"r=0x{c:X} err={err}");
        c = Call("connect", client, SockAddrIn(loopback, port), 16);
        err = LastError();
        Check("a second connect is WSAEALREADY or WSAEISCONN", c == SocketError && err is 10037 or 10056, $"err={err}");

        // select: the listener becomes readable, the client writable.
        long readSet = h.Buffer(4 + 256), writeSet = h.Buffer(4 + 256), timeval = h.Buffer(8);
        e.WriteUInt32(readSet, 1);
        e.WriteUInt32(readSet + 4, (uint)server);
        e.WriteUInt32(writeSet, 1);
        e.WriteUInt32(writeSet + 4, (uint)client);
        e.WriteUInt32(timeval, 5);
        long ready = Ordinal(18, 0, readSet, writeSet, 0, timeval);
        Check("select reports both ready (#18)", ready == 2, $"{ready}");
        Check("__WSAFDIsSet sees the listener in the read set (#151)", Ordinal(151, server, readSet) != 0);
        Check("__WSAFDIsSet is 0 for a socket not in the set", Ordinal(151, client, readSet) == 0);
        Check("select with every set empty is WSAEINVAL", Call("select", 0, 0, 0, 0, timeval) == SocketError && LastError() == 10022);

        long peer = h.Buffer(16), peerLength = IntBox(16);
        long accepted = Ordinal(1, server, peer, peerLength);
        Check("accept (#1)", accepted != SocketError && accepted != server && accepted != client, $"0x{accepted:X}");
        Check("accept writes the peer address", h.U16(peer) == 2 && Int(peer + 4) == loopback && Int(peerLength) == 16);
        long clientName = h.Buffer(16);
        long gsn = Call("getsockname", client, clientName, IntBox(16));
        long acceptedPeer = h.Buffer(16);
        long gpn = Ordinal(5, accepted, acceptedPeer, IntBox(16));
        Check("getpeername (#5) is the client's name", gsn == 0 && gpn == 0 && PortOf(acceptedPeer) == PortOf(clientName) && PortOf(clientName) != 0,
            $"getsockname={gsn} getpeername={gpn} err={LastError()} {PortOf(clientName)} vs {PortOf(acceptedPeer)}");
        Check("connect on a connected socket is WSAEISCONN", Call("connect", client, SockAddrIn(loopback, port), 16) == SocketError && LastError() == 10056);

        // Data.
        long hello = h.Narrow("hello");
        Check("send (#19)", Ordinal(19, client, hello, 5, 0) == 5);
        long available = h.Buffer(4);
        Stopwatch wait = Stopwatch.StartNew();
        while (wait.ElapsedMilliseconds < 2000 && (Call("ioctlsocket", accepted, 0x4004667F, available) != 0 || Int(available) < 5)) Thread.Sleep(5);
        Check("FIONREAD reports the queued bytes", Int(available) == 5, $"{Int(available)}");
        long buffer = h.Buffer(64);
        Check("recv with MSG_PEEK leaves the data", Call("recv", accepted, buffer, 64, 2) == 5 && h.ReadNarrow(buffer) == "hello");
        e.WriteMemory(buffer, new byte[64]);
        Check("recv (#16) on a blocking socket", Ordinal(16, accepted, buffer, 64, 0) == 5 && h.ReadNarrow(buffer) == "hello");
        Check("recv with no data on a non-blocking socket is WSAEWOULDBLOCK", Call("recv", client, buffer, 64, 0) == SocketError && LastError() == 10035);
        Check("recv with an unknown flag is WSAEOPNOTSUPP", Call("recv", accepted, buffer, 64, 0x40) == SocketError && LastError() == 10045);

        Check("SO_RCVTIMEO 150ms", Call("setsockopt", accepted, 0xFFFF, 0x1006, IntBox(150), 4) == 0);
        wait.Restart();
        long r = Call("recv", accepted, buffer, 64, 0);
        long took = wait.ElapsedMilliseconds;
        Check("a blocking recv with nothing to read times out with WSAETIMEDOUT", r == SocketError && LastError() == 10060, $"r=0x{r:X} err={LastError()}");
        Check("...after about the receive timeout", took is >= 140 and < 3000, $"{took} ms");

        // Options.
        Check("setsockopt TCP_NODELAY (#21)", Ordinal(21, client, 6, 1, IntBox(1), 4) == 0);
        e.WriteUInt32(optionLength, 4);
        Check("getsockopt TCP_NODELAY (#7)", Ordinal(7, client, 6, 1, option, optionLength) == 0 && Int(option) == 1);
        e.WriteUInt32(optionLength, 4);
        Check("SO_TYPE is SOCK_STREAM", Call("getsockopt", client, 0xFFFF, 0x1008, option, optionLength) == 0 && Int(option) == 1);
        e.WriteUInt32(optionLength, 4);
        Check("SO_ERROR is 0 on a healthy socket", Call("getsockopt", client, 0xFFFF, 0x1007, option, optionLength) == 0 && Int(option) == 0);
        e.WriteUInt32(optionLength, 4);
        Check("SO_RCVTIMEO reads back", Call("getsockopt", accepted, 0xFFFF, 0x1006, option, optionLength) == 0 && Int(option) == 150);
        Check("an unknown option is WSAENOPROTOOPT", Call("setsockopt", client, 0xFFFF, 0x7777, IntBox(1), 4) == SocketError && LastError() == 10042);
        Check("an unknown level is WSAEINVAL", Call("setsockopt", client, 0x1234, 1, IntBox(1), 4) == SocketError && LastError() == 10022);
        Check("SO_BROADCAST on a stream socket is WSAENOPROTOOPT", Call("setsockopt", client, 0xFFFF, 0x20, IntBox(1), 4) == SocketError && LastError() == 10042);
        e.WriteUInt32(optionLength, 2);
        Check("getsockopt with a short length is WSAEFAULT", Call("getsockopt", client, 0xFFFF, 0x1008, option, optionLength) == SocketError && LastError() == 10014);
        long linger = h.Buffer(4);
        e.WriteMemory(linger, [1, 0, 3, 0]);
        Check("SO_LINGER set", Call("setsockopt", client, 0xFFFF, 0x80, linger, 4) == 0);
        e.WriteUInt32(optionLength, 4);
        Check("SO_LINGER reads back", Call("getsockopt", client, 0xFFFF, 0x80, option, optionLength) == 0 && h.U16(option) == 1 && h.U16(option + 2) == 3);
        e.WriteMemory(linger, [0, 0, 0, 0]);
        Call("setsockopt", client, 0xFFFF, 0x80, linger, 4);

        // WSASend / WSARecv, non-overlapped scatter/gather.
        long wsabufs = h.Buffer(16);
        e.WriteUInt32(wsabufs, 3);
        e.WriteUInt32(wsabufs + 4, (uint)h.Narrow("abc"));
        e.WriteUInt32(wsabufs + 8, 2);
        e.WriteUInt32(wsabufs + 12, (uint)h.Narrow("de"));
        long sent = h.Buffer(4);
        Check("WSASend gathers two buffers", Call("WSASend", client, wsabufs, 2, sent, 0, 0, 0) == 0 && Int(sent) == 5);
        long a = h.Buffer(2), b = h.Buffer(8);
        e.WriteUInt32(wsabufs, 2);
        e.WriteUInt32(wsabufs + 4, (uint)a);
        e.WriteUInt32(wsabufs + 8, 8);
        e.WriteUInt32(wsabufs + 12, (uint)b);
        Call("setsockopt", accepted, 0xFFFF, 0x1006, IntBox(2000), 4);
        long received = h.Buffer(4), flags = IntBox(0);
        long got = Call("WSARecv", accepted, wsabufs, 2, received, flags, 0, 0);
        Check("WSARecv scatters into two buffers", got == 0 && Int(received) == 5 &&
            Encoding.ASCII.GetString(e.ReadMemory(a, 2)) == "ab" && Encoding.ASCII.GetString(e.ReadMemory(b, 3)) == "cde",
            $"r={got} n={Int(received)}");

        // WSAPoll.
        Ordinal(19, client, hello, 5, 0);
        long pollfd = h.Buffer(16);
        e.WriteUInt32(pollfd, (uint)accepted);
        e.WriteMemory(pollfd + 4, BitConverter.GetBytes((ushort)0x100));   // POLLRDNORM
        e.WriteUInt32(pollfd + 8, 0x7777);                                  // not a socket
        e.WriteMemory(pollfd + 12, BitConverter.GetBytes((ushort)0x100));
        long polled = Call("WSAPoll", pollfd, 2, 2000);
        Check("WSAPoll reports readable and POLLNVAL", polled == 2 && h.U16(pollfd + 6) == 0x100 && h.U16(pollfd + 14) == 4,
            $"r={polled} revents={h.U16(pollfd + 6):X},{h.U16(pollfd + 14):X}");
        Call("recv", accepted, buffer, 64, 0);

        // Event selection.
        long ev = Call("WSACreateEvent");
        Check("WSACreateEvent", ev != 0);
        Check("WSAEventSelect(FD_READ|FD_CLOSE)", Call("WSAEventSelect", accepted, ev, 0x21) == 0);
        Check("an event-selected socket cannot be made blocking", Call("ioctlsocket", accepted, 0x8004667E, IntBox(0)) == SocketError && LastError() == 10022);
        Check("nothing to read: the wait times out", Call("WSAWaitForMultipleEvents", 1, IntBox(ev), 0, 0, 0) == 0x102);
        Ordinal(19, client, hello, 5, 0);
        long events = IntBox(ev);
        Check("data arrives: the wait is satisfied", Call("WSAWaitForMultipleEvents", 1, events, 0, 3000, 0) == 0);
        long network = h.Buffer(44);
        Check("WSAEnumNetworkEvents reports FD_READ", Call("WSAEnumNetworkEvents", accepted, ev, network) == 0 && (Int(network) & 1) == 1, $"0x{Int(network):X}");
        Check("...and resets the event", Call("WSAWaitForMultipleEvents", 1, events, 0, 0, 0) == 0x102);
        Check("recv on the event-selected socket", Call("recv", accepted, buffer, 64, 0) == 5);

        // Shutdown.
        Check("shutdown(SD_SEND) (#22)", Ordinal(22, client, 1) == 0);
        Check("send after SD_SEND is WSAESHUTDOWN", Call("send", client, hello, 5, 0) == SocketError && LastError() == 10058);
        Check("shutdown with a bad how is WSAEINVAL", Call("shutdown", client, 7) == SocketError && LastError() == 10022);
        Check("the peer sees FD_CLOSE", Call("WSAWaitForMultipleEvents", 1, events, 0, 3000, 0) == 0 &&
            Call("WSAEnumNetworkEvents", accepted, ev, network) == 0 && (Int(network) & 0x20) != 0, $"0x{Int(network):X}");
        Check("...and recv returns 0", Call("recv", accepted, buffer, 64, 0) == 0);
        Check("WSACloseEvent", Call("WSACloseEvent", ev) == 1);

        // A refused connection.
        long probe = Call("socket", 2, 1, 6);
        Call("bind", probe, SockAddrIn(loopback, 0), 16);
        long probeName = h.Buffer(16);
        Call("getsockname", probe, probeName, IntBox(16));
        int closedPort = PortOf(probeName);
        Call("closesocket", probe);
        long refused = Call("socket", 2, 1, 6);
        Call("ioctlsocket", refused, 0x8004667E, IntBox(1));
        Call("connect", refused, SockAddrIn(loopback, closedPort), 16);
        long exceptSet = h.Buffer(4 + 256);
        e.WriteUInt32(exceptSet, 1);
        e.WriteUInt32(exceptSet + 4, (uint)refused);
        e.WriteUInt32(timeval, 10);
        long refusedReady = Call("select", 0, 0, 0, exceptSet, timeval);
        e.WriteUInt32(optionLength, 4);
        Call("getsockopt", refused, 0xFFFF, 0x1007, option, optionLength);
        Check("a refused non-blocking connect shows in exceptfds with SO_ERROR WSAECONNREFUSED",
            refusedReady == 1 && Int(option) == 10061, $"select={refusedReady} SO_ERROR={Int(option)}");
        Call("closesocket", refused);

        long blockingRefused = Call("socket", 2, 1, 6);
        long br = Call("connect", blockingRefused, SockAddrIn(loopback, closedPort), 16);
        Check("a refused blocking connect is WSAECONNREFUSED", br == SocketError && LastError() == 10061, $"err={LastError()}");
        Call("closesocket", blockingRefused);

        // Close.
        Check("closesocket (#3)", Ordinal(3, client) == 0);
        Check("a closed socket is WSAENOTSOCK", Call("send", client, hello, 5, 0) == SocketError && LastError() == 10038);
        Call("closesocket", accepted);
        Call("closesocket", server);

        // ---- UDP ----
        long u1 = Call("socket", 2, 2, 0), u2 = Call("socket", 2, 2, 17);
        Check("UDP sockets", u1 != SocketError && u2 != SocketError);
        Check("recvfrom on an unbound UDP socket is WSAEINVAL", Call("recvfrom", u1, buffer, 64, 0, 0, 0) == SocketError && LastError() == 10022);
        Call("bind", u1, SockAddrIn(loopback, 0), 16);
        long u1Name = h.Buffer(16);
        Call("getsockname", u1, u1Name, IntBox(16));
        Check("send on an unconnected UDP socket is WSAENOTCONN", Call("send", u2, hello, 5, 0) == SocketError && LastError() == 10057);
        Check("sendto (#20)", Ordinal(20, u2, h.Narrow("dgram"), 5, 0, u1Name, 16) == 5);
        long from = h.Buffer(16), fromLength = IntBox(16);
        Call("setsockopt", u1, 0xFFFF, 0x1006, IntBox(2000), 4);
        e.WriteMemory(buffer, new byte[64]);
        Check("recvfrom (#17)", Ordinal(17, u1, buffer, 64, 0, from, fromLength) == 5 && h.ReadNarrow(buffer) == "dgram");
        long u2Name = h.Buffer(16);
        Call("getsockname", u2, u2Name, IntBox(16));
        Check("recvfrom reports the sender", PortOf(from) == PortOf(u2Name) && Int(from + 4) == loopback && Int(fromLength) == 16);
        Call("sendto", u2, h.Narrow("0123456789"), 10, 0, u1Name, 16);
        long truncated = Call("recvfrom", u1, buffer, 4, 0, from, IntBox(16));
        Check("a datagram longer than the buffer is truncated with WSAEMSGSIZE",
            truncated == SocketError && LastError() == 10040 && Encoding.ASCII.GetString(e.ReadMemory(buffer, 4)) == "0123");
        e.WriteUInt32(optionLength, 4);
        Check("SO_MAX_MSG_SIZE on UDP", Call("getsockopt", u1, 0xFFFF, 0x2003, option, optionLength) == 0 && Int(option) == 65507);
        Call("closesocket", u1);
        Call("closesocket", u2);
        Check("every socket was closed", s.Winsock.OpenSockets == 0, $"{s.Winsock.OpenSockets}");
        Check("the connect targets were logged", s.Winsock.Log.Any(l => l.StartsWith("connect tcp 127.0.0.1:", StringComparison.Ordinal)),
            string.Join("; ", s.Winsock.Log));

        // ---- names ----
        long hints = h.Buffer(32), result = h.Buffer(4);
        e.WriteUInt32(hints + 4, 2);
        e.WriteUInt32(hints + 8, 1);
        long gai = Call("getaddrinfo", h.Narrow("localhost"), h.Narrow("80"), hints, result);
        long info = Int(result);
        Check("getaddrinfo(localhost, 80, AF_INET/STREAM)", gai == 0 && info != 0, $"r={gai}");
        if (info != 0)
        {
            long sa = Int(info + 24);
            Check("addrinfo: family, socktype, addrlen, sockaddr", Int(info + 4) == 2 && Int(info + 8) == 1 && Int(info + 16) == 16 &&
                h.U16(sa) == 2 && PortOf(sa) == 80 && Int(sa + 4) == loopback, $"addr 0x{Int(sa + 4):X}");
            Call("freeaddrinfo", info);
        }

        e.WriteUInt32(hints, 1);   // AI_PASSIVE
        e.WriteUInt32(hints + 4, 2);
        Check("getaddrinfo(NULL, http, PASSIVE) is INADDR_ANY:80",
            Call("getaddrinfo", 0, h.Narrow("http"), hints, result) == 0 && Int(Int(Int(result) + 24) + 4) == 0 && PortOf(Int(Int(result) + 24)) == 80);
        Call("freeaddrinfo", Int(result));
        e.WriteUInt32(hints, 0);
        Check("getaddrinfo(NULL, NULL) is WSAHOST_NOT_FOUND", Call("getaddrinfo", 0, 0, hints, result) == 11001 && Int(result) == 0);
        e.WriteUInt32(hints, 4);   // AI_NUMERICHOST
        Check("AI_NUMERICHOST with a name is WSAHOST_NOT_FOUND", Call("getaddrinfo", h.Narrow("localhost"), 0, hints, result) == 11001);
        Check("...and a literal is returned as is", Call("getaddrinfo", h.Narrow("10.1.2.3"), 0, hints, result) == 0 &&
            Int(Int(Int(result) + 24) + 4) == 0x0302010A);
        Call("freeaddrinfo", Int(result));
        e.WriteUInt32(hints, 0);
        e.WriteUInt32(hints + 4, 77);
        Check("a bad family is WSAEAFNOSUPPORT", Call("getaddrinfo", h.Narrow("localhost"), 0, hints, result) == 10047 && LastError() == 10047);
        e.WriteUInt32(hints + 4, 2);
        Check("an unknown service is WSATYPE_NOT_FOUND", Call("getaddrinfo", h.Narrow("localhost"), h.Narrow("no-such-service"), hints, result) == 10109);
        Check("GetAddrInfoW(localhost)", Call("GetAddrInfoW", h.Wide("localhost"), h.Wide("443"), hints, result) == 0 && PortOf(Int(Int(result) + 24)) == 443);
        Call("FreeAddrInfoW", Int(result));

        long hostent = Ordinal(52, h.Narrow("localhost"));
        Check("gethostbyname(localhost) (#52)", hostent != 0 && h.U16(hostent + 8) == 2 && h.U16(hostent + 10) == 4 &&
            Int(Int(Int(hostent + 12))) == loopback, hostent == 0 ? $"err={LastError()}" : "");
        hostent = Call("gethostbyname", h.Narrow("192.168.7.9"));
        Check("gethostbyname of a literal needs no lookup", hostent != 0 && Int(Int(Int(hostent + 12))) == 0x0907A8C0 && h.ReadNarrow(Int(hostent)) == "192.168.7.9");
        long hostName = h.Buffer(256);
        Check("gethostname (#57)", Ordinal(57, hostName, 256) == 0 && h.ReadNarrow(hostName).Length > 0, h.ReadNarrow(hostName));
        Check("gethostname into too small a buffer is WSAEFAULT", Call("gethostname", hostName, 1) == SocketError && LastError() == 10014);
        long proto = Ordinal(53, h.Narrow("tcp"));
        Check("getprotobyname(tcp) (#53)", proto != 0 && h.U16(proto + 8) == 6 && h.ReadNarrow(Int(proto)) == "tcp");
        Check("getprotobyname(nonsense) is NULL with WSANO_DATA", Call("getprotobyname", h.Narrow("nonsense")) == 0 && LastError() == 11004);
        long serv = Ordinal(55, h.Narrow("http"), h.Narrow("tcp"));
        Check("getservbyname(http, tcp) (#55)", serv != 0 && h.U16(serv + 8) == 0x5000 && h.ReadNarrow(Int(serv + 12)) == "tcp");
        Check("getservbyport(htons(443))", Call("getservbyport", 0xBB01, 0) is var sp && sp != 0 && h.ReadNarrow(Int(sp)) == "https");

        long hostOut = h.Buffer(64), servOut = h.Buffer(16);
        long sa80 = SockAddrIn(loopback, 80);
        Check("getnameinfo NUMERICHOST|NUMERICSERV", Call("getnameinfo", sa80, 16, hostOut, 64, servOut, 16, 2 | 8) == 0 &&
            h.ReadNarrow(hostOut) == "127.0.0.1" && h.ReadNarrow(servOut) == "80");
        Check("getnameinfo names the service", Call("getnameinfo", sa80, 16, hostOut, 64, servOut, 16, 2) == 0 && h.ReadNarrow(servOut) == "http");
        Check("getnameinfo into a short buffer is WSAEFAULT", Call("getnameinfo", sa80, 16, hostOut, 4, servOut, 16, 2) == 10014);
        long addressText = h.Buffer(128), addressLength = IntBox(64);
        Check("WSAAddressToStringW", Call("WSAAddressToStringW", sa80, 16, 0, addressText, addressLength) == 0 &&
            h.ReadWide(addressText) == "127.0.0.1:80" && Int(addressLength) == 13);
        e.WriteUInt32(addressLength, 4);
        Check("WSAAddressToStringW reports the length it needs", Call("WSAAddressToStringW", sa80, 16, 0, addressText, addressLength) == SocketError &&
            LastError() == 10014 && Int(addressLength) == 13);

        // ---- WSAIoctl ----
        long t = Call("socket", 2, 1, 0);
        long returned = h.Buffer(4);
        Check("WSAIoctl FIONBIO", Call("WSAIoctl", t, 0x8004667E, IntBox(1), 4, 0, 0, returned, 0, 0) == 0);
        Check("WSAIoctl with an unsupported code is WSAEOPNOTSUPP", Call("WSAIoctl", t, 0xC8000006, h.Buffer(16), 16, h.Buffer(4), 4, returned, 0, 0) == SocketError &&
            LastError() == 10045);
        Call("closesocket", t);

        // ---- the census sees the ordinals ----
        Check("IsImplemented(WS2_32, #23)", s.IsImplemented("WS2_32.dll", "#23"));
        Check("IsImplemented(WS2_32, #151)", s.IsImplemented("WS2_32.dll", "#151"));
        Check("an XAudio2 ordinal is not mistaken for Winsock", !s.IsImplemented("XAudio2_8.dll", "#5"));

        Check("WSACleanup", Call("WSACleanup") == 0);
        Check("WSACleanup once too often is WSANOTINITIALISED", Call("WSACleanup") == SocketError && LastError() == 10093);
    }
}
