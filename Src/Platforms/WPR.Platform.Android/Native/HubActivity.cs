using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Runtime;
using Android.Text;
using Android.Util;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;

using WPR.Common;
using WPR.Online.Hub;
using WPR.Online.Hub.Client;
using WPR.Shell;

using WprApplication = WPR.Models.Application;

namespace WPR.Platform.Android.Native
{
    /// <summary>
    /// The WPR Hub page, from the Start screen's hub tile: the player's profile, friend requests,
    /// friends with what they are playing, adding a friend, and the games the hub has on record.
    /// The desktop twin is <c>Pages/HubPage</c>.
    /// </summary>
    /// <remarks>
    /// Built in code rather than a layout, like <see cref="GamerpicsActivity"/>: apart from the
    /// header, everything on it is a list of data. Signed out, it shows only the profile block with
    /// a sign-in button. It reloads on every resume, so coming back from sign-in, the gamerpic
    /// picker or a game shows fresh presence.
    /// </remarks>
    [Activity(
        Label = "WPR Hub",
        Theme = "@style/WprTheme",
        ScreenOrientation = ScreenOrientation.Portrait,
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
    [Register("com.wpr.android.HubActivity")]
    public class HubActivity : Activity
    {
        // @color/wp_subtle, the secondary text colour the layouts use.
        private static readonly Color Subtle = HubViews.Subtle;

        private LinearLayout _dynamic = null!;
        private TextView _status = null!;
        private ImageView _picture = null!;
        private TextView _name = null!;
        private TextView _summary = null!;
        private TextView _accountButton = null!;
        private TextView _gamerpicButton = null!;
        private TextView _signOutButton = null!;
        private int _loadGeneration;
        private bool _busy;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            WprStartup.EnsureInitialized(this);

            int margin = Resources!.GetDimensionPixelSize(Resource.Dimension.wp_page_margin);

            ScrollView scroll = new ScrollView(this) { FillViewport = true };
            scroll.SetBackgroundColor(Color.Black);
            LinearLayout content = new LinearLayout(this) { Orientation = Orientation.Vertical };
            content.SetPadding(margin, Dp(28), margin, Dp(36));
            scroll.AddView(content);
            SetContentView(scroll);
            WpTheme.ApplySystemBars(this);

            ImageView logo = new ImageView(this) { ContentDescription = "WPR Hub" };
            logo.SetAdjustViewBounds(true);
            content.AddView(logo, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(64)));

            TextView appTitle = Text("WPR HUB", 14, WpTheme.Accent);
            appTitle.LetterSpacing = 0.1f;
            content.AddView(appTitle);
            HubLogo.Apply(this, logo, appTitle);

            content.AddView(Text("profile", 56, WpTheme.Foreground, light: true));

            content.AddView(BuildProfile());

            _status = Text("", 13, Subtle);
            _status.SetPadding(0, Dp(12), 0, 0);
            _status.Visibility = ViewStates.Gone;
            content.AddView(_status);

            _dynamic = new LinearLayout(this) { Orientation = Orientation.Vertical };
            content.AddView(_dynamic);
        }

        protected override void OnResume()
        {
            base.OnResume();
            _ = LoadAsync();
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
        {
            base.OnActivityResult(requestCode, resultCode, data);
            if (requestCode == GameLauncher.RequestGame) GameLauncher.HandleGameResult(this, resultCode, data);
        }

        // ------------------------------------------------------------------ profile

        private View BuildProfile()
        {
            LinearLayout block = new LinearLayout(this) { Orientation = Orientation.Vertical };
            block.SetPadding(0, Dp(14), 0, 0);

            LinearLayout row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            _picture = new ImageView(this) { ContentDescription = "gamerpic" };
            _picture.SetScaleType(ImageView.ScaleType.CenterCrop);
            _picture.SetBackgroundColor(WpTheme.Chrome);
            row.AddView(_picture, new LinearLayout.LayoutParams(Dp(96), Dp(96)));

            LinearLayout text = new LinearLayout(this) { Orientation = Orientation.Vertical };
            text.SetPadding(Dp(16), 0, 0, 0);
            text.SetGravity(GravityFlags.CenterVertical);
            _name = Text("not signed in", 30, WpTheme.Foreground, light: true);
            _summary = Text("", 14, Subtle);
            text.AddView(_name);
            text.AddView(_summary);
            row.AddView(text, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1f));
            block.AddView(row);

            LinearLayout buttons = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            buttons.SetPadding(0, Dp(14), 0, 0);
            _accountButton = Button("sign in or sign up");
            _gamerpicButton = Button("gamerpic");
            _signOutButton = Button("sign out");
            // Signed out only the first shows; signed in the other two. Each carries its own gap.
            LinearLayout.LayoutParams Gap() => new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(10) };
            buttons.AddView(_accountButton, Gap());
            buttons.AddView(_gamerpicButton, Gap());
            buttons.AddView(_signOutButton);
            block.AddView(buttons);

            _accountButton.Click += (_, _) => StartActivity(new Intent(this, typeof(SignInActivity)));
            _gamerpicButton.Click += (_, _) => StartActivity(new Intent(this, typeof(GamerpicsActivity)));
            // The account lives here and on the Start tile only; settings no longer has an account
            // section. Signing out takes games back to the guest gamertag and the default gamerpic.
            _signOutButton.Click += async (_, _) =>
            {
                if (WPR.Shell.HubSetup.Current is not { } hub) return;
                _signOutButton.Enabled = false;
                await hub.Account.SignOutAsync();
                _signOutButton.Enabled = true;
                if (!IsDestroyed) _ = LoadAsync();
            };
            return block;
        }

