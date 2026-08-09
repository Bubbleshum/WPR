using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace WPR.SilverlightCompability.Threading
{
    /// <summary>Shim for <c>System.Windows.Threading.Dispatcher</c>. Marshals work
    /// back onto the UI thread. Our shim just runs everything inline — fine because
    /// the renderer hasn't taken ownership of a UI thread yet.</summary>
    public class Dispatcher
    {
        internal static readonly Dispatcher Shared = new Dispatcher();

        public bool CheckAccess() => true;

        public DispatcherOperation BeginInvoke(Delegate d, params object?[] args)
        {
            try { d.DynamicInvoke(args); } catch { /* swallow per SL semantics */ }
            return new DispatcherOperation();
        }

        public DispatcherOperation BeginInvoke(Action a)
        {
            try { a(); } catch { }
            return new DispatcherOperation();
        }

        public DispatcherOperation BeginInvoke<T>(Action<T> a, T arg)
        {
            try { a(arg); } catch { }
            return new DispatcherOperation();
        }
    }
}
