using System;

namespace Microsoft.Phone.Tasks
{
    public abstract class ChooserBase<TTaskEventArgs> where TTaskEventArgs : TaskEventArgs
    {
        public ChooserBase()
        {
        }

        public event EventHandler<TTaskEventArgs>? Completed;

        /// <summary>
        /// Invoke <see cref="Completed"/> from derived types. Real WP7 raises Completed
        /// from the OS chooser shell when the user dismisses it; our shims raise it
        /// synchronously from <c>Show()</c> with a Cancel result since there's no
        /// chooser UI on the desktop host.
        /// </summary>
        protected void RaiseCompleted(TTaskEventArgs args)
        {
            Completed?.Invoke(this, args);
        }
    }
}