        private bool SignedIn => !string.IsNullOrEmpty(Configuration.Current?.HubAccessToken);

        private async Task LoadAsync()
        {
            int generation = ++_loadGeneration;
            HubOnline? hub = WPR.Shell.HubSetup.Current;
            _dynamic.RemoveAllViews();

            bool signedIn = SignedIn && hub != null;
            _name.Text = signedIn ? Configuration.Current!.HubUsername ?? "signed in" : "not signed in";
            _summary.Text = signedIn ? "" : "sign in to see your friends, what they're playing, and your games on the hub.";
            _accountButton.Visibility = signedIn ? ViewStates.Gone : ViewStates.Visible;
            _gamerpicButton.Visibility = signedIn ? ViewStates.Visible : ViewStates.Gone;
            _signOutButton.Visibility = signedIn ? ViewStates.Visible : ViewStates.Gone;
            if (!signedIn)
            {
                _picture.SetImageDrawable(null);
                SetStatus(hub == null ? "WPR Hub is not available right now. try again later." : null);
                return;
            }

            SetStatus("loading…");

            Task<Bitmap?> picture = SignInActivity.LoadCurrentGamerpicAsync(hub);
            Task<FriendList?> friends = hub!.Social.GetFriendsAsync();
            Task<FriendRequests?> requests = hub.Social.GetFriendRequestsAsync();
            Task<HubGameList?> games = hub.Social.GetGamesAsync();

            try
            {
                await Task.WhenAll(friends, requests, games);
            }
            catch (Exception ex)
            {
                if (generation != _loadGeneration || IsDestroyed) return;
                // A revoked token signs the device out as a side effect; show that state instead.
                if (!SignedIn) { _ = LoadAsync(); return; }
                SetStatus("could not reach WPR Hub: " + ex.Message);
                return;
            }
            if (generation != _loadGeneration || IsDestroyed) return;

            SetStatus(null);
            IReadOnlyList<HubGame> gameList = games.Result?.Games ?? Array.Empty<HubGame>();
            _summary.Text = HubText.Summary(games.Result);

            if (requests.Result is { } r) HubFriendViews.AddRequests(this, _dynamic, hub, r, ActAsync);
            HubFriendViews.AddFriends(this, _dynamic, hub, friends.Result, ActAsync);
            HubFriendViews.AddFriendInput(this, _dynamic, hub, ActAsync);
            if (requests.Result is { } pending) HubFriendViews.AddOutgoing(this, _dynamic, hub, pending.Outgoing, ActAsync);
            AddGames(gameList);

            Bitmap? bitmap = await picture;
            if (bitmap != null && generation == _loadGeneration && !IsDestroyed) _picture.SetImageBitmap(bitmap);
        }

        // ------------------------------------------------------------------ games

        private void AddGames(IReadOnlyList<HubGame> games)
        {
            Section("GAMES");
            if (games.Count == 0)
            {
                _dynamic.AddView(Hint("nothing yet. playtime and achievements show up here once you've played while signed in."));
                return;
            }

            Dictionary<string, WprApplication> installed = HubFriendViews.InstalledByTitle();
            foreach (HubGame game in games)
            {
                installed.TryGetValue(game.TitleId, out WprApplication? app);

                LinearLayout row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
                row.SetPadding(0, Dp(8), 0, Dp(8));
                row.SetGravity(GravityFlags.CenterVertical);

                ImageView art = new ImageView(this) { ContentDescription = null };
                art.SetScaleType(ImageView.ScaleType.CenterCrop);
                art.SetBackgroundColor(WpTheme.Muted(WpTheme.Accent));
                Bitmap? tile = GameTileArt.Decode(app, game.TitleId);
                if (tile != null) art.SetImageBitmap(tile);
                row.AddView(art, new LinearLayout.LayoutParams(Dp(56), Dp(56)));

                LinearLayout text = new LinearLayout(this) { Orientation = Orientation.Vertical };
                text.SetPadding(Dp(14), 0, 0, 0);
                text.AddView(Text(app?.Name ?? game.Name, 20, WpTheme.Foreground, light: true));

                text.AddView(Text(HubText.GameLine(game), 13, Subtle));
                row.AddView(text, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

                if (app != null)
                {
                    WpTheme.ApplyTilt(row, 0.97f);
                    row.Click += (_, _) => GameLauncher.Launch(this, app);
                }
                _dynamic.AddView(row);
            }
        }

        // ------------------------------------------------------------------ actions

        /// <summary>Run one hub action, report it, and reload. One at a time.</summary>
        private async Task ActAsync(string done, Func<Task> action, Func<string>? doneLate = null)
        {
            if (_busy) return;
            _busy = true;
            SetStatus("working…");
            try
            {
                await action();
                SetStatus(doneLate?.Invoke() ?? done);
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message);
                _busy = false;
                return;
            }
            _busy = false;
            if (IsDestroyed) return;
            string message = _status.Text ?? "";
            await LoadAsync();
            SetStatus(message);
        }

        // ------------------------------------------------------------------ building blocks (HubViews)

        private void Section(string title) => _dynamic.AddView(HubViews.Section(this, title));

        private TextView Hint(string text) => HubViews.Hint(this, text);

        private TextView Button(string label) => HubViews.Button(this, label);

        private void SetStatus(string? text)
        {
            _status.Text = text ?? "";
            _status.Visibility = string.IsNullOrEmpty(text) ? ViewStates.Gone : ViewStates.Visible;
        }

        private TextView Text(string text, float sp, Color color, bool light = false) => HubViews.Text(this, text, sp, color, light);

        private int Dp(int dp) => HubViews.Dp(this, dp);
    }
}
