using System.Threading.Tasks;

namespace WPR.Engine.Online
{
    /// <summary>What a platform declares through <c>caps.Online(...)</c>. Any slot may be null.</summary>
    public sealed class OnlineServices
    {
        public ILeaderboardService? Leaderboards { get; init; }
        public ICrashReporter? Crashes { get; init; }
        public IProgressSync? Progress { get; init; }
        public IPresence? Presence { get; init; }
        public IOnlineLocalStore? Local { get; init; }
    }

    /// <summary>
    /// The registry a platform head fills through <c>caps.Online(...)</c>. One slot per hub
    /// facility, the <c>LauncherBackend</c> shape.
    /// </summary>
    /// <remarks>
    /// <para>Process-lifetime: set at composition and never cleared at game teardown. The contracts
    /// are push-only (no events, no subscribers), so nothing here can pin a game's load context.</para>
    ///
    /// <para><b>Null is the normal unset state, not an error.</b> A platform that declares no
    /// online service keeps every leaderboard empty, reports no crashes and records no playtime.</para>
    ///
    /// <para><b>Android composes twice</b>, in the launcher and again in <c>GameActivity</c>'s
    /// <c>:game</c> process, so each process gets its own instance over the same files and the same
    /// database. That is why everything queued lives on disk and is re-read on every operation
    /// rather than cached: the <c>:game</c> process records, and the launcher is the one that lives
    /// long enough to send.</para>
    /// </remarks>
    public static class OnlineBackend
    {
        public static ILeaderboardService? Leaderboards { get; private set; }

        public static ICrashReporter? Crashes { get; private set; }

        /// <summary>Achievement unlocks and play sessions awaiting upload.</summary>
        public static IProgressSync? Progress { get; private set; }

        public static IPresence? Presence { get; private set; }

        /// <summary>The local database's queue of unlocks and sessions (what <see cref="PlaytimeTracker"/> writes to).</summary>
        public static IOnlineLocalStore? Local { get; private set; }

        public static void Set(OnlineServices services)
        {
            Leaderboards = services.Leaderboards;
            Crashes = services.Crashes;
            Progress = services.Progress;
            Presence = services.Presence;
            Local = services.Local;
        }

        public static void SetLeaderboards(ILeaderboardService? service) => Leaderboards = service;

        public static void SetCrashReporter(ICrashReporter? reporter) => Crashes = reporter;

        /// <summary>
        /// Send whatever is queued: crash reports and the game packages the hub asked for,
        /// leaderboard scores, achievement unlocks and play sessions. The launcher calls this at
        /// startup, after every game run and after sign-in. Never throws.
        /// </summary>
        public static Task FlushAsync()
        {
            Task crashes = Crashes?.FlushAsync() ?? Task.CompletedTask;
            Task scores = Leaderboards?.FlushAsync() ?? Task.CompletedTask;
            Task progress = Progress?.FlushAsync() ?? Task.CompletedTask;
            return Task.WhenAll(crashes, scores, progress);
        }
    }
}
