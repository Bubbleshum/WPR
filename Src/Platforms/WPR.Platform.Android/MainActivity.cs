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

            // WPR Hub: send what an earlier session queued - including a crash report the :game
            // process wrote on its way down, which only a launcher that lives on can send.
            _ = WPR.Engine.Online.OnlineBackend.FlushAsync();

            // ...and bring down what the account earned on other devices, so the gamer card and
            // achievements count are the account's, not just this install's.
            WPR.Shell.HubSetup.ProgressRestored += OnProgressRestored;
            WPR.Shell.HubSetup.RestoreProgressInBackground();

            // "Online" on WPR Hub while signed in. GameLauncher suspends it while the :game
            // process, which has its own, speaks for this device.
            WPR.Engine.Online.OnlineBackend.Presence?.Start();

            RequestNotificationPermissionIfNeeded();

            // A newer WPR on GitHub: notified once per release. AppUpdates asks at most every
            // twelve hours, so the same call on every resume below costs nothing in between.
            Native.UpdateNotifier.CheckInBackground(this);

            // Anything that asks to launch a game through the shared UI abstraction rather
            // than calling GameLauncher directly still works.
            _LaunchRequestHandler = (_, args) => RunOnUiThread(() => GameLauncher.Launch(this, args.Target, args.Diagnostic));
            ApplicationLaunchRequest.Incoming += _LaunchRequestHandler;

            LayoutTiles();
            WireTiles();
            PaintAccent();

            // The counts could not be read before this point: the first OnResume runs straight
            // after OnCreate, while the worker is still seeding the databases, so that read failed
            // and the tiles said 0 until the next time Start was resumed.
            _StartupFinished = true;
            RefreshTileCounts();

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

        /// <summary>A hub restore (sign-in or start-up) earned rows here: recount the tiles.</summary>
        private void OnProgressRestored(int earned) => RunOnUiThread(() =>
        {
            if (!IsFinishing && !IsDestroyed) RefreshTileCounts();
        });

        protected override void OnDestroy()
        {
            WPR.Shell.HubSetup.ProgressRestored -= OnProgressRestored;

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

            // Coming back from reading messages (or a game): beat now rather than at the next
            // interval, so the unread and online counts on the tiles catch up in seconds.
            if (_StartupFinished && !string.IsNullOrEmpty(Configuration.Current?.HubAccessToken))
            {
                HubSetup.Current?.Presence.Resume();
                _ = RepaintLiveTilesSoonAsync();
            }

            // Only after start-up: Configuration.Current (where the check keeps its state) is not
            // there before it. The first check runs from FinishStartupOnUiThread.
            if (_StartupFinished) Native.UpdateNotifier.CheckInBackground(this);
        }

        private async Task RepaintLiveTilesSoonAsync()
        {
            await Task.Delay(3000);
            if (!IsDestroyed && _StartupFinished) RefreshHubTile();
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
                         Resource.Id.tileGames, Resource.Id.tileHub, Resource.Id.tileMessages, Resource.Id.tileAdd,
                         Resource.Id.tileAchievements, Resource.Id.tileSettings,
                         Resource.Id.tileAbout,
                     })
            {
                View view = FindViewById<View>(id)!;
                ViewGroup.LayoutParams layout = view.LayoutParameters!;
                layout.Height = tile;
                view.LayoutParameters = layout;
            }

            // The gamerscore medallion: a thin white ring around the "G".
            var ring = new global::Android.Graphics.Drawables.GradientDrawable();
            ring.SetShape(global::Android.Graphics.Drawables.ShapeType.Oval);
            ring.SetStroke(Math.Max(1, (int)(1.5f * metrics.Density)), global::Android.Graphics.Color.White);
            FindViewById<View>(Resource.Id.tileHubScoreBadge)!.Background = ring;
        }

        /// <summary>
        /// Repaint everything that depends on the accent. Separate from
        /// <see cref="WireTiles"/> so it can run again on resume without re-attaching
        /// handlers.
        /// </summary>
        private void PaintAccent()
        {
            FindViewById<TextView>(Resource.Id.appTitle)!.SetTextColor(WpTheme.Accent);

            // Games, the hub, messages and add are the things you came here to do, so they take the full
            // accent; the rest use the muted variant so the grid has a clear focal point.
            WpTheme.PaintTile(FindViewById<View>(Resource.Id.tileGames)!, primary: true);
            WpTheme.PaintTile(FindViewById<View>(Resource.Id.tileMessages)!, primary: true);
            WpTheme.PaintTile(FindViewById<View>(Resource.Id.tileHub)!, primary: true);
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
            View hub = FindViewById<View>(Resource.Id.tileHub)!;
            View messages = FindViewById<View>(Resource.Id.tileMessages)!;

            foreach (View tile in new[] { games, hub, messages, add, achievements, settings, about })
            {
                WpTheme.ApplyTilt(tile);
            }

            games.Click += (_, _) => StartActivity(new Intent(this, typeof(GamesActivity)));
            // Signed out, the tile is the way in: straight to sign in / sign up.
            // The social page handles signed out itself (with a way to sign in). It opens on
            // messages when some are unread, since that is what the tile's number is counting.
            messages.Click += (_, _) => SocialActivity.Open(this,
                (HubSetup.Current?.Presence.LastBeat?.UnreadMessages ?? 0) > 0 ? SocialActivity.PivotMessages : SocialActivity.PivotFriends);
            hub.Click += (_, _) => StartActivity(new Intent(this,
                string.IsNullOrEmpty(Configuration.Current?.HubAccessToken) ? typeof(SignInActivity) : typeof(HubActivity)));

            // The logo replaces the glyph and "wpr hub" label on the signed-out face, and the label
            // on the gamer card, since it spells out the name itself.
            HubLogo.ApplyWhite(this, FindViewById<ImageView>(Resource.Id.tileHubLogo)!, FindViewById<View>(Resource.Id.tileHubGlyph));
            HubLogo.ApplyWhite(this, FindViewById<ImageView>(Resource.Id.tileHubCardLogo)!, FindViewById<View>(Resource.Id.tileHubCardName));
            FindViewById<View>(Resource.Id.tileHubName)!.Visibility =
                HubLogo.LoadWhite(this) != null ? ViewStates.Gone : ViewStates.Visible;

            achievements.Click += (_, _) => StartActivity(new Intent(this, typeof(AchievementsActivity)));
            settings.Click += (_, _) => StartActivity(new Intent(this, typeof(SettingsActivity)));
            about.Click += (_, _) => StartActivity(new Intent(this, typeof(AboutActivity)));

            // Adding from Start is the same manual pick as the games page's app bar — Android
            // never scans for installable packages, so this is the only way in.
            add.Click += (_, _) => XapInstallFlow.StartPicker(this);
        }

        /// <summary>
        /// Set on the UI thread once start-up has seeded the databases. Until then there is
        /// nothing to count, and reading would race the worker that is writing them.
        /// </summary>
        private bool _StartupFinished;

        /// <summary>Live-tile numbers: installed games, and achievements earned so far.</summary>
        private void RefreshTileCounts()
        {
            if (!_StartupFinished) return;

            int games = 0;
            int earned = 0;

            // WPR.Models.ApplicationContext spelled out: Activity inherits an
            // ApplicationContext property from ContextWrapper that otherwise wins.
            try { games = WPR.Models.ApplicationContext.Current.Applications!.Count(); }
            catch (Exception ex) { WPR.Common.Log.Warn(LogCategory.AppList, $"Could not count installed games: {ex.Message}"); }

            try
            {
                var earnedRows = AchievementContext.Current!.Achievements!.AsNoTracking().Where(a => a.IsEarned);
                earned = earnedRows.Count();
                _GamerScore = earnedRows.Sum(a => a.GamerScore);
            }
            catch (Exception ex) { WPR.Common.Log.Warn(LogCategory.GamerServices, $"Could not count earned achievements: {ex.Message}"); }

            FindViewById<TextView>(Resource.Id.tileGamesCount)!.Text = games.ToString();
            FindViewById<TextView>(Resource.Id.tileAchievementsCount)!.Text = earned.ToString();
            RefreshHubTile();
        }

        /// <summary>Sum of the gamerscore of every achievement earned on this device, for the gamer card.</summary>
        private int _GamerScore;

        /// <summary>
        /// The hub tile. Signed out it is the basic tile, which opens sign-in. Signed in it is a
        /// gamer card: gamertag, gamerscore and friends online, down the left, with the logo bottom
        /// right. No gamerpic, by choice: the card is the text alone.
        ///
        /// <para>Everything on the card is already on the device, so it draws without a request:
        /// the gamerscore is this device's earned achievements, and the friends count is the last
        /// presence heartbeat. The first beat lands a moment after start-up, hence the one delayed
        /// repaint.</para>
        /// </summary>
        private void RefreshHubTile()
        {
            RefreshMessagesTile();

            View basic = FindViewById<View>(Resource.Id.tileHubBasic)!;
            View card = FindViewById<View>(Resource.Id.tileHubCard)!;

            string? username = string.IsNullOrEmpty(Configuration.Current?.HubAccessToken) ? null : Configuration.Current!.HubUsername ?? "signed in";
            if (username == null)
            {
                basic.Visibility = ViewStates.Visible;
                card.Visibility = ViewStates.Gone;
                _HubTileRetried = false;
                return;
            }

            basic.Visibility = ViewStates.Gone;
            card.Visibility = ViewStates.Visible;
            FindViewById<TextView>(Resource.Id.tileHubGamertag)!.Text = username;
            FindViewById<TextView>(Resource.Id.tileHubScore)!.Text = _GamerScore.ToString("N0");

            var beat = HubSetup.Current?.Presence.LastBeat;
            TextView status = FindViewById<TextView>(Resource.Id.tileHubStatus)!;
            if (beat == null)
            {
                status.Text = "online";
            }
            else
            {
                string line = beat.FriendsOnline == 0 ? "no friends online"
                    : beat.FriendsOnline == 1 ? "1 friend online" : $"{beat.FriendsOnline} friends online";
                if (beat.PendingFriendRequests > 0)
                    line += beat.PendingFriendRequests == 1 ? " · 1 friend request" : $" · {beat.PendingFriendRequests} friend requests";
                if (beat.UnreadMessages > 0)
                    line += beat.UnreadMessages == 1 ? " · 1 message" : $" · {beat.UnreadMessages} messages";
                status.Text = line;
            }
            // Set from the social page; worth saying on the card, since friends now see "offline".
            if (Configuration.Current?.HubAppearOffline == true)
                status.Text = beat == null ? "appearing offline" : "appearing offline · " + status.Text;

            if (beat == null && !_HubTileRetried)
            {
                _HubTileRetried = true;
                _ = RefreshHubTileLaterAsync();
            }
        }

        /// <summary>
        /// The messages tile's live number: unread messages, from the last presence heartbeat, so it
        /// costs no request. Repainted with the hub tile, including by its one delayed retry.
        /// </summary>
        private void RefreshMessagesTile()
        {
            TextView count = FindViewById<TextView>(Resource.Id.tileMessagesCount)!;
            TextView status = FindViewById<TextView>(Resource.Id.tileMessagesStatus)!;

            if (string.IsNullOrEmpty(Configuration.Current?.HubAccessToken))
            {
                count.Visibility = ViewStates.Gone;
                status.Text = "sign in to chat";
                status.Visibility = ViewStates.Visible;
                return;
            }

            int unread = HubSetup.Current?.Presence.LastBeat?.UnreadMessages ?? 0;
            count.Text = unread.ToString();
            count.Visibility = unread > 0 ? ViewStates.Visible : ViewStates.Gone;
            status.Text = unread == 1 ? "1 new message" : $"{unread} new messages";
            status.Visibility = unread > 0 ? ViewStates.Visible : ViewStates.Gone;
        }

        private bool _HubTileRetried;

        private async Task RefreshHubTileLaterAsync()
        {
            await Task.Delay(5000);
            if (!IsDestroyed) RefreshHubTile();
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
