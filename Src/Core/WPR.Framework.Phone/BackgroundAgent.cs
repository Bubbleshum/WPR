using System;

namespace Microsoft.Phone
{
    /// <summary>
    /// Shim for <c>Microsoft.Phone.BackgroundAgent</c>, the base of WP7's out-of-process agents.
    ///
    /// WPR runs no background agents: there is no OS scheduler handing us a separate process on a
    /// timer, and a desktop/Android host has no equivalent lifecycle. The type exists so that a
    /// game's agent assembly still LOADS — Kinectimals ships
    /// <c>KinectimalsBackgroundAgent.ScheduledAgent</c>, whose base type is
    /// <see cref="Microsoft.Phone.Scheduler.ScheduledTaskAgent"/>, and an unresolvable base type
    /// is fatal at type load rather than at first use.
    ///
    /// <see cref="OnInvoke"/> is never called by WPR, so an agent's body simply never runs.
    /// </summary>
    public abstract class BackgroundAgent
    {
        /// <summary>
        /// Tell the host this agent is finished. On WP7 this released the agent's process; here
        /// there is no process to release, so it is a no-op that exists to keep agent bodies
        /// compiling and callable.
        /// </summary>
        protected void NotifyComplete()
        {
        }

        /// <summary>
        /// Tell the host the agent cannot continue. No-op for the same reason as
        /// <see cref="NotifyComplete"/>.
        /// </summary>
        protected void Abort()
        {
        }
    }
}
