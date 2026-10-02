using System;

using WPR.Online.Hub.Client;

namespace WPR.Shell
{
    /// <summary>
    /// How the WPR Hub pages of both heads word presence, ages and durations, so a friend reads as
    /// "playing Cut the Rope" or "last seen 3h ago" identically on a PC and a phone. Lower case,
    /// the Windows Phone voice both shells use.
    /// </summary>
    public static class HubText
    {
        public static string Presence(FriendPresence presence)
        {
            if (presence.IsOnline)
            {
                if (!string.IsNullOrEmpty(presence.TitleName)) return "playing " + presence.TitleName;
                return presence.Status == PresenceStatuses.Online ? "online" : presence.Status;
            }
            return presence.LastSeenAt is { } seen ? "last seen " + Ago(seen) : "offline";
        }

        public static string Ago(DateTimeOffset when)
        {
            TimeSpan age = DateTimeOffset.UtcNow - when;
            if (age < TimeSpan.FromMinutes(1)) return "just now";
            if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes}m ago";
            if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours}h ago";
            if (age < TimeSpan.FromDays(30)) return $"{(int)age.TotalDays}d ago";
            return when.ToLocalTime().ToString("d MMM yyyy");
        }

        public static string Duration(long seconds)
        {
            if (seconds < 60) return "under a minute";
            TimeSpan span = TimeSpan.FromSeconds(seconds);
            if (span.TotalHours < 1) return $"{span.Minutes}m";
            return span.Minutes == 0 ? $"{(int)span.TotalHours}h" : $"{(int)span.TotalHours}h {span.Minutes}m";
        }

        public static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

        /// <summary>The profile line: "4 games · 12h 5m played · 37 achievements".</summary>
        public static string Summary(HubGameList? games)
        {
            if (games == null) return "";
            long seconds = 0;
            int unlocked = 0;
            foreach (HubGame g in games.Games)
            {
                seconds += g.PlaytimeSeconds;
                unlocked += g.AchievementsUnlocked;
            }
            return $"{Plural(games.Games.Count, "game")} · {Duration(seconds)} played · {Plural(unlocked, "achievement")}";
        }

        /// <summary>A friend page's totals: "4 games · 37 achievements · 540G".</summary>
        public static string FriendSummary(FriendProfile profile) =>
            $"{Plural(profile.Games, "game")} · {Plural(profile.Achievements, "achievement")} · {profile.Points}G";

        /// <summary>An activity row's first line: "unlocked Hat Trick" / "played Cut the Rope".</summary>
        public static string ActivityTitle(FriendActivity item) =>
            item.Type == FriendActivity.Achievement ? "unlocked " + (item.Name ?? "an achievement") : "played " + item.TitleName;

        /// <summary>An activity row's second line: "Cut the Rope · 20G · 3h ago" / "for 25m · 3h ago".</summary>
        public static string ActivityLine(FriendActivity item)
        {
            if (item.Type == FriendActivity.Achievement)
                return item.Points > 0 ? $"{item.TitleName} · {item.Points}G · {Ago(item.At)}" : $"{item.TitleName} · {Ago(item.At)}";
            return $"for {Duration(item.Seconds)} · {Ago(item.At)}";
        }

        /// <summary>A game row's second line: "2h 10m played · 5/20 achievements · 3d ago".</summary>
        public static string GameLine(HubGame game)
        {
            string achievements = game.AchievementsTotal > 0
                ? $"{game.AchievementsUnlocked}/{game.AchievementsTotal} achievements"
                : Plural(game.AchievementsUnlocked, "achievement");
            string last = game.LastPlayedAt is { } at ? " · " + Ago(at) : "";
            return $"{Duration(game.PlaytimeSeconds)} played · {achievements}{last}";
        }
    }
}
