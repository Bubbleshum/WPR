using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;

using WPR.Common;
using WPR.Online.Hub;
using WPR.Online.Hub.Client;
using WPR.Shell;

using WprApplication = WPR.Models.Application;

namespace WPR.Platform.Android.Native
{
    /// <summary>
    /// One friend's page, from a friend row on the social or hub page: their gamerpic, what they are
    /// doing now (or when they were last seen), their totals, and their latest activity - achievements
    /// unlocked and games played, newest first.
    /// </summary>
    /// <remarks>
    /// Everything shown is the hub's answer to <c>GET /api/friends/{username}</c>. The hub already
    /// hides what a friend's appear-offline status hides: nothing they did while appearing offline is
    /// in the feed, and their "last seen" is the last time they were visibly online. Reloads on resume.
    /// </remarks>
    [Activity(
        Label = "friend",
        Theme = "@style/WprTheme",
        ScreenOrientation = ScreenOrientation.Portrait,
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
    [Register("com.wpr.android.FriendProfileActivity")]
    public class FriendProfileActivity : Activity
    {
        private const string ExtraUsername = "wpr.friend.username";

        private string _username = "";
        private ImageView _picture = null!;
        private TextView _presence = null!;
        private TextView _summary = null!;
        private TextView _since = null!;
        private TextView _status = null!;
        private LinearLayout _dynamic = null!;
        private int _loadGeneration;
        private bool _busy;

        public static void Open(Context context, string username)
        {
            Intent intent = new Intent(context, typeof(FriendProfileActivity));
            intent.PutExtra(ExtraUsername, username);
            context.StartActivity(intent);
        }

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            WprStartup.EnsureInitialized(this);
            _username = Intent?.GetStringExtra(ExtraUsername) ?? "";

            int margin = Resources!.GetDimensionPixelSize(Resource.Dimension.wp_page_margin);
            ScrollView scroll = new ScrollView(this) { FillViewport = true };
            scroll.SetBackgroundColor(Color.Black);
            LinearLayout content = new LinearLayout(this) { Orientation = Orientation.Vertical };
            content.SetPadding(margin, Dp(28), margin, Dp(36));
            scroll.AddView(content);
            SetContentView(scroll);
            WpTheme.ApplySystemBars(this);

            HubViews.Header(this, content, _username);
            content.AddView(BuildProfile());

            _status = HubViews.Hint(this, "");
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
            _presence = HubViews.Text(this, "", 20, HubViews.Subtle, light: true);
            _summary = HubViews.Text(this, "", 14, HubViews.Subtle);
            _since = HubViews.Text(this, "", 13, HubViews.Subtle);
            text.AddView(_presence);
            text.AddView(_summary);
            text.AddView(_since);
            row.AddView(text, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1f));
            block.AddView(row);

            LinearLayout buttons = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            buttons.SetPadding(0, Dp(14), 0, 0);
            TextView message = HubViews.Button(this, "message", accent: true);
            TextView remove = HubViews.Button(this, "remove friend");
            buttons.AddView(message, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(10) });
            buttons.AddView(remove);
            block.AddView(buttons);

            message.Click += (_, _) => ConversationActivity.Open(this, _username);
            remove.Click += (_, _) => _ = RemoveAsync();
            return block;
        }

