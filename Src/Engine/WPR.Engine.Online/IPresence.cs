using System.Threading.Tasks;

namespace WPR.Engine.Online
{
    /// <summary>
    /// "Online, playing X": a heartbeat to WPR Hub while a player is signed in. The hub turns the
    /// heartbeats into online sessions (when they came online, for how long, from which device).
    /// </summary>
    /// <remarks>
    /// <para><b>One process talks at a time.</b> On Android the launcher and the <c>:game</c>
    /// process each have one of these; the launcher calls <see cref="Suspend"/> while a game runs,
    /// so the hub does not see "idle" and "playing X" alternate every minute.</para>
    /// </remarks>
    public interface IPresence
    {
        /// <summary>Begin heartbeating (idles while signed out). Idempotent.</summary>
        void Start();

        /// <summary>What is being played now, or null for none. Sent at once, not at the next beat.</summary>
        void SetPlaying(string? titleId, string? titleName);

        /// <summary>Stop beating without saying offline: another process is about to speak for this device.</summary>
        void Suspend();

        /// <summary>Resume after <see cref="Suspend"/>.</summary>
        void Resume();

        /// <summary>Tell the hub this device went offline, and stop. Never throws.</summary>
        Task GoOfflineAsync();
    }
}
