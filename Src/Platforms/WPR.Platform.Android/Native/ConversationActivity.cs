using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Runtime;
using Android.Text;
using Android.Views;
using Android.Widget;

using WPR.Common;
using WPR.Online.Hub;
using WPR.Online.Hub.Client;
using WPR.Shell;

namespace WPR.Platform.Android.Native
{
    /// <summary>
    /// One conversation with one player: the messages, oldest at the top, and a box to reply.
    /// Opened from <see cref="SocialActivity"/> and from a friend's "message" button on
    /// <see cref="HubActivity"/>.
    /// </summary>
    /// <remarks>
    /// <para>There is no push channel, so while the page is in front it asks the hub for anything
    /// newer than the last message it has, every few seconds (the hub's own design:
    /// <c>?after=&lt;id&gt;</c>), and marks what it shows as read.</para>
    ///
    /// <para>Tap and hold a message to copy it, delete it (for you; the other player keeps theirs)
    /// or, for one sent to you, report it. A report is the only way an admin ever sees a private
    /// message. A conversation with someone who is no longer a friend is read-only, and offers a
    /// friend request instead of a reply box.</para>
    /// </remarks>
    [Activity(
        Label = "conversation",
        Theme = "@style/WprTheme",
        ScreenOrientation = ScreenOrientation.Portrait,
        WindowSoftInputMode = SoftInput.AdjustResize | SoftInput.StateHidden,
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
    [Register("com.wpr.android.ConversationActivity")]
    public class ConversationActivity : Activity
    {
        private const string ExtraUsername = "wpr.hub.conversation.username";
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

        public static void Open(Activity host, string username)
        {
            Intent intent = new Intent(host, typeof(ConversationActivity));
            intent.PutExtra(ExtraUsername, username);
            host.StartActivity(intent);
        }

        private string _with = "";
        private ScrollView _scroll = null!;
        private LinearLayout _messages = null!;
        private TextView _status = null!;
        private LinearLayout _compose = null!;
        private LinearLayout _notFriends = null!;
        private EditText _input = null!;
        private TextView _send = null!;
        private readonly Dictionary<long, View> _shown = new Dictionary<long, View>();
        private long _newestId;
        private long _oldestId;
        private CancellationTokenSource? _poll;
        private bool _sending;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            WprStartup.EnsureInitialized(this);
            _with = Intent?.GetStringExtra(ExtraUsername) ?? "";

            int margin = Resources!.GetDimensionPixelSize(Resource.Dimension.wp_page_margin);

            LinearLayout root = new LinearLayout(this) { Orientation = Orientation.Vertical };
            root.SetBackgroundColor(Color.Black);
            root.SetPadding(margin, Dp(28), margin, Dp(16));
            SetContentView(root);
            WpTheme.ApplySystemBars(this);

            TextView appTitle = HubViews.Text(this, "WPR HUB · MESSAGES", 14, WpTheme.Accent);
            appTitle.LetterSpacing = 0.1f;
            root.AddView(appTitle);
            TextView title = HubViews.Text(this, _with, 44, WpTheme.Foreground, light: true);
            title.SetSingleLine(true);
            title.Ellipsize = TextUtils.TruncateAt.End;
            root.AddView(title);

            _status = HubViews.Hint(this, "loading…");
            root.AddView(_status);

            _scroll = new ScrollView(this) { FillViewport = true };
            _messages = new LinearLayout(this) { Orientation = Orientation.Vertical };
            _messages.SetPadding(0, Dp(8), 0, Dp(8));
            _scroll.AddView(_messages);
            root.AddView(_scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

            root.AddView(BuildCompose());
            root.AddView(BuildNotFriends());
        }

        protected override void OnResume()
        {
            base.OnResume();
            _poll?.Cancel();
            _poll = new CancellationTokenSource();
            _ = PollAsync(_poll.Token);
        }

        protected override void OnPause()
        {
            _poll?.Cancel();
            _poll = null;
            base.OnPause();
        }

        // ------------------------------------------------------------------ loading

        private async Task PollAsync(CancellationToken ct)
        {
            HubOnline? hub = HubSetup.Current;
            if (hub == null || string.IsNullOrEmpty(Configuration.Current?.HubAccessToken))
            {
                SetStatus("sign in to WPR Hub to message your friends.");
                _compose.Visibility = ViewStates.Gone;
                return;
            }

            while (!ct.IsCancellationRequested && !IsDestroyed)
            {
                try
                {
                    MessageThread? thread = await hub.Social.GetThreadAsync(_with, after: _newestId == 0 ? null : _newestId);
                    if (ct.IsCancellationRequested || IsDestroyed) return;
                    if (thread != null) Apply(hub, thread, initial: _newestId == 0 && _shown.Count == 0);
                }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested || IsDestroyed) return;
                    SetStatus("could not reach WPR Hub: " + ex.Message);
                }

                try { await Task.Delay(PollInterval, ct); }
                catch (System.OperationCanceledException) { return; }
            }
        }

        private void Apply(HubOnline hub, MessageThread thread, bool initial)
        {
            _with = thread.With; // the hub's spelling of the name
            _compose.Visibility = thread.IsFriend ? ViewStates.Visible : ViewStates.Gone;
            _notFriends.Visibility = thread.IsFriend ? ViewStates.Gone : ViewStates.Visible;

            if (initial)
            {
                if (thread.HasMore) _messages.AddView(BuildLoadEarlier(hub));
                if (thread.Messages.Count == 0)
                    SetStatus(thread.IsFriend ? "no messages yet. say hello." : null);
                else SetStatus(null);
                if (thread.Messages.Count > 0) _oldestId = thread.Messages[0].Id;
            }
            else if (thread.Messages.Count > 0)
            {
                SetStatus(null);
            }

            bool incoming = false;
            foreach (ChatMessage m in thread.Messages)
            {
                if (_shown.ContainsKey(m.Id)) continue;
                AddBubble(hub, m, atTop: false);
                _newestId = Math.Max(_newestId, m.Id);
                incoming |= !m.Mine;
            }

            if (thread.Messages.Count > 0) ScrollToBottom();
            if (incoming || initial) _ = hub.Social.MarkReadAsync(_with, _newestId == 0 ? null : _newestId);
        }

        private View BuildLoadEarlier(HubOnline hub)
        {
            TextView more = HubViews.Button(this, "load earlier messages");
            var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(10) };
            lp.Gravity = GravityFlags.CenterHorizontal;
            more.LayoutParameters = lp;
            more.Click += async (_, _) =>
            {
                if (_oldestId == 0) return;
                more.Text = "loading…";
                try
                {
                    MessageThread? older = await hub.Social.GetThreadAsync(_with, before: _oldestId);
                    if (older == null || IsDestroyed) return;
                    _messages.RemoveView(more);
                    // Oldest-first from the hub; insert in reverse so each lands just below the button slot.
                    foreach (ChatMessage m in older.Messages.Reverse())
                        if (!_shown.ContainsKey(m.Id)) AddBubble(hub, m, atTop: true);
                    if (older.Messages.Count > 0) _oldestId = older.Messages[0].Id;
                    if (older.HasMore)
                    {
                        more.Text = "load earlier messages";
                        _messages.AddView(more, 0);
                    }
                }
                catch (Exception ex)
                {
                    more.Text = "load earlier messages";
                    SetStatus(ex.Message);
                }
            };
            return more;
        }

