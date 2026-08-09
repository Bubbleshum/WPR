using System;
using System.Globalization;
using System.Linq;
using Microsoft.Xna.Framework.GamerServices;

using WPR.Common;

namespace WPR
{
    /// <summary>
    /// Receives <c>WPR_ACH:*</c> lines from Runner.exe stdout (emitted by our patched
    /// achievement scripts in <c>game.win</c>) and persists them into the same SQLite
    /// <see cref="AchievementContext"/> the XNA path uses, so GameMaker games show up
    /// in WPR's achievement UI alongside XNA games.
    ///
    /// Protocol (one event per stdout line):
    ///   <c>WPR_ACH:register:&lt;key&gt;</c>      — game registered an achievement (legitimate call).
    ///   <c>WPR_ACH:unlock:&lt;key&gt;</c>        — achievement transitioned to "reached".
    ///   <c>WPR_ACH:premature</c>          — achievements_add fired before achievements_define;
    ///                                       no-op'd (diagnostic).
    /// </summary>
    public static class GameMakerAchievementBridge
    {
        private const string Prefix = "WPR_ACH:";

        /// <summary>
        /// Parse a single stdout line. No-op if it isn't a <c>WPR_ACH:*</c> line.
        /// Safe to call from any thread.
        /// </summary>
        public static void HandleStdoutLine(string? line, string productId, string appName)
        {
            if (string.IsNullOrEmpty(line)) return;
            int idx = line.IndexOf(Prefix, StringComparison.Ordinal);
            if (idx < 0) return;

            string body = line.Substring(idx + Prefix.Length).Trim();
            string[] parts = body.Split(':', 2);
            string verb = parts[0].Trim();
            string? arg = parts.Length > 1 ? parts[1].Trim() : null;

            try
            {
                switch (verb)
                {
                    case "register":
                        if (!string.IsNullOrEmpty(arg)) Register(arg, productId, appName);
                        break;
                    case "unlock":
                        if (!string.IsNullOrEmpty(arg)) Unlock(arg, productId);
                        break;
                    case "premature":
                        Log.Info(LogCategory.AppLaunch,
                            $"GM achievement: premature add (before achievements_define) — ignored");
                        break;
                    default:
                        Log.Info(LogCategory.AppLaunch, $"GM achievement: unknown verb '{verb}' (line='{line.Trim()}')");
                        break;
                }
            }
            catch (Exception ex)
            {
                // Never let achievement persistence kill the launch. The game keeps running.
                Log.Warn(LogCategory.AppLaunch, $"GM achievement bridge: {ex.Message}");
            }
        }

        /// <summary>
        /// Upsert an achievement by (ProductId, Key). Each game call (e.g. 23 Briquid Mini
        /// achievements_add invocations during init) should land as a single DB row per key.
        /// </summary>
        private static void Register(string key, string productId, string appName)
        {
            var ctx = AchievementContext.Current;
            var existing = ctx.Achievements!
                .FirstOrDefault(a => a.OwnProductId == productId && a.Key == key);

            if (existing != null)
            {
                // Already known — nothing to do for register.
                return;
            }

            var ach = new Achievement
            {
                OwnProductId = productId,
                Key = key,
                Name = key,                   // GameMaker doesn't pass a display name in arg[1]; key doubles as it
                Description = appName,        // Group-by-game heuristic for UI
                _IconPath = string.Empty,
                IsEarned = false,
                EarnedDateTime = DateTime.MinValue,
                EarnedOnline = false,
                GamerScore = 0,
                HowToEarn = string.Empty,
                DisplayBeforeEarned = true,
            };
            ctx.Achievements!.Add(ach);
            ctx.SaveChanges();
            Log.Info(LogCategory.AppLaunch, $"GM achievement registered: {productId}/{key}");
        }

        private static void Unlock(string key, string productId)
        {
            var ctx = AchievementContext.Current;
            var ach = ctx.Achievements!
                .FirstOrDefault(a => a.OwnProductId == productId && a.Key == key);

            if (ach == null)
            {
                // Unlock arrived before register — synthesise a row so it doesn't go missing.
                ach = new Achievement
                {
                    OwnProductId = productId,
                    Key = key,
                    Name = key,
                    Description = string.Empty,
                    _IconPath = string.Empty,
                    DisplayBeforeEarned = true,
                };
                ctx.Achievements!.Add(ach);
            }

            if (!ach.IsEarned)
            {
                ach.IsEarned = true;
                ach.EarnedDateTime = DateTime.UtcNow;
                ach.EarnedOnline = false;
                ctx.SaveChanges();
                Log.Info(LogCategory.AppLaunch, $"GM achievement UNLOCKED: {productId}/{key}");
            }
        }
    }
}
