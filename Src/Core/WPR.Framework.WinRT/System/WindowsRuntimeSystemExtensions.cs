using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using WPR.WinRT;

namespace System
{
    /// <summary>
    /// Shim for <c>System.WindowsRuntimeSystemExtensions</c> (System.Runtime.WindowsRuntime):
    /// what C# compiled <c>await</c> on a WinRT operation, and <c>AsTask</c> / <c>AsAsyncAction</c>,
    /// into. 80 packages in the library await a WinRT operation this way.
    /// </summary>
    /// <remarks>
    /// <para>An object WPR made (<see cref="TaskAsyncInfo"/>) hands back its own task. Anything
    /// else is bridged through its Completed handler, as the real projection did.</para>
    /// <para><b>A null source completes at once with a default result</b> rather than throwing
    /// ArgumentNullException as .NET did. Every null async object a game can hold here comes from
    /// a WinmdStubber stub (a WinRT component WPR does not implement), and treating those calls as
    /// "done, nothing to report" is what keeps the rest of the title running.</para>
    /// </remarks>
    public static class WindowsRuntimeSystemExtensions
    {
        private static int _nullSourcesReported;

        public static Task AsTask(this IAsyncAction source) => AsTask(source, CancellationToken.None);

        public static Task AsTask(this IAsyncAction source, CancellationToken cancellationToken)
        {
            if (source is null) return NullSource(Task.CompletedTask);
            if (source is TaskAsyncInfo own) return own.Task;

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Register(source, cancellationToken, () => tcs.TrySetCanceled(cancellationToken));
            source.put_Completed((op, status) => Settle(status, tcs, () => op.GetResults(), op.ErrorCode));
            return tcs.Task;
        }

        public static TaskAwaiter GetAwaiter(this IAsyncAction source) => source.AsTask().GetAwaiter();

        public static Task<TResult> AsTask<TResult>(this IAsyncOperation<TResult> source) => AsTask(source, CancellationToken.None);

        public static Task<TResult> AsTask<TResult>(this IAsyncOperation<TResult> source, CancellationToken cancellationToken)
        {
            if (source is null) return NullSource(Task.FromResult(default(TResult)!));
            if (source is TaskAsyncInfo own && own.Task is Task<TResult> typed) return typed;

            var tcs = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Register(source, cancellationToken, () => tcs.TrySetCanceled(cancellationToken));
            source.put_Completed((op, status) => Settle(status, tcs, op.GetResults, op.ErrorCode));
            return tcs.Task;
        }

        public static TaskAwaiter<TResult> GetAwaiter<TResult>(this IAsyncOperation<TResult> source) => source.AsTask().GetAwaiter();

        public static Task AsTask<TProgress>(this IAsyncActionWithProgress<TProgress> source) =>
            AsTask(source, CancellationToken.None, null);

        public static Task AsTask<TProgress>(this IAsyncActionWithProgress<TProgress> source, CancellationToken cancellationToken) =>
            AsTask(source, cancellationToken, null);

        public static Task AsTask<TProgress>(this IAsyncActionWithProgress<TProgress> source, IProgress<TProgress>? progress) =>
            AsTask(source, CancellationToken.None, progress);

        public static Task AsTask<TProgress>(this IAsyncActionWithProgress<TProgress> source, CancellationToken cancellationToken, IProgress<TProgress>? progress)
        {
            if (source is null) return NullSource(Task.CompletedTask);
            if (source is TaskAsyncInfo own) return own.Task;

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Register(source, cancellationToken, () => tcs.TrySetCanceled(cancellationToken));
            if (progress != null) source.put_Progress((_, p) => progress.Report(p));
            source.put_Completed((op, status) => Settle(status, tcs, () => op.GetResults(), op.ErrorCode));
            return tcs.Task;
        }

        public static TaskAwaiter GetAwaiter<TProgress>(this IAsyncActionWithProgress<TProgress> source) => source.AsTask().GetAwaiter();

        public static Task<TResult> AsTask<TResult, TProgress>(this IAsyncOperationWithProgress<TResult, TProgress> source) =>
            AsTask(source, CancellationToken.None, null);

        public static Task<TResult> AsTask<TResult, TProgress>(this IAsyncOperationWithProgress<TResult, TProgress> source, CancellationToken cancellationToken) =>
            AsTask(source, cancellationToken, null);

        public static Task<TResult> AsTask<TResult, TProgress>(this IAsyncOperationWithProgress<TResult, TProgress> source, IProgress<TProgress>? progress) =>
            AsTask(source, CancellationToken.None, progress);

        public static Task<TResult> AsTask<TResult, TProgress>(this IAsyncOperationWithProgress<TResult, TProgress> source, CancellationToken cancellationToken, IProgress<TProgress>? progress)
        {
            if (source is null) return NullSource(Task.FromResult(default(TResult)!));
            if (source is TaskAsyncInfo own && own.Task is Task<TResult> typed) return typed;

            var tcs = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Register(source, cancellationToken, () => tcs.TrySetCanceled(cancellationToken));
            if (progress != null) source.put_Progress((_, p) => progress.Report(p));
            source.put_Completed((op, status) => Settle(status, tcs, op.GetResults, op.ErrorCode));
            return tcs.Task;
        }

        public static TaskAwaiter<TResult> GetAwaiter<TResult, TProgress>(this IAsyncOperationWithProgress<TResult, TProgress> source) =>
            source.AsTask().GetAwaiter();

        public static IAsyncAction AsAsyncAction(this Task source)
        {
            ArgumentNullException.ThrowIfNull(source);
            return new TaskAsyncAction(source);
        }

        public static IAsyncOperation<TResult> AsAsyncOperation<TResult>(this Task<TResult> source)
        {
            ArgumentNullException.ThrowIfNull(source);
            return new TaskAsyncOperation<TResult>(source);
        }

        private static void Register(IAsyncInfo source, CancellationToken token, Action setCanceled)
        {
            if (token.CanBeCanceled)
            {
                token.Register(() =>
                {
                    try { source.Cancel(); } catch { }
                    setCanceled();
                });
            }
        }

        private static void Settle(AsyncStatus status, TaskCompletionSource tcs, Action getResults, Exception? error)
        {
            switch (status)
            {
                case AsyncStatus.Completed:
                    try { getResults(); tcs.TrySetResult(); }
                    catch (Exception ex) { tcs.TrySetException(ex); }
                    break;
                case AsyncStatus.Canceled:
                    tcs.TrySetCanceled();
                    break;
                default:
                    tcs.TrySetException(error ?? new InvalidOperationException($"The operation ended with status {status}."));
                    break;
            }
        }

        private static void Settle<TResult>(AsyncStatus status, TaskCompletionSource<TResult> tcs, Func<TResult> getResults, Exception? error)
        {
            switch (status)
            {
                case AsyncStatus.Completed:
                    try { tcs.TrySetResult(getResults()); }
                    catch (Exception ex) { tcs.TrySetException(ex); }
                    break;
                case AsyncStatus.Canceled:
                    tcs.TrySetCanceled();
                    break;
                default:
                    tcs.TrySetException(error ?? new InvalidOperationException($"The operation ended with status {status}."));
                    break;
            }
        }

        private static T NullSource<T>(T result)
        {
            if (Interlocked.Increment(ref _nullSourcesReported) <= 5)
            {
                Trace.WriteLine("[wpr-winrt] awaited a null WinRT operation (a stubbed component returned nothing); treated as completed with a default result");
            }
            return result;
        }
    }
}
