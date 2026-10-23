using System;
using System.Threading.Tasks;
using Windows.Foundation;
using WPR.WinRT;

namespace Windows.System.Threading
{
    /// <summary>Shim for <c>Windows.System.Threading.WorkItemHandler</c>: the work a
    /// <see cref="ThreadPool.RunAsync(WorkItemHandler)"/> runs, handed the action that represents it.</summary>
    public delegate void WorkItemHandler(IAsyncAction operation);

    /// <summary>Shim for <c>Windows.System.Threading.WorkItemPriority</c>.</summary>
    public enum WorkItemPriority
    {
        Low = -1,
        Normal = 0,
        High = 1,
    }

    /// <summary>Shim for <c>Windows.System.Threading.WorkItemOptions</c>.</summary>
    [Flags]
    public enum WorkItemOptions : uint
    {
        None = 0,
        TimeSliced = 1,
    }

    /// <summary>
    /// Shim for <c>Windows.System.Threading.ThreadPool</c>: runs a work item on the .NET thread
    /// pool. The returned action completes when the handler returns, and faults with what it
    /// throws. Priority and options only steer scheduling on the phone, so they are accepted and
    /// do not change anything here. YouTube loads its feed thumbnails through it
    /// (IdentityMine's <c>ResultItems.LoadIsoImages</c>).
    /// </summary>
    public static class ThreadPool
    {
        public static IAsyncAction RunAsync(WorkItemHandler handler) => RunAsync(handler, WorkItemPriority.Normal, WorkItemOptions.None);

        public static IAsyncAction RunAsync(WorkItemHandler handler, WorkItemPriority priority) => RunAsync(handler, priority, WorkItemOptions.None);

        public static IAsyncAction RunAsync(WorkItemHandler handler, WorkItemPriority priority, WorkItemOptions options)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var operation = new TaskAsyncAction(done.Task);
            Task.Run(() =>
            {
                try
                {
                    handler(operation);
                    done.SetResult();
                }
                catch (Exception ex)
                {
                    done.SetException(ex);
                }
            });
            return operation;
        }
    }
}
