using System;

using Android.App;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Android.Widget;

using WPR.Common;

namespace WPR.Platform.Android.Native
{
    /// <summary>
    /// Settings for the Android head: gamertag and accent colour.
    ///
    /// <para>The desktop page also has data-store and game-library folder pickers. Neither
    /// applies here — the data store is the app-private external files dir Android hands
    /// us, and there is no scanned library at all (see <see cref="XapInstallFlow"/>).</para>
    /// </summary>
    [Activity(
        Label = "settings",
        Theme = "@style/WprTheme",
        ScreenOrientation = ScreenOrientation.Portrait,
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
    [Register("com.wpr.android.SettingsActivity")]
    public class SettingsActivity : Activity
    {
        private EditText _GamerTag = null!;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            WprStartup.EnsureInitialized(this);

            SetContentView(Resource.Layout.activity_settings);
            WpTheme.ApplySystemBars(this);

            FindViewById<TextView>(Resource.Id.appTitle)!.SetTextColor(WpTheme.Accent);

            _GamerTag = FindViewById<EditText>(Resource.Id.gamerTagInput)!;
            _GamerTag.Text = Configuration.Current?.GamerTag ?? "";

            FindViewById<TextView>(Resource.Id.storagePathText)!.Text =
                Configuration.Current?.DataStorePath ?? "(not initialised)";

            BuildAccentGrid();
        }

        /// <summary>
        /// Persist on the way out rather than on every keystroke: <c>Configuration.Save</c>
        /// rewrites config.json, and a per-character write would hammer the disk while the
        /// user types their gamertag.
        /// </summary>
        protected override void OnPause()
        {
            base.OnPause();

            if (Configuration.Current == null) return;

            // Only the persisted value matters. Games read it through GamerServices when
            // they launch, in their own process — there is no signed-in gamer object in the
            // launcher process to update live.
            Configuration.Current.GamerTag = _GamerTag.Text ?? "";
            Configuration.Current.Save();
        }

        private void BuildAccentGrid()
        {
            GridLayout grid = FindViewById<GridLayout>(Resource.Id.accentGrid)!;
            grid.RemoveAllViews();

            DisplayMetrics metrics = Resources!.DisplayMetrics!;
            int swatch = (int)TypedValue.ApplyDimension(ComplexUnitType.Dip, 46, metrics);
            int gap = (int)TypedValue.ApplyDimension(ComplexUnitType.Dip, 6, metrics);
            int ring = (int)TypedValue.ApplyDimension(ComplexUnitType.Dip, 3, metrics);

            Color current = WpTheme.Accent;

            foreach (WpAccent accent in WpTheme.Accents)
            {
                Color color = accent.Color;
                bool selected = color.ToArgb() == current.ToArgb();

                // The selection marker is a white frame around the swatch, which is how WP
                // showed the active theme colour — no tick, no shadow.
                FrameLayout frame = new FrameLayout(this);
                frame.SetBackgroundColor(selected ? WpTheme.Foreground : Color.Transparent);
                int pad = selected ? ring : 0;
                frame.SetPadding(pad, pad, pad, pad);

                View fill = new View(this);
                fill.SetBackgroundColor(color);
                fill.LayoutParameters = new FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
                frame.AddView(fill);

                GridLayout.LayoutParams layout = new GridLayout.LayoutParams
                {
                    Width = swatch,
                    Height = swatch,
                };
                layout.SetMargins(0, 0, gap, gap);
                frame.LayoutParameters = layout;

                frame.Clickable = true;
                frame.ContentDescription = accent.Name;
                WpTheme.ApplyTilt(frame, 0.9f);
                frame.Click += (_, _) => SelectAccent(accent);

                grid.AddView(frame);
            }
        }

        private void SelectAccent(WpAccent accent)
        {
            if (Configuration.Current == null) return;

            Configuration.Current.AccentColor = accent.Hex;
            Configuration.Current.Save();

            // Every accented surface is painted in OnCreate, so a recreate is both the
            // simplest and the most complete way to repaint. Running games are unaffected:
            // PhoneTheme captures the accent into element styles at XAML load, so they pick
            // the new colour up on their next launch.
            Recreate();
        }
    }
}
