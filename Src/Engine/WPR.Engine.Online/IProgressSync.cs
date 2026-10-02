using System.Threading.Tasks;

namespace WPR.Engine.Online
{
    /// <summary>
    /// Sends what <see cref="IOnlineLocalStore"/> holds as awaiting upload: achievement unlocks and
    /// play sessions. Both need a signed-in player, and both are idempotent on the hub (the earliest
    /// unlock wins; a session re-sent with the same id keeps its longest copy), so a send that dies
    /// half-way is simply repeated.
    /// </summary>
    public interface IProgressSync
    {
        /// <summary>
        /// An achievement was just recorded locally. When signed in, starts an upload in the
        /// background; otherwise it waits for the next flush after sign-in. Must not throw or block.
        /// </summary>
        void Unlocked();

        /// <summary>Upload everything waiting. Never throws.</summary>
        Task FlushAsync();
    }
}
