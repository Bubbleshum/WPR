using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using WPR.SilverlightCompability.Threading;
using Xunit;

namespace WPR.SilverlightCompability.Tests
{
    /// <summary>The Silverlight host's thread is the UI thread to SynchronizationContext.Current, so
    /// BackgroundWorker completion and await continuations come back to it (Minesweeper WP7).</summary>
    [Collection("Dispatcher")]
    public class DispatcherContextTests
    {
        private static void PumpUntil(Func<bool> done)
        {
            var clock = Stopwatch.StartNew();
            while (!done() && clock.Elapsed < TimeSpan.FromSeconds(10))
            {
                Dispatcher.PumpQueued();
                Thread.Sleep(5);
            }
            Assert.True(done());
        }

        [Fact]
        public void Attach_InstallsTheContext_AndDetachRestoresTheOldOne()
        {
            SynchronizationContext? before = SynchronizationContext.Current;
            Dispatcher.AttachToCurrentThread();
            try { Assert.IsType<DispatcherSynchronizationContext>(SynchronizationContext.Current); }
            finally { Dispatcher.Detach(); }
            Assert.Same(before, SynchronizationContext.Current);
        }

        [Fact]
        public void BackgroundWorker_CompletesOnTheAttachedThread()
        {
            int uiThread = Environment.CurrentManagedThreadId;
            int? completedOn = null, progressOn = null;
            Dispatcher.AttachToCurrentThread();
            try
            {
                var worker = new BackgroundWorker { WorkerReportsProgress = true };
                worker.DoWork += (s, e) => worker.ReportProgress(50);
                worker.ProgressChanged += (s, e) => progressOn = Environment.CurrentManagedThreadId;
                worker.RunWorkerCompleted += (s, e) => completedOn = Environment.CurrentManagedThreadId;
                worker.RunWorkerAsync();

                Thread.Sleep(100);
                Assert.Null(completedOn); // waits for the UI thread to pump
                PumpUntil(() => completedOn != null);
                Assert.Equal(uiThread, completedOn);
                Assert.Equal(uiThread, progressOn);
            }
            finally { Dispatcher.Detach(); }
        }

        [Fact]
        public void AwaitContinuation_ReturnsToTheAttachedThread()
        {
            int uiThread = Environment.CurrentManagedThreadId;
            int? resumedOn = null;
            Dispatcher.AttachToCurrentThread();
            try
            {
                async Task Run()
                {
                    await Task.Delay(10);
                    resumedOn = Environment.CurrentManagedThreadId;
                }
                Task task = Run();
                PumpUntil(() => task.IsCompleted);
                Assert.Equal(uiThread, resumedOn);
            }
            finally { Dispatcher.Detach(); }
        }
    }
}
