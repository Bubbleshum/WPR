using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Android.App;
using Android.Content;
using Android.Text;
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
    /// The friend lists shared by the social page's friends pivot (<see cref="SocialActivity"/>) and the
    /// hub page (<see cref="HubActivity"/>): incoming requests, friends, adding a friend, and requests
    /// waiting for an answer. Every action goes through the page's <see cref="Act"/>, which reports
    /// it and reloads, so both pages behave the same.
    /// </summary>
    internal static class HubFriendViews
    {
        /// <summary>Run one hub action, report <paramref name="done"/> (or <paramref name="doneLate"/>'s answer), and reload the page.</summary>
        public delegate Task Act(string done, Func<Task> action, Func<string>? doneLate = null);

        public static void AddRequests(Activity activity, LinearLayout into, HubOnline hub, FriendRequests requests, Act act)
        {
            if (requests.Incoming.Count == 0) return;
            into.AddView(HubViews.Section(activity, $"FRIEND REQUESTS ({requests.Incoming.Count})"));
            foreach (IncomingFriendRequest request in requests.Incoming)
            {
                LinearLayout row = HubViews.PersonRow(activity, hub, request.GamerpicUrl, request.From, "wants to be your friend",
                    WpTheme.Foreground, out LinearLayout trailing);
                TextView accept = HubViews.Button(activity, "accept");
                TextView decline = HubViews.Button(activity, "decline");
                trailing.AddView(accept);
                var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { LeftMargin = HubViews.Dp(activity, 8) };
                trailing.AddView(decline, lp);
                accept.Click += (_, _) => _ = act($"accepted. {request.From} is now your friend.", () => hub.Social.AcceptAsync(request.Id));
                decline.Click += (_, _) => _ = act("declined.", () => hub.Social.DeclineAsync(request.Id));
                into.AddView(row);
            }
        }

        /// <summary>
        /// Every friend, online first. Tapping a row opens their page (<see cref="FriendProfileActivity"/>);
        /// tap and hold removes them.
        /// </summary>
        public static void AddFriends(Activity activity, LinearLayout into, HubOnline hub, FriendList? list, Act act)
        {
            IReadOnlyList<Friend> friends = list?.Friends ?? Array.Empty<Friend>();
            int online = friends.Count(f => f.Presence.IsOnline);
            into.AddView(HubViews.Section(activity, friends.Count == 0 ? "FRIENDS" : $"FRIENDS · {online} ONLINE"));

            if (friends.Count == 0)
            {
                into.AddView(HubViews.Hint(activity, "no friends yet. add someone by their WPR Hub username below."));
                return;
            }

            foreach (Friend friend in friends)
            {
                string line = HubText.Presence(friend.Presence);
                if (friend.UnreadMessages > 0) line += $" · {HubText.Plural(friend.UnreadMessages, "unread message")}";
                LinearLayout row = HubViews.PersonRow(activity, hub, friend.GamerpicUrl, friend.Username, line,
                    friend.Presence.IsOnline ? WpTheme.Accent : HubViews.Subtle, out LinearLayout trailing);
                if (!friend.Presence.IsOnline) row.Alpha = 0.7f;

                TextView message = HubViews.Button(activity, "message");
                trailing.AddView(message);
                message.Click += (_, _) => ConversationActivity.Open(activity, friend.Username);

                WpTheme.ApplyTilt(row, 0.97f);
                row.Click += (_, _) => FriendProfileActivity.Open(activity, friend.Username);
                row.LongClickable = true;
                row.LongClick += async (_, _) =>
                {
                    if (await ConfirmRemoveAsync(activity, friend.Username))
                        await act($"{friend.Username} removed.", () => hub.Social.RemoveFriendAsync(friend.Username));
                };
                into.AddView(row);
            }

            if (list != null && friends.Count >= list.MaxFriends)
                into.AddView(HubViews.Hint(activity, $"you've reached the limit of {list.MaxFriends} friends."));
            else
                into.AddView(HubViews.Hint(activity, "tap a friend to see what they've been up to. tap and hold to remove them."));
        }

        public static Task<bool> ConfirmRemoveAsync(Activity activity, string username) =>
            WpDialogs.ConfirmAsync(activity, "remove friend",
                $"remove {username} from your friends? they won't be told, but you'll stop seeing each other online.",
                yes: "remove", no: "cancel");

        public static void AddFriendInput(Activity activity, LinearLayout into, HubOnline hub, Act act)
        {
            into.AddView(HubViews.Section(activity, "ADD A FRIEND"));
            LinearLayout row = new LinearLayout(activity) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);

            EditText input = new EditText(activity)
            {
                Hint = "username",
                InputType = InputTypes.ClassText | InputTypes.TextFlagNoSuggestions,
                ImeOptions = ImeAction.Send,
            };
            input.SetSingleLine(true);
            input.SetTextColor(WpTheme.Foreground);
            input.SetHintTextColor(HubViews.Subtle);
            input.SetBackgroundColor(WpTheme.Chrome);
            int pad = HubViews.Dp(activity, 12), padV = HubViews.Dp(activity, 10);
            input.SetPadding(pad, padV, pad, padV);
            input.SetFilters(new IInputFilter[] { new InputFilterLengthFilter(24) });
            row.AddView(input, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

            TextView send = HubViews.Button(activity, "add");
            var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { LeftMargin = HubViews.Dp(activity, 10) };
            row.AddView(send, lp);
            into.AddView(row);

            async Task SendAsync()
            {
                string username = input.Text?.Trim() ?? "";
                if (username.Length == 0) return;
                ((InputMethodManager)activity.GetSystemService(Context.InputMethodService)!).HideSoftInputFromWindow(input.WindowToken, 0);
                string done = $"request sent to {username}.";
                await act(done, async () =>
                {
                    FriendRequestResult result = await hub.Social.SendFriendRequestAsync(username);
                    if (result.Status == "accepted") done = $"{username} had already asked you, so you're now friends.";
                }, () => done);
            }

            send.Click += (_, _) => _ = SendAsync();
            input.EditorAction += (_, e) =>
            {
                if (e.ActionId != ImeAction.Send) { e.Handled = false; return; }
                e.Handled = true;
                _ = SendAsync();
            };
        }

        public static void AddOutgoing(Activity activity, LinearLayout into, HubOnline hub, IReadOnlyList<OutgoingFriendRequest> outgoing, Act act)
        {
            if (outgoing.Count == 0) return;
            into.AddView(HubViews.Section(activity, "WAITING FOR AN ANSWER"));
            foreach (OutgoingFriendRequest request in outgoing)
            {
                LinearLayout row = HubViews.PersonRow(activity, hub, null, request.To, "sent " + HubText.Ago(request.SentAt),
                    HubViews.Subtle, out LinearLayout trailing);
                TextView cancel = HubViews.Button(activity, "cancel");
                trailing.AddView(cancel);
                cancel.Click += (_, _) => _ = act("request cancelled.", () => hub.Social.CancelRequestAsync(request.Id));
                into.AddView(row);
            }
        }

        /// <summary>Installed games keyed the way the hub keys titles, so a row can show local tile art and launch.</summary>
        public static Dictionary<string, WprApplication> InstalledByTitle()
        {
            var map = new Dictionary<string, WprApplication>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (WprApplication app in WPR.Models.ApplicationContext.Current.Applications!.ToList())
                {
                    if (string.IsNullOrEmpty(app.ProductId)) continue;
                    map[TitleIds.Normalize(app.ProductId)] = app;
                }
            }
            catch (Exception ex)
            {
                WPR.Common.Log.Warn(LogCategory.AppList, "hub page could not read installed games: " + ex.Message);
            }
            return map;
        }
    }
}
