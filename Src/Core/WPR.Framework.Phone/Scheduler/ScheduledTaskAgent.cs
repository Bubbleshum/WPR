namespace Microsoft.Phone.Scheduler
{
    /// <summary>
    /// Shim for <c>Microsoft.Phone.Scheduler.ScheduledTaskAgent</c>.
    ///
    /// The base class a game derives from to run periodic or resource-intensive background work.
    /// WPR never invokes agents (see <see cref="Microsoft.Phone.BackgroundAgent"/>), so
    /// <see cref="OnInvoke"/> is declared purely so derived agents can override it and the
    /// assembly loads. Kinectimals' KinectimalsBackgroundAgent.ScheduledAgent derives from this.
    /// </summary>
    public abstract class ScheduledTaskAgent : BackgroundAgent
    {
        /// <summary>
        /// Called by WP7 when the OS scheduler runs the agent. Never called here.
        /// </summary>
        protected abstract void OnInvoke(ScheduledTask task);
    }
}
