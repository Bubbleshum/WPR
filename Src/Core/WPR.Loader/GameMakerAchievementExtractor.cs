using System.IO;

using WPR.Common;

namespace WPR
{
    /// <summary>
    /// Read-only achievement metadata extractor for installed GameMaker apps.
    ///
    /// Previously shelled out to UndertaleModCli + <c>wpr_extract_achievements.csx</c>
    /// to decompile scripts and regex-match <c>achievements_add(...)</c> calls. That
    /// subprocess hangs at &lt;1% CPU before producing output on this environment, so
    /// the install bar was stalling indefinitely on the "scanning game.win for
    /// achievements" line.
    ///
    /// This implementation is currently a stub. The .win is read-only so it's safe to
    /// run, but no metadata gets persisted — the catalog stays empty for GameMaker apps
    /// until we port the extractor to in-tree bytecode pattern matching (planned
    /// follow-up: walk CODE entries whose name contains "achievement", recognise the
    /// <c>Push.String x 5 → Push.Int x 2 → Call achievements_add</c> shape, resolve
    /// the string IDs via STRG, and persist).
    ///
    /// Important: the patcher in <see cref="GameMakerWinPatcher"/> still runs after
    /// this and is fully in-tree, so the runtime fix (neutralising the
    /// <c>achievements_add</c> script body) still happens at install time.
    /// </summary>
    public static class GameMakerAchievementExtractor
    {
        public static void ExtractInPlace(string installFolder, string productId, string appName)
        {
            string gameWin = Path.Combine(installFolder, "Assets", "game.win");
            if (!File.Exists(gameWin)) return;

            Log.Info(LogCategory.AppInstall,
                "GameMakerAchievementExtractor: in-tree extractor not implemented yet — " +
                "skipping achievement metadata extraction. The runtime neutraliser still " +
                "runs after this; the empty catalog only affects WPR's UI list, not the " +
                "game's ability to launch.");
        }
    }
}
