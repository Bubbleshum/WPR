using System;
using System.IO;

using Android.Graphics;

using WPR.Common;

using WprApplication = WPR.Models.Application;

namespace WPR.Platform.Android.Native
{
    /// <summary>
    /// A game's tile art, decoded off disk.
    ///
    /// <para>Shared by the games list — which caches the result per product, because its adapter
    /// runs on every fling frame — and by <see cref="GameShortcuts"/>, which needs the same bitmap
    /// to build a home-screen icon. Resolving where that file actually is is the one thing both
    /// callers have to agree on, and it is not just <see cref="WprApplication.IconPath"/>: that
    /// names a file inside the install folder, so <see cref="GameIconStore"/> owns the rule and
    /// falls back to its own copy. Named for the art rather than the file so it does not read as a
    /// second icon store beside that one.</para>
    /// </summary>
    internal static class GameTileArt
    {
        /// <summary>
        /// Null when the game shipped no icon, the file has gone, or the bytes will not decode.
        /// All three mean "draw the placeholder" rather than being errors — plenty of XAPs carry
        /// no tile art at all.
        /// </summary>
        public static Bitmap? Decode(WprApplication model)
        {
            try
            {
                string? relative = GameIconStore.Resolve(model.ProductId, model.IconPath);
                if (string.IsNullOrWhiteSpace(relative)) return null;

                string full = Configuration.Current!.DataPath(relative!);
                return File.Exists(full) ? BitmapFactory.DecodeFile(full) : null;
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppList,
                    $"Could not decode icon for {model.ProductId ?? model.Name}: {ex.Message}");
                return null;
            }
        }
    }
}
