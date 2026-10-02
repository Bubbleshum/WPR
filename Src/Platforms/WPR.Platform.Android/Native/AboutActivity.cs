using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;

using WPR.Common;
using WPR.Shell;

namespace WPR.Platform.Android.Native
{
    /// <summary>
    /// The About page, as two WP pivots: <b>about</b> (version, updates, project links, WPR Hub) and
    /// <b>what's new</b> (the release notes bundled into this APK, the installed version first).
    /// </summary>
    /// <remarks>
    /// The update row is the durable half of <see cref="UpdateNotifier"/>: the notification is shown
    /// once per release, this row says so for as long as the release is newer. Tapping it downloads
    /// the APK in the browser, or checks GitHub now when nothing newer is known.
    /// </remarks>
    [Activity(
        Label = "about",
        Theme = "@style/WprTheme",
        ScreenOrientation = ScreenOrientation.Portrait,
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
    [Register("com.wpr.android.AboutActivity")]
    public class AboutActivity : Activity
    {
        private const string RepoUrl = AppUpdates.RepoUrl;
        private const string CompatibilityUrl = "https://bubbleshum.github.io/WPR/";
        private const string StatePivot = "pivot";

        private string _version = "unknown";
        private bool _onWhatsNew;
        private bool _whatsNewBuilt;
        private bool _checking;
        private AppRelease? _update;

        private TextView _pivotAbout = null!;
        private TextView _pivotWhatsNew = null!;
        private View _aboutPage = null!;
        private LinearLayout _whatsNewPage = null!;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            // Opened from the update notification, this can be the first activity of a new process.
            WprStartup.EnsureInitialized(this);

            SetContentView(Resource.Layout.activity_about);
            WpTheme.ApplySystemBars(this);

            FindViewById<TextView>(Resource.Id.appTitle)!.SetTextColor(WpTheme.Accent);

            // Read the version off the installed package rather than hardcoding it, so the
            // page cannot drift from what the csproj actually shipped.
            _version = UpdateNotifier.InstalledVersion(this);

            string release = Build.VERSION.Release ?? "?";
            int api = (int)Build.VERSION.SdkInt;
            string model = Build.Model ?? "device";

            FindViewById<TextView>(Resource.Id.aboutVersion)!.Text = $"WPR {_version}";
            FindViewById<TextView>(Resource.Id.aboutBuild)!.Text =
                $"DEVELOPER EDITION  ·  android {release} (API {api})  ·  {model}";

            FindViewById<View>(Resource.Id.githubRow)!.Click += (_, _) => Open(RepoUrl);
            FindViewById<View>(Resource.Id.compatibilityRow)!.Click += (_, _) => Open(CompatibilityUrl);
            FindViewById<View>(Resource.Id.issuesRow)!.Click += (_, _) => Open(RepoUrl + "/issues");
            FindViewById<View>(Resource.Id.termsRow)!.Click += (_, _) => OpenHubPage("terms");
            FindViewById<View>(Resource.Id.privacyRow)!.Click += (_, _) => OpenHubPage("privacy");
            FindViewById<View>(Resource.Id.updateRow)!.Click += (_, _) => OnUpdateRowTapped();

            _pivotAbout = FindViewById<TextView>(Resource.Id.pivotAbout)!;
            _pivotWhatsNew = FindViewById<TextView>(Resource.Id.pivotWhatsNew)!;
            _aboutPage = FindViewById<View>(Resource.Id.aboutPage)!;
            _whatsNewPage = FindViewById<LinearLayout>(Resource.Id.whatsNewPage)!;
            _pivotAbout.Click += (_, _) => ShowPivot(false);
            _pivotWhatsNew.Click += (_, _) => ShowPivot(true);

            // Whatever the last check found, straight away; OnResume then asks again if it is due.
            AppRelease? known = AppUpdates.LastKnown;
            _update = known != null && AppUpdates.IsNewer(known.Version, _version) ? known : null;
            PaintUpdateRow();

            ShowPivot(savedInstanceState?.GetBoolean(StatePivot) == true);
        }

        protected override void OnSaveInstanceState(Bundle outState)
        {
            base.OnSaveInstanceState(outState);
            outState.PutBoolean(StatePivot, _onWhatsNew);
        }

        // Re-read on every return: the Hub address can be changed in settings and come back here.
        protected override void OnResume()
        {
            base.OnResume();

            Uri? hub = Configuration.Current?.HubUri;
            FindViewById<TextView>(Resource.Id.hubText)!.Text = hub == null
                ? "WPR Hub isn't available, so online features are off."
                : $"accounts, friends, leaderboards and online play come from WPR Hub at {hub.Host}. using them means agreeing to its terms.";
            ViewStates links = hub == null ? ViewStates.Gone : ViewStates.Visible;
            FindViewById<View>(Resource.Id.termsRow)!.Visibility = links;
            FindViewById<View>(Resource.Id.privacyRow)!.Visibility = links;

            _ = CheckForUpdateAsync(force: false);
        }

        // ------------------------------------------------------------------ pivots

        private void ShowPivot(bool whatsNew)
        {
            _onWhatsNew = whatsNew;
            _pivotAbout.SetTextColor(whatsNew ? HubViews.Subtle : WpTheme.Foreground);
            _pivotWhatsNew.SetTextColor(whatsNew ? WpTheme.Foreground : HubViews.Subtle);
            _aboutPage.Visibility = whatsNew ? ViewStates.Gone : ViewStates.Visible;
            _whatsNewPage.Visibility = whatsNew ? ViewStates.Visible : ViewStates.Gone;
            FindViewById<ScrollView>(Resource.Id.aboutScroll)!.ScrollTo(0, 0);

            if (whatsNew && !_whatsNewBuilt)
            {
                _whatsNewBuilt = true;
                BuildWhatsNew();
            }
        }

        /// <summary>
        /// The installed version's notes in full, then every earlier version as a row that expands
        /// in place. A newer release, when one is known, is offered above them.
        /// </summary>
        private void BuildWhatsNew()
        {
            _whatsNewPage.RemoveAllViews();

            if (_update is { } update)
            {
                _whatsNewPage.AddView(HubViews.Section(this, "AVAILABLE NOW"));
                _whatsNewPage.AddView(HubViews.Text(this, $"WPR {update.Version}", 22, WpTheme.Accent, light: true));
                _whatsNewPage.AddView(HubViews.Hint(this, "a newer version than the one installed. its notes are on GitHub."));
                LinearLayout buttons = new LinearLayout(this) { Orientation = Orientation.Horizontal };
                TextView download = HubViews.Button(this, "download", accent: true);
                download.Click += (_, _) => Open(update.ApkUrl ?? update.PageUrl);
                TextView notes = HubViews.Button(this, "release notes");
                notes.Click += (_, _) => Open(update.PageUrl);
                buttons.AddView(download, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
                {
                    RightMargin = HubViews.Dp(this, 10),
                });
                buttons.AddView(notes);
                _whatsNewPage.AddView(buttons, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
                {
                    TopMargin = HubViews.Dp(this, 12),
                });
            }

            IReadOnlyList<string> versions = ReleaseNotesView.BundledVersions(this);
            if (versions.Count == 0)
            {
                _whatsNewPage.AddView(HubViews.Hint(this, "this build carries no release notes. they are all on GitHub."));
                return;
            }

            // The installed version's notes; a development build between releases gets the newest
            // notes at or below its own version.
            string current = versions.FirstOrDefault(v => AppUpdates.CompareVersions(v, _version) == 0)
                ?? versions.FirstOrDefault(v => AppUpdates.CompareVersions(v, _version) <= 0)
                ?? versions[0];

            _whatsNewPage.AddView(HubViews.Section(this, current == _version ? "IN THIS VERSION" : $"IN WPR {current}"));
            _whatsNewPage.AddView(HubViews.Text(this, $"WPR {current}", 22, WpTheme.Foreground, light: true));
            ReleaseNotesView.Render(this, _whatsNewPage, ReleaseNotesView.Read(this, current) ?? "the notes could not be read.");

            List<string> earlier = versions.Where(v => AppUpdates.CompareVersions(v, current) < 0).ToList();
            if (earlier.Count == 0) return;

            TextView earlierHeader = HubViews.Section(this, "EARLIER VERSIONS");
            earlierHeader.SetPadding(0, HubViews.Dp(this, 36), 0, HubViews.Dp(this, 6));
            _whatsNewPage.AddView(earlierHeader);
            foreach (string version in earlier) _whatsNewPage.AddView(EarlierVersionRow(version));
        }

        private View EarlierVersionRow(string version)
        {
            LinearLayout block = new LinearLayout(this) { Orientation = Orientation.Vertical };
            TextView title = HubViews.Text(this, $"WPR {version}", 20, WpTheme.Foreground, light: true);
            title.SetPadding(0, HubViews.Dp(this, 10), 0, HubViews.Dp(this, 10));
            title.Clickable = true;
            title.Focusable = true;
            WpTheme.ApplyTilt(title);
            block.AddView(title);

            LinearLayout body = new LinearLayout(this) { Orientation = Orientation.Vertical, Visibility = ViewStates.Gone };
            body.SetPadding(0, 0, 0, HubViews.Dp(this, 18));
            block.AddView(body);

            title.Click += (_, _) =>
            {
                bool opening = body.Visibility != ViewStates.Visible;
                if (opening && body.ChildCount == 0)
                {
                    ReleaseNotesView.Render(this, body, ReleaseNotesView.Read(this, version) ?? "the notes could not be read.");
                }
                body.Visibility = opening ? ViewStates.Visible : ViewStates.Gone;
                title.SetTextColor(opening ? WpTheme.Accent : WpTheme.Foreground);
            };
            return block;
        }

        // ------------------------------------------------------------------ updates

        private void OnUpdateRowTapped()
        {
            if (_update != null) Open(_update.ApkUrl ?? _update.PageUrl);
            else _ = CheckForUpdateAsync(force: true);
        }

        private async Task CheckForUpdateAsync(bool force)
        {
            if (_checking) return;
            if (!force && Configuration.Current?.UpdateCheckEnabled == false) return;

            _checking = true;
            if (force) PaintUpdateRow();
            AppRelease? update = null;
            try
            {
                update = await Task.Run(() => AppUpdates.CheckAsync(_version, force));
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("WPR", "update check failed: " + ex.Message);
            }
            finally
            {
                _checking = false;
            }
            if (IsFinishing || IsDestroyed) return;

            bool changed = update?.Version != _update?.Version;
            _update = update;
            PaintUpdateRow();
            // A newer release found while "what's new" was already built: offer it there too.
            if (changed && _whatsNewBuilt) BuildWhatsNew();
        }

        private void PaintUpdateRow()
        {
            TextView title = FindViewById<TextView>(Resource.Id.updateTitle)!;
            TextView detail = FindViewById<TextView>(Resource.Id.updateDetail)!;
            title.SetTextColor(_update != null ? WpTheme.Accent : WpTheme.Foreground);

            if (_update != null)
            {
                title.Text = $"WPR {_update.Version} is available";
                detail.Text = $"you have {_version}. tap to download it, then install it over this one: your games and saves stay.";
            }
            else if (_checking)
            {
                title.Text = "check for updates";
                detail.Text = "checking GitHub…";
            }
            else if (AppUpdates.LastCheckFailed)
            {
                title.Text = "check for updates";
                detail.Text = "GitHub could not be reached. tap to try again.";
            }
            else if (AppUpdates.LastKnown != null)
            {
                title.Text = "you have the latest version";
                detail.Text = Configuration.Current?.UpdateCheckEnabled == false
                    ? "automatic checks are off in settings. tap to check again."
                    : "WPR checks for a new version now and then. tap to check now.";
            }
            else
            {
                title.Text = "check for updates";
                detail.Text = Configuration.Current?.UpdateCheckEnabled == false
                    ? "automatic checks are off in settings. tap to check now."
                    : "look for a newer WPR on GitHub";
            }
        }

        // ------------------------------------------------------------------ links

        /// <summary>The terms and privacy notice are the Hub's own pages, so a self-hosted Hub shows its own.</summary>
        private void OpenHubPage(string path)
        {
            Uri? hub = Configuration.Current?.HubUri;
            // Joined by hand: new Uri(hub, path) would drop the last segment of a Hub at https://host/wpr.
            if (hub != null) Open(hub.ToString().TrimEnd('/') + "/" + path);
        }

        private void Open(string url)
        {
            TextView status = FindViewById<TextView>(Resource.Id.statusText)!;
            status.Visibility = ViewStates.Gone;
            try
            {
                StartActivity(new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(url)));
            }
            catch (Exception ex)
            {
                status.Text = $"could not open a browser ({ex.Message}). go to {url} yourself.";
                status.Visibility = ViewStates.Visible;
            }
        }
    }
}
