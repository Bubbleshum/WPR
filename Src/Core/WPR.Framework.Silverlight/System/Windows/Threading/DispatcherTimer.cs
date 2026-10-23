using System;
using System.Threading;
using Microsoft.Xna.Framework;

namespace WPR.SilverlightCompability.Threading
{
    /// <summary>Shim for <c>System.Windows.Threading.DispatcherTimer</c>.</summary>
    /// <remarks>
    /// <para><b>Tick is raised on the game thread</b>, at the start of the next frame after the
    /// interval elapses (<see cref="WprGameThread"/>), which is where Silverlight raised it: on the
    /// UI thread. Until patcher version 47 it was raised straight from a thread-pool timer, so game
    /// handlers ran concurrently with the frame. The WP Toolkit's GestureListener is the case that
    /// made it matter: its 100 ms timer reads <c>TouchPanel</c> gestures and raises the game's
    /// Hold/Drag handlers, racing the game thread's <c>TouchPanel.Update</c>.</para>
    ///
    /// <para><b>At most one tick is queued</b>: a slow frame does not build a backlog that then
    /// fires in a burst, which matches a dispatcher timer's coalescing. A tick queued before
    /// <see cref="Stop"/> (or a restart) is dropped, so a stopped timer never ticks again.</para>
    ///
    /// <para><b>Start/Stop are thread-safe.</b> Games stop timers from BackgroundWorker completion
    /// handlers (Sudoku's MainPage does), and the old check-then-dispose raced the game thread into
    /// a NullReferenceException that killed the process.</para>
    /// </remarks>
    public class DispatcherTimer
    {
        private System.Timers.Timer? _timer;
        private int _tickQueued;

        public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);
        public bool IsEnabled => Volatile.Read(ref _timer) != null;

        public event EventHandler? Tick;

        public void Start()
        {
            var timer = new System.Timers.Timer(Math.Max(1, Interval.TotalMilliseconds)) { AutoReset = true };
            timer.Elapsed += (_, _) => QueueTick(timer);
            DisposeTimer(Interlocked.Exchange(ref _timer, timer));
            timer.Start();
        }

        public void Stop() => DisposeTimer(Interlocked.Exchange(ref _timer, null));

        private void QueueTick(System.Timers.Timer source)
        {
            if (Interlocked.Exchange(ref _tickQueued, 1) == 1) return;
            WprGameThread.Post(() =>
            {
                Volatile.Write(ref _tickQueued, 0);
                if (ReferenceEquals(Volatile.Read(ref _timer), source)) Tick?.Invoke(this, EventArgs.Empty);
            });
        }

        private static void DisposeTimer(System.Timers.Timer? timer)
        {
            if (timer == null) return;
            timer.Stop();
            timer.Dispose();
        }
    }
}
