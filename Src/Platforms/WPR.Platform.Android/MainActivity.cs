using WPR.Shell;
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
        Icon = "@mipmap/ic_launcher",
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

            // ONLY THE CHEAP WORK BELONGS HERE, and that is about how long the screen stays
            // black. Android paints nothing until OnCreate RETURNS, so anything slow in it is
            // time the user spends looking at the splash — adding views earlier cannot help,
            // because none of them are drawn until the method is done.
            //
            // Measured on a Galaxy S24 Ultra before this was split: process start to first frame
            // 2.24 s, of which ~1.9 s was WprStartup.EnsureInitialized seeding the databases and
            // reconciling the achievement catalogues. The intro clip then started at 2.31 s —
            // i.e. the startup work ran BEFORE the thing whose whole job is to cover it.
            SetContentView(Resource.Layout.activity_start);
            WpTheme.ApplySystemBars(this);

            // Over the top of the Start screen, which carries on building underneath. Once per
            // process, so returning here from a game does not replay it — see IntroVideo.
            IntroVideo.PlayOnce(this);

            // Everything past this point needs the database, and the seeding is the ~1.9 s that
            // used to be in front of the first frame. It runs on a WORKER now, not merely later
            // on the UI thread.
            //
            // That distinction was measured, and posting alone is a trap: it got the first frame
            // down to 353 ms but pushed the first frame OF THE CLIP out to 2.74 s, WORSE than
            // before. VideoView prepares asynchronously and then calls start() back on the main
            // thread, so a main thread busy seeding delays the very thing the work is meant to
            // hide. Only the disk and database half goes to the worker; everything that touches
            // views or registries comes back — see FinishStartupOnUiThread.
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    WprStartup.EnsureInitialized(this);
                }
                catch (Exception ex)
                {
                    global::Android.Util.Log.Error("WPR", $"start-up initialisation failed: {ex}");
                }

                RunOnUiThread(FinishStartupOnUiThread);
            });
        }

        /// <summary>
        /// The half of start-up that must touch views and registries, run once
        /// <c>WprStartup.EnsureInitialized</c> has finished on a worker.
        /// </summary>
        /// <remarks>
        /// Split out of <see cref="OnCreate"/> so the intro clip is on screen while the databases
        /// are seeded rather than after — see the note there, which carries the measurements.
        /// </remarks>
        private void FinishStartupOnUiThread()
        {
            // The worker outlives a fast Back: the activity can be gone by the time it returns.
            if (IsFinishing || IsDestroyed) return;

            // Guide.ShowMessageBox / ShowInputBox are installed process-wide by ServicesSetup
            // and dispatch onto MessageBoxUtils.MainActivity. GameActivity redoes both in its
            // own process; this covers anything that raises a guide dialog from the launcher.
            MessageBoxUtils.MainActivity = this;
            ServicesSetup.Start();

            RequestNotificationPermissionIfNeeded();

            // Anything that asks to launch a game through the shared UI abstraction rather
            // than calling GameLauncher directly still works.
            _LaunchRequestHandler = (_, args) => RunOnUiThread(() => GameLauncher.Launch(this, args.Target));
            ApplicationLaunchRequest.Incoming += _LaunchRequestHandler;

            LayoutTiles();
            WireTiles();
            PaintAccent();

            global::Android.Util.Log.Info("WPR", "MainActivity OnCreate completed (native shell)");
        }

        /// <summary>Request code for the POST_NOTIFICATIONS prompt. The result is not acted on —
        /// see <see cref="RequestNotificationPermissionIfNeeded"/>.</summary>
        private const int NotificationPermissionRequestCode = 4301;

        /// <summary>
        /// Asks for POST_NOTIFICATIONS on API 33+, where it became a runtime permission.
        ///
        /// <para>Without it <c>NotificationManagerCompat.Notify</c> is a silent no-op, so an
        /// achievement unlock would award and persist correctly and still show the player nothing.
        /// Asked here rather than in <c>GameActivity</c> on purpose: permissions are per-app, not
        /// per-process, so the grant this obtains covers the <c>:game</c> process too — and a
        /// system permission dialog appearing over a game that is mid-launch would be worse than
        /// no notification at all.</para>
        ///
        /// <para>The answer is deliberately not acted on. A player who declines simply gets no
        /// unlock notifications; nothing else in the launcher depends on the permission, and
        /// re-asking on every start would be nagging. Android itself stops re-prompting after two
        /// refusals.</para>
        /// </summary>
        private void RequestNotificationPermissionIfNeeded()
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(33))
            {
                // Install-time permission below API 33 — already granted by the manifest entry.
                return;
            }

            try
            {
                if (CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications)
                    == Permission.Granted)
                {
                    return;
                }

                RequestPermissions(
                    new[] { global::Android.Manifest.Permission.PostNotifications },
                    NotificationPermissionRequestCode);
            }
            catch (Exception ex)
            {
                // Never let a permission prompt take down the Start screen.
                global::Android.Util.Log.Warn("WPR",
                    "POST_NOTIFICATIONS request failed: " + ex);
            }
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
