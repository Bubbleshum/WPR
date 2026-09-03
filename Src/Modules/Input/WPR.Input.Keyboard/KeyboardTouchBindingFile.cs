using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using WPR.Common;

namespace WPR.Input.Keyboard
{
    /// <summary>
    /// Loads the per-game key-to-touch bindings from <c>input-bindings.json</c> in the game's own
    /// install folder.
    ///
    /// <para><b>Per game, not global, and that is forced by the data.</b> A binding says "Q swipes
    /// from (20,30) to (200,30)" — coordinates that only mean anything against one title's layout.
    /// A global table would be wrong for every game but one. The tilt keys stay in
    /// <c>Configuration</c> because a tilt direction is game-independent.</para>
    ///
    /// <para><b>Not in Configuration, and not in the database.</b> Configuration is a flat record
    /// with fixed fields and cannot hold a list. The achievements database would need a schema
    /// change, and this repo never runs EF migrations — the schema comes from shipped .db files —
    /// so a new table would have to be hand-shipped. A JSON file beside the game is editable by
    /// hand, travels with the install, and disappears when the game is uninstalled.</para>
    ///
    /// <para>Absent or malformed means "no bindings", never an error: a game with no file behaves
    /// exactly as it did before this feature existed.</para>
    /// </summary>
    public static class KeyboardTouchBindingFile
    {
        public const string FileName = "input-bindings.json";

        private sealed class Document
        {
            public List<KeyboardTouchBinding>? Touch { get; set; }
        }

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true,

            // Required, and its absence is invisible until runtime: without it System.Text.Json
            // will not map "Tap"/"Swipe" onto KeyboardTouchGestureKind and throws on the whole
            // document, so every binding in the file is silently lost. Writing the kind as an
            // integer would work without this, but a hand-edited file should not have to.
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };

        /// <summary>Full path to the bindings file for the game currently being launched, or null
        /// if no game is active.</summary>
        public static string? PathForCurrentGame()
        {
            string? folder = WprHostEnvironment.CurrentInstallFolder;
            return string.IsNullOrEmpty(folder) ? null : Path.Combine(folder, FileName);
        }

        /// <summary>Bindings file inside an arbitrary install folder — what the editor uses, since
        /// it edits a game that is not the one running (usually none is).</summary>
        public static string PathIn(string installFolder) => Path.Combine(installFolder, FileName);

        /// <summary>
        /// Reads the bindings for a specific install folder without touching the live
        /// <see cref="KeyboardTouchBindings"/> registry — the editor must not disturb whatever a
        /// running game has loaded.
        /// </summary>
        public static List<KeyboardTouchBinding> Read(string installFolder)
        {
            var result = new List<KeyboardTouchBinding>();
            string path = PathIn(installFolder);
            try
            {
                if (File.Exists(path))
                {
                    Document? doc = JsonSerializer.Deserialize<Document>(File.ReadAllText(path), Options);
                    if (doc?.Touch != null)
                    {
                        foreach (KeyboardTouchBinding b in doc.Touch)
                        {
                            if (b != null && !string.IsNullOrWhiteSpace(b.Key)) result.Add(b);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppList, $"input-bindings.json could not be read ({path}): {ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// Writes the bindings for a specific install folder. An empty list deletes the file
        /// rather than leaving an empty one behind, so "no bindings" has exactly one on-disk
        /// representation and an uninstall leaves nothing odd.
        /// </summary>
        public static void Write(string installFolder, IReadOnlyList<KeyboardTouchBinding> bindings)
        {
            string path = PathIn(installFolder);
            if (bindings == null || bindings.Count == 0)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }

            var doc = new Document { Touch = new List<KeyboardTouchBinding>(bindings) };
            File.WriteAllText(path, JsonSerializer.Serialize(doc, Options));
        }

        /// <summary>
        /// Loads the bindings for the game currently being launched into
        /// <see cref="KeyboardTouchBindings"/>. Always assigns — including the empty set — so a
        /// previous game's bindings can never leak into the next launch in the same session.
        /// </summary>
        public static void LoadForCurrentGame()
        {
            List<KeyboardTouchBinding> loaded = new List<KeyboardTouchBinding>();
            string? path = PathForCurrentGame();

            try
            {
                if (path != null && File.Exists(path))
                {
                    Document? doc = JsonSerializer.Deserialize<Document>(File.ReadAllText(path), Options);
                    if (doc?.Touch != null)
                    {
                        foreach (KeyboardTouchBinding b in doc.Touch)
                        {
                            // A binding with no key can never fire; dropping it here keeps the
                            // resolve path free of null checks.
                            if (b != null && !string.IsNullOrWhiteSpace(b.Key)) loaded.Add(b);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Malformed JSON must not take a launch down — the game simply gets no bindings.
                //
                // Reported through Trace, NOT Log: WPR.Common.Log writes to stdout, which a WinExe
                // discards, so this warning was invisible when a bad file dropped every binding.
                // Trace reaches the per-game wpr_game_debug.log, which is where anyone debugging a
                // game's input is already looking.
                System.Diagnostics.Trace.WriteLine(
                    $"[wpr-input] input-bindings.json could not be read ({path}): {ex.Message}");
            }

            KeyboardTouchBindings.Set(loaded);

            // Unconditional, including the zero case: "no bindings loaded" and "the feature never
            // ran" look identical from inside a game, and telling them apart was the whole
            // difficulty the first time this failed.
            System.Diagnostics.Trace.WriteLine(
                $"[wpr-input] {loaded.Count} touch binding(s) loaded from {path ?? "(no install folder)"}");
        }
    }
}
