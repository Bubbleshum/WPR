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
using WPR.Engine.Vibration;

namespace WPR.Platform.Android.Native
{
    /// <summary>
    /// Settings for the Android head: gamertag, accent colour and the global vibration switch.
    ///
    /// <para>The desktop page also has data-store and game-library folder pickers. Neither
    /// applies here — the data store is the app-private external files dir Android hands
    /// us, and there is no scanned library at all (see <see cref="XapInstallFlow"/>).</para>
    ///
    /// <para>The vibration switch has no desktop counterpart yet: a PC has no motor, so the only
    /// thing it would mute there is controller rumble. The setting itself is cross-platform
    /// (<c>Configuration.VibrationEnabled</c>), so adding that page is UI work only.</para>
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
            BuildVibrationToggle();
        }

        /// <summary>
        /// The global vibration switch. One setting for every game and every vibration path —
        /// the WP7 handset motor and controller rumble both consult
        /// <c>VibrationBackend.IsEnabled</c>, which reads what this writes.
        /// </summary>
        private void BuildVibrationToggle()
        {
            Switch toggle = FindViewById<Switch>(Resource.Id.vibrationSwitch)!;
            TextView state = FindViewById<TextView>(Resource.Id.vibrationStateText)!;

            WpTheme.ApplySwitch(toggle);

            toggle.Checked = Configuration.Current?.VibrationEnabled != false;
            state.Text = toggle.Checked ? "on" : "off";

            toggle.CheckedChange += (_, args) =>
            {
                state.Text = args.IsChecked ? "on" : "off";

                if (Configuration.Current == null) return;

                // Written straight away rather than in OnPause like the gamertag: this is a
                // single deliberate tap, not a stream of keystrokes, and persisting now means the
                // setting survives the process being killed from the task switcher.
                Configuration.Current.VibrationEnabled = args.IsChecked;
                Configuration.Current.Save();

                // Confirm with the motor itself when switching ON — the same sample buzz the
                // platform's own haptics settings give, and the only feedback that distinguishes
                // "enabled" from "enabled but this device cannot". Deliberately not on the way
                // off, where a buzz would contradict what was just asked for.
                //
                // Device rather than IsEnabled: the preference was set true a line ago, so the
                // gate would pass anyway, and going direct says this is the settings page
                // demonstrating the hardware rather than a game asking to vibrate.
                if (args.IsChecked)
                {
                    VibrationBackend.Device?.Vibrate(TimeSpan.FromMilliseconds(40), 1f);
                }
            };

            // If we can tell that the device has no motor, say so — otherwise switching this on
            // and feeling nothing looks like a bug in WPR rather than a fact about the hardware.
            //
            // Device is null when this process has not composed the platform, which happens when
            // Android recreates the process straight into this activity instead of MainActivity.
            // In that case say nothing at all: a wrong claim is worse than a missing one.
            if (VibrationBackend.Device?.IsSupported == false)
            {
                FindViewById<TextView>(Resource.Id.vibrationNote)!.Text =
                    "this device has no vibration motor, so only a connected controller will rumble.";
            }
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
