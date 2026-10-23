using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;

namespace WPR.WinRT
{
    /// <summary>
    /// A WinRT async object over a <see cref="Task"/>: what WPR's own WinRT surface
    /// (Microsoft.Xbox, AsAsyncAction, AsyncInfo.Run) hands a game.
    /// </summary>
    /// <remarks>
    /// <para><b>Completed fires exactly once</b>, whenever it is set relative to completion. WinRT
    /// raises it at once (on the setting thread) when the operation has already finished, and a
    /// WP8 title routinely sets it after a call that WPR answers synchronously. Otherwise it runs
    /// on a thread-pool thread when the task ends, as WinRT ran it on a worker; titles marshal to
    /// their UI thread themselves.</para>
    /// <para><b>GetResults</b> throws what the task threw, the way the projection did, and
    /// <see cref="InvalidOperationException"/> before completion.</para>
    /// </remarks>
    public abstract class TaskAsyncInfo : IAsyncInfo
    {
        private static int _nextId;
        private readonly CancellationTokenSource? _cancel;
        private int _completedRaised;

        protected TaskAsyncInfo(Task task, CancellationTokenSource? cancel)
        {
            Task = task;
            _cancel = cancel;
            Id = (uint)Interlocked.Increment(ref _nextId);
        }

        /// <summary>The task this object reports; <c>AsTask</c> hands it back unwrapped.</summary>
        public Task Task { get; }

        public uint Id { get; }

        public AsyncStatus Status => Task.Status switch
        {
            TaskStatus.RanToCompletion => AsyncStatus.Completed,
            TaskStatus.Canceled => AsyncStatus.Canceled,
            TaskStatus.Faulted => AsyncStatus.Error,
            _ => AsyncStatus.Started,
        };

        public Exception? ErrorCode => Task.IsFaulted ? Unwrap(Task.Exception) : null;

        public void Cancel()
        {
            try { _cancel?.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        public void Close()
        {
        }

        /// <summary>Raise <paramref name="raise"/> once: now if done, else when the task ends.</summary>
        protected void OnCompletedSet(Action raise)
        {
            if (Task.IsCompleted)
            {
                RaiseOnce(raise);
                return;
            }

            Task.ContinueWith(_ => RaiseOnce(raise), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private void RaiseOnce(Action raise)
        {
            if (Interlocked.Exchange(ref _completedRaised, 1) == 0)
            {
                raise();
            }
        }

        protected void ThrowIfNotSucceeded()
        {
            switch (Task.Status)
            {
                case TaskStatus.RanToCompletion:
                    return;
                case TaskStatus.Faulted:
                    throw Unwrap(Task.Exception) ?? new InvalidOperationException("The operation failed.");
                case TaskStatus.Canceled:
                    throw new OperationCanceledException();
                default:
                    throw new InvalidOperationException("The operation has not completed.");
            }
        }

        private static Exception? Unwrap(AggregateException? ex) =>
            ex is null ? null : ex.InnerExceptions.Count == 1 ? ex.InnerExceptions[0] : ex;
    }

    /// <summary><see cref="IAsyncAction"/> over a <see cref="Task"/>.</summary>
    public sealed class TaskAsyncAction : TaskAsyncInfo, IAsyncAction
    {
        private AsyncActionCompletedHandler? _completed;

        public TaskAsyncAction(Task task, CancellationTokenSource? cancel = null) : base(task, cancel)
        {
        }

        public AsyncActionCompletedHandler? get_Completed() => _completed;

        public void put_Completed(AsyncActionCompletedHandler? handler)
        {
            _completed = handler;
            if (handler != null)
            {
                OnCompletedSet(() => handler(this, Status));
            }
        }

        public void GetResults() => ThrowIfNotSucceeded();
    }

    /// <summary><see cref="IAsyncOperation{TResult}"/> over a <see cref="Task{TResult}"/>.</summary>
    public sealed class TaskAsyncOperation<TResult> : TaskAsyncInfo, IAsyncOperation<TResult>
    {
        private AsyncOperationCompletedHandler<TResult>? _completed;

        public TaskAsyncOperation(Task<TResult> task, CancellationTokenSource? cancel = null) : base(task, cancel)
        {
        }

        public AsyncOperationCompletedHandler<TResult>? get_Completed() => _completed;

        public void put_Completed(AsyncOperationCompletedHandler<TResult>? handler)
        {
            _completed = handler;
            if (handler != null)
            {
                OnCompletedSet(() => handler(this, Status));
            }
        }

        public TResult GetResults()
        {
            ThrowIfNotSucceeded();
            return ((Task<TResult>)Task).Result;
        }
    }

    /// <summary>
    /// <see cref="IAsyncOperationWithProgress{TResult, TProgress}"/> over a <see cref="Task{TResult}"/>.
    /// No progress is reported: WPR answers stream reads and writes at once.
    /// </summary>
    public sealed class TaskAsyncOperationWithProgress<TResult, TProgress> : TaskAsyncInfo, IAsyncOperationWithProgress<TResult, TProgress>
    {
        private AsyncOperationWithProgressCompletedHandler<TResult, TProgress>? _completed;
        private AsyncOperationProgressHandler<TResult, TProgress>? _progress;

        public TaskAsyncOperationWithProgress(Task<TResult> task, CancellationTokenSource? cancel = null) : base(task, cancel)
        {
        }

        public AsyncOperationProgressHandler<TResult, TProgress>? get_Progress() => _progress;

        public void put_Progress(AsyncOperationProgressHandler<TResult, TProgress>? handler) => _progress = handler;

        public AsyncOperationWithProgressCompletedHandler<TResult, TProgress>? get_Completed() => _completed;

        public void put_Completed(AsyncOperationWithProgressCompletedHandler<TResult, TProgress>? handler)
        {
            _completed = handler;
            if (handler != null)
            {
                OnCompletedSet(() => handler(this, Status));
            }
        }

        public TResult GetResults()
        {
            ThrowIfNotSucceeded();
            return ((Task<TResult>)Task).Result;
        }
    }

    /// <summary>Ready-made WinRT async results for WPR's own WinRT surface.</summary>
    public static class WinRtAsync
    {
        public static IAsyncAction Completed() => new TaskAsyncAction(Task.CompletedTask);

        public static IAsyncOperation<T> FromResult<T>(T value) => new TaskAsyncOperation<T>(Task.FromResult(value));

        public static IAsyncAction Failed(Exception error) => new TaskAsyncAction(Task.FromException(error));

        public static IAsyncOperation<T> Failed<T>(Exception error) => new TaskAsyncOperation<T>(Task.FromException<T>(error));

        /// <summary>Run <paramref name="work"/> on the thread pool: for anything that may wait on disk or the network.</summary>
        public static IAsyncAction Run(Action work) => new TaskAsyncAction(Task.Run(work));

        /// <inheritdoc cref="Run(Action)"/>
        public static IAsyncOperation<T> Run<T>(Func<T> work) => new TaskAsyncOperation<T>(Task.Run(work));
    }
}
