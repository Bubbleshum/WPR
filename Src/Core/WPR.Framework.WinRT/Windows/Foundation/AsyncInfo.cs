using System;

namespace Windows.Foundation
{
    // WinRT's async family as .NET's projection showed it to C#. Two details are load-bearing:
    //
    // - Setters are METHODS named put_X. WinRT metadata names a property setter put_X, and the C#
    //   compiler that built a WP8 title emitted "callvirt put_Completed", so a C# property (which
    //   compiles its setter to set_Completed) would not bind. Getters are get_X either way.
    // - ErrorCode is System.Exception: the projection maps WinRT's HResult to it.

    /// <summary>Shim for <c>Windows.Foundation.AsyncStatus</c>.</summary>
    public enum AsyncStatus
    {
        Started = 0,
        Completed = 1,
        Canceled = 2,
        Error = 3,
    }

    /// <summary>Shim for <c>Windows.Foundation.IAsyncInfo</c>.</summary>
    public interface IAsyncInfo
    {
        uint Id { get; }
        AsyncStatus Status { get; }
        Exception? ErrorCode { get; }
        void Cancel();
        void Close();
    }

    /// <summary>Shim for <c>Windows.Foundation.AsyncActionCompletedHandler</c>.</summary>
    public delegate void AsyncActionCompletedHandler(IAsyncAction asyncInfo, AsyncStatus asyncStatus);

    /// <summary>Shim for <c>Windows.Foundation.AsyncOperationCompletedHandler`1</c>.</summary>
    public delegate void AsyncOperationCompletedHandler<TResult>(IAsyncOperation<TResult> asyncInfo, AsyncStatus asyncStatus);

    /// <summary>Shim for <c>Windows.Foundation.AsyncActionProgressHandler`1</c>.</summary>
    public delegate void AsyncActionProgressHandler<TProgress>(IAsyncActionWithProgress<TProgress> asyncInfo, TProgress progressInfo);

    /// <summary>Shim for <c>Windows.Foundation.AsyncActionWithProgressCompletedHandler`1</c>.</summary>
    public delegate void AsyncActionWithProgressCompletedHandler<TProgress>(IAsyncActionWithProgress<TProgress> asyncInfo, AsyncStatus asyncStatus);

    /// <summary>Shim for <c>Windows.Foundation.AsyncOperationProgressHandler`2</c>.</summary>
    public delegate void AsyncOperationProgressHandler<TResult, TProgress>(IAsyncOperationWithProgress<TResult, TProgress> asyncInfo, TProgress progressInfo);

    /// <summary>Shim for <c>Windows.Foundation.AsyncOperationWithProgressCompletedHandler`2</c>.</summary>
    public delegate void AsyncOperationWithProgressCompletedHandler<TResult, TProgress>(IAsyncOperationWithProgress<TResult, TProgress> asyncInfo, AsyncStatus asyncStatus);

    /// <summary>Shim for <c>Windows.Foundation.IAsyncAction</c>.</summary>
    public interface IAsyncAction : IAsyncInfo
    {
        AsyncActionCompletedHandler? get_Completed();
        void put_Completed(AsyncActionCompletedHandler? handler);
        void GetResults();
    }

    /// <summary>Shim for <c>Windows.Foundation.IAsyncOperation`1</c>.</summary>
    public interface IAsyncOperation<TResult> : IAsyncInfo
    {
        AsyncOperationCompletedHandler<TResult>? get_Completed();
        void put_Completed(AsyncOperationCompletedHandler<TResult>? handler);
        TResult GetResults();
    }

    /// <summary>Shim for <c>Windows.Foundation.IAsyncActionWithProgress`1</c>.</summary>
    public interface IAsyncActionWithProgress<TProgress> : IAsyncInfo
    {
        AsyncActionProgressHandler<TProgress>? get_Progress();
        void put_Progress(AsyncActionProgressHandler<TProgress>? handler);
        AsyncActionWithProgressCompletedHandler<TProgress>? get_Completed();
        void put_Completed(AsyncActionWithProgressCompletedHandler<TProgress>? handler);
        void GetResults();
    }

    /// <summary>Shim for <c>Windows.Foundation.IAsyncOperationWithProgress`2</c>.</summary>
    public interface IAsyncOperationWithProgress<TResult, TProgress> : IAsyncInfo
    {
        AsyncOperationProgressHandler<TResult, TProgress>? get_Progress();
        void put_Progress(AsyncOperationProgressHandler<TResult, TProgress>? handler);
        AsyncOperationWithProgressCompletedHandler<TResult, TProgress>? get_Completed();
        void put_Completed(AsyncOperationWithProgressCompletedHandler<TResult, TProgress>? handler);
        TResult GetResults();
    }
}
