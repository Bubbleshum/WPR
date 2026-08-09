using System;
using System.IO;

using WPR.Common;

namespace WPR
{
    /// <summary>
    /// Install-time pass that, for GameMaker apps, produces a sibling
    /// <c>game.win.patched</c> where one specific script
    /// (<c>gml_Script_achievements_add</c>) has been surgically replaced with a
    /// single <c>Exit</c> opcode.
    ///
    /// Why this exists: Briquid Mini's <c>god</c> object's PreCreate event calls
    /// <c>achievements_add</c> before <c>achievements_define</c> has created the
    /// <c>achievements</c> singleton. On WP that's fine because Microsoft's
    /// Live extension stubs the call; on YoYo's Runner it FATALs at
    /// <c>achievements.count += 1</c>. Replacing the body with a no-op lets the
    /// game boot past PreCreate.
    ///
    /// Implementation: in-process via <see cref="GameMakerWinNeutralizer"/>. The
    /// previous shell-out to UndertaleModCli + <c>wpr_neutralize.csx</c> was
    /// dropped because (a) the CLI was hanging at &lt;1% CPU before producing
    /// output on this environment, leaving <c>.patched</c> never produced and
    /// the FATAL re-surfacing, and (b) UMT is GPL-3 and the subprocess hop
    /// existed mostly to avoid linking it. The in-tree neutralizer only encodes
    /// public-format facts (chunk IDs, opcode numbers, field offsets) and stays
    /// MIT-clean.
    ///
    /// Why "surgical": the previous full <c>wpr_patch.csx</c> approach went through
    /// Underanalyzer's GML compiler (recompiling wrapped scripts) and broke
    /// variable scope info on Runner 2.1.4.200. The byte-level patch here is
    /// the smallest possible mutation: overwrite the first 4 bytes of one
    /// script's bytecode with <c>Exit Int32</c> and set its Length to 4. All
    /// other chunks, all other entries, all file offsets stay untouched —
    /// file size is preserved exactly.
    ///
    /// Output strategy: produce <c>game.win.patched</c> as a sibling. Original
    /// <c>game.win</c> is never modified, so the read-only extractor and any
    /// future re-runs operate from clean source. <see cref="GameMakerLauncher"/>
    /// hands the patched sibling to Runner.exe via the <c>-game</c> argument
    /// when it exists, falls back to <c>game.win</c> otherwise. Deleting the
    /// <c>.patched</c> file restores original behavior with no other state to
    /// clean up.
    /// </summary>
    public static class GameMakerWinPatcher
    {
        public const string PatchedSuffix = ".patched";

        /// <summary>
        /// Path to the patched .win sibling for the given install folder, or null
        /// if no <c>Assets/game.win</c> is present.
        /// </summary>
        public static string? GetPatchedWinPath(string installFolder)
        {
            string gameWin = Path.Combine(installFolder, "Assets", "game.win");
            return File.Exists(gameWin) ? gameWin + PatchedSuffix : null;
        }

        /// <summary>
        /// Produce <c>game.win.patched</c> next to <c>game.win</c>. Idempotent —
        /// re-running rewrites the .patched file from the (untouched) original.
        /// </summary>
        public static void PatchInPlace(string installFolder)
        {
            string gameWin = Path.Combine(installFolder, "Assets", "game.win");
            if (!File.Exists(gameWin)) return;

            string patched = gameWin + PatchedSuffix;
            string tempOut = patched + ".tmp";
            try
            {
                Log.Info(LogCategory.AppInstall,
                    $"GameMakerWinPatcher: neutralizing offending scripts in {gameWin}");

                int neutralized = GameMakerWinNeutralizer.Neutralize(gameWin, tempOut);

                if (!File.Exists(tempOut))
                {
                    Log.Warn(LogCategory.AppInstall,
                        $"GameMakerWinPatcher: neutralizer produced no output for {gameWin}");
                    return;
                }

                if (File.Exists(patched))
                {
                    try { File.Delete(patched); } catch { /* will fail later if unwritable */ }
                }
                File.Move(tempOut, patched);
                Log.Info(LogCategory.AppInstall,
                    $"GameMakerWinPatcher: produced {patched} ({neutralized} script(s) neutralized); original game.win untouched");
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppInstall, $"GameMakerWinPatcher: {ex}");
                if (File.Exists(tempOut)) try { File.Delete(tempOut); } catch { }
            }
        }
    }
}
