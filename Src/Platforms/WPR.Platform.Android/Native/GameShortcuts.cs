using System;
using System.Collections.Generic;

using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Widget;

using AndroidX.Core.Content;
using AndroidX.Core.Content.PM;
using AndroidX.Core.Graphics.Drawable;

using WPR.Common;

namespace WPR.Platform.Android.Native
{
    /// <summary>
    /// WP7's "pin to start", against the Android home screen: one launcher icon per game,
    /// carrying that game's tile art and name, which starts it without the WPR shell appearing
    /// on the way.
    ///
    /// <para>Everything goes through <c>ShortcutManagerCompat</c> rather than
    /// <c>ShortcutManager</c> directly, because this app's minimum is API 21 and pinning only
    /// became a first-class API in 26. Below that the compat library falls back to the legacy
    /// <c>com.android.launcher.action.INSTALL_SHORTCUT</c> broadcast — which is why the manifest
    /// declares that permission, and why <see cref="GameShortcutActivity"/> has to be
    /// exported.</para>
    ///
    /// <para>The shortcut points at <see cref="GameShortcutActivity"/> and carries nothing but a
    /// product id. A shortcut is a permanent thing on someone's home screen, so it must not hold
    /// a snapshot of the catalogue row — see that activity for what goes wrong if it does.</para>
    /// </summary>
    internal static class GameShortcuts
    {
        /// <summary>
        /// Namespaced so ids stay distinct if anything else in this app ever publishes a
        /// shortcut. Derived from the product id rather than generated, so pinning the same game
        /// twice updates the existing shortcut instead of adding a second one.
        /// </summary>
        private const string IdPrefix = "game:";

        /// <summary>
        /// Adaptive-icon geometry: the bitmap handed to the launcher is a 108-unit square, of
        /// which only the centre 72 units survive whatever mask the launcher applies (circle,
        /// squircle, rounded square — the user picks).
        /// </summary>
        private const float CanvasUnits = 108f;

        private const float SafeZoneUnits = 72f;

        public static string IdFor(string productId) => IdPrefix + productId;

        /// <summary>
        /// Whether this home screen accepts pinned shortcuts at all. False on API 26+ for the
        /// handful of launchers that decline, and below 26 whenever no launcher answers the
        /// legacy install broadcast. Callers use it to leave the action out of the menu rather
        /// than offering something that cannot work.
        /// </summary>
        public static bool IsSupported(Context context)
        {
            try
            {
                return ShortcutManagerCompat.IsRequestPinShortcutSupported(context);
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppList, $"[wpr-shortcut] pin support probe failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>Ask the home screen to add a shortcut for <paramref name="entry"/>.</summary>
        public static void Pin(Activity host, GameEntry entry)
        {
            string? productId = entry.ProductId;
            if (string.IsNullOrWhiteSpace(productId))
            {
                // The id is the shortcut's identity and its only payload, so there is nothing to
                // pin without one.
                WpDialogs.Error(host, "pin to start", "this game has no product id, so it cannot be pinned.");
                return;
            }

            try
            {
                if (!IsSupported(host))
                {
                    WpDialogs.Error(host, "pin to start", "this home screen does not accept pinned shortcuts.");
                    return;
                }

                // null callback: on API 26+ the system's own "add to home screen" sheet IS the
                // confirmation, and the legacy path below it installs with a launcher toast. An
                // IntentSender here would buy a success signal at the cost of a BroadcastReceiver
                // in the manifest, for something the user can already see happen.
                if (!ShortcutManagerCompat.RequestPinShortcut(host, Describe(host, entry, productId!), null))
                {
                    WpDialogs.Error(host, "pin to start", "the home screen refused the shortcut.");
                    return;
                }

                Log.Info(LogCategory.AppList, $"[wpr-shortcut] pin requested for {entry.Name} ({productId})");

                // The pre-26 broadcast lands silently as far as this app can tell, so say
                // something. On 26+ the system sheet has already spoken and a toast would be a
                // second answer to the same question.
                if (!OperatingSystem.IsAndroidVersionAtLeast(26))
                {
                    Toast.MakeText(host, $"pinned {entry.Name}", ToastLength.Short)?.Show();
                }
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppList, $"[wpr-shortcut] pin failed for {entry.Name}: {ex}");
                WpDialogs.Error(host, "pin to start", ex.Message);
            }
        }

        /// <summary>
        /// Retire the shortcut for a game that is no longer installed.
        ///
        /// <para>A pinned shortcut belongs to the home screen, not to the app that published it,
        /// so it cannot be deleted from here — only disabled, which greys it out and shows
        /// <paramref name="reason"/> when it is tapped. Doing nothing instead would leave a live
        /// tile that starts <see cref="GameShortcutActivity"/> for a product id with no row
        /// behind it.</para>
        /// </summary>
        public static void Retire(Context context, string? productId, string reason)
        {
            if (string.IsNullOrWhiteSpace(productId)) return;

            try
            {
                ShortcutManagerCompat.DisableShortcuts(
                    context, new List<string> { IdFor(productId!) }, reason);
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppList, $"[wpr-shortcut] could not retire {productId}: {ex.Message}");
            }
        }

