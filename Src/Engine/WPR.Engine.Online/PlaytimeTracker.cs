using System;
using System.Diagnostics;
using System.Threading;

namespace WPR.Engine.Online
{
    /// <summary>
    /// Times the game that is running and records it as a play session in the local database
    /// (<see cref="OnlineBackend.Local"/>), for WPR Hub's playtime and "unlocked after 2 minutes".
    /// </summary>
    /// <remarks>
    /// <para><b>Recorded locally, sent later, like everything else.</b> The session is written when
    /// it starts, again every <see cref="Checkpoint"/>, and once more when it ends, so a crash or a
    /// killed <c>:game</c> process loses at most a minute. The hub sync sends it whenever someone is
    /// signed in. The hub keeps the longest copy of a session id, so re-sending a running one is
    /// harmless.</para>
    ///
    /// <para><b>Only time in front of the game counts.</b> <see cref="Pause"/> and
    /// <see cref="Resume"/> are wired to the game's own Deactivated/Activated, so a game left in the
    /// background (Android) or behind another window (desktop) stops accruing.</para>
    ///
    /// <para>One game at a time, which is how both heads run games. Every member is safe to call
    /// with no store registered and never throws: a playtime log must not be why a game fails.</para>
    /// </remarks>
    public static class PlaytimeTracker
    {
        /// <summary>How often a running session is written.</summary>
        public static readonly TimeSpan Checkpoint = TimeSpan.FromMinutes(1);

        private static readonly object Gate = new object();
        private static Timer? _timer;
        private static Guid _sessionId;
        private static string? _titleId;
        private static string? _titleName;
        private static DateTimeOffset _startedAt;
        private static readonly Stopwatch Active = new Stopwatch();

        /// <summary>The game being timed, or null.</summary>
        public static string? CurrentTitleId { get { lock (Gate) return _titleId; } }

        /// <summary>Seconds played in the current session so far.</summary>
        public static long CurrentSeconds { get { lock (Gate) return (long)Active.Elapsed.TotalSeconds; } }

        public static void Begin(string? titleId, string? titleName)
        {
            if (string.IsNullOrEmpty(titleId)) return;
            lock (Gate)
            {
                if (_titleId != null) EndLocked(); // a previous run never ended cleanly
                _sessionId = Guid.NewGuid();
                _titleId = titleId;
                _titleName = titleName;
                _startedAt = DateTimeOffset.UtcNow;
                Active.Restart();
                _timer = new Timer(_ => Save(ended: false), null, Checkpoint, Checkpoint);
            }
            Save(ended: false);
            SafePresence(p => p.SetPlaying(titleId, titleName));
        }

        public static void Pause()
        {
            lock (Gate) { if (_titleId != null) Active.Stop(); }
        }

        public static void Resume()
        {
            lock (Gate) { if (_titleId != null) Active.Start(); }
        }

        public static void End()
        {
            bool had;
            lock (Gate)
            {
                had = _titleId != null;
                EndLocked();
            }
            if (had) SafePresence(p => p.SetPlaying(null, null));
        }

        private static void EndLocked()
        {
            if (_titleId == null) return;
            _timer?.Dispose();
            _timer = null;
            Active.Stop();
            SaveLocked(ended: true);
            _titleId = null;
            _titleName = null;
        }

        private static void Save(bool ended)
        {
            lock (Gate) SaveLocked(ended);
        }

        private static void SaveLocked(bool ended)
        {
            IOnlineLocalStore? store = OnlineBackend.Local;
            if (store == null || _titleId == null) return;

            DateTimeOffset now = DateTimeOffset.UtcNow;
            var record = new PlaySessionRecord(_sessionId, _titleId, _titleName, _startedAt, now,
                ended ? now : null, (long)Active.Elapsed.TotalSeconds);
            try
            {
                // Synchronous on purpose: the ending save may be the last thing a dying process does.
                store.SavePlaySessionAsync(record).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[wpr-online] could not save the play session: " + ex.Message);
            }
        }

        private static void SafePresence(Action<IPresence> action)
        {
            try { if (OnlineBackend.Presence is { } presence) action(presence); }
            catch (Exception) { /* presence is decoration */ }
        }
    }
}