        // ------------------------------------------------------------------ bubbles

        private void AddBubble(HubOnline hub, ChatMessage message, bool atTop)
        {
            LinearLayout wrap = new LinearLayout(this) { Orientation = Orientation.Vertical };
            wrap.SetGravity(message.Mine ? GravityFlags.End : GravityFlags.Start);
            wrap.SetPadding(message.Mine ? Dp(48) : 0, Dp(4), message.Mine ? 0 : Dp(48), Dp(4));

            TextView body = HubViews.Text(this, message.Body, 16, WpTheme.Foreground);
            body.SetBackgroundColor(message.Mine ? WpTheme.Accent : WpTheme.Chrome);
            body.SetPadding(Dp(12), Dp(8), Dp(12), Dp(8));
            body.SetTextIsSelectable(false);
            wrap.AddView(body, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));

            string when = HubText.Ago(message.SentAt);
            if (message.Mine && message.ReadAt != null) when += " · read";
            TextView meta = HubViews.Text(this, when, 11, HubViews.Subtle);
            meta.SetPadding(0, Dp(2), 0, 0);
            wrap.AddView(meta);

            body.LongClickable = true;
            body.LongClick += (_, _) => _ = ShowActionsAsync(hub, message, wrap);

            if (atTop) _messages.AddView(wrap, 0);
            else _messages.AddView(wrap);
            _shown[message.Id] = wrap;
        }

        private async Task ShowActionsAsync(HubOnline hub, ChatMessage message, View bubble)
        {
            var items = new List<string> { "copy", "delete for me" };
            if (!message.Mine) items.Add("report…");
            int choice = await WpDialogs.ChooseAsync(this, "message", items.ToArray());
            switch (choice)
            {
                case 0:
                    var clipboard = (global::Android.Content.ClipboardManager?)GetSystemService(ClipboardService);
                    if (clipboard != null) clipboard.PrimaryClip = ClipData.NewPlainText("message", message.Body);
                    SetStatus("copied.");
                    break;

                case 1:
                    try
                    {
                        await hub.Social.DeleteMessageAsync(message.Id);
                        _messages.RemoveView(bubble);
                        _shown.Remove(message.Id);
                        SetStatus($"deleted for you. {_with} still has their copy.");
                    }
                    catch (Exception ex) { SetStatus(ex.Message); }
                    break;

                case 2:
                    await ReportAsync(hub, message);
                    break;
            }
        }

        private async Task ReportAsync(HubOnline hub, ChatMessage message)
        {
            string[] reasons = { MessageReportReasons.Harassment, MessageReportReasons.Spam, MessageReportReasons.Inappropriate, MessageReportReasons.Other };
            int reason = await WpDialogs.ChooseAsync(this, "why are you reporting it?", reasons);
            if (reason < 0) return;
            bool block = await WpDialogs.ConfirmAsync(this, "block " + _with + "?",
                $"an admin will see this one message. do you also want to block {_with}? they won't be able to message you or send you friend requests.",
                yes: "report and block", no: "just report");
            try
            {
                await hub.Social.ReportMessageAsync(message.Id, reasons[reason], block);
                SetStatus(block ? $"reported, and {_with} is blocked." : "reported. thanks for letting us know.");
                if (block) _compose.Visibility = ViewStates.Gone;
            }
            catch (Exception ex) { SetStatus(ex.Message); }
        }

        // ------------------------------------------------------------------ compose

        private View BuildCompose()
        {
            _compose = new LinearLayout(this) { Orientation = Orientation.Horizontal, Visibility = ViewStates.Gone };
            _compose.SetGravity(GravityFlags.Bottom);
            _compose.SetPadding(0, Dp(8), 0, 0);

            _input = new EditText(this)
            {
                Hint = "message",
                InputType = InputTypes.ClassText | InputTypes.TextFlagMultiLine | InputTypes.TextFlagCapSentences,
            };
            _input.SetMaxLines(5);
            _input.SetTextColor(WpTheme.Foreground);
            _input.SetHintTextColor(HubViews.Subtle);
            _input.SetBackgroundColor(WpTheme.Chrome);
            _input.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
            _input.SetFilters(new IInputFilter[] { new InputFilterLengthFilter(1000) });
            _compose.AddView(_input, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

            _send = HubViews.Button(this, "send", accent: true);
            _compose.AddView(_send, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { LeftMargin = Dp(8) });
            _send.Click += (_, _) => _ = SendAsync();
            return _compose;
        }

        private View BuildNotFriends()
        {
            _notFriends = new LinearLayout(this) { Orientation = Orientation.Vertical, Visibility = ViewStates.Gone };
            _notFriends.AddView(HubViews.Hint(this, "you're not friends any more, so this conversation is read-only. you can message each other again once you're friends."));
            TextView request = HubViews.Button(this, "send friend request");
            var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { TopMargin = Dp(10) };
            _notFriends.AddView(request, lp);
            request.Click += async (_, _) =>
            {
                HubOnline? hub = HubSetup.Current;
                if (hub == null) return;
                try
                {
                    FriendRequestResult result = await hub.Social.SendFriendRequestAsync(_with);
                    SetStatus(result.Status == "accepted" ? $"you and {_with} are friends again." : $"friend request sent to {_with}.");
                    if (result.Status == "accepted") { _compose.Visibility = ViewStates.Visible; _notFriends.Visibility = ViewStates.Gone; }
                }
                catch (Exception ex) { SetStatus(ex.Message); }
            };
            return _notFriends;
        }

        private async Task SendAsync()
        {
            HubOnline? hub = HubSetup.Current;
            string body = _input.Text?.Trim() ?? "";
            if (hub == null || body.Length == 0 || _sending) return;

            _sending = true;
            _send.Alpha = 0.5f;
            try
            {
                ChatMessage sent = await hub.Social.SendMessageAsync(_with, body);
                if (IsDestroyed) return;
                _input.Text = "";
                SetStatus(null);
                if (!_shown.ContainsKey(sent.Id)) AddBubble(hub, sent, atTop: false);
                _newestId = Math.Max(_newestId, sent.Id);
                ScrollToBottom();
            }
            catch (HubException ex) when (ex.ErrorCode == "not_friends")
            {
                SetStatus("you can only message friends.");
                _compose.Visibility = ViewStates.Gone;
                _notFriends.Visibility = ViewStates.Visible;
            }
            catch (Exception ex)
            {
                SetStatus("not sent: " + ex.Message);
            }
            finally
            {
                _sending = false;
                _send.Alpha = 1f;
            }
        }

        // ------------------------------------------------------------------ bits

        // ScrollTo, not FullScroll: FullScroll moves focus to the bottom view and would take it off the reply box.
        private void ScrollToBottom() => _scroll.Post(() => _scroll.SmoothScrollTo(0, _messages.Bottom));

        private void SetStatus(string? text)
        {
            _status.Text = text ?? "";
            _status.Visibility = string.IsNullOrEmpty(text) ? ViewStates.Gone : ViewStates.Visible;
        }

        private int Dp(int dp) => HubViews.Dp(this, dp);
    }
}
