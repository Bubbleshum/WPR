using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace WPR.SilverlightCompability.Threading
{
    /// <summary>Shim for <c>System.Windows.Threading.DispatcherOperation</c>.</summary>
    public class DispatcherOperation
    {
        public Task Task { get; } = System.Threading.Tasks.Task.CompletedTask;
        public object? Wait() => null;
    }
}
