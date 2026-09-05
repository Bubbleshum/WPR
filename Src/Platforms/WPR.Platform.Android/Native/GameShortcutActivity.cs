using System;
using System.Linq;

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;

using AndroidX.Core.Content.PM;

using WPR.Common;

using WprApplication = WPR.Models.Application;

namespace WPR.Platform.Android.Native
{
    /// <summary>
    /// What a home-screen game shortcut actually starts: resolve the product id back to a
    /// catalogue row, hand it to <see cref="GameLauncher"/>, and get out of the way.
    ///
    /// <para><b>Why a trampoline rather than pointing the shortcut straight at
    /// <c>GameActivity</c>.</b> Three things happen between "the user picked a game" and the
    /// game running, and all three would be lost: the install is brought up to the current
    /// <c>ApplicationPatcher.Version</c> first (a shortcut carrying a serialised
    /// <c>Application</c> would go stale the next time the patcher table changes, and the game
    /// would TypeLoadException on launch); native ports are routed to their own activity
    /// entirely; and a game process that dies reports the reason through
    /// <c>onActivityResult</c>, which needs a caller to report it to.</para>
    ///
    /// <para><b>Its own task</b> (see the affinity below). A shortcut tap must not surface the
    /// launcher UI on the way to the game, and when the game is over the home screen should come
    /// back rather than the games list — so nothing of this app's own is left underneath.</para>
    /// </summary>
    [Activity(
        Label = "@string/app_title",
        Theme = "@style/WprTheme",
        // Kept out of the launcher shell's task so a shortcut never brings the Start screen
        // forward, and so finishing here returns to the home screen the shortcut was tapped
        // from. GameActivity is started without NEW_TASK and therefore joins this task, which is
        // what keeps startActivityForResult working across the process boundary.
        TaskAffinity = "com.wpr.android.shortcut",
        // The task is in recents for as long as the game is running, and gone the moment it is
        // not. Without this the finished task lingers as a card labelled with the app name — one
        // per game ever launched this way, each of which silently restarts its game when tapped.
        // The home-screen shortcut is the one place that should offer to do that.
        AutoRemoveFromRecents = true,
        // Deliberately no ScreenOrientation: this window exists for a few hundred milliseconds
        // and pinning it to portrait would rotate the device twice on the way into a landscape
        // game.
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize,
        // Required. Below API 26 ShortcutManagerCompat falls back to the legacy INSTALL_SHORTCUT
        // broadcast, after which the launcher starts this intent as ITSELF rather than on our
        // behalf, and an unexported activity would refuse it. The exposure is bounded: all this
        // can do is start a game that is already installed, which is what the shortcut is for.
        Exported = true)]
    [Register("com.wpr.android.GameShortcutActivity")]
    public class GameShortcutActivity : Activity
    {
        /// <summary>The one thing a shortcut carries. See the class remarks for why.</summary>
        public const string ExtraProductId = "wpr.shortcut.ProductId";

        /// <summary>Set once something else has taken the foreground — see <see cref="OnResume"/>.</summary>
        private bool _HandedOver;

        /// <summary>
        /// True while the game-failure dialog owns the screen. That dialog is shown ON this
        /// activity, so it has to survive the <see cref="OnResume"/> that follows the result.
        /// </summary>
        private bool _ShowingError;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            // A shortcut tap is a one-shot instruction, and the launcher process is a prime
            // low-memory kill target while a game holds the foreground. Being recreated with
            // saved state therefore means "the game already ran", not "start it": relaunching
            // here would put a second copy of the game behind the one the user just left.
            if (savedInstanceState != null)
            {
                Finish();
                return;
            }

            // Nothing else in this task has run, and on a cold tap this is the first activity in
            // the process — so the configuration and databases are this activity's to set up.
            WprStartup.EnsureInitialized(this);

            string? productId = Intent?.GetStringExtra(ExtraProductId);
            if (string.IsNullOrWhiteSpace(productId))
            {
                Fail("this shortcut does not name a game.");
                return;
            }

            WprApplication? app = Find(productId!);
            if (app == null)
            {
                // Uninstalling through the games list already retires the shortcut; this covers
                // a row that went away some other way (a wiped database, a hand-deleted folder).
                GameShortcuts.Retire(this, productId, "this game is no longer installed.");
                Fail("that game is not installed any more.");
                return;
            }

            // Tells the launcher the shortcut is in use, which is what lets it rank and suggest
            // it. A no-op when the shortcut was never actually pinned.
            try
            {
                ShortcutManagerCompat.ReportShortcutUsed(this, GameShortcuts.IdFor(productId!));
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppList, $"[wpr-shortcut] usage report failed: {ex.Message}");
            }

            Log.Info(LogCategory.AppList, $"[wpr-shortcut] launching {app.Name} ({productId}) from the home screen");

            GameLauncher.Launch(this, app);
        }

        protected override void OnStop()
        {
            base.OnStop();

            // The game — or a native port — is now in front of us. Nothing else in this task can
            // bring us back except that activity ending, which is the signal to bow out.
            _HandedOver = true;
        }

        protected override void OnResume()
        {
            base.OnResume();

            // The only reason to be foregrounded again is that whatever we started has ended,
            // and there is nothing behind us in this task — so finishing hands the screen back
            // to wherever the shortcut was tapped from. This also covers the native-port path,
            // which starts an activity of its own and never reports a result.
            if (_HandedOver && !_ShowingError) Finish();
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
        {
            base.OnActivityResult(requestCode, resultCode, data);

            if (requestCode != GameLauncher.RequestGame) return;

            // Runs before OnResume, so the flag is in place by the time that decides whether to
            // finish. On the clean path there is no dialog and OnResume takes us out.
            _ShowingError = resultCode != Result.Ok;

            GameLauncher.HandleGameResult(this, resultCode, data, onErrorAcknowledged: Finish);
        }

        /// <summary>
        /// The one lookup this activity does. Materialised before the comparison because
        /// <c>ApplicationContext.Current</c> is EF over SQLite and cannot translate a
        /// <see cref="StringComparison"/>; the table is a few dozen rows.
        /// </summary>
        private static WprApplication? Find(string productId)
        {
            try
            {
                // Fully qualified: Activity inherits an ApplicationContext property from
                // ContextWrapper that otherwise wins over the EF context type.
                return WPR.Models.ApplicationContext.Current.Applications!
                    .ToList()
                    .FirstOrDefault(a => string.Equals(a.ProductId, productId, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                Log.Error(LogCategory.AppList, $"[wpr-shortcut] could not read the application table:\n{ex}");
                return null;
            }
        }

        /// <summary>
        /// Report why nothing is going to start, then leave. Every dismissal path finishes,
        /// because a bare black activity with nothing on it is not somewhere to strand anyone.
        /// </summary>
        private void Fail(string message)
        {
            Log.Warn(LogCategory.AppList, $"[wpr-shortcut] {message}");

            AlertDialog dialog = new AlertDialog.Builder(this)!
                .SetTitle("cannot start")!
                .SetMessage(message)!
                .SetPositiveButton("OK", (IDialogInterfaceOnClickListener?)null)!
                .SetCancelable(true)!
                .Create()!;

            dialog.DismissEvent += (_, _) => Finish();
            dialog.Show();
        }
    }
}
