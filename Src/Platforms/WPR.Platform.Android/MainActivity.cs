using System;
using System.Linq;
using System.Threading.Tasks;

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;

using Microsoft.EntityFrameworkCore;
using Microsoft.Xna.Framework.GamerServices;

using WPR.Common;
using WPR.Models;
using WPR.Platform.Android.Native;

namespace WPR.Platform.Android
{
    /// <summary>
    /// The Start screen: a Windows Phone tile grid over native Android views.
    ///
    /// <para>This used to be an <c>AvaloniaMainActivity&lt;App&gt;</c> hosting a shared
    /// Avalonia UserControl with a bottom tab bar. The whole launcher shell — this page,
    /// games, achievements, settings, about — is now plain <c>android.app.Activity</c> with
    /// XML layouts, which is what makes the phone chrome (tile tilt, app bar, list momentum,
    /// system back) behave like the platform instead of approximating it. Avalonia remains
    /// on the reference graph only for <c>MessageBox.Avalonia</c>'s enums and the AndroidX
    /// theme resources; nothing in this process initialises it.</para>
    ///
    /// <para>LaunchMode is SingleTask, NOT SingleInstance. A singleInstance activity is the
    /// only activity allowed in its task, so every activity it starts is forced into a
    /// SEPARATE task — and Android delivers the result for a cross-task
    /// startActivityForResult immediately as RESULT_CANCELED, before the child has done
    /// anything. That made the launcher report "the game process exited unexpectedly" the
    /// instant a game started. SingleTask keeps the "only one MainActivity" property without
    /// either problem.</para>
    /// </summary>
    [Activity(
        Label = "WPR",
        Theme = "@style/WprTheme",
        Icon = "@drawable/icon",
        MainLauncher = true,
        LaunchMode = LaunchMode.SingleTask,
        ScreenOrientation = ScreenOrientation.Portrait,
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
    [Register("com.wpr.android.MainActivity")]
    public class MainActivity : Activity
    {
        private EventHandler<ApplicationLaunchRequestArgs>? _LaunchRequestHandler;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            WprStartup.EnsureInitialized(this);

            SetContentView(Resource.Layout.activity_start);
            WpTheme.ApplySystemBars(this);

            // Guide.ShowMessageBox / ShowInputBox are installed process-wide by ServicesSetup
            // and dispatch onto MessageBoxUtils.MainActivity. GameActivity redoes both in its
            // own process; this covers anything that raises a guide dialog from the launcher.
            MessageBoxUtils.MainActivity = this;
            ServicesSetup.Start();

            // Anything that asks to launch a game through the shared UI abstraction rather
            // than calling GameLauncher directly still works.
            _LaunchRequestHandler = (_, args) => RunOnUiThread(() => GameLauncher.Launch(this, args.Target));
            ApplicationLaunchRequest.Incoming += _LaunchRequestHandler;

            LayoutTiles();
            WireTiles();
            PaintAccent();

            global::Android.Util.Log.Info("WPR", "MainActivity OnCreate completed (native shell)");
        }

        protected override void OnDestroy()
        {
            if (_LaunchRequestHandler != null)
            {
                ApplicationLaunchRequest.Incoming -= _LaunchRequestHandler;
                _LaunchRequestHandler = null;
            }

            base.OnDestroy();
        }

        protected override void OnResume()
        {
            base.OnResume();

            // Settings recreates itself when the accent changes, but Start is only paused
            // underneath it — so repaint here rather than leaving stale tiles behind.
            PaintAccent();
            RefreshTileCounts();
        }

        /// <summary>
        /// Give the tiles WP's square proportion. Computed from the display width rather
        /// than measured through a layout listener: the page margin and the inter-tile gap
        /// are both fixed resources, so the arithmetic is exact and runs before first paint
        /// (no visible resize on launch).
        /// </summary>
        private void LayoutTiles()
        {
            var metrics = Resources!.DisplayMetrics!;
            int margin = Resources.GetDimensionPixelSize(Resource.Dimension.wp_page_margin);
            int gap = Resources.GetDimensionPixelSize(Resource.Dimension.wp_tile_gap);

            int content = metrics.WidthPixels - (margin * 2);
            int tile = Math.Max(1, (content - gap) / 2);

            foreach (int id in new[]
                     {
                         Resource.Id.tileGames, Resource.Id.tileAdd,
                         Resource.Id.tileAchievements, Resource.Id.tileSettings,
                         Resource.Id.tileAbout,
                     })
            {
                View view = FindViewById<View>(id)!;
                ViewGroup.LayoutParams layout = view.LayoutParameters!;
                layout.Height = tile;
                view.LayoutParameters = layout;
            }
        }

        /// <summary>
        /// Repaint everything that depends on the accent. Separate from
        /// <see cref="WireTiles"/> so it can run again on resume without re-attaching
        /// handlers.
        /// </summary>
        private void PaintAccent()
        {
            FindViewById<TextView>(Resource.Id.appTitle)!.SetTextColor(WpTheme.Accent);

            // Games and add are the two things you came here to do, so they take the full
            // accent; the rest use the muted variant so the grid has a clear focal point.
            WpTheme.PaintTile(FindViewById<View>(Resource.Id.tileGames)!, primary: true);
            WpTheme.PaintTile(FindViewById<View>(Resource.Id.tileAdd)!, primary: true);
            WpTheme.PaintTile(FindViewById<View>(Resource.Id.tileAchievements)!, primary: false);
            WpTheme.PaintTile(FindViewById<View>(Resource.Id.tileSettings)!, primary: false);
            WpTheme.PaintTile(FindViewById<View>(Resource.Id.tileAbout)!, primary: false);
        }

        private void WireTiles()
        {
            View games = FindViewById<View>(Resource.Id.tileGames)!;
            View add = FindViewById<View>(Resource.Id.tileAdd)!;
            View achievements = FindViewById<View>(Resource.Id.tileAchievements)!;
            View settings = FindViewById<View>(Resource.Id.tileSettings)!;
            View about = FindViewById<View>(Resource.Id.tileAbout)!;

            foreach (View tile in new[] { games, add, achievements, settings, about })
            {
                WpTheme.ApplyTilt(tile);
            }

            games.Click += (_, _) => StartActivity(new Intent(this, typeof(GamesActivity)));
            achievements.Click += (_, _) => StartActivity(new Intent(this, typeof(AchievementsActivity)));
            settings.Click += (_, _) => StartActivity(new Intent(this, typeof(SettingsActivity)));
            about.Click += (_, _) => StartActivity(new Intent(this, typeof(AboutActivity)));

            // Adding from Start is the same manual pick as the games page's app bar — Android
            // never scans for installable packages, so this is the only way in.
            add.Click += (_, _) => XapInstallFlow.StartPicker(this);
        }

        /// <summary>Live-tile numbers: installed games, and achievements earned so far.</summary>
        private void RefreshTileCounts()
        {
            int games = 0;
            int earned = 0;

            // WPR.Models.ApplicationContext spelled out: Activity inherits an
            // ApplicationContext property from ContextWrapper that otherwise wins.
            try { games = WPR.Models.ApplicationContext.Current.Applications!.Count(); }
            catch (Exception ex) { WPR.Common.Log.Warn(LogCategory.AppList, $"Could not count installed games: {ex.Message}"); }

            try { earned = AchievementContext.Current!.Achievements!.AsNoTracking().Count(a => a.IsEarned); }
            catch (Exception ex) { WPR.Common.Log.Warn(LogCategory.GamerServices, $"Could not count earned achievements: {ex.Message}"); }

            FindViewById<TextView>(Resource.Id.tileGamesCount)!.Text = games.ToString();
            FindViewById<TextView>(Resource.Id.tileAchievementsCount)!.Text = earned.ToString();
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
        {
            base.OnActivityResult(requestCode, resultCode, data);

            switch (requestCode)
            {
                case XapInstallFlow.RequestPickXap:
                    _ = InstallPickedAsync(resultCode, data);
                    break;

                case GameLauncher.RequestGame:
                    GameLauncher.HandleGameResult(this, resultCode, data);
                    break;
            }
        }

        private async Task InstallPickedAsync(Result resultCode, Intent? data)
        {
            bool installed = await XapInstallFlow.OnPickResultAsync(this, resultCode, data);
            RefreshTileCounts();

            // A fresh install is worth showing off — drop the user straight into the list
            // rather than leaving them on Start wondering whether it worked.
            if (installed) StartActivity(new Intent(this, typeof(GamesActivity)));
        }
    }
}
