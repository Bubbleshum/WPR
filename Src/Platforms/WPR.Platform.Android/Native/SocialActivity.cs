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
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;

using WPR.Common;
using WPR.Online.Hub;
using WPR.Online.Hub.Client;
using WPR.Shell;

namespace WPR.Platform.Android.Native
{
    /// <summary>
    /// The social page, from the Start screen's social tile, as a two-pivot page in the Windows Phone
    /// style: <b>friends</b> (the appear-offline switch, friend requests, friends, adding a friend) and
    /// <b>messages</b> (message a player by username, the conversations, and friends not messaged
    /// yet). A friend opens in <see cref="FriendProfileActivity"/>, a conversation in
    /// <see cref="ConversationActivity"/>.
    /// </summary>
    /// <remarks>
    /// The hub only lets friends message each other, so a username that is not a friend is offered a
    /// friend request instead. The shown pivot reloads on every resume, which is also how unread
    /// counts clear after reading a conversation. There is no push: new messages show up here on the
    /// next resume, and on the tile from the presence heartbeat.
    /// </remarks>
    [Activity(
        Label = "social",
        Theme = "@style/WprTheme",
        ScreenOrientation = ScreenOrientation.Portrait,
        WindowSoftInputMode = SoftInput.AdjustResize,
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
    [Register("com.wpr.android.SocialActivity")]
    public class SocialActivity : Activity
    {
        /// <summary>Intent extra naming the pivot to open on: <see cref="PivotFriends"/> or <see cref="PivotMessages"/>.</summary>
        public const string ExtraPivot = "wpr.social.pivot";
        public const string PivotFriends = "friends";
        public const string PivotMessages = "messages";

        private const string StatePivot = "pivot";

        private TextView _friendsHeader = null!;
        private TextView _messagesHeader = null!;
        private LinearLayout _friendsPage = null!;
        private LinearLayout _messagesPage = null!;
        private LinearLayout _friendsDynamic = null!;
        private LinearLayout _messagesDynamic = null!;
        private View _appearOffline = null!;
        private Switch _appearOfflineSwitch = null!;
        private View _compose = null!;
        private TextView _status = null!;
        private EditText _to = null!;
        private bool _onMessages;
        private int _loadGeneration;
        private bool _busy;

        public static void Open(Context context, string pivot)
        {
            Intent intent = new Intent(context, typeof(SocialActivity));
            intent.PutExtra(ExtraPivot, pivot);
            context.StartActivity(intent);
        }

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

            HubViews.Header(this, content, "social");
            content.AddView(BuildPivots());

            _status = HubViews.Hint(this, "");
            _status.Visibility = ViewStates.Gone;
            content.AddView(_status);

            _friendsPage = new LinearLayout(this) { Orientation = Orientation.Vertical };
            _appearOffline = BuildAppearOffline();
            _friendsPage.AddView(_appearOffline);
            _friendsDynamic = new LinearLayout(this) { Orientation = Orientation.Vertical };
            _friendsPage.AddView(_friendsDynamic);
            content.AddView(_friendsPage);

            _messagesPage = new LinearLayout(this) { Orientation = Orientation.Vertical };
            _compose = BuildCompose();
            _messagesPage.AddView(_compose);
            _messagesDynamic = new LinearLayout(this) { Orientation = Orientation.Vertical };
            _messagesPage.AddView(_messagesDynamic);
            content.AddView(_messagesPage);

            string? pivot = savedInstanceState?.GetString(StatePivot) ?? Intent?.GetStringExtra(ExtraPivot);
            ShowPivot(pivot == PivotMessages, load: false);
        }

        protected override void OnSaveInstanceState(Bundle outState)
        {
            base.OnSaveInstanceState(outState);
            outState.PutString(StatePivot, _onMessages ? PivotMessages : PivotFriends);
        }

        protected override void OnResume()
        {
            base.OnResume();
            _ = LoadAsync();
        }

        private bool SignedIn => !string.IsNullOrEmpty(Configuration.Current?.HubAccessToken);

        // ------------------------------------------------------------------ pivots

        private View BuildPivots()
        {
            LinearLayout row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetPadding(0, Dp(4), 0, Dp(4));
            _friendsHeader = PivotHeader("friends");
            _messagesHeader = PivotHeader("messages");
            row.AddView(_friendsHeader, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(24) });
            row.AddView(_messagesHeader);
            _friendsHeader.Click += (_, _) => ShowPivot(false, load: true);
            _messagesHeader.Click += (_, _) => ShowPivot(true, load: true);
            return row;
        }

        private TextView PivotHeader(string label)
        {
            TextView header = HubViews.Text(this, label, 32, WpTheme.Foreground, light: true);
            header.Clickable = true;
            header.Focusable = true;
            return header;
        }

        private void ShowPivot(bool messages, bool load)
        {
            if (load && messages == _onMessages) return;
            _onMessages = messages;
            _friendsHeader.SetTextColor(messages ? HubViews.Subtle : WpTheme.Foreground);
            _messagesHeader.SetTextColor(messages ? WpTheme.Foreground : HubViews.Subtle);
            _friendsPage.Visibility = messages ? ViewStates.Gone : ViewStates.Visible;
            _messagesPage.Visibility = messages ? ViewStates.Visible : ViewStates.Gone;
            SetStatus(null);
            if (load) _ = LoadAsync();
        }

        private Task LoadAsync() => _onMessages ? LoadMessagesAsync() : LoadFriendsAsync();

        /// <summary>The signed-out state of either pivot: a line and a way to sign in. True if it was shown.</summary>
        private bool ShowSignedOut(LinearLayout into, HubOnline? hub, string why)
        {
            _appearOffline.Visibility = hub != null && SignedIn ? ViewStates.Visible : ViewStates.Gone;
            _compose.Visibility = hub != null && SignedIn ? ViewStates.Visible : ViewStates.Gone;
            if (hub != null && SignedIn) return false;

            into.AddView(HubViews.Hint(this, hub == null ? "WPR Hub is not available right now. try again later." : why));
            if (hub == null) return true;
            TextView signIn = HubViews.Button(this, "sign in or sign up", accent: true);
            var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(12) };
            into.AddView(signIn, lp);
            signIn.Click += (_, _) => StartActivity(new Intent(this, typeof(SignInActivity)));
            return true;
        }