        private static ShortcutInfoCompat Describe(Context context, GameEntry entry, string productId)
        {
            Intent intent = new Intent(context, typeof(GameShortcutActivity));

            // A shortcut intent must name an action or the framework rejects the whole shortcut.
            // The component is explicit, so the action carries no routing weight.
            intent.SetAction(Intent.ActionView);
            intent.PutExtra(GameShortcutActivity.ExtraProductId, productId);

            // Its own task, cleared on every tap. Without ClearTask, tapping a shortcut while
            // another game is still up would resume that task as it stands — i.e. hand back the
            // running game instead of the one that was asked for.
            intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTask);

            return new ShortcutInfoCompat.Builder(context, IdFor(productId))
                // Short label sits under the icon; long label is used wherever the launcher has
                // room for it. The catalogue name either way, which is the name the games list
                // shows rather than the XAP's internal title.
                .SetShortLabel(entry.Name)!
                .SetLongLabel(entry.Name)!
                .SetIcon(BuildIcon(context, entry))!
                .SetIntent(intent)!
                .Build()!;
        }

        private static IconCompat BuildIcon(Context context, GameEntry entry)
        {
            Bitmap? framed = Frame(context, GameTileArt.Decode(entry.Model));

            // Adaptive even below API 26, where IconCompat does its own rounding: one code path,
            // and the launcher gets to apply the user's chosen icon shape on the versions that
            // have one.
            return framed != null
                ? IconCompat.CreateWithAdaptiveBitmap(framed)!
                : IconCompat.CreateWithResource(context, Resource.Drawable.wp_tile_placeholder)!;
        }

        /// <summary>
        /// Compose the launcher icon: the game's square tile scaled to fit the adaptive-icon safe
        /// zone, centred on the live WP accent.
        ///
        /// <para><b>Contained rather than full-bleed</b> — a WP tile is a square and the mask is
        /// not, so filling the canvas would crop whatever the artist put near the edge. Fitting
        /// the tile to the 72-unit safe zone makes it exactly the size of a normal app icon's
        /// artwork and lets the accent fill the mask around it. A circle-masked launcher still
        /// clips the tile's own corners, which WP tile art tolerates because its content is
        /// already inset.</para>
        ///
        /// <para>The accent is sampled at pin time, so a shortcut keeps the colour the shell had
        /// when it was created. Repainting pinned icons on an accent change is possible
        /// (<c>ShortcutManagerCompat.UpdateShortcuts</c>) and deliberately not done — it would
        /// mean the Settings page reaching into the shortcut store on every write.</para>
        /// </summary>
        private static Bitmap? Frame(Context context, Bitmap? tile)
        {
            int side = CanvasSide(context);

            Bitmap? output = Bitmap.CreateBitmap(side, side, Bitmap.Config.Argb8888!);
            if (output == null) return null;

            using (Canvas canvas = new Canvas(output))
            {
                canvas.DrawColor(WpTheme.Accent);

                int inset = (int)Math.Round(side * (CanvasUnits - SafeZoneUnits) / 2f / CanvasUnits);
                int safe = Math.Max(1, side - (inset * 2));

                if (tile != null && tile.Width > 0 && tile.Height > 0)
                {
                    float scale = Math.Min((float)safe / tile.Width, (float)safe / tile.Height);
                    int width = Math.Max(1, (int)Math.Round(tile.Width * scale));
                    int height = Math.Max(1, (int)Math.Round(tile.Height * scale));
                    int left = (side - width) / 2;
                    int top = (side - height) / 2;

                    using (Paint paint = new Paint(PaintFlags.FilterBitmap | PaintFlags.AntiAlias))
                    using (Rect source = new Rect(0, 0, tile.Width, tile.Height))
                    using (Rect destination = new Rect(left, top, left + width, top + height))
                    {
                        canvas.DrawBitmap(tile, source, destination, paint);
                    }
                }
                else
                {
                    // The same stand-in the games list draws, so a game with no tile art is
                    // recognisably the same thing in both places.
                    Drawable? placeholder = ContextCompat.GetDrawable(context, Resource.Drawable.wp_tile_placeholder);
                    if (placeholder != null)
                    {
                        placeholder.SetBounds(inset, inset, inset + safe, inset + safe);
                        placeholder.Draw(canvas);
                    }
                }
            }

            return output;
        }

        /// <summary>
        /// Size of the composed bitmap: the launcher's own icon size scaled up by 108/72, so the
        /// tile lands at native resolution inside the safe zone. Clamped to what the shortcut API
        /// will carry, because the bitmap travels to the launcher over IPC.
        /// </summary>
        private static int CanvasSide(Context context)
        {
            int visible = 0;

            try
            {
                ActivityManager? activityManager =
                    (ActivityManager?)context.GetSystemService(Context.ActivityService);
                visible = activityManager?.LauncherLargeIconSize ?? 0;
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppList, $"[wpr-shortcut] launcher icon size unavailable: {ex.Message}");
            }

            if (visible <= 0)
            {
                // 48dp is the baseline launcher icon across every density bucket.
                visible = (int)Math.Round(48 * (context.Resources?.DisplayMetrics?.Density ?? 2f));
            }

            int side = (int)Math.Round(visible * CanvasUnits / SafeZoneUnits);

            try
            {
                int max = Math.Min(
                    ShortcutManagerCompat.GetIconMaxWidth(context),
                    ShortcutManagerCompat.GetIconMaxHeight(context));

                if (max > 0) side = Math.Min(side, max);
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppList, $"[wpr-shortcut] icon size limit unavailable: {ex.Message}");
            }

            return Math.Max(1, side);
        }
    }
}
