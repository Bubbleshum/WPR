using System;
using System.Threading;

namespace WPR.SilverlightCompability.Threading
{
    /// <summary>
    /// Shim for <c>System.Windows.Threading.DispatcherSynchronizationContext</c>: the UI thread's
    /// synchronisation context, which posts through the <see cref="Dispatcher"/>.
    /// </summary>
    /// <remarks>
    /// <para><see cref="Dispatcher.AttachToCurrentThread"/> installs one on the Silverlight host's
    /// thread, as Silverlight had on its UI thread. Without it, everything that returns to "the
    /// thread that started it" through <see cref="SynchronizationContext.Current"/> ran on the
    /// thread pool instead: <c>BackgroundWorker.RunWorkerCompleted</c> and <c>ProgressChanged</c>,
    /// and every <c>await</c> continuation in a WP8 app. Minesweeper (WP7, <c>0b00c4a3-…</c>) fills
    /// its board in a BackgroundWorker and builds the page in RunWorkerCompleted; on a pool thread
    /// that raced the page's own layout and the process died with a NullReferenceException in
    /// <c>GameplayPage</c> about half the time a game was started.</para>
    /// <para>WPR code that blocks the host thread on a task must not await on this context, or the
    /// continuation waits for the thread it blocks. The GamerServices calls do their work in
    /// <c>Task.Run</c> (no context there) and LeaderboardReader uses ConfigureAwait(false).</para>
    /// </remarks>
    public class DispatcherSynchronizationContext : SynchronizationContext
    {
        private readonly Dispatcher _dispatcher;

        public DispatcherSynchronizationContext() : this(Dispatcher.Shared) { }

        public DispatcherSynchronizationContext(Dispatcher dispatcher) =>
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        public override void Post(SendOrPostCallback d, object? state) => _dispatcher.BeginInvoke(() => d(state));

        /// <summary>Runs <paramref name="d"/> on the UI thread and waits for it, as Silverlight's does.</summary>
        public override void Send(SendOrPostCallback d, object? state)
        {
            if (_dispatcher.CheckAccess())
            {
                d(state);
                return;
            }
            using var done = new ManualResetEventSlim();
            Exception? failure = null;
            _dispatcher.BeginInvoke(() =>
            {
                try { d(state); }
                catch (Exception ex) { failure = ex; }
                finally { done.Set(); }
            });
            done.Wait();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        public override SynchronizationContext CreateCopy() => new DispatcherSynchronizationContext(_dispatcher);
    }
}