        // ------------------------------------------------------------------ friends

        /// <summary>
        /// Appear offline: friends see this player as offline, last seen when they were last visibly
        /// online. Presence keeps beating (as "invisible"), so messages and the tile counts still
        /// work, and the hub records the time as appear-offline rather than as nothing.
        /// </summary>
        private View BuildAppearOffline()
        {
            LinearLayout block = new LinearLayout(this) { Orientation = Orientation.Vertical };
            block.SetPadding(0, Dp(14), 0, 0);

            LinearLayout row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.AddView(HubViews.Text(this, "appear offline", 20, WpTheme.Foreground, light: true),
                new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            _appearOfflineSwitch = new Switch(this) { Checked = Configuration.Current?.HubAppearOffline == true };
            WpTheme.ApplySwitch(_appearOfflineSwitch);
            row.AddView(_appearOfflineSwitch);
            block.AddView(row);
            block.AddView(HubViews.Hint(this, "friends see you as offline, and not what you're playing. you can still message them."));

            _appearOfflineSwitch.CheckedChange += (_, e) =>
            {
                if (Configuration.Current is not { } config || config.HubAppearOffline == e.IsChecked) return;
                config.HubAppearOffline = e.IsChecked;
                config.Save();
                // Tell the hub now rather than at the next heartbeat, a minute away.
                HubSetup.Current?.Presence.Refresh();
                SetStatus(e.IsChecked ? "you now appear offline to your friends." : "your friends can see you're online again.");
            };
            return block;
        }

        private async Task LoadFriendsAsync()
        {
            int generation = ++_loadGeneration;
            _friendsDynamic.RemoveAllViews();
            HubOnline? hub = HubSetup.Current;
            if (ShowSignedOut(_friendsDynamic, hub, "sign in to WPR Hub to see your friends and what they're playing.")) return;
            _appearOfflineSwitch.Checked = Configuration.Current?.HubAppearOffline == true;

            Task<FriendList?> friends = hub!.Social.GetFriendsAsync();
            Task<FriendRequests?> requests = hub.Social.GetFriendRequestsAsync();
            try
            {
                await Task.WhenAll(friends, requests);
            }
            catch (Exception ex)
            {
                if (generation != _loadGeneration || IsDestroyed) return;
                if (!SignedIn) { _ = LoadAsync(); return; }
                _friendsDynamic.AddView(HubViews.Hint(this, "could not reach WPR Hub: " + ex.Message));
                return;
            }
            if (generation != _loadGeneration || IsDestroyed || _onMessages) return;

            if (requests.Result is { } r) HubFriendViews.AddRequests(this, _friendsDynamic, hub, r, ActAsync);
            HubFriendViews.AddFriends(this, _friendsDynamic, hub, friends.Result, ActAsync);
            HubFriendViews.AddFriendInput(this, _friendsDynamic, hub, ActAsync);
            if (requests.Result is { } pending) HubFriendViews.AddOutgoing(this, _friendsDynamic, hub, pending.Outgoing, ActAsync);
        }

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

        // ------------------------------------------------------------------ messages: compose

        private View BuildCompose()
        {
            LinearLayout block = new LinearLayout(this) { Orientation = Orientation.Vertical };
            block.AddView(HubViews.Section(this, "NEW MESSAGE"));

            _to = new EditText(this)
            {
                Hint = "player's username",
                InputType = InputTypes.ClassText | InputTypes.TextFlagNoSuggestions,
                ImeOptions = ImeAction.Go,
            };
            _to.SetSingleLine(true);
            _to.SetTextColor(WpTheme.Foreground);
            _to.SetHintTextColor(HubViews.Subtle);
            _to.SetBackgroundColor(WpTheme.Chrome);
            _to.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
            _to.SetFilters(new IInputFilter[] { new InputFilterLengthFilter(24) });
            block.AddView(_to, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

            LinearLayout buttons = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            buttons.SetPadding(0, Dp(10), 0, 0);
            TextView message = HubViews.Button(this, "message", accent: true);
            TextView request = HubViews.Button(this, "send friend request");
            buttons.AddView(message, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { RightMargin = Dp(10) });
            buttons.AddView(request);
            block.AddView(buttons);

            message.Click += (_, _) => _ = MessageAsync();
            request.Click += (_, _) => _ = RequestAsync(TypedName());
            _to.EditorAction += (_, e) =>
            {
                if (e.ActionId != ImeAction.Go) { e.Handled = false; return; }
                e.Handled = true;
                _ = MessageAsync();
            };
            return block;
        }

        private string TypedName() => _to.Text?.Trim() ?? "";

        /// <summary>
        /// Open a conversation with the typed player. A friend goes straight to it; anyone else is
        /// offered a friend request, because the hub only carries messages between friends.
        /// </summary>
        private async Task MessageAsync()
        {
            string username = TypedName();
            HubOnline? hub = HubSetup.Current;
            if (username.Length == 0 || hub == null || !SignedIn || _busy) return;
            HideKeyboard();

            _busy = true;
            SetStatus($"looking for {username}…");
            FriendList? friends;
            try
            {
                friends = await hub.Social.GetFriendsAsync();
            }
            catch (Exception ex)
            {
                SetStatus(ex.Message);
                return;
            }
            finally
            {
                _busy = false;
            }

            Friend? friend = friends?.Friends.FirstOrDefault(f => string.Equals(f.Username, username, StringComparison.OrdinalIgnoreCase));
            if (friend != null)
            {
                SetStatus(null);
                _to.Text = "";
                ConversationActivity.Open(this, friend.Username);
                return;
            }

            bool send = await WpDialogs.ConfirmAsync(this, "not a friend yet",
                $"you can only message friends. send {username} a friend request? you can message them once they accept.",
                yes: "send request", no: "cancel");
            if (send) await RequestAsync(username);
            else SetStatus(null);
        }

        private async Task RequestAsync(string username)
        {
            HubOnline? hub = HubSetup.Current;
            if (username.Length == 0 || hub == null || !SignedIn || _busy) return;
            HideKeyboard();

            _busy = true;
            SetStatus($"sending {username} a friend request…");
            try
            {
                FriendRequestResult result = await hub.Social.SendFriendRequestAsync(username);
                _to.Text = "";
                SetStatus(result.Status == "accepted"
                    ? $"{username} had already asked you, so you're now friends. you can message them."
                    : $"friend request sent to {username}. you can message them once they accept.");
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

        // ------------------------------------------------------------------ messages: lists

        private async Task LoadMessagesAsync()
        {
            int generation = ++_loadGeneration;
            _messagesDynamic.RemoveAllViews();
            HubOnline? hub = HubSetup.Current;
            if (ShowSignedOut(_messagesDynamic, hub, "sign in to WPR Hub to message your friends.")) return;

            Task<ConversationList?> conversations = hub!.Social.GetConversationsAsync();
            Task<FriendList?> friends = hub.Social.GetFriendsAsync();
            try
            {
                await Task.WhenAll(conversations, friends);
            }
            catch (Exception ex)
            {
                if (generation != _loadGeneration || IsDestroyed) return;
                if (!SignedIn) { _ = LoadAsync(); return; }
                _messagesDynamic.AddView(HubViews.Hint(this, "could not reach WPR Hub: " + ex.Message));
                return;
            }
            if (generation != _loadGeneration || IsDestroyed || !_onMessages) return;

            IReadOnlyList<Conversation> list = conversations.Result?.Conversations ?? Array.Empty<Conversation>();
            IReadOnlyList<Friend> friendList = friends.Result?.Friends ?? Array.Empty<Friend>();
            var online = friendList.ToDictionary(f => f.Username, f => f, StringComparer.OrdinalIgnoreCase);

            _messagesDynamic.AddView(HubViews.Section(this, "CONVERSATIONS"));
            if (list.Count == 0)
                _messagesDynamic.AddView(HubViews.Hint(this, "no messages yet. message a friend below, or type a username above."));

            foreach (Conversation c in list)
            {
                string preview = (c.LastMessage.Mine ? "you: " : "") + OneLine(c.LastMessage.Body);
                string line = $"{HubText.Ago(c.LastMessage.SentAt)} · {preview}";
                bool unread = c.Unread > 0;
                LinearLayout row = HubViews.PersonRow(this, hub, c.GamerpicUrl, c.With, line,
                    unread ? WpTheme.Accent : HubViews.Subtle, out LinearLayout trailing);
                if (unread)
                {
                    TextView count = HubViews.Text(this, c.Unread.ToString(), 26, WpTheme.Accent, light: true);
                    trailing.AddView(count);
                }
                else if (!c.IsFriend)
                {
                    trailing.AddView(HubViews.Text(this, "not friends", 12, HubViews.Subtle));
                }
                else if (online.TryGetValue(c.With, out Friend? f) && f.Presence.IsOnline)
                {
                    trailing.AddView(HubViews.Text(this, "online", 12, WpTheme.Accent));
                }

                WpTheme.ApplyTilt(row, 0.97f);
                string with = c.With;
                row.Click += (_, _) => ConversationActivity.Open(this, with);
                _messagesDynamic.AddView(row);
            }

            // Friends with no conversation yet: one tap starts one.
            var talkedTo = new HashSet<string>(list.Select(c => c.With), StringComparer.OrdinalIgnoreCase);
            List<Friend> fresh = friendList.Where(f => !talkedTo.Contains(f.Username)).ToList();
            if (fresh.Count > 0)
            {
                _messagesDynamic.AddView(HubViews.Section(this, "START A CONVERSATION"));
                foreach (Friend f in fresh)
                {
                    LinearLayout row = HubViews.PersonRow(this, hub, f.GamerpicUrl, f.Username, HubText.Presence(f.Presence),
                        f.Presence.IsOnline ? WpTheme.Accent : HubViews.Subtle, out LinearLayout trailing);
                    TextView message = HubViews.Button(this, "message");
                    trailing.AddView(message);
                    string username = f.Username;
                    message.Click += (_, _) => ConversationActivity.Open(this, username);
                    _messagesDynamic.AddView(row);
                }
            }
            else if (friendList.Count == 0)
            {
                _messagesDynamic.AddView(HubViews.Hint(this, "you have no friends on WPR Hub yet. add one on the friends pivot, or type a username above."));
            }
        }

        private static string OneLine(string text)
        {
            string flat = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
            return flat.Length > 80 ? flat.Substring(0, 80) + "…" : flat;
        }

        private void HideKeyboard() =>
            ((InputMethodManager)GetSystemService(InputMethodService)!).HideSoftInputFromWindow(_to.WindowToken, 0);

        private void SetStatus(string? text)
        {
            _status.Text = text ?? "";
            _status.Visibility = string.IsNullOrEmpty(text) ? ViewStates.Gone : ViewStates.Visible;
        }

        private int Dp(int dp) => HubViews.Dp(this, dp);
    }
}
