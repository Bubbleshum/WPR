using System;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;

namespace WPR.WindowsCompability
{
    /// <summary>
    /// Static stand-ins for <see cref="Socket"/>'s <c>*Async(SocketAsyncEventArgs)</c> methods,
    /// installed by <c>ApplicationPatcher.RedirectSocketCalls</c> (patcher v55), which rewrites
    /// every such call site in a game to the method here with the socket as argument zero.
    ///
    /// <para><b>What differs.</b> On Silverlight (and so on WP7) these operations always completed
    /// asynchronously: they returned <c>true</c> and raised <see cref="SocketAsyncEventArgs.Completed"/>
    /// later, on a worker thread. .NET may finish one immediately, and then it returns <c>false</c>
    /// and does NOT raise <c>Completed</c>; the caller is meant to read the result itself. A small
    /// send on a connected socket nearly always finishes that way. WP7 titles were written against
    /// the Silverlight contract and routinely ignore the return value.</para>
    ///
    /// <para><b>Reference case: Pool Pro Online 3</b> (<c>062d24c2-…</c>). Its <c>TCPManager.sendData</c>
    /// calls <c>SendAsync</c> and then <c>clientDone.WaitOne()</c> with no timeout; only the
    /// <c>Completed</c> handler sets <c>clientDone</c>. The register packet went out, the server
    /// accepted it, and the game thread then waited for ever on "Connecting to server…". No
    /// exception, nothing in any log, and the TCP connection stayed healthy.</para>
    ///
    /// <para>So each method here calls the real one and, when that finished at once, raises
    /// <c>Completed</c> itself on a thread-pool thread and returns <c>true</c>: the Silverlight
    /// contract. The raise goes through the protected <c>OnCompleted</c>, the same path .NET
    /// takes, so the sender is the socket as usual.</para>
    /// </summary>
    public static class SilverlightSockets
    {
        private static readonly MethodInfo? OnCompleted = typeof(SocketAsyncEventArgs).GetMethod(
            "OnCompleted", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(SocketAsyncEventArgs) }, null);

        public static bool ConnectAsync(Socket socket, SocketAsyncEventArgs e) => Pending(socket.ConnectAsync(e), e);

        public static bool ConnectAsync(SocketType socketType, ProtocolType protocolType, SocketAsyncEventArgs e) =>
            Pending(Socket.ConnectAsync(socketType, protocolType, e), e);

        public static bool SendAsync(Socket socket, SocketAsyncEventArgs e) => Pending(socket.SendAsync(e), e);

        public static bool ReceiveAsync(Socket socket, SocketAsyncEventArgs e) => Pending(socket.ReceiveAsync(e), e);

        public static bool SendToAsync(Socket socket, SocketAsyncEventArgs e) => Pending(socket.SendToAsync(e), e);

        public static bool ReceiveFromAsync(Socket socket, SocketAsyncEventArgs e) => Pending(socket.ReceiveFromAsync(e), e);

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<EventHandler<SocketAsyncEventArgs>, EventHandler<SocketAsyncEventArgs>> Wrapped = new();

        /// <summary>
        /// <c>e.Completed += handler</c> in a game (patcher v56). A receive's callback runs between the
        /// game's frames (<see cref="WPR.Xna.Rhi.GameFrameGate"/>): on the single-core phones WP7
        /// games were written for, it never overlapped the frame, and games call into their game
        /// logic from it without a lock. Pool Pro Online 3 lost packets that way on a multi-core
        /// machine. Connect and send callbacks run as they come: a game's frame may be waiting for
        /// one (Pool Pro's waits for every send).
        /// </summary>
        public static void AddCompleted(SocketAsyncEventArgs e, EventHandler<SocketAsyncEventArgs>? handler)
        {
            if (handler == null) return;
            EventHandler<SocketAsyncEventArgs> wrapper = Wrapped.GetValue(handler, h => (sender, args) =>
            {
                if (args.LastOperation is SocketAsyncOperation.Receive or SocketAsyncOperation.ReceiveFrom
                    or SocketAsyncOperation.ReceiveMessageFrom)
                {
                    WPR.Xna.Rhi.GameFrameGate.RunBetweenFrames(() => h(sender, args));
                }
                else
                {
                    h(sender, args);
                }
            });
            e.Completed += wrapper;
        }

        /// <summary><c>e.Completed -= handler</c> in a game: removes the wrapper <see cref="AddCompleted"/> made.</summary>
        public static void RemoveCompleted(SocketAsyncEventArgs e, EventHandler<SocketAsyncEventArgs>? handler)
        {
            if (handler != null && Wrapped.TryGetValue(handler, out EventHandler<SocketAsyncEventArgs>? wrapper))
            {
                e.Completed -= wrapper;
            }
        }

        /// <summary>Always <c>true</c>: if the operation already finished, <c>Completed</c> is raised later.</summary>
        internal static bool Pending(bool pending, SocketAsyncEventArgs e)
        {
            if (!pending)
            {
                // Later, not here: the caller hasn't reached the code that waits for it yet, and
                // Silverlight never raised Completed on the calling thread.
                ThreadPool.UnsafeQueueUserWorkItem(static args => Raise(args), e, preferLocal: false);
            }
            return true;
        }

        [DynamicDependency("OnCompleted(System.Net.Sockets.SocketAsyncEventArgs)", typeof(SocketAsyncEventArgs))]
        private static void Raise(SocketAsyncEventArgs e)
        {
            if (OnCompleted is null)
            {
                System.Diagnostics.Trace.WriteLine("[wpr-socket] SocketAsyncEventArgs.OnCompleted not found; a synchronous completion was not reported");
                return;
            }
            try
            {
                OnCompleted.Invoke(e, new object[] { e });
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                // A game handler threw, as it could have from .NET's own worker thread.
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            }
        }
    }
}
