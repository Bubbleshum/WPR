using WPR.Shell;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;

using WPR.Common;
using WPR.Models;

namespace WPR.Platform.Android.Native
{
    /// <summary>
    /// The games list — WP page chrome over the installed-application table, with an app
    /// bar whose primary action is adding a .xap by hand.
    ///
    /// <para>Only installed games appear here. There is no discovery pass: see
    /// <see cref="XapInstallFlow"/> for why the Android head does not scan.</para>
    /// </summary>
    [Activity(
        Label = "games",
        Theme = "@style/WprTheme",
        ScreenOrientation = ScreenOrientation.Portrait,
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
    [Register("com.wpr.android.GamesActivity")]
    public class GamesActivity : Activity
    {
        private GameListAdapter _Adapter = null!;
        private ListView _List = null!;
        private View _EmptyState = null!;

        /// <summary>
        /// Suppresses the reload in <see cref="OnResume"/> for the one resume that follows a
        /// picker or game result — those paths reload themselves once their own work is
        /// finished, and reloading first would just flash a stale list.
        /// </summary>
        private bool _SkipNextResumeReload;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            // Android can recreate the process straight into this activity after a
            // low-memory kill, so it cannot assume the Start screen ran first.
            WprStartup.EnsureInitialized(this);

            SetContentView(Resource.Layout.activity_games);
            WpTheme.ApplySystemBars(this);

            _List = FindViewById<ListView>(Resource.Id.gameList)!;
            _EmptyState = FindViewById<View>(Resource.Id.emptyState)!;

            _Adapter = new GameListAdapter(this);
            _List.Adapter = _Adapter;

            _List.ItemClick += (_, e) => GameLauncher.Launch(this, _Adapter[e.Position].Model);
            _List.ItemLongClick += (_, e) => _ = ShowContextSheetAsync(_Adapter[e.Position]);

            WireAppBar();
            PaintAccent();
            Reload();
        }

        /// <summary>
        /// Repaint the accented chrome. Runs on resume too, so an accent chosen in Settings
        /// reaches this page without it having to be recreated.
        /// </summary>
        private void PaintAccent()
        {
            FindViewById<TextView>(Resource.Id.appTitle)!.SetTextColor(WpTheme.Accent);

            foreach (int id in new[] { Resource.Id.appBarAddIcon, Resource.Id.appBarAchievementsIcon, Resource.Id.appBarRefreshIcon })
            {
                WpTheme.ApplyAppBarButton(FindViewById<View>(id)!);
            }
        }

        private void WireAppBar()
        {
            FindViewById<View>(Resource.Id.appBarAdd)!.Click += (_, _) => XapInstallFlow.StartPicker(this);

            FindViewById<View>(Resource.Id.appBarAchievements)!.Click +=
                (_, _) => StartActivity(new Intent(this, typeof(AchievementsActivity)));

            FindViewById<View>(Resource.Id.appBarRefresh)!.Click += (_, _) => Reload();
        }

        protected override void OnResume()
        {
            base.OnResume();

            PaintAccent();

            if (_SkipNextResumeReload)
            {
                _SkipNextResumeReload = false;
                return;
            }

            Reload();
        }

        /// <summary>
        /// Re-read the application table. Runs on the UI thread on purpose:
        /// <see cref="ApplicationContext.Current"/> is a single shared EF context, which is
        /// not thread-safe, and the table is a few dozen rows of local SQLite.
        /// </summary>
        private void Reload()
        {
            List<GameEntry> entries;
            try
            {
                // Fully qualified: Activity inherits an ApplicationContext property from
                // ContextWrapper, which otherwise wins over the EF context type.
                entries = WPR.Models.ApplicationContext.Current.Applications!
                    .ToList()
                    .Select(app => new GameEntry(app))
                    .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                Log.Error(LogCategory.AppList, $"Unable to query the application database:\n{ex}");
                entries = new List<GameEntry>();
            }

            _Adapter.SetItems(entries);
            _EmptyState.Visibility = entries.Count == 0 ? ViewStates.Visible : ViewStates.Gone;
            _List.Visibility = entries.Count == 0 ? ViewStates.Gone : ViewStates.Visible;
        }

        /// <summary>
        /// WP7's long-press context menu. Kept to the things that are actually
        /// destructive-or-slow enough to deserve confirmation of intent, plus the ones that
        /// belong on a per-game menu and nowhere else; a plain tap still just plays the game.
        /// </summary>
        private async Task ShowContextSheetAsync(GameEntry entry)
        {
            var actions = new List<string> { "play", "achievements", "info" };

            // WP7's own wording, and the same gesture: long-press a game, pin it to Start.
            // Offered only when the home screen will take it — a few third-party launchers
            // decline, and below API 26 pinning needs a launcher that answers the legacy
            // install broadcast.
            if (GameShortcuts.IsSupported(this)) actions.Add("pin to start");

            actions.Add("re-patch");
            actions.Add("uninstall");

            int choice = await WpDialogs.ChooseAsync(this, entry.Name, actions.ToArray());

            // Dismissal returns -1. Dispatching on the label rather than the index keeps
            // this switch correct when an action is inserted into the list above.
            if (choice < 0 || choice >= actions.Count) return;

            switch (actions[choice])
            {
                case "play":
                    GameLauncher.Launch(this, entry.Model);
                    break;

                case "achievements":
                    Intent intent = new Intent(this, typeof(AchievementsActivity));
                    intent.PutExtra(AchievementsActivity.ExtraProductId, entry.ProductId);
                    intent.PutExtra(AchievementsActivity.ExtraGameName, entry.Name);
                    StartActivity(intent);
                    break;

                case "info":
                    Intent info = new Intent(this, typeof(GameInfoActivity));
                    info.PutExtra(GameInfoActivity.ExtraProductId, entry.ProductId);
                    info.PutExtra(GameInfoActivity.ExtraGameName, entry.Name);
                    StartActivity(info);
                    break;

                case "pin to start":
                    GameShortcuts.Pin(this, entry);
                    break;

                case "re-patch":
                    await RepatchAsync(entry);
                    break;

                case "uninstall":
                    await UninstallAsync(entry);
                    break;
            }
        }

        /// <summary>
        /// Re-run the patcher over an already-extracted install. Cheaper than uninstall +
        /// reinstall when the only thing that changed is WPR's redirect table, and it avoids
        /// the file-lock pitfalls of deleting the install folder.
        /// </summary>
        private async Task RepatchAsync(GameEntry entry)
        {
            WpProgressDialog progress = WpProgressDialog.Show(this, entry.Name, "re-patching assemblies…", indeterminate: false);

            try
            {
                // Cecil resolves references out of the staging folder, which is also the
                // process current directory. A game launch can have moved it.
                WprStartup.SetupDllPatchForCecil(this);

                ApplicationInstallError error = await ApplicationInstaller.RepatchAsync(
                    entry.Model,
                    percent => progress.SetProgress(percent),
                    CancellationToken.None);

                progress.Dismiss();

                if (error != ApplicationInstallError.None && error != ApplicationInstallError.Canceled)
                {
                    WpDialogs.Error(this, WPR.Shell.Resources.InstallationFailed,
                        LocaleUtils.GetDisplayName(error));
                }
            }
            catch (Exception ex)
            {
                progress.Dismiss();
                Log.Error(LogCategory.AppInstall, $"Re-patch failed for {entry.Name}:\n{ex}");
                WpDialogs.Error(this, WPR.Shell.Resources.InstallationFailed, ex.Message);
            }
            finally
            {
                Reload();
            }
        }

        private async Task UninstallAsync(GameEntry entry)
        {
            bool confirmed = await WpDialogs.ConfirmAsync(
                this,
                "uninstall",
                $"remove {entry.Name} and everything it has saved on this device?");

            if (!confirmed) return;

            try
            {
                await ApplicationInstaller.UninstallAsync(entry.Model);

                // A pinned shortcut outlives the game it points at — the home screen owns it and
                // this app cannot delete it — so retire it here rather than leaving a live tile
                // for a product id with no row behind it.
                GameShortcuts.Retire(this, entry.ProductId, $"{entry.Name} is no longer installed.");
            }
            catch (Exception ex)
            {
                Log.Error(LogCategory.AppList, $"Uninstall failed for {entry.Name}:\n{ex}");
                WpDialogs.Error(this, "uninstall failed", ex.Message);
            }

            Reload();
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
        {
            base.OnActivityResult(requestCode, resultCode, data);

            switch (requestCode)
            {
                case XapInstallFlow.RequestPickXap:
                    _SkipNextResumeReload = true;
                    _ = InstallPickedAsync(resultCode, data);
                    break;

                case GameLauncher.RequestGame:
                    GameLauncher.HandleGameResult(this, resultCode, data);
                    break;
            }
        }

        private async Task InstallPickedAsync(Result resultCode, Intent? data)
        {
            await XapInstallFlow.OnPickResultAsync(this, resultCode, data);
            Reload();
        }
    }
}
