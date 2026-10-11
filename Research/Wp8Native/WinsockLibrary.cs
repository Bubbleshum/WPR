using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnicornEngine.Const;

namespace WPR.Wp8Native
{
    /// <summary>
    /// WS2_32 on Windows Phone 8: real sockets over <see cref="System.Net.Sockets.Socket"/>, to the
    /// documented Winsock 2 contract.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These used to be answered as "the network is down" (socket/connect/send/... all
    /// SOCKET_ERROR, WSAGetLastError WSAENETDOWN). Whether a game should reach the network at all is
    /// a product decision; this library only makes the API tell the truth. Every connect target, UDP
    /// destination and name lookup is written to <see cref="Log"/> so it shows in diagnostics.
    /// </para>
    /// <para>
    /// <b>Blocking.</b> Every host socket is non-blocking; a guest socket in blocking mode blocks
    /// the calling GUEST thread (<see cref="ArmEmulator.BlockCurrentThread"/>) with a readiness
    /// predicate that polls the host socket, so other guest threads run meanwhile. When no other
    /// guest thread can run, the host thread waits - but never for ever: at most the socket's own
    /// timeout (SO_RCVTIMEO / SO_SNDTIMEO, select's timeval) or <see cref="HostWaitCap"/>, after
    /// which the call fails with WSAETIMEDOUT (select returns 0). A real phone would wait longer in
    /// that one case; the cap is what keeps the frame loop alive.
    /// </para>
    /// <para>
    /// <b>Layouts (ARM32).</b> fd_set { u_int fd_count; SOCKET fd_array[64]; }, timeval { long; long; },
    /// addrinfo { int flags, family, socktype, protocol; size_t addrlen; char* canonname; sockaddr* addr;
    /// addrinfo* next; } (32 bytes), hostent { char* name; char** aliases; short addrtype, length;
    /// char** addr_list; } (16), WSADATA (400), WSABUF { u_long len; char* buf; }, WSAPOLLFD
    /// { SOCKET; short events, revents; }, OVERLAPPED { Internal, InternalHigh, Offset, OffsetHigh, hEvent }.
    /// </para>
    /// <para>
    /// WSAGetLastError/WSASetLastError are GetLastError/SetLastError, as on Windows: the error is
    /// handed to whatever implements those, so the two can never disagree.
    /// </para>
    /// </remarks>
    public sealed class WinsockLibrary
    {
        // ---- error codes (winerror.h) ----
        public const int WSAEINTR = 10004, WSAEBADF = 10009, WSAEFAULT = 10014, WSAEINVAL = 10022, WSAEMFILE = 10024,
            WSAEWOULDBLOCK = 10035, WSAEINPROGRESS = 10036, WSAEALREADY = 10037, WSAENOTSOCK = 10038,
            WSAEDESTADDRREQ = 10039, WSAEMSGSIZE = 10040, WSAEPROTOTYPE = 10041, WSAENOPROTOOPT = 10042,
            WSAEPROTONOSUPPORT = 10043, WSAESOCKTNOSUPPORT = 10044, WSAEOPNOTSUPP = 10045, WSAEAFNOSUPPORT = 10047,
            WSAEADDRINUSE = 10048, WSAEADDRNOTAVAIL = 10049, WSAENETDOWN = 10050, WSAECONNRESET = 10054,
            WSAEISCONN = 10056, WSAENOTCONN = 10057, WSAESHUTDOWN = 10058, WSAETIMEDOUT = 10060,
            WSAECONNREFUSED = 10061, WSAVERNOTSUPPORTED = 10092, WSANOTINITIALISED = 10093,
            WSATYPE_NOT_FOUND = 10109, WSAHOST_NOT_FOUND = 11001, WSATRY_AGAIN = 11002, WSANO_RECOVERY = 11003,
            WSANO_DATA = 11004, WSA_INVALID_HANDLE = 6, WSA_INVALID_PARAMETER = 87, WSA_IO_INCOMPLETE = 996,
            WSA_IO_PENDING = 997, WSA_OPERATION_ABORTED = 995;

        private const uint SocketError = 0xFFFFFFFF;
        private const uint InvalidSocket = 0xFFFFFFFF;
        private const int AfUnspec = 0, AfInet = 2, AfInet6 = 23;
        private const int SockStream = 1, SockDgram = 2;
        private const int IpProtoTcp = 6, IpProtoUdp = 17;
        private const int MsgOob = 1, MsgPeek = 2, MsgDontRoute = 4, MsgWaitAll = 8;
        private const int FdSetSize = 64;

        // Network events (FD_*), and their bit index in WSANETWORKEVENTS.iErrorCode.
        public const int FdRead = 0x01, FdWrite = 0x02, FdOob = 0x04, FdAccept = 0x08, FdConnect = 0x10, FdClose = 0x20;

        /// <summary>The longest the host thread waits when no other guest thread could run.</summary>
        public static readonly TimeSpan HostWaitCap = TimeSpan.FromSeconds(10);

        /// <summary>
        /// WS2_32's ordinal-only exports: the import table names these by number. Only ordinals this
        /// library implements are listed, so anything else still reaches the "defaulted" record.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> OrdinalNames = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["#1"] = "accept", ["#2"] = "bind", ["#3"] = "closesocket", ["#4"] = "connect", ["#5"] = "getpeername",
            ["#6"] = "getsockname", ["#7"] = "getsockopt", ["#8"] = "htonl", ["#9"] = "htons", ["#10"] = "ioctlsocket",
            ["#11"] = "inet_addr", ["#12"] = "inet_ntoa", ["#13"] = "listen", ["#14"] = "ntohl", ["#15"] = "ntohs",
            ["#16"] = "recv", ["#17"] = "recvfrom", ["#18"] = "select", ["#19"] = "send", ["#20"] = "sendto",
            ["#21"] = "setsockopt", ["#22"] = "shutdown", ["#23"] = "socket",
            ["#51"] = "gethostbyaddr", ["#52"] = "gethostbyname", ["#53"] = "getprotobyname", ["#54"] = "getprotobynumber",
            ["#55"] = "getservbyname", ["#56"] = "getservbyport", ["#57"] = "gethostname",
            ["#111"] = "WSAGetLastError", ["#112"] = "WSASetLastError", ["#115"] = "WSAStartup", ["#116"] = "WSACleanup",
            ["#151"] = "__WSAFDIsSet",
        };

        private readonly ArmEmulator _emulator;
        private readonly CallFrame _frame;
        private readonly Func<SyncLibrary> _sync;
        private readonly Dictionary<string, Action> _handlers;
        private readonly Dictionary<uint, GuestSocket> _sockets = new();
        private readonly Dictionary<(int Thread, int Kind), long> _threadBuffers = new();
        private readonly Dictionary<long, Overlapped> _overlapped = new();
        private readonly HashSet<string> _logged = new();
        private readonly List<string> _log = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private uint _nextSocket = 0x100;
        private int _startups;
        private int _fallbackLastError;

        public WinsockLibrary(ArmEmulator emulator, CallFrame frame, Func<SyncLibrary> sync, Dictionary<string, Action> handlers)
        {
            _emulator = emulator;
            _frame = frame;
            _sync = sync;
            _handlers = handlers;
        }

        /// <summary>Every connect target, UDP destination and name lookup, in order, capped.</summary>
        public IReadOnlyList<string> Log => _log;

        /// <summary>Sockets currently open.</summary>
        public int OpenSockets => _sockets.Count;

        private sealed class GuestSocket(Socket host, int family, int type, int protocol)
        {
            public Socket Host { get; } = host;
            public int Family { get; } = family;
            public int Type { get; } = type;
            public int Protocol { get; } = protocol;
            public bool NonBlocking { get; set; }
            public bool Listening { get; set; }
            public ConnectState State { get; set; }
            public int ConnectError { get; set; }
            public int PendingSoError { get; set; }
            public EndPoint? Peer { get; set; }
            public bool ShutRecv { get; set; }
            public bool ShutSend { get; set; }
            public int RecvTimeoutMs { get; set; }
            public int SendTimeoutMs { get; set; }
            public bool Debug { get; set; }

            // WSAEventSelect.
            public long Event { get; set; }
            public int EventMask { get; set; }
            public int Recorded { get; set; }
            public int[] Errors { get; } = new int[10];
            public bool ReadArmed { get; set; } = true;
            public bool AcceptArmed { get; set; } = true;
            public bool WriteArmed { get; set; } = true;
            public bool ConnectArmed { get; set; } = true;
            public bool CloseArmed { get; set; } = true;
        }

        private enum ConnectState { None, Pending, Connected, Failed }

        private sealed class Overlapped
        {
            public uint Socket;
            public bool Done;
            public int Error;
            public int Bytes;
            public long Event;
            public Func<bool> Ready = () => true;
            public Func<(int Error, int Bytes)> Perform = () => (0, 0);
        }

        public void RegisterInto(Dictionary<string, Action> handlers)
        {
            handlers["WSAStartup"] = Startup;
            handlers["WSACleanup"] = Cleanup;
            handlers["WSAGetLastError"] = () =>
            {
                if (_handlers.TryGetValue("GetLastError", out Action? get)) get();
                else _frame.Return(_fallbackLastError);
            };
            handlers["WSASetLastError"] = () => { SetLastError(_frame.SignedArg(0)); _frame.Return(0); };

            handlers["socket"] = () => CreateSocket(_frame.SignedArg(0), _frame.SignedArg(1), _frame.SignedArg(2));
            // SOCKET WSASocketW(af, type, protocol, LPWSAPROTOCOL_INFOW, GROUP, DWORD flags): the protocol-info
            // and group arguments select among providers; there is one provider per (af, type) here.
            handlers["WSASocketW"] = () => CreateSocket(_frame.SignedArg(0), _frame.SignedArg(1), _frame.SignedArg(2));
            handlers["WSASocketA"] = handlers["WSASocketW"];
            handlers["closesocket"] = CloseSocket;
            handlers["bind"] = Bind;
            handlers["listen"] = Listen;
            handlers["accept"] = Accept;
            handlers["connect"] = Connect;
            handlers["send"] = () => Send(Handle(0), _frame.Arg(1), _frame.SignedArg(2), _frame.SignedArg(3), 0, 0);
            handlers["sendto"] = () => Send(Handle(0), _frame.Arg(1), _frame.SignedArg(2), _frame.SignedArg(3), _frame.Arg(4), _frame.SignedArg(5));
            handlers["recv"] = () => Receive(Handle(0), _frame.Arg(1), _frame.SignedArg(2), _frame.SignedArg(3), 0, 0);
            handlers["recvfrom"] = () => Receive(Handle(0), _frame.Arg(1), _frame.SignedArg(2), _frame.SignedArg(3), _frame.Arg(4), _frame.Arg(5));
            handlers["select"] = Select;
            handlers["__WSAFDIsSet"] = () => _frame.Return(FdSetContains(_frame.Arg(1), Handle(0)) ? 1 : 0);
            handlers["ioctlsocket"] = () => IoctlSocket(Handle(0), _frame.Arg(1), _frame.Arg(2));
            handlers["setsockopt"] = SetSockOpt;
            handlers["getsockopt"] = GetSockOpt;
            handlers["shutdown"] = Shutdown;
            handlers["getsockname"] = () => SocketName(peer: false);
            handlers["getpeername"] = () => SocketName(peer: true);

            handlers["htonl"] = () => _frame.Return(Swap32((uint)_frame.Arg(0)));
            handlers["ntohl"] = handlers["htonl"];
            handlers["htons"] = () => _frame.Return(Swap16((ushort)_frame.Arg(0)));
            handlers["ntohs"] = handlers["htons"];
            handlers["inet_addr"] = () => _frame.Return(InetAddr(_frame.ReadNarrowString(_frame.Arg(0), 256)));
            handlers["inet_ntoa"] = InetNtoa;
            handlers["inet_pton"] = () => InetPton(_frame.SignedArg(0), _frame.Arg(1) == 0 ? null : _frame.ReadNarrowString(_frame.Arg(1), 256), _frame.Arg(2));
            handlers["InetPtonW"] = () => InetPton(_frame.SignedArg(0), _frame.Arg(1) == 0 ? null : _emulator.ReadUtf16String(_frame.Arg(1), 256), _frame.Arg(2));
            handlers["inet_ntop"] = () => InetNtop(wide: false);
            handlers["InetNtopW"] = () => InetNtop(wide: true);

            handlers["gethostname"] = GetHostName;
            handlers["gethostbyname"] = GetHostByName;
            handlers["gethostbyaddr"] = GetHostByAddr;
            handlers["getprotobyname"] = () => ProtoEnt(Protocols.FirstOrDefault(p => p.Names.Contains(_frame.ReadNarrowString(_frame.Arg(0), 256), StringComparer.OrdinalIgnoreCase)));
            handlers["getprotobynumber"] = () => ProtoEnt(Protocols.FirstOrDefault(p => p.Number == _frame.SignedArg(0)));
            handlers["getservbyname"] = GetServByName;
            handlers["getservbyport"] = GetServByPort;
            handlers["getaddrinfo"] = () => GetAddrInfo(wide: false);
            handlers["GetAddrInfoW"] = () => GetAddrInfo(wide: true);
            handlers["freeaddrinfo"] = FreeAddrInfo;
            handlers["FreeAddrInfoW"] = FreeAddrInfo;
            handlers["getnameinfo"] = () => GetNameInfo(wide: false);
            handlers["GetNameInfoW"] = () => GetNameInfo(wide: true);
            handlers["WSAAddressToStringW"] = () => AddressToString(wide: true);
            handlers["WSAAddressToStringA"] = () => AddressToString(wide: false);

            handlers["WSAPoll"] = Poll;
            handlers["WSAIoctl"] = WsaIoctl;
            handlers["WSASend"] = () => WsaSend(to: false);
            handlers["WSASendTo"] = () => WsaSend(to: true);
            handlers["WSARecv"] = () => WsaRecv(from: false);
            handlers["WSARecvFrom"] = () => WsaRecv(from: true);
            handlers["WSAGetOverlappedResult"] = GetOverlappedResult;

            handlers["WSACreateEvent"] = () => _frame.Return(_sync().CreateEvent(manualReset: true, initiallySet: false));
            handlers["WSACloseEvent"] = () => WinBool(_sync().CloseEvent(_frame.Arg(0)), WSA_INVALID_HANDLE);
            handlers["WSASetEvent"] = () => WinBool(_sync().SetEvent(_frame.Arg(0)), WSA_INVALID_HANDLE);
            handlers["WSAResetEvent"] = () => WinBool(_sync().ResetEvent(_frame.Arg(0)), WSA_INVALID_HANDLE);
            handlers["WSAEventSelect"] = EventSelect;
            handlers["WSAEnumNetworkEvents"] = EnumNetworkEvents;
            handlers["WSAWaitForMultipleEvents"] = WaitForMultipleEvents;
        }

