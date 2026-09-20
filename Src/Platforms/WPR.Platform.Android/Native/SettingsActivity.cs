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
    /// Settings for the Android head: gamertag, accent colour, the global vibration switch and the
    /// graphics-driver escape hatch.
    ///
    /// <para>The desktop page also has data-store and game-library folder pickers. Neither
    /// applies here — the data store is the app-private external files dir Android hands
    /// us, and there is no scanned library at all (see <see cref="XapInstallFlow"/>).</para>
    ///
    /// <para>The vibration switch has no desktop counterpart yet: a PC has no motor, so the only
    /// thing it would mute there is controller rumble. The setting itself is cross-platform
    /// (<c>Configuration.VibrationEnabled</c>), so adding that page is UI work only.</para>
    ///
    /// <para>Neither does the graphics picker, for a different reason: the Windows head declares no
    /// driver at all (D3D11 is picked automatically), so there would be nothing for it to
    /// override.</para>
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
            BuildGraphicsDriverPicker();
        }

        /// <summary>
        /// The FNA3D graphics driver games launch with. An escape hatch, not a preference: Android
        /// forces Vulkan for a measured reason (the OpenGL driver blocks every off-thread GPU call
        /// until the next swap, which deadlocks games that load content on a worker), so the only
        /// people who should ever touch this are the ones for whom Vulkan does not work at all.
        ///
        /// <para><b>Why it has to be reachable from the phone.</b> FNA3D's driver ladder can rescue
        /// a driver that declines at <c>PrepareWindowAttributes</c>, but not one that prepares and
        /// then fails inside <c>FNA3D_CreateDevice</c> — that arrives as a black screen, an error
        /// dialog and a dead game process, every single launch. Until now the only way out was
        /// writing <c>fna3d_driver.txt</c> into the app's external files directory, which needs a
        /// PC and adb. That file still wins over this, and stays the triage tool.</para>
        ///
        /// <para>Deliberately no "automatic" option. On Android automatic means OpenGL, because
        /// that is the driver FNA3D offers first, so it would be a third name for one of the two
        /// buttons already here.</para>
        /// </summary>
        private void BuildGraphicsDriverPicker()
        {
            LinearLayout options = FindViewById<LinearLayout>(Resource.Id.graphicsDriverOptions)!;
            options.RemoveAllViews();

            // Null (nothing chosen) reads as Vulkan, which is what AndroidPlatform declares for it.
            string current =
                string.Equals(Configuration.Current?.GraphicsDriver, "OpenGL", StringComparison.OrdinalIgnoreCase)
                    ? "OpenGL"
                    : "Vulkan";

            foreach (string driver in new[] { "Vulkan", "OpenGL" })
            {
                options.AddView(BuildDriverOption(driver, selected: driver == current));
            }
        }

        private View BuildDriverOption(string driver, bool selected)
        {
            DisplayMetrics metrics = Resources!.DisplayMetrics!;
            int gap = (int)TypedValue.ApplyDimension(ComplexUnitType.Dip, 10, metrics);
            int padX = (int)TypedValue.ApplyDimension(ComplexUnitType.Dip, 20, metrics);
            int padY = (int)TypedValue.ApplyDimension(ComplexUnitType.Dip, 11, metrics);

            // The chosen one is an accent tile; the other is the same chrome the gamertag field
            // uses. Deliberately NOT the accent grid's white-ring marker — that grid is a field of
            // colours where a fill cannot mean "selected", and reusing the ring here would leave
            // two differently-marked selections on one page.
            TextView option = new TextView(this);
            option.Text = driver.ToLowerInvariant();
            option.SetTextColor(WpTheme.Foreground);
            option.TextSize = 17;
            option.SetPadding(padX, padY, padX, padY);
            option.SetBackgroundColor(selected ? WpTheme.Accent : WpTheme.Chrome);

            LinearLayout.LayoutParams layout = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            layout.SetMargins(0, 0, gap, 0);
            option.LayoutParameters = layout;

            option.Clickable = true;
            option.ContentDescription = driver;
            WpTheme.ApplyTilt(option, 0.95f);
            option.Click += (_, _) => SelectGraphicsDriver(driver);

            return option;
        }

        private void SelectGraphicsDriver(string driver)
        {
            if (Configuration.Current == null) return;

            // Vulkan is stored as null rather than as the string, so a config.json that has never
            // been touched and one that has been set back to the default are the same thing — and
            // so a future change of platform default is not silently pinned by an old config.
            Configuration.Current.GraphicsDriver =
                string.Equals(driver, "OpenGL", StringComparison.OrdinalIgnoreCase) ? "OpenGL" : null;

            // Straight away, not in OnPause: one deliberate tap, and someone who is here because
            // games crash may well kill the app from the task switcher rather than navigate back.
            Configuration.Current.Save();

            // Forget anything the crash breadcrumb had concluded. A deliberate pick outranks it
            // either way, so this changes nothing today — it matters when they pick Vulkan back,
            // which stores null: without this, the old verdict would survive and immediately
            // demote them to OpenGL again, so the button would look broken.
            WPR.Engine.Graphics.GraphicsDriverProbe.Clear();

            // Re-declare the platform, because THIS process captured the old answer at startup.
            //
            // The driver is the one setting that is read once and stored rather than consulted
            // live: AndroidPlatform.ChosenGraphicsDriver() reads config.json during
            // ServicesSetup.Start(), and PlatformComposition hands the result to
            // GraphicsDriverPreference, which then holds it for the life of the process. The
            // :game process is born fresh for every launch and re-reads config.json, so games
            // always got the new driver immediately — but everything the LAUNCHER says about it
            // kept quoting the startup answer until the app was restarted. Measured on a Galaxy
            // S24: after picking opengl, the game ran "FNA3D Driver: OpenGL" while the game info
            // page still read "SELECTION driver=Vulkan" beside "BACKEND OpenGL", contradicting
            // itself on one screen. The misleading half is GameLauncher's failure dialog, which
            // names the driver and tells the player to try the other one — the one message that
            // has to be right is shown to someone whose game just died.
            //
            // ServicesSetup.Start() rather than a PlatformComposition.Apply call of our own:
            // building the descriptor here would be a second copy of the composition root's
            // argument list to keep in step. It is safe to run twice by construction — that is
            // the same property GameActivity's :game process already relies on, every registry
            // underneath being set-by-assignment and the audio stack de-duplicating by name.
            //
            // Vibration needs none of this and must not be added to it: VibrationBackend.IsEnabled
            // reads Configuration.Current every time it is asked, so the switch above is already
            // live. Prefer that shape for a new setting; this call is the price of storing one.
            ServicesSetup.Start();

            // Only repaint the picker. Recreate() would be the accent-grid answer, but nothing else
            // on this page depends on the driver and the gamertag field would lose an unsaved edit.
            BuildGraphicsDriverPicker();
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
