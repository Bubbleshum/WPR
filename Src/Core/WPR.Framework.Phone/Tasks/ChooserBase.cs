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
        /// Opens the chooser. Virtual, as on the phone: games call it through the base type
        /// (Asphalt 7 does, on its <see cref="GameInviteTask"/>). A chooser with no screen of its
        /// own here overrides it to answer at once.
        /// </summary>
        public virtual void Show()
        {
        }

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