        private async Task LoadAsync()
        {
            int generation = ++_loadGeneration;
            HubOnline? hub = HubSetup.Current;
            if (hub == null || string.IsNullOrEmpty(Configuration.Current?.HubAccessToken) || _username.Length == 0)
            {
                Finish();
                return;
            }

            SetStatus(_dynamic.ChildCount == 0 ? "loading…" : null);
            FriendProfile? profile;
            try
            {
                profile = await hub.Social.GetFriendAsync(_username);
            }
            catch (Exception ex)
            {
                if (generation != _loadGeneration || IsDestroyed) return;
                SetStatus(ex is HubException { ErrorCode: "not_friends" }
                    ? $"you aren't friends with {_username} any more."
                    : "could not reach WPR Hub: " + ex.Message);
                return;
            }
            if (generation != _loadGeneration || IsDestroyed || profile == null) return;

            SetStatus(null);
            _presence.Text = HubText.Presence(profile.Presence);
            _presence.SetTextColor(profile.Presence.IsOnline ? WpTheme.Accent : HubViews.Subtle);
            _summary.Text = HubText.FriendSummary(profile);
            _since.Text = profile.FriendsSince is { } since ? "friends since " + since.ToLocalTime().ToString("d MMM yyyy") : "";
            if (!string.IsNullOrEmpty(profile.GamerpicUrl)) _ = HubViews.LoadImageAsync(this, hub, _picture, profile.GamerpicUrl!);

            _dynamic.RemoveAllViews();
            _dynamic.AddView(HubViews.Section(this, "LATEST ACTIVITY"));
            if (profile.Activity.Count == 0)
            {
                _dynamic.AddView(HubViews.Hint(this, $"nothing yet. games {_username} plays and achievements they unlock show up here."));
                return;
            }

            Dictionary<string, WprApplication> installed = HubFriendViews.InstalledByTitle();
            foreach (FriendActivity item in profile.Activity)
                _dynamic.AddView(ActivityRow(hub, item, installed));
        }

        /// <summary>
        /// One activity row: the achievement's icon (or the game's art), what happened, and when. Local
        /// tile art is preferred for a game that is installed here, as on the hub page.
        /// </summary>
        private View ActivityRow(HubOnline hub, FriendActivity item, Dictionary<string, WprApplication> installed)
        {
            LinearLayout row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetPadding(0, Dp(8), 0, Dp(8));
            row.SetGravity(GravityFlags.CenterVertical);

            ImageView art = new ImageView(this) { ContentDescription = null };
            art.SetScaleType(ImageView.ScaleType.CenterCrop);
            art.SetBackgroundColor(WpTheme.Muted(WpTheme.Accent));
            row.AddView(art, new LinearLayout.LayoutParams(Dp(56), Dp(56)));

            bool achievement = item.Type == FriendActivity.Achievement;
            installed.TryGetValue(TitleIds.Normalize(item.TitleId), out WprApplication? app);
            Bitmap? tile = GameTileArt.Decode(app, item.TitleId);
            if (achievement && !string.IsNullOrEmpty(item.IconUrl)) _ = HubViews.LoadImageAsync(this, hub, art, item.IconUrl!);
            else if (tile != null) art.SetImageBitmap(tile);
            else if (!string.IsNullOrEmpty(item.TitleIconUrl)) _ = HubViews.LoadImageAsync(this, hub, art, item.TitleIconUrl!);

            LinearLayout text = new LinearLayout(this) { Orientation = Orientation.Vertical };
            text.SetPadding(Dp(14), 0, 0, 0);
            TextView title = HubViews.Text(this, HubText.ActivityTitle(item), 18, WpTheme.Foreground, light: true);
            title.SetMaxLines(2);
            text.AddView(title);
            text.AddView(HubViews.Text(this, HubText.ActivityLine(item), 13, achievement ? WpTheme.Accent : HubViews.Subtle));
            if (achievement && !string.IsNullOrEmpty(item.Description))
            {
                TextView description = HubViews.Text(this, item.Description!, 13, HubViews.Subtle);
                description.SetMaxLines(2);
                description.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
                text.AddView(description);
            }
            row.AddView(text, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            return row;
        }

        private async Task RemoveAsync()
        {
            HubOnline? hub = HubSetup.Current;
            if (hub == null || _busy || !await HubFriendViews.ConfirmRemoveAsync(this, _username)) return;
            _busy = true;
            SetStatus("working…");
            try
            {
                await hub.Social.RemoveFriendAsync(_username);
                Finish();
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message);
            }
            finally
            {
                _busy = false;
            }
        }

        private void SetStatus(string? text)
        {
            _status.Text = text ?? "";
            _status.Visibility = string.IsNullOrEmpty(text) ? ViewStates.Gone : ViewStates.Visible;
        }

        private int Dp(int dp) => HubViews.Dp(this, dp);
    }
}