        // ================================================================ plumbing

        private uint Handle(int argument) => (uint)_frame.Arg(argument);

        private long NowMs => _clock.ElapsedMilliseconds;

        private void Note(string line)
        {
            if (_log.Count < 1000 && _logged.Add(line))
            {
                _log.Add(line);
            }
        }

        /// <summary>SetLastError, through whatever implements it, without disturbing r0-r3.</summary>
        private void SetLastError(int code)
        {
            if (_handlers.TryGetValue("SetLastError", out Action? set))
            {
                long[] saved = SaveArgs();
                _emulator.WriteRegister(Arm.UC_ARM_REG_R0, code);
                set();
                RestoreArgs(saved);
            }

            _fallbackLastError = code;
        }

        private long[] SaveArgs() =>
        [
            _emulator.ReadRegister(Arm.UC_ARM_REG_R0), _emulator.ReadRegister(Arm.UC_ARM_REG_R1),
            _emulator.ReadRegister(Arm.UC_ARM_REG_R2), _emulator.ReadRegister(Arm.UC_ARM_REG_R3),
        ];

        private void RestoreArgs(long[] saved)
        {
            _emulator.WriteRegister(Arm.UC_ARM_REG_R0, saved[0]);
            _emulator.WriteRegister(Arm.UC_ARM_REG_R1, saved[1]);
            _emulator.WriteRegister(Arm.UC_ARM_REG_R2, saved[2]);
            _emulator.WriteRegister(Arm.UC_ARM_REG_R3, saved[3]);
        }

        /// <summary>Sets the last error and returns SOCKET_ERROR (which is also INVALID_SOCKET).</summary>
        private void Fail(int code)
        {
            SetLastError(code);
            _frame.Return(SocketError);
        }

        private void WinBool(bool ok, int error)
        {
            if (!ok) SetLastError(error);
            _frame.Return(ok ? 1 : 0);
        }

        private static int ErrorOf(SocketException ex) => ex.SocketErrorCode switch
        {
            System.Net.Sockets.SocketError.SocketError => WSAEINVAL,
            System.Net.Sockets.SocketError.Success => WSAEINVAL,
            var code => (int)code,
        };

        private bool Started()
        {
            if (_startups > 0) return true;
            Fail(WSANOTINITIALISED);
            return false;
        }

        /// <summary>The socket for a handle, failing the call with WSAENOTSOCK (or WSANOTINITIALISED) if none.</summary>
        private GuestSocket? Lookup(uint handle)
        {
            if (!Started()) return null;
            if (_sockets.TryGetValue(handle, out GuestSocket? s)) return s;
            Fail(WSAENOTSOCK);
            return null;
        }

        /// <summary>
        /// Blocks the calling guest thread until <paramref name="ready"/>, then runs
        /// <paramref name="complete"/>; on timeout runs <paramref name="timedOut"/>. Arguments must
        /// already be captured: the closures run after the thread is switched back in.
        /// </summary>
        private void Wait(Func<bool> ready, Action complete, Action timedOut, long? timeoutMs, string why)
        {
            long deadline = timeoutMs is { } t ? NowMs + Math.Max(0, t) : long.MaxValue;
            bool resumed = false;
            void Resume()
            {
                resumed = true;
                if (ready()) complete();
                else timedOut();
            }

            _frame.Return(SocketError);
            if (_emulator.BlockCurrentThread(() => ready() || NowMs >= deadline, Resume, why))
            {
                return;
            }

            if (resumed)
            {
                return;
            }

            // Nothing else can run. Wait on the host, bounded - the guest must never hang the host.
            long hostDeadline = Math.Min(deadline, NowMs + (long)HostWaitCap.TotalMilliseconds);
            while (!ready() && NowMs < hostDeadline)
            {
                System.Threading.Thread.Sleep(1);
            }

            Resume();
        }

        /// <summary>Completes now if ready, fails WSAEWOULDBLOCK on a non-blocking socket, else blocks.</summary>
        private void BlockOn(GuestSocket s, Func<bool> ready, Action complete, int timeoutMs, string why)
        {
            if (ready())
            {
                complete();
                return;
            }

            if (s.NonBlocking)
            {
                Fail(WSAEWOULDBLOCK);
                return;
            }

            Wait(ready, complete, () => Fail(WSAETIMEDOUT), timeoutMs > 0 ? timeoutMs : null, why);
        }

        private long ThreadBuffer(int kind, int size)
        {
            var key = (_emulator.CurrentThreadId, kind);
            if (!_threadBuffers.TryGetValue(key, out long buffer))
            {
                buffer = _emulator.AllocateHeap(size);
                _threadBuffers[key] = buffer;
            }

            _emulator.WriteMemory(buffer, new byte[size]);
            return buffer;
        }

        private static uint Swap32(uint v) => (v >> 24) | ((v >> 8) & 0xFF00) | ((v << 8) & 0xFF0000) | (v << 24);

        private static uint Swap16(ushort v) => (uint)(((v >> 8) & 0xFF) | ((v & 0xFF) << 8));

        // ================================================================ sockaddr

        private static int SockAddrLength(int family) => family == AfInet6 ? 28 : 16;

        /// <summary>Reads a sockaddr, or returns a WSA error.</summary>
        private int ReadSockAddr(long address, int length, int expectedFamily, out IPEndPoint endpoint)
        {
            endpoint = new IPEndPoint(IPAddress.Any, 0);
            if (address == 0 || length < 2) return WSAEFAULT;
            byte[] head = _emulator.ReadMemory(address, 2);
            int family = head[0] | (head[1] << 8);
            if (family != AfInet && family != AfInet6) return WSAEAFNOSUPPORT;
            if (expectedFamily != AfUnspec && family != expectedFamily) return WSAEAFNOSUPPORT;
            if (length < SockAddrLength(family)) return WSAEFAULT;
            byte[] raw = _emulator.ReadMemory(address, SockAddrLength(family));
            int port = (raw[2] << 8) | raw[3];
            if (family == AfInet)
            {
                endpoint = new IPEndPoint(new IPAddress(raw.AsSpan(4, 4)), port);
            }
            else
            {
                uint scope = BitConverter.ToUInt32(raw, 24);
                endpoint = new IPEndPoint(new IPAddress(raw.AsSpan(8, 16), scope), port);
            }

            return 0;
        }

        private static byte[] EncodeSockAddr(IPEndPoint endpoint)
        {
            bool v6 = endpoint.AddressFamily == AddressFamily.InterNetworkV6;
            byte[] raw = new byte[v6 ? 28 : 16];
            raw[0] = (byte)(v6 ? AfInet6 : AfInet);
            raw[2] = (byte)(endpoint.Port >> 8);
            raw[3] = (byte)endpoint.Port;
            byte[] address = endpoint.Address.GetAddressBytes();
            if (v6)
            {
                address.CopyTo(raw, 8);
                BitConverter.GetBytes((uint)endpoint.Address.ScopeId).CopyTo(raw, 24);
            }
            else
            {
                address.CopyTo(raw, 4);
            }

            return raw;
        }

        /// <summary>Writes a sockaddr through (address, int* length); a WSA error if it does not fit.</summary>
        private int WriteSockAddr(IPEndPoint endpoint, long address, long lengthPointer)
        {
            byte[] raw = EncodeSockAddr(endpoint);
            if (lengthPointer == 0) return WSAEFAULT;
            int room = unchecked((int)_emulator.ReadUInt32(lengthPointer));
            if (room < raw.Length) return WSAEFAULT;
            _emulator.WriteMemory(address, raw);
            _emulator.WriteUInt32(lengthPointer, (uint)raw.Length);
            return 0;
        }

        // ================================================================ start-up

        /// <summary>int WSAStartup(WORD wVersionRequested, LPWSADATA): the error is returned, not set.</summary>
        private void Startup()
        {
            int requested = (int)(_frame.Arg(0) & 0xFFFF);
            long data = _frame.Arg(1);
            int major = requested & 0xFF, minor = requested >> 8;
            const int highest = 0x0202;

            if (data == 0)
            {
                _frame.Return(WSAEFAULT);
                return;
            }

            int version = major >= 3 || (major == 2 && minor > 2) ? highest
                : major == 1 && minor > 1 ? 0x0101
                : requested;

            // WSADATA on 32-bit: wVersion, wHighVersion, szDescription[257], szSystemStatus[129],
            // iMaxSockets, iMaxUdpDg, lpVendorInfo (at 396).
            byte[] block = new byte[400];
            BitConverter.GetBytes((ushort)version).CopyTo(block, 0);
            BitConverter.GetBytes((ushort)highest).CopyTo(block, 2);
            Encoding.ASCII.GetBytes("WinSock 2.0").CopyTo(block, 4);
            Encoding.ASCII.GetBytes("Running").CopyTo(block, 261);
            _emulator.WriteMemory(data, block);

            if (major == 0)
            {
                _frame.Return(WSAVERNOTSUPPORTED);
                return;
            }

            _startups++;
            _frame.Return(0);
        }

        private void Cleanup()
        {
            if (_startups == 0)
            {
                Fail(WSANOTINITIALISED);
                return;
            }

            if (--_startups == 0)
            {
                foreach (uint handle in _sockets.Keys.ToList())
                {
                    Close(handle);
                }
            }

            _frame.Return(0);
        }

        // ================================================================ socket lifetime

        private void CreateSocket(int family, int type, int protocol)
        {
            if (!Started()) return;
            if (family == AfUnspec)
            {
                // "When af is AF_UNSPEC, the protocol must be specified" - and then it picks IPv4.
                if (protocol is not (IpProtoTcp or IpProtoUdp))
                {
                    Fail(WSAEAFNOSUPPORT);
                    return;
                }

                family = AfInet;
            }

            if (family is not (AfInet or AfInet6))
            {
                Fail(WSAEAFNOSUPPORT);
                return;
            }

            if (type is not (SockStream or SockDgram))
            {
                Fail(WSAESOCKTNOSUPPORT);
                return;
            }

            if (protocol == 0)
            {
                protocol = type == SockStream ? IpProtoTcp : IpProtoUdp;
            }
            else if (protocol is IpProtoTcp or IpProtoUdp)
            {
                if ((type == SockStream) != (protocol == IpProtoTcp))
                {
                    Fail(WSAEPROTOTYPE);
                    return;
                }
            }
            else
            {
                Fail(WSAEPROTONOSUPPORT);
                return;
            }

            Socket host;
            try
            {
                host = new Socket(
                    family == AfInet6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork,
                    type == SockStream ? SocketType.Stream : SocketType.Dgram,
                    protocol == IpProtoTcp ? ProtocolType.Tcp : ProtocolType.Udp)
                {
                    Blocking = false,
                };
                if (family == AfInet6)
                {
                    // Windows' default; Linux (an Android host) defaults the other way.
                    host.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, true);
                }
            }
            catch (SocketException ex)
            {
                Fail(ErrorOf(ex));
                return;
            }

