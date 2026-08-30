using System;
using System.Collections.Generic;

using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;

using WPR.Common;

// Android.Content.Res exports a Configuration type too; the shell always means WPRs.
using WprConfiguration = WPR.Common.Configuration;

namespace WPR.Platform.Android.Native
{
    /// <summary>
    /// One WP7 system accent. The palette below is the same twenty colours the phone's
    /// theme picker offered, and the same list the desktop head shows in
    /// the desktop head's <c>WP7AccentColors</c>.
    ///
    /// <para>It is duplicated here rather than referenced because that type carries an
    /// <c>Avalonia.Media.IBrush</c> per entry: the native shell has no Avalonia
    /// application, and touching those brushes would drag Avalonia's type system into a
    /// process that never initialises it. The values are fixed WP7 constants — if one
    /// ever changes, change it in both places.</para>
    /// </summary>
    internal sealed class WpAccent
    {
        public WpAccent(string name, string hex)
        {
            Name = name;
            Hex = hex;
        }

        /// <summary>Lowercase display name, as WP7 spelled it in Settings.</summary>
        public string Name { get; }

        /// <summary>"#AARRGGBB", the exact form <see cref="Configuration.AccentColor"/> persists.</summary>
        public string Hex { get; }

        public Color Color => Color.ParseColor(Hex);
    }

    /// <summary>
    /// Colour and chrome helpers for the native launcher shell. Everything the shell
    /// paints in the accent colour goes through here so that changing the accent in
    /// Settings is a single write to <see cref="Configuration.AccentColor"/> plus an
    /// activity recreate.
    /// </summary>
    internal static class WpTheme
    {
        /// <summary>WP7's device default, "Cyan". Matches <c>Resources/values/colors.xml</c>.</summary>
        public const string DefaultAccentHex = "#FF1BA1E2";

        public static readonly IReadOnlyList<WpAccent> Accents = new[]
        {
            new WpAccent("lime",    "#FFA4C400"),
            new WpAccent("green",   "#FF60A917"),
            new WpAccent("emerald", "#FF008A00"),
            new WpAccent("teal",    "#FF00ABA9"),
            new WpAccent("cyan",    DefaultAccentHex),
            new WpAccent("cobalt",  "#FF0050EF"),
            new WpAccent("indigo",  "#FF6A00FF"),
            new WpAccent("violet",  "#FFAA00FF"),
            new WpAccent("pink",    "#FFF472D0"),
            new WpAccent("magenta", "#FFD80073"),
            new WpAccent("crimson", "#FFA20025"),
            new WpAccent("red",     "#FFE51400"),
            new WpAccent("orange",  "#FFFA6800"),
            new WpAccent("amber",   "#FFF0A30A"),
            new WpAccent("yellow",  "#FFE3C800"),
            new WpAccent("brown",   "#FF825A2C"),
            new WpAccent("olive",   "#FF6D8764"),
            new WpAccent("steel",   "#FF647687"),
            new WpAccent("mauve",   "#FF76608A"),
            new WpAccent("sienna",  "#FFA0522D"),
        };

        /// <summary>
        /// The live accent. Reads <see cref="WprConfiguration.AccentColor"/> every call rather
        /// than caching: Settings writes the config and recreates the activity, so a cache
        /// would only add a way to be stale.
        /// </summary>
        public static Color Accent
        {
            get
            {
                string? hex = null;
                try { hex = WprConfiguration.Current?.AccentColor; }
                catch { /* config not initialised yet — fall through to the default */ }

                if (!string.IsNullOrWhiteSpace(hex))
                {
                    try { return Color.ParseColor(hex); }
                    catch (Exception) { /* corrupt value in config.json; use the default */ }
                }

                return Color.ParseColor(DefaultAccentHex);
            }
        }

        /// <summary>
        /// A darker sibling of the accent, for the "unaccented" tiles on the Start screen.
        /// WP7 let every tile take the accent; muting the secondary ones instead keeps the
        /// games tile as the obvious primary action.
        /// </summary>
        public static Color Muted(Color accent) =>
            Color.Argb(255,
                (int)(accent.R * 0.42),
                (int)(accent.G * 0.42),
                (int)(accent.B * 0.42));

        public static readonly Color Chrome = Color.ParseColor("#FF1F1F1F");
        public static readonly Color Foreground = Color.White;

        /// <summary>
        /// The WP "tilt" press feedback: the tile scales down under the finger and springs
        /// back on release. Returns without consuming the event so the view still gets its
        /// normal click.
        /// </summary>
        public static void ApplyTilt(View view, float pressedScale = 0.95f)
        {
            view.Touch += (sender, e) =>
            {
                if (sender is not View v || e.Event == null)
                {
                    e.Handled = false;
                    return;
                }

                switch (e.Event.Action)
                {
                    case MotionEventActions.Down:
                        v.Animate()?.ScaleX(pressedScale)?.ScaleY(pressedScale)?.SetDuration(90)?.Start();
                        break;
                    case MotionEventActions.Up:
                    case MotionEventActions.Cancel:
                        v.Animate()?.ScaleX(1f)?.ScaleY(1f)?.SetDuration(130)?.Start();
                        break;
                }

                // Never handled: the tilt is decoration layered on top of the click.
                e.Handled = false;
            };
        }

        /// <summary>
        /// Paint a Start-screen tile. <paramref name="primary"/> picks the full accent over
        /// the muted variant.
        ///
        /// <para>Colour only — the tilt press is attached separately, because this runs again
        /// on every resume (to pick up an accent changed in Settings) and re-attaching the
        /// touch handler each time would stack duplicates.</para>
        /// </summary>
        public static void PaintTile(View tile, bool primary)
        {
            Color accent = Accent;
            tile.SetBackgroundColor(primary ? accent : Muted(accent));
        }

        /// <summary>
        /// Rebuild an app-bar button's ring so its pressed fill uses the live accent. The
        /// XML selector can only name the static <c>@color/wp_accent</c>, so a user who
        /// picked, say, magenta would still get a cyan flash without this.
        /// </summary>
        public static void ApplyAppBarButton(View button)
        {
            float density = button.Resources?.DisplayMetrics?.Density ?? 2f;
            int stroke = Math.Max(1, (int)Math.Round(2 * density));

            GradientDrawable normal = new GradientDrawable();
            normal.SetShape(ShapeType.Oval);
            normal.SetColor(Color.Transparent);
            normal.SetStroke(stroke, Foreground);

            GradientDrawable pressed = new GradientDrawable();
            pressed.SetShape(ShapeType.Oval);
            pressed.SetColor(Accent);
            pressed.SetStroke(stroke, Foreground);

            StateListDrawable states = new StateListDrawable();
            states.AddState(new[] { global::Android.Resource.Attribute.StatePressed }, pressed);
            states.AddState(Array.Empty<int>(), normal);

            button.Background = states;
        }

        /// <summary>Tint a horizontal progress bar with the live accent.</summary>
        public static void ApplyProgress(ProgressBar bar)
        {
            bar.ProgressTintList = ColorStateList.ValueOf(Accent);
        }

        /// <summary>
        /// Paint the system bars to match the page. Without this the status bar keeps the
        /// platform's translucent grey and the black page reads as a floating panel.
        /// </summary>
        public static void ApplySystemBars(global::Android.App.Activity activity)
        {
            var window = activity.Window;
            if (window == null) return;

            Color background = Color.Black;
            window.SetStatusBarColor(background);
            window.SetNavigationBarColor(background);
        }
    }
}
