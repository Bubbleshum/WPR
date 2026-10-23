using System;
using System.Collections.Concurrent;
using System.Threading;

namespace WPR.SilverlightCompability.Threading
{
    /// <summary>
    /// Shim for <c>System.Windows.Threading.Dispatcher</c>: work posted with
    /// <see cref="BeginInvoke(Action)"/> runs later, on the UI thread, as Silverlight's did.
    /// </summary>
    /// <remarks>
    /// <para>The Silverlight host (<c>SilverlightGame</c>) attaches the queue to its thread
    /// (<see cref="AttachToCurrentThread"/>) and drains it once a frame (<see cref="PumpQueued"/>).
    /// With no host attached (an XNA title, a test) a post still runs inline, which is what every
    /// post did before.</para>
    /// <para>Running posts inline is not merely early, it reorders a page's own code. Microsoft
    /// Solitaire's GamePage posts SetupGameMode and THEN subscribes to the ReadyToSetGame event
    /// that SetupGameMode raises; inline, the event fired with no subscriber and the game page sat
    /// on its preloader for ever. Galactic Reign's navigation, which re-posts itself, recursed
    /// until the stack overflowed.</para>
    /// </remarks>
    public class Dispatcher
    {
        internal static readonly Dispatcher Shared = new Dispatcher();

        private static readonly ConcurrentQueue<Action> Queue = new();
        private static int _uiThreadId;

        // The context this thread had before AttachToCurrentThread installed the UI thread's.
        private static SynchronizationContext? _previousContext;

        /// <summary>Posts from now on are queued for this thread, which must call <see cref="PumpQueued"/>.
        /// The thread also becomes the UI thread to <see cref="SynchronizationContext.Current"/>
        /// (see <see cref="DispatcherSynchronizationContext"/>).</summary>
        public static void AttachToCurrentThread()
        {
            Queue.Clear();
            _uiThreadId = Environment.CurrentManagedThreadId;
            _previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Shared));
        }

        /// <summary>Back to inline posts, dropping anything queued (a game run ended).</summary>
        public static void Detach()
        {
            if (_uiThreadId == Environment.CurrentManagedThreadId
                && SynchronizationContext.Current is DispatcherSynchronizationContext)
                SynchronizationContext.SetSynchronizationContext(_previousContext);
            _previousContext = null;
            _uiThreadId = 0;
            Queue.Clear();
        }

        /// <summary>Runs the posts queued so far; a post made while pumping waits for the next pump.</summary>
        public static void PumpQueued()
        {
            for (int count = Queue.Count; count > 0 && Queue.TryDequeue(out Action? action); count--)
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"[wpr-dispatcher] a posted callback threw: {ex}");
                }
            }
        }

        public bool CheckAccess() => _uiThreadId == 0 || _uiThreadId == Environment.CurrentManagedThreadId;

        public DispatcherOperation BeginInvoke(Delegate d, params object?[] args)
        {
            Post(() => d.DynamicInvoke(args));
            return new DispatcherOperation();
        }

        public DispatcherOperation BeginInvoke(Action a)
        {
            Post(a);
            return new DispatcherOperation();
        }

        public DispatcherOperation BeginInvoke<T>(Action<T> a, T arg)
        {
            Post(() => a(arg));
            return new DispatcherOperation();
        }

        private static void Post(Action action)
        {
            if (Volatile.Read(ref _uiThreadId) != 0)
            {
                Queue.Enqueue(action);
                return;
            }

            try { action(); } catch { /* swallow per SL semantics */ }
        }
    }
}