            _frame.Return(Register(new GuestSocket(host, family, type, protocol)));
        }

        private uint Register(GuestSocket socket)
        {
            uint handle = _nextSocket;
            _nextSocket += 4;
            _sockets[handle] = socket;
            return handle;
        }

        private void CloseSocket()
        {
            if (Lookup(Handle(0)) is null) return;
            Close(Handle(0));
            _frame.Return(0);
        }

        private void Close(uint handle)
        {
            if (!_sockets.Remove(handle, out GuestSocket? s)) return;
            foreach ((long address, Overlapped o) in _overlapped)
            {
                if (o.Socket == handle && !o.Done)
                {
                    o.Done = true;
                    o.Error = WSA_OPERATION_ABORTED;
                    WriteOverlapped(address, o);
                }
            }

            try
            {
                s.Host.Dispose();
            }
            catch (Exception)
            {
            }
        }

        private void Bind()
        {
            if (Lookup(Handle(0)) is not { } s) return;
            int error = ReadSockAddr(_frame.Arg(1), _frame.SignedArg(2), s.Family, out IPEndPoint endpoint);
            if (error != 0)
            {
                Fail(error);
                return;
            }

            if (s.Host.IsBound)
            {
                Fail(WSAEINVAL);
                return;
            }

            try
            {
                s.Host.Bind(endpoint);
                _frame.Return(0);
            }
            catch (SocketException ex)
            {
                Fail(ErrorOf(ex));
            }
        }

        private void Listen()
        {
            if (Lookup(Handle(0)) is not { } s) return;
            if (s.Type != SockStream)
            {
                Fail(WSAEOPNOTSUPP);
                return;
            }

            if (s.State is ConnectState.Connected or ConnectState.Pending)
            {
                Fail(WSAEISCONN);
                return;
            }

            if (!s.Host.IsBound)
            {
                Fail(WSAEINVAL);
                return;
            }

            try
            {
                s.Host.Listen(Math.Max(1, _frame.SignedArg(1)));
                s.Listening = true;
                _frame.Return(0);
            }
            catch (SocketException ex)
            {
                Fail(ErrorOf(ex));
            }
        }

        private void Accept()
        {
            uint handle = Handle(0);
            long address = _frame.Arg(1), lengthPointer = _frame.Arg(2);
            if (Lookup(handle) is not { } s) return;
            if (!s.Listening)
            {
                Fail(WSAEINVAL);
                return;
            }

            if (address != 0 && (lengthPointer == 0 || unchecked((int)_emulator.ReadUInt32(lengthPointer)) < SockAddrLength(s.Family)))
            {
                Fail(WSAEFAULT);
                return;
            }

            BlockOn(s, () => HostPoll(s, SelectMode.SelectRead), () =>
            {
                Socket accepted;
                try
                {
                    accepted = s.Host.Accept();
                }
                catch (SocketException ex)
                {
                    Fail(ErrorOf(ex));
                    return;
                }
                catch (ObjectDisposedException)
                {
                    Fail(WSAEINTR);
                    return;
                }

                s.AcceptArmed = true;
                accepted.Blocking = false;
                // "The newly created socket has the same properties as s", event selection included.
                GuestSocket child = new(accepted, s.Family, s.Type, s.Protocol)
                {
                    NonBlocking = s.NonBlocking,
                    State = ConnectState.Connected,
                    Peer = accepted.RemoteEndPoint,
                    Event = s.Event,
                    EventMask = s.EventMask,
                    RecvTimeoutMs = s.RecvTimeoutMs,
                    SendTimeoutMs = s.SendTimeoutMs,
                };
                if (address != 0 && accepted.RemoteEndPoint is IPEndPoint remote)
                {
                    WriteSockAddr(remote, address, lengthPointer);
                }

                _frame.Return(Register(child));
            }, 0, $"accept on socket 0x{handle:X}");
        }

        // ================================================================ connect

        /// <summary>Moves a pending non-blocking connect to Connected or Failed once the host says so.</summary>
        private void UpdateConnect(GuestSocket s)
        {
            if (s.State != ConnectState.Pending) return;
            bool writable, errored;
            try
            {
                writable = s.Host.Poll(0, SelectMode.SelectWrite);
                errored = s.Host.Poll(0, SelectMode.SelectError);
            }
            catch (Exception)
            {
                writable = false;
                errored = true;
            }

            if (!writable && !errored) return;
            int error = 0;
            try
            {
                error = (int)s.Host.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error)!;
            }
            catch (SocketException ex)
            {
                error = ErrorOf(ex);
            }

            if (error == 0 && !writable) error = WSAECONNREFUSED;
            if (error != 0)
            {
                s.State = ConnectState.Failed;
                s.ConnectError = error;
                s.PendingSoError = error;
            }
            else
            {
                s.State = ConnectState.Connected;
            }

            Record(s, FdConnect, error);
        }

        private void Connect()
        {
            uint handle = Handle(0);
            if (Lookup(handle) is not { } s) return;
            int error = ReadSockAddr(_frame.Arg(1), _frame.SignedArg(2), s.Family, out IPEndPoint endpoint);
            if (error != 0)
            {
                Fail(error);
                return;
            }

            if (s.Listening)
            {
                Fail(WSAEINVAL);
                return;
            }

            bool any = endpoint.Address.Equals(IPAddress.Any) || endpoint.Address.Equals(IPAddress.IPv6Any);
            if (s.Type == SockDgram)
            {
                // A datagram "connect" only sets the default peer; INADDR_ANY with port 0 dissolves it.
                Note($"connect udp {endpoint}");
                if (any && endpoint.Port == 0)
                {
                    s.Peer = null;
                    _frame.Return(0);
                    return;
                }

                try
                {
                    s.Host.Connect(endpoint);
                    s.Peer = endpoint;
                    _frame.Return(0);
                }
                catch (SocketException ex)
                {
                    Fail(ErrorOf(ex));
                }

                return;
            }

            UpdateConnect(s);
            switch (s.State)
            {
                case ConnectState.Connected:
                    Fail(WSAEISCONN);
                    return;
                case ConnectState.Pending:
                    Fail(s.NonBlocking ? WSAEALREADY : WSAEINVAL);
                    return;
                case ConnectState.Failed:
                    s.State = ConnectState.None;
                    break;
            }

            if (any || endpoint.Port == 0)
            {
                Fail(WSAEADDRNOTAVAIL);
                return;
            }

            Note($"connect tcp {endpoint}");
            try
            {
                s.Host.Connect(endpoint);
                s.State = ConnectState.Connected;
                s.Peer = endpoint;
                Record(s, FdConnect, 0);
                _frame.Return(0);
                return;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is System.Net.Sockets.SocketError.WouldBlock or System.Net.Sockets.SocketError.InProgress)
            {
                s.State = ConnectState.Pending;
                s.Peer = endpoint;
                s.ConnectArmed = true;
            }
            catch (SocketException ex)
            {
                Fail(ErrorOf(ex));
                return;
            }

            if (s.NonBlocking)
            {
                Fail(WSAEWOULDBLOCK);
                return;
            }

            Wait(
                () =>
                {
                    UpdateConnect(s);
                    return s.State != ConnectState.Pending;
                },
                () =>
                {
                    if (s.State == ConnectState.Connected)
                    {
                        _frame.Return(0);
                    }
                    else
                    {
                        s.PendingSoError = 0;
                        Fail(s.ConnectError);
                    }
                },
                () => Fail(WSAETIMEDOUT),
                null,
                $"connect to {endpoint}");
        }

        // ================================================================ send / receive

        private static SocketFlags ToFlags(int flags) =>
            ((flags & MsgOob) != 0 ? SocketFlags.OutOfBand : 0) |
            ((flags & MsgPeek) != 0 ? SocketFlags.Peek : 0) |
            ((flags & MsgDontRoute) != 0 ? SocketFlags.DontRoute : 0);

        /// <summary>Checks a socket can send; on failure the call has already been failed.</summary>
        private bool CanSend(GuestSocket s, int flags, bool hasDestination)
        {
            if ((flags & ~(MsgOob | MsgDontRoute)) != 0 || (s.Type == SockDgram && (flags & MsgOob) != 0))
            {
                Fail(WSAEOPNOTSUPP);
                return false;
            }

            if (s.ShutSend)
            {
                Fail(WSAESHUTDOWN);
                return false;
            }

            if (s.Type == SockStream)
            {
                UpdateConnect(s);
                if (s.State != ConnectState.Connected)
                {
                    Fail(WSAENOTCONN);
                    return false;
                }
            }
            else if (!hasDestination && s.Peer is null)
            {
                Fail(WSAENOTCONN);
                return false;
            }

            return true;
        }

        /// <summary>send / sendto: <paramref name="to"/> 0 means "the connected peer".</summary>
        private void Send(uint handle, long buffer, int length, int flags, long to, int toLength)
        {
            if (Lookup(handle) is not { } s) return;
            if (length < 0)
            {
                Fail(WSAEFAULT);
                return;
            }

            IPEndPoint? destination = null;
            if (to != 0 && s.Type == SockDgram)
            {
                int error = ReadSockAddr(to, toLength, s.Family, out IPEndPoint endpoint);
                if (error != 0)
                {
                    Fail(error);
                    return;
                }

                if (endpoint.Port == 0)
                {
                    Fail(WSAEADDRNOTAVAIL);
                    return;
                }

                destination = endpoint;
            }

            if (!CanSend(s, flags, destination is not null)) return;
            byte[] data = length == 0 ? [] : _emulator.ReadMemory(buffer, length);
            SendBytes(s, handle, data, flags, destination, n => _frame.Return(n));
        }

        /// <summary>Sends everything (a blocking stream send transfers the whole buffer), then reports the count.</summary>
        private void SendBytes(GuestSocket s, uint handle, byte[] data, int flags, IPEndPoint? destination, Action<int> done)
        {
            if (s.Type == SockDgram)
            {
                if (data.Length > 65507)
                {
                    Fail(WSAEMSGSIZE);
                    return;
                }

                EndPoint target = destination ?? s.Peer!;
                Note($"sendto udp {target}");
                try
                {
                    int sent = destination is null
                        ? s.Host.Send(data, ToFlags(flags))
                        : s.Host.SendTo(data, ToFlags(flags), destination);
                    done(sent);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == System.Net.Sockets.SocketError.WouldBlock)
                {
                    s.WriteArmed = true;
                    Fail(WSAEWOULDBLOCK);
                }
                catch (SocketException ex)
                {
                    Fail(ErrorOf(ex));
                }

                return;
            }

            int offset = 0;
            void Step()
            {
                if (!_sockets.ContainsKey(handle))
                {
                    Fail(WSAEINTR);   // closed by another thread while this one was blocked in send
                    return;
                }

                int n = s.Host.Send(data, offset, data.Length - offset, ToFlags(flags), out System.Net.Sockets.SocketError error);
                if (error == System.Net.Sockets.SocketError.WouldBlock)
                {
                    n = 0;
                }
                else if (error != System.Net.Sockets.SocketError.Success)
                {
                    Fail(error == System.Net.Sockets.SocketError.SocketError ? WSAECONNRESET : (int)error);
                    return;
                }

                offset += n;
                if (offset >= data.Length || s.NonBlocking)
                {
                    if (offset == 0 && data.Length > 0)
                    {
                        s.WriteArmed = true;
                        Fail(WSAEWOULDBLOCK);
                        return;
                    }

                    done(offset);
                    return;
                }

                Wait(() => HostPoll(s, SelectMode.SelectWrite), Step, () => Fail(WSAETIMEDOUT),
                    s.SendTimeoutMs > 0 ? s.SendTimeoutMs : null, $"send on socket 0x{handle:X}");
            }

            if (data.Length == 0)
            {
                done(0);
                return;
            }

            BlockOn(s, () => HostPoll(s, SelectMode.SelectWrite), Step, s.SendTimeoutMs, $"send on socket 0x{handle:X}");
        }

        /// <summary>Checks a socket can receive; on failure the call has already been failed.</summary>
        private bool CanReceive(GuestSocket s, int flags)
        {
            if ((flags & ~(MsgOob | MsgPeek | MsgWaitAll)) != 0 ||
                ((flags & MsgWaitAll) != 0 && (s.Type == SockDgram || s.NonBlocking)) ||
                (s.Type == SockDgram && (flags & MsgOob) != 0))
            {
                Fail(WSAEOPNOTSUPP);
                return false;
            }

            if (s.ShutRecv)
            {
                Fail(WSAESHUTDOWN);
                return false;
            }

            if (s.Type == SockStream)
            {
                UpdateConnect(s);
                if (s.State != ConnectState.Connected)
                {
                    Fail(WSAENOTCONN);
                    return false;
                }
            }
            else if (!s.Host.IsBound)
            {
                Fail(WSAEINVAL);
                return false;
            }

            return true;
        }

        private static bool Readable(GuestSocket s) => HostPoll(s, SelectMode.SelectRead);

        /// <summary>Socket.Poll(0) that answers "ready" for a socket closed underneath a wait; the call then reports it.</summary>
        private static bool HostPoll(GuestSocket s, SelectMode mode)
        {
            try
            {
                return s.Host.Poll(0, mode);
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>
        /// Receives into a host buffer and reports (error, count, sender). A datagram longer than
        /// <paramref name="length"/> is truncated with WSAEMSGSIZE, as Winsock does.
        /// </summary>
        private (int Error, byte[] Data, IPEndPoint? From) ReceiveNow(GuestSocket s, int length, int flags)
        {
            SocketFlags socketFlags = ToFlags(flags);
            try
            {
                if (s.Type == SockDgram)
                {
                    byte[] whole = new byte[65536];
                    EndPoint from = new IPEndPoint(s.Family == AfInet6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
                    int n = s.Host.ReceiveFrom(whole, socketFlags, ref from);
                    s.ReadArmed = true;
                    return (n > length ? WSAEMSGSIZE : 0, whole[..Math.Min(n, length)], from as IPEndPoint);
                }

                byte[] buffer = new byte[length];
                int got = s.Host.Receive(buffer, 0, length, socketFlags, out System.Net.Sockets.SocketError error);
                s.ReadArmed = true;
                if (error == System.Net.Sockets.SocketError.WouldBlock) return (WSAEWOULDBLOCK, [], null);
                if (error != System.Net.Sockets.SocketError.Success) return ((int)error, [], null);
                return (0, buffer[..got], s.Peer as IPEndPoint);
            }
            catch (SocketException ex)
            {
                s.ReadArmed = true;
                return (ErrorOf(ex), [], null);
            }
            catch (ObjectDisposedException)
            {
                return (WSAEINTR, [], null);   // closed by another thread while this one was blocked
            }
        }

        /// <summary>recv / recvfrom: <paramref name="from"/> 0 means the plain recv.</summary>
        private void Receive(uint handle, long buffer, int length, int flags, long from, long fromLength)
        {
            if (Lookup(handle) is not { } s) return;
            if (length < 0)
            {
                Fail(WSAEFAULT);
                return;
            }

            if (s.Type == SockDgram && from != 0 &&
                (fromLength == 0 || unchecked((int)_emulator.ReadUInt32(fromLength)) < SockAddrLength(s.Family)))
            {
                Fail(WSAEFAULT);
                return;
            }

            if (!CanReceive(s, flags)) return;

            Func<bool> ready = (flags & MsgWaitAll) != 0
                ? () => s.Host.Available >= length || (Readable(s) && s.Host.Available == 0)
                : () => Readable(s);
            BlockOn(s, ready, () =>
            {
                (int error, byte[] data, IPEndPoint? sender) = ReceiveNow(s, length, flags);
                if (error is not (0 or WSAEMSGSIZE))
                {
                    Fail(error);
                    return;
                }

                _emulator.WriteMemory(buffer, data);
                if (s.Type == SockDgram && from != 0 && sender is not null)
                {
                    WriteSockAddr(sender, from, fromLength);
                }

                if (error != 0)
                {
                    Fail(error);
                    return;
                }

                _frame.Return(data.Length);
            }, s.RecvTimeoutMs, $"recv on socket 0x{handle:X}");
        }

        // ================================================================ select / poll

        private bool Writable(GuestSocket s)
        {
            UpdateConnect(s);
            if (s.Type == SockStream && s.State != ConnectState.Connected) return false;
            try
            {
                return s.Host.Poll(0, SelectMode.SelectWrite);
            }
            catch (Exception)
            {
                return true;
            }
        }

        private bool Excepted(GuestSocket s)
        {
            UpdateConnect(s);
            if (s.State == ConnectState.Failed) return true;
            if (s.State != ConnectState.Connected) return false;
            try
            {
                return s.Host.Poll(0, SelectMode.SelectError);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private uint[] ReadFdSet(long set)
        {
            if (set == 0) return [];
            int count = (int)Math.Min(_emulator.ReadUInt32(set), FdSetSize);
            uint[] handles = new uint[count];
            for (int i = 0; i < count; i++) handles[i] = _emulator.ReadUInt32(set + 4 + (i * 4));
            return handles;
        }

        private bool FdSetContains(long set, uint handle) => set != 0 && ReadFdSet(set).Contains(handle);

        private void WriteFdSet(long set, IReadOnlyList<uint> handles)
        {
            if (set == 0) return;
            _emulator.WriteUInt32(set, (uint)handles.Count);
            for (int i = 0; i < handles.Count; i++) _emulator.WriteUInt32(set + 4 + (i * 4), handles[i]);
        }

        /// <summary>int select(int nfds, fd_set* read, fd_set* write, fd_set* except, const timeval*).</summary>
        private void Select()
        {
            if (!Started()) return;
            long readSet = _frame.Arg(1), writeSet = _frame.Arg(2), exceptSet = _frame.Arg(3), timeout = _frame.Arg(4);
            uint[] reads = ReadFdSet(readSet), writes = ReadFdSet(writeSet), excepts = ReadFdSet(exceptSet);
            if (reads.Length + writes.Length + excepts.Length == 0)
            {
                Fail(WSAEINVAL);
                return;
            }

            if (reads.Concat(writes).Concat(excepts).Any(h => !_sockets.ContainsKey(h)))
            {
                Fail(WSAENOTSOCK);
                return;
            }

            long? milliseconds = null;
            if (timeout != 0)
            {
                int seconds = unchecked((int)_emulator.ReadUInt32(timeout));
                int micro = unchecked((int)_emulator.ReadUInt32(timeout + 4));
                if (seconds < 0 || micro < 0)
                {
                    Fail(WSAEINVAL);
                    return;
                }

                milliseconds = (seconds * 1000L) + ((micro + 999) / 1000);
            }

            (List<uint> R, List<uint> W, List<uint> E) Evaluate() =>
            (
                reads.Where(h => _sockets.TryGetValue(h, out var s) && Readable(s)).ToList(),
                writes.Where(h => _sockets.TryGetValue(h, out var s) && Writable(s)).ToList(),
                excepts.Where(h => _sockets.TryGetValue(h, out var s) && Excepted(s)).ToList()
            );

            void Complete()
            {
                var (r, w, e) = Evaluate();
                WriteFdSet(readSet, r);
                WriteFdSet(writeSet, w);
                WriteFdSet(exceptSet, e);
                _frame.Return(r.Count + w.Count + e.Count);
            }

            bool Any()
            {
                var (r, w, e) = Evaluate();
                return r.Count + w.Count + e.Count > 0;
            }

            if (Any() || milliseconds == 0)
            {
                Complete();
                return;
            }

            Wait(Any, Complete, Complete, milliseconds, "select");
        }

        /// <summary>int WSAPoll(WSAPOLLFD* fds, ULONG count, INT timeout).</summary>
        private void Poll()
        {
            if (!Started()) return;
            long fds = _frame.Arg(0);
            int count = _frame.SignedArg(1), timeout = _frame.SignedArg(2);
            const int pollRdNorm = 0x100, pollRdBand = 0x200, pollPri = 0x400, pollWrNorm = 0x10, pollWrBand = 0x20;
            const int pollErr = 0x1, pollHup = 0x2, pollNval = 0x4;
            const int valid = pollRdNorm | pollRdBand | pollPri | pollWrNorm | pollWrBand;
            if (count <= 0)
            {
                Fail(WSAEINVAL);
                return;
            }

            if (fds == 0)
            {
                Fail(WSAEFAULT);
                return;
            }

            (uint Handle, int Events)[] entries = new (uint, int)[count];
            for (int i = 0; i < count; i++)
            {
                entries[i] = (_emulator.ReadUInt32(fds + (i * 8)), BitConverter.ToUInt16(_emulator.ReadMemory(fds + (i * 8) + 4, 2)));
                if ((entries[i].Events & ~valid) != 0)
                {
                    Fail(WSAEINVAL);
                    return;
                }
            }

            int Revents(uint handle, int events)
            {
                if (unchecked((int)handle) < 0) return 0;
                if (!_sockets.TryGetValue(handle, out GuestSocket? s)) return pollNval;
                int r = 0;
                UpdateConnect(s);
                if (s.State == ConnectState.Failed) r |= pollErr;
                if ((events & (pollRdNorm | pollRdBand)) != 0 && Readable(s))
                {
                    bool closed = s.Type == SockStream && !s.Listening && s.Host.Available == 0;
                    r |= closed ? pollHup : (events & pollRdNorm) != 0 ? pollRdNorm : pollRdBand;
                }

                if ((events & (pollWrNorm | pollWrBand)) != 0 && Writable(s)) r |= pollWrNorm & events | (events & pollWrBand);
                return r;
            }

            bool AnyReady() => entries.Any(e => Revents(e.Handle, e.Events) != 0);

            void Complete()
            {
                int ready = 0;
                for (int i = 0; i < count; i++)
                {
                    int r = Revents(entries[i].Handle, entries[i].Events);
                    _emulator.WriteMemory(fds + (i * 8) + 6, BitConverter.GetBytes((ushort)r));
                    if (r != 0) ready++;
                }

                _frame.Return(ready);
            }

            if (timeout == 0 || AnyReady())
            {
                Complete();
                return;
            }

            Wait(AnyReady, Complete, Complete, timeout < 0 ? null : timeout, "WSAPoll");
        }

        // ================================================================ options and control

        private const uint Fionbio = 0x8004667E, Fionread = 0x4004667F, Siocatmark = 0x40047307;
        private const uint SioKeepaliveVals = 0x98000004, SioAddressListQuery = 0x48000016;

        /// <summary>The three ioctlsocket commands; a WSA error, or 0 with the value to write back.</summary>
        private int Control(GuestSocket s, uint command, uint input, out uint output)
        {
            output = 0;
            switch (command)
            {
                case Fionbio:
                    if (input == 0 && s.Event != 0)
                    {
                        return WSAEINVAL;   // a socket with WSAEventSelect active cannot be made blocking
                    }

                    s.NonBlocking = input != 0;
                    return 0;
                case Fionread:
                    try
                    {
                        output = (uint)s.Host.Available;
                    }
                    catch (SocketException ex)
                    {
                        return ErrorOf(ex);
                    }

                    return 0;
                case Siocatmark:
                    output = 1;   // no out-of-band data is waiting
                    return 0;
                default:
                    return WSAEINVAL;
            }
        }

        private void IoctlSocket(uint handle, long command, long argument)
        {
            if (Lookup(handle) is not { } s) return;
            if (argument == 0)
            {
                Fail(WSAEFAULT);
                return;
            }

            int error = Control(s, (uint)command, _emulator.ReadUInt32(argument), out uint output);
            if (error != 0)
            {
                Fail(error);
                return;
            }

            if ((uint)command != Fionbio) _emulator.WriteUInt32(argument, output);
            _frame.Return(0);
        }

        /// <summary>
        /// int WSAIoctl(s, code, in, inLength, out, outLength, DWORD* returned, OVERLAPPED*, routine).
        /// FIONBIO/FIONREAD/SIOCATMARK, SIO_KEEPALIVE_VALS and SIO_ADDRESS_LIST_QUERY; others WSAEOPNOTSUPP.
        /// </summary>
        private void WsaIoctl()
        {
            uint handle = Handle(0);
            uint code = (uint)_frame.Arg(1);
            long input = _frame.Arg(2), output = _frame.Arg(4), returned = _frame.Arg(6);
            int inputLength = _frame.SignedArg(3), outputLength = _frame.SignedArg(5);
            if (Lookup(handle) is not { } s) return;

            switch (code)
            {
                case Fionbio:
                case Fionread:
                case Siocatmark:
                {
                    if (code == Fionbio && (input == 0 || inputLength < 4))
                    {
                        Fail(WSAEFAULT);
                        return;
                    }

                    if (code != Fionbio && (output == 0 || outputLength < 4))
                    {
                        Fail(WSAEFAULT);
                        return;
                    }

                    int error = Control(s, code, code == Fionbio ? _emulator.ReadUInt32(input) : 0, out uint value);
                    if (error != 0)
                    {
                        Fail(error);
                        return;
                    }

                    if (code != Fionbio) _emulator.WriteUInt32(output, value);
                    if (returned != 0) _emulator.WriteUInt32(returned, code == Fionbio ? 0u : 4u);
                    _frame.Return(0);
                    return;
                }

                case SioKeepaliveVals:
                {
                    // struct tcp_keepalive { u_long onoff, keepalivetime, keepaliveinterval; } (ms).
                    if (input == 0 || inputLength < 12)
                    {
                        Fail(WSAEFAULT);
                        return;
                    }

                    if (s.Type != SockStream)
                    {
                        Fail(WSAEOPNOTSUPP);
                        return;
                    }

                    uint on = _emulator.ReadUInt32(input), time = _emulator.ReadUInt32(input + 4), interval = _emulator.ReadUInt32(input + 8);
                    try
                    {
                        s.Host.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, on != 0);
                        if (on != 0)
                        {
                            s.Host.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, (int)Math.Max(1, time / 1000));
                            s.Host.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, (int)Math.Max(1, interval / 1000));
                        }
                    }
                    catch (Exception)
                    {
                        // Keep-alive timing is advisory; the switch itself was applied first.
                    }

                    if (returned != 0) _emulator.WriteUInt32(returned, 0);
                    _frame.Return(0);
                    return;
                }

                case SioAddressListQuery:
                {
                    // SOCKET_ADDRESS_LIST { INT count; SOCKET_ADDRESS { sockaddr* ; INT length; } [count]; } + the sockaddrs.
                    var addresses = LocalAddresses(s.Family == AfInet6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork);
                    int length = SockAddrLength(s.Family);
                    int needed = 4 + (addresses.Count * 8) + (addresses.Count * length);
                    if (returned != 0) _emulator.WriteUInt32(returned, (uint)needed);
                    if (output == 0 || outputLength < needed)
                    {
                        Fail(WSAEFAULT);
                        return;
                    }

                    _emulator.WriteUInt32(output, (uint)addresses.Count);
                    long blobs = output + 4 + (addresses.Count * 8);
                    for (int i = 0; i < addresses.Count; i++)
                    {
                        long at = blobs + (i * length);
                        _emulator.WriteMemory(at, EncodeSockAddr(new IPEndPoint(addresses[i], 0)));
                        _emulator.WriteUInt32(output + 4 + (i * 8), (uint)at);
                        _emulator.WriteUInt32(output + 8 + (i * 8), (uint)length);
                    }

                    _frame.Return(0);
                    return;
                }

                default:
                    Fail(WSAEOPNOTSUPP);
                    return;
            }
        }

        private static List<IPAddress> LocalAddresses(AddressFamily family)
        {
            try
            {
                return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(u => u.Address))
                    .Where(a => a.AddressFamily == family && !IPAddress.IsLoopback(a))
                    .ToList();
            }
            catch (Exception)
            {
                return [];
            }
        }

        private const int SolSocket = 0xFFFF, IpProtoIp = 0, IpProtoIpv6 = 41;
        private const int SoDebug = 1, SoAcceptConn = 2, SoReuseAddr = 4, SoKeepAlive = 8, SoDontRoute = 0x10, SoBroadcast = 0x20,
            SoLinger = 0x80, SoOobInline = 0x100, SoDontLinger = unchecked((int)0xFFFFFF7F), SoExclusiveAddrUse = unchecked((int)0xFFFFFFFB),
            SoSndBuf = 0x1001, SoRcvBuf = 0x1002, SoSndTimeo = 0x1005, SoRcvTimeo = 0x1006, SoError = 0x1007, SoType = 0x1008,
            SoMaxMsgSize = 0x2003;
        private const int TcpNoDelay = 1;
        private const int IpTos = 3, IpTtl = 4, IpMulticastIf = 9, IpMulticastTtl = 10, IpMulticastLoop = 11, IpAddMembership = 12, IpDropMembership = 13;
        private const int Ipv6UnicastHops = 4, Ipv6MulticastIf = 9, Ipv6MulticastHops = 10, Ipv6MulticastLoop = 11, Ipv6AddMembership = 12,
            Ipv6DropMembership = 13, Ipv6V6Only = 27;

        /// <summary>How an option's value is shaped, or null if (level, name) is not an option this socket has.</summary>
        private static string? OptionShape(GuestSocket s, int level, int name) => level switch
        {
            SolSocket => name switch
            {
                SoDebug or SoReuseAddr or SoDontRoute or SoExclusiveAddrUse or SoAcceptConn => "bool",
                SoKeepAlive or SoOobInline or SoDontLinger => s.Type == SockStream ? "bool" : null,
                SoBroadcast => s.Type == SockDgram ? "bool" : null,
                SoLinger => s.Type == SockStream ? "linger" : null,
                SoSndBuf or SoRcvBuf or SoSndTimeo or SoRcvTimeo or SoError or SoType => "int",
                SoMaxMsgSize => s.Type == SockDgram ? "int" : null,
                _ => null,
            },
            IpProtoTcp => s.Type == SockStream && name == TcpNoDelay ? "bool" : null,
            IpProtoIp => s.Family != AfInet ? null : name switch
            {
                IpTos or IpTtl or IpMulticastTtl => "int",
                IpMulticastLoop => "bool",
                IpMulticastIf => "inaddr",
                IpAddMembership or IpDropMembership => s.Type == SockDgram ? "mreq" : null,
                _ => null,
            },
            IpProtoIpv6 => s.Family != AfInet6 ? null : name switch
            {
                Ipv6V6Only => "bool",
                Ipv6UnicastHops or Ipv6MulticastHops or Ipv6MulticastIf => "int",
                Ipv6MulticastLoop => "bool",
                Ipv6AddMembership or Ipv6DropMembership => s.Type == SockDgram ? "mreq6" : null,
                _ => null,
            },
            _ => null,
        };

        private static bool KnownLevel(int level) => level is SolSocket or IpProtoTcp or IpProtoIp or IpProtoIpv6 or IpProtoUdp;

        /// <summary>int setsockopt(SOCKET, int level, int name, const char* value, int length).</summary>
        private void SetSockOpt()
        {
            uint handle = Handle(0);
            int level = _frame.SignedArg(1), name = _frame.SignedArg(2);
            long value = _frame.Arg(3);
            int length = _frame.SignedArg(4);
            if (Lookup(handle) is not { } s) return;
            if (!KnownLevel(level))
            {
                Fail(WSAEINVAL);
                return;
            }

            string? shape = OptionShape(s, level, name);
            if (shape is null || (level == SolSocket && name is SoAcceptConn or SoError or SoType or SoMaxMsgSize))
            {
                Fail(WSAENOPROTOOPT);
                return;
            }

            int need = shape switch { "linger" => 4, "mreq" => 8, "mreq6" => 20, "bool" => 1, _ => 4 };
            if (value == 0 || length < need)
            {
                Fail(WSAEFAULT);
                return;
            }

            int number = length >= 4 ? unchecked((int)_emulator.ReadUInt32(value)) : _emulator.ReadMemory(value, 1)[0];
            try
            {
                Apply(s, level, name, shape, value, number);
                _frame.Return(0);
            }
            catch (SocketException ex)
            {
                Fail(ErrorOf(ex));
            }
        }

        private void Apply(GuestSocket s, int level, int name, string shape, long value, int number)
        {
            Socket h = s.Host;
            switch (level, name)
            {
                case (SolSocket, SoDebug):
                    s.Debug = number != 0;
                    return;
                case (SolSocket, SoRcvTimeo):
                    s.RecvTimeoutMs = number;
                    return;
                case (SolSocket, SoSndTimeo):
                    s.SendTimeoutMs = number;
                    return;
                case (SolSocket, SoLinger):
                {
                    byte[] raw = _emulator.ReadMemory(value, 4);
                    h.LingerState = new LingerOption(BitConverter.ToUInt16(raw, 0) != 0, BitConverter.ToUInt16(raw, 2));
                    return;
                }

                case (SolSocket, SoDontLinger):
                    h.LingerState = new LingerOption(number == 0, h.LingerState?.LingerTime ?? 0);
                    return;
                case (IpProtoIp, IpMulticastIf):
                    TrySet(() => h.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, _emulator.ReadMemory(value, 4)));
                    return;
                case (IpProtoIp, IpAddMembership or IpDropMembership):
                {
                    byte[] raw = _emulator.ReadMemory(value, 8);
                    var option = new MulticastOption(new IPAddress(raw.AsSpan(0, 4)), new IPAddress(raw.AsSpan(4, 4)));
                    h.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)name, option);
                    return;
                }

                case (IpProtoIpv6, Ipv6AddMembership or Ipv6DropMembership):
                {
                    byte[] raw = _emulator.ReadMemory(value, 20);
                    var option = new IPv6MulticastOption(new IPAddress(raw.AsSpan(0, 16)), BitConverter.ToUInt32(raw, 16));
                    h.SetSocketOption(SocketOptionLevel.IPv6, (SocketOptionName)name, option);
                    return;
                }
            }

            int applied = shape == "bool" ? (number != 0 ? 1 : 0) : number;
            TrySet(() => h.SetSocketOption((SocketOptionLevel)level, (SocketOptionName)name, applied));
        }

        /// <summary>
        /// Applies an option the host may not support (an Android host has no SO_EXCLUSIVEADDRUSE): a
        /// valid Winsock option is never refused for that reason.
        /// </summary>
        private static void TrySet(Action set)
        {
            try
            {
                set();
            }
            catch (PlatformNotSupportedException)
            {
            }
            catch (SocketException ex) when (ex.SocketErrorCode is System.Net.Sockets.SocketError.ProtocolOption or System.Net.Sockets.SocketError.OperationNotSupported)
            {
            }
        }

        /// <summary>int getsockopt(SOCKET, int level, int name, char* value, int* length).</summary>
        private void GetSockOpt()
        {
            uint handle = Handle(0);
            int level = _frame.SignedArg(1), name = _frame.SignedArg(2);
            long value = _frame.Arg(3), lengthPointer = _frame.Arg(4);
            if (Lookup(handle) is not { } s) return;
            if (!KnownLevel(level))
            {
                Fail(WSAEINVAL);
                return;
            }

            string? shape = OptionShape(s, level, name);
            if (shape is null || shape is "mreq" or "mreq6")
            {
                Fail(WSAENOPROTOOPT);
                return;
            }

            if (value == 0 || lengthPointer == 0)
            {
                Fail(WSAEFAULT);
                return;
            }

            int room = unchecked((int)_emulator.ReadUInt32(lengthPointer));
            if (room < 4)
            {
                Fail(WSAEFAULT);
                return;
            }

            byte[] result;
            try
            {
                result = Query(s, level, name, shape);
            }
            catch (SocketException ex)
            {
                Fail(ErrorOf(ex));
                return;
            }

            _emulator.WriteMemory(value, result);
            _emulator.WriteUInt32(lengthPointer, (uint)result.Length);
            _frame.Return(0);
        }

        private byte[] Query(GuestSocket s, int level, int name, string shape)
        {
            Socket h = s.Host;
            int number;
            switch (level, name)
            {
                case (SolSocket, SoDebug): number = s.Debug ? 1 : 0; break;
                case (SolSocket, SoAcceptConn): number = s.Listening ? 1 : 0; break;
                case (SolSocket, SoRcvTimeo): number = s.RecvTimeoutMs; break;
                case (SolSocket, SoSndTimeo): number = s.SendTimeoutMs; break;
                case (SolSocket, SoType): number = s.Type; break;
                case (SolSocket, SoMaxMsgSize): number = 65507; break;
                case (SolSocket, SoError):
                {
                    UpdateConnect(s);
                    number = s.PendingSoError;
                    s.PendingSoError = 0;
                    if (number == 0) number = (int)h.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error)!;
                    break;
                }

                case (SolSocket, SoLinger):
                {
                    LingerOption linger = h.LingerState ?? new LingerOption(false, 0);
                    byte[] raw = new byte[4];
                    BitConverter.GetBytes((ushort)(linger.Enabled ? 1 : 0)).CopyTo(raw, 0);
                    BitConverter.GetBytes((ushort)linger.LingerTime).CopyTo(raw, 2);
                    return raw;
                }

                case (SolSocket, SoDontLinger): number = h.LingerState is { Enabled: true } ? 0 : 1; break;
                case (IpProtoIp, IpMulticastIf):
                    return TryGet(() => h.GetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, 4), new byte[4]);
                default:
                {
                    int fallback = 0;
                    number = BitConverter.ToInt32(TryGet(
                        () => BitConverter.GetBytes(Convert.ToInt32(h.GetSocketOption((SocketOptionLevel)level, (SocketOptionName)name))),
                        BitConverter.GetBytes(fallback)));
                    if (shape == "bool") number = number != 0 ? 1 : 0;
                    break;
                }
            }

            return BitConverter.GetBytes(number);
        }

        private static byte[] TryGet(Func<byte[]> get, byte[] fallback)
        {
            try
            {
                return get();
            }
            catch (PlatformNotSupportedException)
            {
                return fallback;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is System.Net.Sockets.SocketError.ProtocolOption or System.Net.Sockets.SocketError.OperationNotSupported)
            {
                return fallback;
            }
        }

        private void Shutdown()
        {
            if (Lookup(Handle(0)) is not { } s) return;
            int how = _frame.SignedArg(1);
            if (how is < 0 or > 2)
            {
                Fail(WSAEINVAL);
                return;
            }

            UpdateConnect(s);
            if (s.Type == SockStream && s.State != ConnectState.Connected)
            {
                Fail(WSAENOTCONN);
                return;
            }

            try
            {
                s.Host.Shutdown(how switch { 0 => SocketShutdown.Receive, 1 => SocketShutdown.Send, _ => SocketShutdown.Both });
            }
            catch (SocketException ex) when (s.Type == SockDgram && ex.SocketErrorCode == System.Net.Sockets.SocketError.NotConnected)
            {
                // A datagram socket has no connection to shut down; Winsock just stops the direction.
            }
            catch (SocketException ex)
            {
                Fail(ErrorOf(ex));
                return;
            }

            if (how != 1) s.ShutRecv = true;
            if (how != 0) s.ShutSend = true;
            _frame.Return(0);
        }

        private void SocketName(bool peer)
        {
            if (Lookup(Handle(0)) is not { } s) return;
            long address = _frame.Arg(1), lengthPointer = _frame.Arg(2);
            UpdateConnect(s);
            EndPoint? endpoint;
            try
            {
                endpoint = peer
                    ? (s.Type == SockStream ? (s.State == ConnectState.Connected ? s.Host.RemoteEndPoint ?? s.Peer : null) : s.Peer)
                    // Not IsBound: .NET leaves that false after a non-blocking connect implicitly binds,
                    // while LocalEndPoint asks the OS. Null is the unbound case.
                    : s.Host.LocalEndPoint;
            }
            catch (SocketException)
            {
                endpoint = null;
            }

            if (endpoint is not IPEndPoint ip)
            {
                Fail(peer ? WSAENOTCONN : WSAEINVAL);
                return;
            }

            int error = WriteSockAddr(ip, address, lengthPointer);
            if (error != 0)
            {
                Fail(error);
                return;
            }

            _frame.Return(0);
        }

        // ================================================================ address text

        /// <summary>inet_addr: the BSD forms a, a.b, a.b.c, a.b.c.d, each part decimal, octal or hex.</summary>
        public static uint InetAddr(string text)
        {
            const uint none = 0xFFFFFFFF;
            int end = text.IndexOfAny([' ', '\t', '\n']);
            if (end >= 0) text = text[..end];
            string[] parts = text.Split('.');
            if (text.Length == 0 || parts.Length > 4) return none;
            ulong[] values = new ulong[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!ParseInetPart(parts[i], out values[i])) return none;
            }

            ulong address;
            switch (parts.Length)
            {
                case 1:
                    if (values[0] > 0xFFFFFFFF) return none;
                    address = values[0];
                    break;
                case 2:
                    if (values[0] > 0xFF || values[1] > 0xFFFFFF) return none;
                    address = (values[0] << 24) | values[1];
                    break;
                case 3:
                    if (values[0] > 0xFF || values[1] > 0xFF || values[2] > 0xFFFF) return none;
                    address = (values[0] << 24) | (values[1] << 16) | values[2];
                    break;
                default:
                    if (values.Any(v => v > 0xFF)) return none;
                    address = (values[0] << 24) | (values[1] << 16) | (values[2] << 8) | values[3];
                    break;
            }

            // Host order -> network order in memory, i.e. as a little-endian register value.
            return Swap32((uint)address);
        }

        private static bool ParseInetPart(string part, out ulong value)
        {
            value = 0;
            if (part.Length == 0) return false;
            int radix = 10, at = 0;
            if (part.Length > 1 && part[0] == '0' && part[1] is 'x' or 'X')
            {
                radix = 16;
                at = 2;
                if (part.Length == 2) return false;
            }
            else if (part.Length > 1 && part[0] == '0')
            {
                radix = 8;
                at = 1;
            }

            for (; at < part.Length; at++)
            {
                int digit = part[at] switch
                {
                    >= '0' and <= '9' => part[at] - '0',
                    >= 'a' and <= 'f' => part[at] - 'a' + 10,
                    >= 'A' and <= 'F' => part[at] - 'A' + 10,
                    _ => 99,
                };
                if (digit >= radix) return false;
                value = (value * (ulong)radix) + (ulong)digit;
                if (value > 0xFFFFFFFF) return false;
            }

            return true;
        }

        /// <summary>char* inet_ntoa(struct in_addr): a per-thread buffer, valid until the next call on that thread.</summary>
        private void InetNtoa()
        {
            uint address = (uint)_frame.Arg(0);
            string text = $"{address & 0xFF}.{(address >> 8) & 0xFF}.{(address >> 16) & 0xFF}.{address >> 24}";
            long buffer = ThreadBuffer(kind: 1, size: 16);
            _emulator.WriteMemory(buffer, Encoding.ASCII.GetBytes(text + "\0"));
            _frame.Return(buffer);
        }

        /// <summary>Strict dotted-decimal: exactly four decimal parts, as inet_pton demands.</summary>
        private static bool TryParseDottedQuad(string text, out byte[] bytes)
        {
            bytes = new byte[4];
            string[] parts = text.Split('.');
            if (parts.Length != 4) return false;
            for (int i = 0; i < 4; i++)
            {
                if (parts[i].Length is 0 or > 3 || !parts[i].All(char.IsAsciiDigit) || !int.TryParse(parts[i], out int n) || n > 255)
                {
                    return false;
                }

                bytes[i] = (byte)n;
            }

            return true;
        }

        private static bool TryParseIpv6(string text, out byte[] bytes)
        {
            bytes = new byte[16];
            if (text.Contains('%') || text.Contains('[') || !text.Contains(':')) return false;
            if (!IPAddress.TryParse(text, out IPAddress? address) || address.AddressFamily != AddressFamily.InterNetworkV6) return false;
            bytes = address.GetAddressBytes();
            return true;
        }

        /// <summary>INT inet_pton(INT family, PCSTR text, PVOID out): 1, 0 if not an address, -1 with WSAEAFNOSUPPORT.</summary>
        private void InetPton(int family, string? text, long output)
        {
            if (text is null || output == 0)
            {
                SetLastError(WSAEFAULT);
                _frame.Return(SocketError);
                return;
            }

            byte[] bytes;
            bool ok;
            switch (family)
            {
                case AfInet: ok = TryParseDottedQuad(text, out bytes); break;
                case AfInet6: ok = TryParseIpv6(text, out bytes); break;
                default:
                    SetLastError(WSAEAFNOSUPPORT);
                    _frame.Return(SocketError);
                    return;
            }

            if (ok) _emulator.WriteMemory(output, bytes);
            _frame.Return(ok ? 1 : 0);
        }

        /// <summary>PCSTR inet_ntop(INT family, const VOID* address, PSTR buffer, size_t size): buffer, or NULL.</summary>
        private void InetNtop(bool wide)
        {
            int family = _frame.SignedArg(0);
            long address = _frame.Arg(1), buffer = _frame.Arg(2);
            long size = _frame.Arg(3);
            string text;
            if (family == AfInet)
            {
                text = new IPAddress(_emulator.ReadMemory(address, 4)).ToString();
            }
            else if (family == AfInet6)
            {
                text = new IPAddress(_emulator.ReadMemory(address, 16)).ToString();
            }
            else
            {
                SetLastError(WSAEAFNOSUPPORT);
                _frame.Return(0);
                return;
            }

            if (buffer == 0 || size < text.Length + 1)
            {
                SetLastError(WSA_INVALID_PARAMETER);
                _frame.Return(0);
                return;
            }

            _emulator.WriteMemory(buffer, wide ? Encoding.Unicode.GetBytes(text + "\0") : Encoding.ASCII.GetBytes(text + "\0"));
            _frame.Return(buffer);
        }

        private static string FormatAddress(IPEndPoint endpoint, bool withPort)
        {
            string host = endpoint.Address.ToString();
            if (!withPort || endpoint.Port == 0) return host;
            return endpoint.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{host}]:{endpoint.Port}" : $"{host}:{endpoint.Port}";
        }

        /// <summary>INT WSAAddressToStringW(sockaddr*, DWORD length, LPWSAPROTOCOL_INFOW, LPWSTR out, LPDWORD outLength).</summary>
        private void AddressToString(bool wide)
        {
            if (!Started()) return;
            long address = _frame.Arg(0), output = _frame.Arg(3), lengthPointer = _frame.Arg(4);
            int error = ReadSockAddr(address, _frame.SignedArg(1), AfUnspec, out IPEndPoint endpoint);
            if (error != 0 || lengthPointer == 0)
            {
                Fail(error == WSAEAFNOSUPPORT || error == 0 ? WSAEINVAL : error);
                return;
            }

            string text = FormatAddress(endpoint, withPort: true);
            uint needed = (uint)text.Length + 1;
            uint room = _emulator.ReadUInt32(lengthPointer);
            _emulator.WriteUInt32(lengthPointer, needed);
            if (output == 0 || room < needed)
            {
                Fail(WSAEFAULT);
                return;
            }

            _emulator.WriteMemory(output, wide ? Encoding.Unicode.GetBytes(text + "\0") : Encoding.ASCII.GetBytes(text + "\0"));
            _frame.Return(0);
        }

        // ================================================================ names

        private sealed record Protocol(int Number, string[] Names);

        private sealed record Service(string Name, int Port, string Proto, string[] Aliases);

        /// <summary>%SystemRoot%\system32\drivers\etc\protocol, which is what getprotobyname reads.</summary>
        private static readonly Protocol[] Protocols =
        [
            new(0, ["ip", "IP"]), new(1, ["icmp", "ICMP"]), new(3, ["ggp", "GGP"]), new(6, ["tcp", "TCP"]),
            new(8, ["egp", "EGP"]), new(12, ["pup", "PUP"]), new(17, ["udp", "UDP"]), new(20, ["hmp", "HMP"]),
            new(22, ["xns-idp", "XNS-IDP"]), new(27, ["rdp", "RDP"]), new(41, ["ipv6", "IPv6"]),
            new(43, ["ipv6-route", "IPv6-Route"]), new(44, ["ipv6-frag", "IPv6-Frag"]), new(50, ["esp", "ESP"]),
            new(51, ["ah", "AH"]), new(58, ["ipv6-icmp", "IPv6-ICMP"]), new(59, ["ipv6-nonxt", "IPv6-NoNxt"]),
            new(60, ["ipv6-opts", "IPv6-Opts"]), new(66, ["rvd", "RVD"]),
        ];

        /// <summary>The commonly used entries of %SystemRoot%\system32\drivers\etc\services.</summary>
        private static readonly Service[] Services =
        [
            new("echo", 7, "tcp", []), new("echo", 7, "udp", []), new("discard", 9, "tcp", ["sink", "null"]),
            new("discard", 9, "udp", ["sink", "null"]), new("daytime", 13, "tcp", []), new("daytime", 13, "udp", []),
            new("ftp-data", 20, "tcp", []), new("ftp", 21, "tcp", []), new("ssh", 22, "tcp", []), new("telnet", 23, "tcp", []),
            new("smtp", 25, "tcp", ["mail"]), new("time", 37, "tcp", ["timserver"]), new("time", 37, "udp", ["timserver"]),
            new("domain", 53, "tcp", []), new("domain", 53, "udp", []), new("bootps", 67, "udp", ["dhcps"]),
            new("bootpc", 68, "udp", ["dhcpc"]), new("tftp", 69, "udp", []), new("http", 80, "tcp", ["www", "www-http"]),
            new("kerberos", 88, "tcp", ["krb5", "kerberos-sec"]), new("kerberos", 88, "udp", ["krb5", "kerberos-sec"]),
            new("pop3", 110, "tcp", []), new("nntp", 119, "tcp", ["usenet"]), new("ntp", 123, "udp", []),
            new("imap", 143, "tcp", ["imap4"]), new("snmp", 161, "udp", []), new("snmptrap", 162, "udp", ["snmp-trap"]),
            new("ldap", 389, "tcp", []), new("https", 443, "tcp", ["MCom"]), new("https", 443, "udp", ["MCom"]),
            new("microsoft-ds", 445, "tcp", []), new("microsoft-ds", 445, "udp", []), new("submission", 587, "tcp", []),
            new("ldaps", 636, "tcp", ["sldap"]), new("imaps", 993, "tcp", []), new("pop3s", 995, "tcp", ["spop3"]),
        ];

        private static Service? FindService(string name, string? proto) =>
            Services.FirstOrDefault(s => (proto is null || s.Proto.Equals(proto, StringComparison.OrdinalIgnoreCase)) &&
                                         (s.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || s.Aliases.Contains(name, StringComparer.OrdinalIgnoreCase)));

        /// <summary>Writes strings and a NULL-terminated pointer array into a block; returns the array's address.</summary>
        private long PointerArray(ref long cursor, IEnumerable<byte[]> items)
        {
            List<byte[]> list = items.ToList();
            long array = cursor;
            cursor += (list.Count + 1) * 4;
            for (int i = 0; i < list.Count; i++)
            {
                _emulator.WriteMemory(cursor, list[i]);
                _emulator.WriteUInt32(array + (i * 4), (uint)cursor);
                cursor += (list[i].Length + 3) & ~3;
            }

            _emulator.WriteUInt32(array + (list.Count * 4), 0);
            return array;
        }

        private static byte[] Z(string text) => Encoding.ASCII.GetBytes(text + "\0");

        /// <summary>protoent { char* p_name; char** p_aliases; short p_proto; }, per thread.</summary>
        private void ProtoEnt(Protocol? protocol)
        {
            if (!Started()) return;
            if (protocol is null)
            {
                SetLastError(WSANO_DATA);
                _frame.Return(0);
                return;
            }

            long block = ThreadBuffer(kind: 2, size: 256);
            long cursor = block + 12;
            long name = cursor;
            _emulator.WriteMemory(name, Z(protocol.Names[0]));
            cursor += 32;
            long aliases = PointerArray(ref cursor, protocol.Names.Skip(1).Select(Z));
            _emulator.WriteUInt32(block, (uint)name);
            _emulator.WriteUInt32(block + 4, (uint)aliases);
            _emulator.WriteMemory(block + 8, BitConverter.GetBytes((short)protocol.Number));
            _frame.Return(block);
        }

        /// <summary>servent { char* s_name; char** s_aliases; short s_port (network order); char* s_proto; }, per thread.</summary>
        private void ServEnt(Service? service)
        {
            if (service is null)
            {
                SetLastError(WSANO_DATA);
                _frame.Return(0);
                return;
            }

            long block = ThreadBuffer(kind: 3, size: 256);
            long cursor = block + 16;
            long name = cursor;
            _emulator.WriteMemory(name, Z(service.Name));
            cursor += 32;
            long proto = cursor;
            _emulator.WriteMemory(proto, Z(service.Proto));
            cursor += 8;
            long aliases = PointerArray(ref cursor, service.Aliases.Select(Z));
            _emulator.WriteUInt32(block, (uint)name);
            _emulator.WriteUInt32(block + 4, (uint)aliases);
            _emulator.WriteMemory(block + 8, BitConverter.GetBytes((ushort)Swap16((ushort)service.Port)));
            _emulator.WriteUInt32(block + 12, (uint)proto);
            _frame.Return(block);
        }

        private void GetServByName()
        {
            if (!Started()) return;
            string name = _frame.ReadNarrowString(_frame.Arg(0), 256);
            string? proto = _frame.Arg(1) == 0 ? null : _frame.ReadNarrowString(_frame.Arg(1), 32);
            ServEnt(FindService(name, proto));
        }

        private void GetServByPort()
        {
            if (!Started()) return;
            int port = (int)Swap16((ushort)_frame.Arg(0));
            string? proto = _frame.Arg(1) == 0 ? null : _frame.ReadNarrowString(_frame.Arg(1), 32);
            ServEnt(Services.FirstOrDefault(s => s.Port == port && (proto is null || s.Proto.Equals(proto, StringComparison.OrdinalIgnoreCase))));
        }

        private static string LocalHostName()
        {
            try
            {
                return Dns.GetHostName();
            }
            catch (SocketException)
            {
                return "localhost";
            }
        }

        private void GetHostName()
        {
            if (!Started()) return;
            long buffer = _frame.Arg(0);
            int length = _frame.SignedArg(1);
            byte[] name = Z(LocalHostName());
            if (buffer == 0 || length < name.Length)
            {
                Fail(WSAEFAULT);
                return;
            }

            _emulator.WriteMemory(buffer, name);
            _frame.Return(0);
        }

        private static int DnsError(Exception ex) =>
            (ex is AggregateException a ? a.InnerException : ex) is SocketException s &&
            s.SocketErrorCode is System.Net.Sockets.SocketError.TryAgain or System.Net.Sockets.SocketError.NoRecovery or System.Net.Sockets.SocketError.NoData
                ? (int)s.SocketErrorCode
                : WSAHOST_NOT_FOUND;

        /// <summary>
        /// Runs a name lookup without blocking other guest threads: the calling thread waits on the
        /// host task, then <paramref name="complete"/> gets the result (or the exception).
        /// </summary>
        private void Resolve<T>(Task<T> lookup, Action<T> complete, Action<int> fail, string why)
        {
            void Finish()
            {
                if (lookup.IsCompletedSuccessfully) complete(lookup.Result);
                else fail(DnsError(lookup.Exception ?? new Exception()));
            }

            if (lookup.IsCompleted)
            {
                Finish();
                return;
            }

            Wait(() => lookup.IsCompleted, Finish, () => fail(WSATRY_AGAIN), null, why);
        }

        /// <summary>hostent { h_name; h_aliases; short h_addrtype; short h_length; h_addr_list }, per thread.</summary>
        private long WriteHostEnt(string name, IEnumerable<string> aliases, IReadOnlyList<IPAddress> addresses)
        {
            List<IPAddress> v4 = addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Take(35).ToList();
            byte[] nameBytes = Z(name);
            List<byte[]> aliasBytes = aliases.Take(35).Select(Z).ToList();
            int size = 16 + ((nameBytes.Length + 3) & ~3) + ((aliasBytes.Count + 1) * 4) + aliasBytes.Sum(b => (b.Length + 3) & ~3) +
                       ((v4.Count + 1) * 4) + (v4.Count * 4) + 16;
            long block;
            if (size <= 1024)
            {
                block = ThreadBuffer(kind: 4, size: 1024);
            }
            else
            {
                // Bigger than the per-thread buffer: a fresh block. The previous hostent is invalid
                // from this call on, as the contract says, so nothing still points into it.
                block = _emulator.AllocateHeap(size);
                _emulator.WriteMemory(block, new byte[size]);
            }

            long cursor = block + 16;
            long nameAt = cursor;
            _emulator.WriteMemory(nameAt, nameBytes);
            cursor += (nameBytes.Length + 3) & ~3;
            long aliasArray = PointerArray(ref cursor, aliasBytes);
            long addressArray = PointerArray(ref cursor, v4.Select(a => a.GetAddressBytes()));
            _emulator.WriteUInt32(block, (uint)nameAt);
            _emulator.WriteUInt32(block + 4, (uint)aliasArray);
            _emulator.WriteMemory(block + 8, BitConverter.GetBytes((short)AfInet));
            _emulator.WriteMemory(block + 10, BitConverter.GetBytes((short)4));
            _emulator.WriteUInt32(block + 12, (uint)addressArray);
            return block;
        }

        private void GetHostByName()
        {
            if (!Started()) return;
            string name = _frame.Arg(0) == 0 ? string.Empty : _frame.ReadNarrowString(_frame.Arg(0), 256);
            if (name.Length == 0) name = LocalHostName();

            if (TryParseDottedQuad(name, out byte[] literal))
            {
                _frame.Return(WriteHostEnt(name, [], [new IPAddress(literal)]));
                return;
            }

            Note($"resolve {name}");
            Resolve(Dns.GetHostEntryAsync(name), entry =>
            {
                if (!entry.AddressList.Any(a => a.AddressFamily == AddressFamily.InterNetwork))
                {
                    SetLastError(WSANO_DATA);
                    _frame.Return(0);
                    return;
                }

                _frame.Return(WriteHostEnt(entry.HostName, entry.Aliases, entry.AddressList));
            }, error =>
            {
                SetLastError(error);
                _frame.Return(0);
            }, $"gethostbyname {name}");
        }

        private void GetHostByAddr()
        {
            if (!Started()) return;
            long address = _frame.Arg(0);
            int length = _frame.SignedArg(1), type = _frame.SignedArg(2);
            if (type != AfInet)
            {
                SetLastError(WSAEAFNOSUPPORT);
                _frame.Return(0);
                return;
            }

            if (address == 0 || length < 4)
            {
                SetLastError(WSAEFAULT);
                _frame.Return(0);
                return;
            }

            IPAddress ip = new(_emulator.ReadMemory(address, 4));
            Note($"reverse-resolve {ip}");
            Resolve(Dns.GetHostEntryAsync(ip), entry =>
            {
                _frame.Return(WriteHostEnt(entry.HostName, entry.Aliases, [ip]));
            }, error =>
            {
                SetLastError(error);
                _frame.Return(0);
            }, $"gethostbyaddr {ip}");
        }

        private const int AiPassive = 1, AiCanonName = 2, AiNumericHost = 4, AiNumericServ = 8, AiAll = 0x100, AiAddrConfig = 0x400, AiV4Mapped = 0x800;

        /// <summary>
        /// INT getaddrinfo(PCSTR node, PCSTR service, const ADDRINFOA* hints, PADDRINFOA* result) and the
        /// wide twin. The error is returned and also set as the last error.
        /// </summary>
        private void GetAddrInfo(bool wide)
        {
            long nodeAt = _frame.Arg(0), serviceAt = _frame.Arg(1), hints = _frame.Arg(2), result = _frame.Arg(3);
            string? Text(long at) => at == 0 ? null : wide ? _emulator.ReadUtf16String(at, 1024) : _frame.ReadNarrowString(at, 1024);
            string? node = Text(nodeAt), service = Text(serviceAt);

            void Return(int error)
            {
                if (error != 0) SetLastError(error);
                _frame.Return(error);
            }

            if (result != 0) _emulator.WriteUInt32(result, 0);
            if (_startups == 0)
            {
                Return(WSANOTINITIALISED);
                return;
            }

            if (result == 0)
            {
                Return(WSAEINVAL);
                return;
            }

            int flags = 0, family = AfUnspec, socketType = 0, protocol = 0;
            if (hints != 0)
            {
                flags = unchecked((int)_emulator.ReadUInt32(hints));
                family = unchecked((int)_emulator.ReadUInt32(hints + 4));
                socketType = unchecked((int)_emulator.ReadUInt32(hints + 8));
                protocol = unchecked((int)_emulator.ReadUInt32(hints + 12));
            }

            if (node is null && service is null)
            {
                Return(WSAHOST_NOT_FOUND);
                return;
            }

            if (family is not (AfUnspec or AfInet or AfInet6))
            {
                Return(WSAEAFNOSUPPORT);
                return;
            }

            if (socketType is not (0 or SockStream or SockDgram))
            {
                Return(WSAESOCKTNOSUPPORT);
                return;
            }

            if ((flags & AiCanonName) != 0 && string.IsNullOrEmpty(node))
            {
                Return(WSAEINVAL);
                return;
            }

            int port = 0;
            if (!string.IsNullOrEmpty(service))
            {
                if (int.TryParse(service, System.Globalization.NumberStyles.None, null, out int number))
                {
                    if (number > 65535)
                    {
                        Return(WSATYPE_NOT_FOUND);
                        return;
                    }

                    port = number;
                }
                else if ((flags & AiNumericServ) != 0)
                {
                    Return(WSAHOST_NOT_FOUND);
                    return;
                }
                else if (FindService(service, socketType == SockDgram ? "udp" : socketType == SockStream ? "tcp" : null) is { } known)
                {
                    port = known.Port;
                }
                else
                {
                    Return(WSATYPE_NOT_FOUND);
                    return;
                }
            }

            void Build(IReadOnlyList<IPAddress> found, string? canonical)
            {
                IEnumerable<IPAddress> chosen = family switch
                {
                    AfInet => found.Where(a => a.AddressFamily == AddressFamily.InterNetwork),
                    AfInet6 when (flags & AiV4Mapped) != 0 =>
                        found.Any(a => a.AddressFamily == AddressFamily.InterNetworkV6) && (flags & AiAll) == 0
                            ? found.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6)
                            : found.Select(a => a.AddressFamily == AddressFamily.InterNetwork ? a.MapToIPv6() : a),
                    AfInet6 => found.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6),
                    _ => found,
                };
                List<IPAddress> addresses = chosen.Distinct().ToList();
                if (addresses.Count == 0)
                {
                    Return(WSAHOST_NOT_FOUND);
                    return;
                }

                long head = 0, previous = 0;
                for (int i = 0; i < addresses.Count; i++)
                {
                    byte[] sockaddr = EncodeSockAddr(new IPEndPoint(addresses[i], port));
                    byte[]? canon = i == 0 && (flags & AiCanonName) != 0 && canonical is not null
                        ? wide ? Encoding.Unicode.GetBytes(canonical + "\0") : Z(canonical)
                        : null;
                    // One block per node: the addrinfo, its sockaddr and its canonical name, so freeaddrinfo
                    // frees exactly what was allocated.
                    long node32 = _emulator.AllocateHeap(32 + 28 + (canon?.Length ?? 0));
                    byte[] entry = new byte[32];
                    BitConverter.GetBytes(0).CopyTo(entry, 0);
                    BitConverter.GetBytes(addresses[i].AddressFamily == AddressFamily.InterNetworkV6 ? AfInet6 : AfInet).CopyTo(entry, 4);
                    BitConverter.GetBytes(socketType).CopyTo(entry, 8);
                    BitConverter.GetBytes(protocol).CopyTo(entry, 12);
                    BitConverter.GetBytes(sockaddr.Length).CopyTo(entry, 16);
                    BitConverter.GetBytes(canon is null ? 0u : (uint)(node32 + 60)).CopyTo(entry, 20);
                    BitConverter.GetBytes((uint)(node32 + 32)).CopyTo(entry, 24);
                    _emulator.WriteMemory(node32, entry);
                    _emulator.WriteMemory(node32 + 32, sockaddr.Concat(new byte[28 - sockaddr.Length]).ToArray());
                    if (canon is not null) _emulator.WriteMemory(node32 + 60, canon);
                    if (previous != 0) _emulator.WriteUInt32(previous + 28, (uint)node32);
                    else head = node32;
                    previous = node32;
                }

                _emulator.WriteUInt32(result, (uint)head);
                Return(0);
            }

            if (string.IsNullOrEmpty(node))
            {
                bool passive = (flags & AiPassive) != 0;
                List<IPAddress> local = [];
                if (family != AfInet) local.Add(passive ? IPAddress.IPv6Any : IPAddress.IPv6Loopback);
                if (family != AfInet6) local.Add(passive ? IPAddress.Any : IPAddress.Loopback);
                Build(local, null);
                return;
            }

            if (TryParseDottedQuad(node, out byte[] v4))
            {
                Build([new IPAddress(v4)], node);
                return;
            }

            if (IPAddress.TryParse(node, out IPAddress? literal) && literal.AddressFamily == AddressFamily.InterNetworkV6)
            {
                Build([literal], node);
                return;
            }

            if ((flags & AiNumericHost) != 0)
            {
                Return(WSAHOST_NOT_FOUND);
                return;
            }

            Note($"resolve {node}");
            Resolve(Dns.GetHostEntryAsync(node), entry => Build(entry.AddressList, entry.HostName), Return, $"getaddrinfo {node}");
        }

        /// <summary>void freeaddrinfo(PADDRINFOA): every node in the chain.</summary>
        private void FreeAddrInfo()
        {
            long node = _frame.Arg(0);
            for (int guard = 0; node != 0 && guard < 4096; guard++)
            {
                long next = _emulator.ReadUInt32(node + 28);
                _emulator.FreeHeap(node);
                node = next;
            }

            _frame.Return(0);
        }

        private const int NiNoFqdn = 1, NiNumericHost = 2, NiNameReqd = 4, NiNumericServ = 8, NiDgram = 0x10;

        /// <summary>
        /// INT getnameinfo(const SOCKADDR*, socklen_t, PCHAR host, DWORD hostLength, PCHAR service,
        /// DWORD serviceLength, INT flags), and GetNameInfoW.
        /// </summary>
        private void GetNameInfo(bool wide)
        {
            long address = _frame.Arg(0);
            int length = _frame.SignedArg(1);
            long host = _frame.Arg(2), hostLength = _frame.Arg(3), service = _frame.Arg(4), serviceLength = _frame.Arg(5);
            int flags = _frame.SignedArg(6);

            void Return(int error)
            {
                if (error != 0) SetLastError(error);
                _frame.Return(error);
            }

            if (_startups == 0)
            {
                Return(WSANOTINITIALISED);
                return;
            }

            int error = ReadSockAddr(address, length, AfUnspec, out IPEndPoint endpoint);
            if (error != 0)
            {
                Return(error);
                return;
            }

            if ((host == 0 || hostLength == 0) && (service == 0 || serviceLength == 0))
            {
                Return(WSAHOST_NOT_FOUND);
                return;
            }

            bool Put(long at, long room, string text)
            {
                if (at == 0 || room == 0) return true;
                if (room < text.Length + 1) return false;
                _emulator.WriteMemory(at, wide ? Encoding.Unicode.GetBytes(text + "\0") : Z(text));
                return true;
            }

            string serviceText = (flags & NiNumericServ) == 0 &&
                                 Services.FirstOrDefault(s => s.Port == endpoint.Port && s.Proto == ((flags & NiDgram) != 0 ? "udp" : "tcp")) is { } named
                ? named.Name
                : endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);

            void Finish(string hostText)
            {
                if ((flags & NiNoFqdn) != 0 && (flags & NiNumericHost) == 0 && hostText.IndexOf('.') is > 0 and var dot &&
                    !IPAddress.TryParse(hostText, out _))
                {
                    hostText = hostText[..dot];
                }

                if (!Put(host, hostLength, hostText) || !Put(service, serviceLength, serviceText))
                {
                    Return(WSAEFAULT);
                    return;
                }

                Return(0);
            }

            string numeric = endpoint.Address.ToString();
            if (host == 0 || hostLength == 0 || (flags & NiNumericHost) != 0)
            {
                if ((flags & NiNumericHost) != 0 && (flags & NiNameReqd) != 0)
                {
                    Return(WSAHOST_NOT_FOUND);
                    return;
                }

                Finish(numeric);
                return;
            }

            Note($"reverse-resolve {numeric}");
            Resolve(Dns.GetHostEntryAsync(endpoint.Address), entry => Finish(entry.HostName), code =>
            {
                if ((flags & NiNameReqd) != 0) Return(code);
                else Finish(numeric);
            }, $"getnameinfo {numeric}");
        }

        // ================================================================ WSA* scatter/gather and overlapped

        private (long Address, int Length)[] ReadBuffers(long array, int count)
        {
            var buffers = new (long, int)[Math.Clamp(count, 0, 1024)];
            for (int i = 0; i < buffers.Length; i++)
            {
                buffers[i] = (_emulator.ReadUInt32(array + (i * 8) + 4), unchecked((int)_emulator.ReadUInt32(array + (i * 8))));
            }

            return buffers;
        }

        private void Scatter((long Address, int Length)[] buffers, byte[] data)
        {
            int offset = 0;
            foreach (var (address, length) in buffers)
            {
                if (offset >= data.Length) break;
                int n = Math.Min(length, data.Length - offset);
                _emulator.WriteMemory(address, data[offset..(offset + n)]);
                offset += n;
            }
        }

        /// <summary>
        /// WSASend(s, WSABUF*, count, DWORD* sent, flags, OVERLAPPED*, routine) and WSASendTo(s, WSABUF*,
        /// count, DWORD* sent, flags, sockaddr* to, int toLength, OVERLAPPED*, routine).
        /// </summary>
        private void WsaSend(bool to)
        {
            uint handle = Handle(0);
            long array = _frame.Arg(1), sentPointer = _frame.Arg(3);
            int count = _frame.SignedArg(2), flags = _frame.SignedArg(4);
            long target = to ? _frame.Arg(5) : 0;
            int targetLength = to ? _frame.SignedArg(6) : 0;
            long overlapped = _frame.Arg(to ? 7 : 5), routine = _frame.Arg(to ? 8 : 6);
            if (Lookup(handle) is not { } s) return;

            byte[] data = ReadBuffers(array, count).SelectMany(b => b.Length > 0 ? _emulator.ReadMemory(b.Address, b.Length) : []).ToArray();
            IPEndPoint? destination = null;
            if (target != 0 && s.Type == SockDgram)
            {
                int error = ReadSockAddr(target, targetLength, s.Family, out IPEndPoint endpoint);
                if (error != 0)
                {
                    Fail(error);
                    return;
                }

                destination = endpoint;
            }

            if (!CanSend(s, flags, destination is not null)) return;

            if (overlapped == 0)
            {
                SendBytes(s, handle, data, flags, destination, n =>
                {
                    if (sentPointer != 0) _emulator.WriteUInt32(sentPointer, (uint)n);
                    _frame.Return(0);
                });
                return;
            }

            if (routine != 0)
            {
                Fail(WSAEOPNOTSUPP);   // completion routines need alertable waits, which this host has none of
                return;
            }

            StartOverlapped(handle, s, overlapped, sentPointer, 0,
                () => s.Type == SockDgram || Writable(s),
                () =>
                {
                    try
                    {
                        int n = destination is null ? s.Host.Send(data, ToFlags(flags)) : s.Host.SendTo(data, ToFlags(flags), destination);
                        return (0, n);
                    }
                    catch (SocketException ex)
                    {
                        return (ErrorOf(ex), 0);
                    }
                });
        }

        /// <summary>
        /// WSARecv(s, WSABUF*, count, DWORD* received, DWORD* flags, OVERLAPPED*, routine) and
        /// WSARecvFrom(..., DWORD* flags, sockaddr* from, int* fromLength, OVERLAPPED*, routine).
        /// </summary>
        private void WsaRecv(bool from)
        {
            uint handle = Handle(0);
            long array = _frame.Arg(1), receivedPointer = _frame.Arg(3), flagsPointer = _frame.Arg(4);
            int count = _frame.SignedArg(2);
            long source = from ? _frame.Arg(5) : 0, sourceLength = from ? _frame.Arg(6) : 0;
            long overlapped = _frame.Arg(from ? 7 : 5), routine = _frame.Arg(from ? 8 : 6);
            if (Lookup(handle) is not { } s) return;
            if (flagsPointer == 0)
            {
                Fail(WSAEFAULT);
                return;
            }

            int flags = unchecked((int)_emulator.ReadUInt32(flagsPointer));
            var buffers = ReadBuffers(array, count);
            int total = buffers.Sum(b => Math.Max(0, b.Length));
            if (!CanReceive(s, flags)) return;

            void Deliver(byte[] data, IPEndPoint? sender)
            {
                Scatter(buffers, data);
                if (source != 0 && sender is not null && s.Type == SockDgram) WriteSockAddr(sender, source, sourceLength);
            }

            if (overlapped == 0)
            {
                BlockOn(s, () => Readable(s), () =>
                {
                    (int error, byte[] data, IPEndPoint? sender) = ReceiveNow(s, total, flags);
                    if (error is not (0 or WSAEMSGSIZE))
                    {
                        Fail(error);
                        return;
                    }

                    Deliver(data, sender);
                    if (receivedPointer != 0) _emulator.WriteUInt32(receivedPointer, (uint)data.Length);
                    _emulator.WriteUInt32(flagsPointer, error == WSAEMSGSIZE ? 0x8000u : 0u);   // MSG_PARTIAL
                    if (error != 0)
                    {
                        Fail(error);
                        return;
                    }

                    _frame.Return(0);
                }, s.RecvTimeoutMs, $"WSARecv on socket 0x{handle:X}");
                return;
            }

            if (routine != 0)
            {
                Fail(WSAEOPNOTSUPP);
                return;
            }

            StartOverlapped(handle, s, overlapped, receivedPointer, flagsPointer, () => Readable(s), () =>
            {
                (int error, byte[] data, IPEndPoint? sender) = ReceiveNow(s, total, flags);
                if (error is 0 or WSAEMSGSIZE) Deliver(data, sender);
                return (error, data.Length);
            });
        }

        private void StartOverlapped(uint handle, GuestSocket s, long overlapped, long countPointer, long flagsPointer,
            Func<bool> ready, Func<(int Error, int Bytes)> perform)
        {
            var op = new Overlapped
            {
                Socket = handle,
                Event = _emulator.ReadUInt32(overlapped + 16),
                Ready = ready,
                Perform = perform,
            };
            _overlapped[overlapped] = op;
            if (op.Event != 0) _sync().ResetEvent(op.Event);

            if (ready())
            {
                Finish(overlapped, op);
                if (op.Error != 0)
                {
                    Fail(op.Error);
                    return;
                }

                if (countPointer != 0) _emulator.WriteUInt32(countPointer, (uint)op.Bytes);
                if (flagsPointer != 0) _emulator.WriteUInt32(flagsPointer, 0);
                _frame.Return(0);
                return;
            }

            // The overlapped status is STATUS_PENDING until the pump completes it.
            _emulator.WriteUInt32(overlapped, 0x103);
            Fail(WSA_IO_PENDING);
        }

        private void Finish(long address, Overlapped op)
        {
            (op.Error, op.Bytes) = op.Perform();
            op.Done = true;
            WriteOverlapped(address, op);
        }

        private void WriteOverlapped(long address, Overlapped op)
        {
            _emulator.WriteUInt32(address, op.Error == 0 ? 0u : 0xC0000001);
            _emulator.WriteUInt32(address + 4, (uint)op.Bytes);
            if (op.Event != 0) _sync().SetEvent(op.Event);
        }

        /// <summary>BOOL WSAGetOverlappedResult(s, OVERLAPPED*, DWORD* transferred, BOOL wait, DWORD* flags).</summary>
        private void GetOverlappedResult()
        {
            uint handle = Handle(0);
            long overlapped = _frame.Arg(1), transferred = _frame.Arg(2), flagsPointer = _frame.Arg(4);
            bool wait = _frame.Arg(3) != 0;
            if (!Started()) return;
            if (!_sockets.ContainsKey(handle) && !(_overlapped.TryGetValue(overlapped, out var gone) && gone.Done))
            {
                Fail(WSAENOTSOCK);
                return;
            }

            if (!_overlapped.TryGetValue(overlapped, out Overlapped? op))
            {
                Fail(WSAEINVAL);
                return;
            }

            void Report()
            {
                if (transferred != 0) _emulator.WriteUInt32(transferred, (uint)op.Bytes);
                if (flagsPointer != 0) _emulator.WriteUInt32(flagsPointer, 0);
                if (op.Error != 0)
                {
                    SetLastError(op.Error);
                    _frame.Return(0);
                    return;
                }

                _frame.Return(1);
            }

            Pump();
            if (op.Done)
            {
                Report();
                return;
            }

            if (!wait)
            {
                SetLastError(WSA_IO_INCOMPLETE);
                _frame.Return(0);
                return;
            }

            Wait(() => { Pump(); return op.Done; }, Report, () =>
            {
                SetLastError(WSA_IO_INCOMPLETE);
                _frame.Return(0);
            }, null, "WSAGetOverlappedResult");
        }

        // ================================================================ event selection

        private static int Bit(int networkEvent) => networkEvent switch
        {
            FdRead => 0, FdWrite => 1, FdOob => 2, FdAccept => 3, FdConnect => 4, _ => 5,
        };

        private void Record(GuestSocket s, int networkEvent, int error)
        {
            if (networkEvent == FdConnect)
            {
                if (!s.ConnectArmed) return;
                s.ConnectArmed = false;
            }

            s.Recorded |= networkEvent;
            s.Errors[Bit(networkEvent)] = error;
            if (s.Event != 0 && (s.Recorded & s.EventMask) != 0) _sync().SetEvent(s.Event);
        }

        /// <summary>
        /// Records network events on event-selected sockets and completes overlapped I/O that has
        /// become possible. Called by every wait in this library and once per frame by the host.
        /// </summary>
        public void Pump()
        {
            foreach (GuestSocket s in _sockets.Values)
            {
                if (s.Event == 0) continue;
                UpdateConnect(s);
                if (s.Listening)
                {
                    if (s.AcceptArmed && Readable(s))
                    {
                        s.AcceptArmed = false;
                        Record(s, FdAccept, 0);
                    }

                    continue;
                }

                bool connected = s.Type == SockDgram || s.State == ConnectState.Connected;
                if (!connected) continue;
                int available;
                try
                {
                    available = s.Host.Available;
                }
                catch (SocketException)
                {
                    available = 0;
                }

                if (s.ReadArmed && available > 0)
                {
                    s.ReadArmed = false;
                    Record(s, FdRead, 0);
                }
                else if (s.Type == SockStream && s.CloseArmed && available == 0 && Readable(s))
                {
                    s.CloseArmed = false;
                    Record(s, FdClose, 0);
                }

                if (s.WriteArmed && Writable(s))
                {
                    s.WriteArmed = false;
                    Record(s, FdWrite, 0);
                }
            }

            foreach ((long address, Overlapped op) in _overlapped.ToList())
            {
                if (!op.Done && op.Ready()) Finish(address, op);
            }
        }

        /// <summary>int WSAEventSelect(SOCKET, WSAEVENT, long networkEvents): makes the socket non-blocking.</summary>
        private void EventSelect()
        {
            if (Lookup(Handle(0)) is not { } s) return;
            long handle = _frame.Arg(1);
            int mask = _frame.SignedArg(2);
            if (mask != 0 && _sync().CouldWait(handle) is null)
            {
                Fail(WSAEINVAL);
                return;
            }

            s.Event = mask == 0 ? 0 : handle;
            s.EventMask = mask;
            s.Recorded = 0;
            s.NonBlocking = true;
            s.ReadArmed = s.AcceptArmed = s.WriteArmed = s.CloseArmed = true;
            Pump();
            _frame.Return(0);
        }

        /// <summary>int WSAEnumNetworkEvents(SOCKET, WSAEVENT, WSANETWORKEVENTS*): reports and clears.</summary>
        private void EnumNetworkEvents()
        {
            if (Lookup(Handle(0)) is not { } s) return;
            long handle = _frame.Arg(1), output = _frame.Arg(2);
            if (output == 0)
            {
                Fail(WSAEFAULT);
                return;
            }

            Pump();
            byte[] block = new byte[44];
            BitConverter.GetBytes(s.Recorded & s.EventMask).CopyTo(block, 0);
            for (int i = 0; i < 10; i++) BitConverter.GetBytes(s.Errors[i]).CopyTo(block, 4 + (i * 4));
            _emulator.WriteMemory(output, block);
            s.Recorded &= ~s.EventMask;
            Array.Clear(s.Errors);
            if (handle != 0) _sync().ResetEvent(handle);
            _frame.Return(0);
        }

        /// <summary>DWORD WSAWaitForMultipleEvents(DWORD count, const WSAEVENT*, BOOL waitAll, DWORD timeout, BOOL alertable).</summary>
        private void WaitForMultipleEvents()
        {
            const uint waitFailed = 0xFFFFFFFF, waitTimeout = 0x102;
            int count = _frame.SignedArg(0);
            long array = _frame.Arg(1);
            bool all = _frame.Arg(2) != 0;
            uint timeout = (uint)_frame.Arg(3);
            if (count is < 1 or > 64 || array == 0)
            {
                SetLastError(WSA_INVALID_PARAMETER);
                _frame.Return(waitFailed);
                return;
            }

            long[] events = Enumerable.Range(0, count).Select(i => (long)_emulator.ReadUInt32(array + (i * 4))).ToArray();
            if (events.Any(e => _sync().CouldWait(e) is null))
            {
                SetLastError(WSA_INVALID_HANDLE);
                _frame.Return(waitFailed);
                return;
            }

            bool Ready()
            {
                Pump();
                return all ? events.All(e => _sync().CouldWait(e) == true) : events.Any(e => _sync().CouldWait(e) == true);
            }

            void Complete()
            {
                if (all)
                {
                    foreach (long e in events) _sync().TryWait(e);
                    _frame.Return(0);
                    return;
                }

                for (int i = 0; i < events.Length; i++)
                {
                    if (_sync().TryWait(events[i]))
                    {
                        _frame.Return(i);
                        return;
                    }
                }

                _frame.Return(waitTimeout);
            }

            if (Ready() || timeout == 0)
            {
                if (Ready()) Complete();
                else _frame.Return(waitTimeout);
                return;
            }

            Wait(Ready, Complete, () => _frame.Return(waitTimeout), timeout == 0xFFFFFFFF ? null : timeout, "WSAWaitForMultipleEvents");
        }
    }
}
