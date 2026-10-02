using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

using WPR.Common;
using WPR.Online.Hub.Client;

namespace WPR.Shell
{
    /// <summary>
    /// "How did it run?": after a game, now and again, ask the player to rate it and send the rating
    /// to WPR Hub's compatibility page. Both heads ask the same question on the same rules; only the
    /// dialog is theirs.
    /// </summary>
    /// <remarks>
    /// <para><b>The rules, which exist so this never nags:</b></para>
    /// <list type="bullet">
    /// <item>at most one question per cooldown: the player's setting, an hour by default, down to
    /// "after every game" (<see cref="EveryGame"/>) or "never" (0);</item>
    /// <item>never again about a game the player has rated, or said "don't ask about this game" for,
    /// on this WPR version. A new WPR version is a fresh question, because the hub rates each game by
    /// the newest version anyone reported;</item>
    /// <item>"ask me later" (and closing the question without answering) postpones it: the game is
    /// asked about again the next time it is finished, once the cooldown allows. That is for a player
    /// who did not get a good enough feel for it yet;</item>
    /// <item>only after a run that ended normally and lasted at least a minute: a crash already
    /// shows its own dialog, a logging run shows its report, and a few seconds of play is not
    /// enough to judge;</item>
    /// <item>only when signed in, because the hub keeps one rating per player.</item>
    /// </list>
    /// <para>The first version of this also refused to ask about the same game twice in a row. That
    /// went with "ask me later", whose whole point is to ask about the same game again.</para>
    /// <para>Remembered in <c>&lt;DataStore&gt;/Online/rating-prompts.json</c>. A rating that fails to
    /// send is not remembered as answered, so the game is simply asked about again later.</para>
    /// </remarks>
    public static class GameRatingPrompt
    {
        /// <summary>The cooldown value meaning "after every game": no gap between questions.</summary>
        public const int EveryGame = -1;

        /// <summary>The cooldown value meaning "never ask".</summary>
        public const int Never = 0;

        public static readonly TimeSpan MinimumPlay = TimeSpan.FromMinutes(1);

        /// <summary>The choices both settings pages offer, in minutes, with their wording.</summary>
        public static readonly IReadOnlyList<(int Minutes, string Label)> CooldownChoices = new[]
        {
            (EveryGame, "after every game"),
            (30, "every 30 minutes"),
            (60, "once an hour"),
            (180, "every 3 hours"),
            (1440, "once a day"),
            (10080, "once a week"),
            (Never, "never"),
        };

        /// <summary>The wording of a setting, or "every N minutes" for a value set by hand in config.json.</summary>
        public static string DescribeCooldown(int minutes)
        {
            foreach (var (m, label) in CooldownChoices) if (m == minutes) return label;
            return $"every {minutes} minutes";
        }

        private static int CooldownMinutes =>
            Configuration.Current?.RatingPromptCooldownMinutes ?? Configuration.DefaultRatingPromptCooldownMinutes;

        private sealed class State
        {
            public long LastAskedAtTicks { get; set; }
            /// <summary>The game the last question was about; kept for the log, no longer a rule.</summary>
            public string? LastAskedTitle { get; set; }
            /// <summary>"TITLEID|wprversion" of every game rated.</summary>
            public List<string> Answered { get; set; } = new List<string>();
            /// <summary>"TITLEID|wprversion" of every game the player said not to ask about.</summary>
            public List<string> Declined { get; set; } = new List<string>();
        }

        private static readonly object Gate = new object();

        /// <summary>Should the head ask about this game now, after a run of <paramref name="played"/>?</summary>
        public static bool ShouldAsk(string? productId, TimeSpan played)
        {
            if (string.IsNullOrEmpty(productId) || played < MinimumPlay) return false;
            int cooldown = CooldownMinutes;
            if (cooldown == Never) return false;
            if (HubSetup.Current == null || string.IsNullOrEmpty(Configuration.Current?.HubAccessToken)) return false;

            string key = VersionKey(TitleIds.Normalize(productId));
            lock (Gate)
            {
                State state = Load();
                if (state.Answered.Contains(key) || state.Declined.Contains(key)) return false;
                if (cooldown == EveryGame) return true;
                return DateTime.UtcNow - new DateTime(state.LastAskedAtTicks, DateTimeKind.Utc) >= TimeSpan.FromMinutes(cooldown);
            }
        }

        /// <summary>
        /// The question was shown. Call it when showing: whatever happens next, the cooldown runs from
        /// now. With no other call the game counts as postponed ("ask me later").
        /// </summary>
        public static void MarkAsked(string productId)
        {
            lock (Gate)
            {
                State state = Load();
                state.LastAskedAtTicks = DateTime.UtcNow.Ticks;
                state.LastAskedTitle = TitleIds.Normalize(productId);
                Save(state);
            }
        }

        /// <summary>"Don't ask about this game": not asked about again on this WPR version.</summary>
        public static void Decline(string productId)
        {
            lock (Gate)
            {
                State state = Load();
                string key = VersionKey(TitleIds.Normalize(productId));
                if (!state.Declined.Contains(key)) state.Declined.Add(key);
                Save(state);
            }
            Log.Info(LogCategory.AppList, $"[wpr-rating] {productId}: player asked not to be asked about it");
        }

        /// <summary>
        /// Send the player's rating. On success the game is not asked about again on this WPR
        /// version. Throws when the hub cannot be reached or refuses it, for the head to mention.
        /// </summary>
        public static async Task SubmitAsync(string productId, string rating, string platform, string? osVersion,
            string? runtimePath, string? graphicsBackend = null, string? gpu = null)
        {
            var hub = HubSetup.Current ?? throw new InvalidOperationException("WPR Hub is not available.");
            await hub.Social.SubmitCompatibilityAsync(new CompatibilitySubmission(
                productId, rating, platform, osVersion, HubSetup.Version(),
                Arch: System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
                Gpu: gpu, GraphicsBackend: graphicsBackend, RuntimePath: runtimePath)).ConfigureAwait(false);

            lock (Gate)
            {
                State state = Load();
                string key = VersionKey(TitleIds.Normalize(productId));
                if (!state.Answered.Contains(key)) state.Answered.Add(key);
                Save(state);
            }
            Log.Info(LogCategory.AppList, $"[wpr-rating] {productId} rated {rating}");
        }

        private static string VersionKey(string title) => title + "|" + HubSetup.Version();

        private static string? FilePath() =>
            Configuration.Current == null ? null : Path.Combine(Configuration.Current.DataPath("Online"), "rating-prompts.json");

        private static State Load()
        {
            try
            {
                string? path = FilePath();
                if (path != null && File.Exists(path))
                    return JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? new State();
            }
            catch (Exception) { /* a torn file just means "never asked" */ }
            return new State();
        }

        private static void Save(State state)
        {
            try
            {
                string? path = FilePath();
                if (path == null) return;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(state));
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppList, "[wpr-rating] could not save rating prompts: " + ex.Message);
            }
        }
    }
}
