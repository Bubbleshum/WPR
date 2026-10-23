using System;
using System.Collections.Generic;
using WPR.Common;
using WPR.Engine.Notifications;
using WPR.Online.Hub;
using WPR.Online.Hub.Client;

namespace WPR.Shell
{
    /// <summary>
    /// Pop-up notifications for WPR Hub: a new message, a game invite, a friend request. The sender
    /// is the title, the message (or "Invited you to play ...", "Sent you a friend request.") the
    /// body, and an invite shows the game's tile. Tapping a message or invite opens the
    /// conversation, a friend request the friends list (<see cref="ActivationFor"/>,
    /// <see cref="ActivationFriends"/>). Both platforms show them through their notification
    /// manager, the one achievement toasts use, and each kind has its own switch in settings.
    /// </summary>
    /// <remarks>
    /// Two sources: the hub's push (Android, <see cref="ShowPush"/>), and the heartbeat
    /// (<see cref="HubMessageAlerts"/>), which is the desktop's only source and Android's fallback.
    /// On a device that push reaches (<see cref="PushHandlesThisDevice"/>) the heartbeat's alerts are
    /// ignored, so nothing pops twice.
    /// </remarks>
    public static class HubMessageNotifications
    {
        public const string ConversationPrefix = "conversation:";

        /// <summary>The activation id that opens the friends list (a friend request's notification).</summary>
        public const string ActivationFriends = "friends";

        /// <summary>The conversation on screen right now, if any: nothing pops for it, its page shows it.</summary>
        public static string? ActiveConversation { get; set; }

        /// <summary>A game's tile as an absolute file path, for an invite's picture. Set by the platform.</summary>
        public static Func<string, string?>? GameIconPath { get; set; }

        /// <summary>True when the hub pushes to this device, so the heartbeat's alerts would be duplicates.</summary>
        public static Func<bool>? PushHandlesThisDevice { get; set; }

        /// <summary>The activation id that opens <paramref name="username"/>'s conversation.</summary>
        public static string ActivationFor(string username) => ConversationPrefix + username;

        /// <summary>The gamertag an activation id opens, or null when it is not a conversation.</summary>
        public static string? ConversationFrom(string? activationId) =>
            activationId != null && activationId.StartsWith(ConversationPrefix, StringComparison.Ordinal)
                ? activationId.Substring(ConversationPrefix.Length)
                : null;

        internal static void Attach(HubOnline hub)
        {
            hub.MessageAlerts.Arrived += alerts =>
            {
                if (PushHandlesThisDevice?.Invoke() == true) return;
                foreach (MessageAlert alert in alerts) Show(Build(alert), alert.From, alert.Message.IsInvite ? Kind.Invite : Kind.Message);
            };
            hub.MessageAlerts.FriendRequestsArrived += requests =>
            {
                if (PushHandlesThisDevice?.Invoke() == true) return;
                foreach (IncomingFriendRequest request in requests) Show(BuildFriendRequest(request.From), request.From, Kind.FriendRequest);
            };
        }

        /// <summary>
        /// A push from the hub (its data: kind, sender, body, title_id; "from" is reserved by Firebase). Returns false when it was not
        /// shown (switched off, the conversation is open, or not something WPR knows).
        /// </summary>
        public static bool ShowPush(IDictionary<string, string> data)
        {
            data.TryGetValue("kind", out string? kind);
            if (!data.TryGetValue("sender", out string? from)) data.TryGetValue("from", out from);
            data.TryGetValue("body", out string? body);
            data.TryGetValue("title_id", out string? titleId);
            if (string.IsNullOrEmpty(from)) return false;
            switch (kind)
            {
                case "message":
                case MessageKinds.Invite:
                    bool invite = kind == MessageKinds.Invite;
                    var message = new ChatMessage(0, from, false, body ?? "", DateTimeOffset.UtcNow, null,
                        invite ? MessageKinds.Invite : MessageKinds.Text, titleId);
                    return Show(Build(new MessageAlert(from, null, message, 1)), from, invite ? Kind.Invite : Kind.Message);
                case "friend_request":
                    return Show(BuildFriendRequest(from), from, Kind.FriendRequest);
                default:
                    return false;
            }
        }

        private enum Kind { Message, Invite, FriendRequest }

        private static bool Show(Notification notification, string from, Kind kind)
        {
            INotificationManager? manager = NotificationBackend.Manager;
            if (manager == null) return false;
            if (kind != Kind.FriendRequest && string.Equals(from, ActiveConversation, StringComparison.OrdinalIgnoreCase)) return false;
            // Read live: the settings switches apply at once, in every process.
            if (Configuration.Current is { } config && !(kind switch
                {
                    Kind.Invite => config.NotifyGameInvites,
                    Kind.FriendRequest => config.NotifyFriendRequests,
                    _ => config.NotifyMessages,
                }))
            {
                return false;
            }
            try
            {
                _ = manager.ShowNotification(notification);
                return true;
            }
            catch (Exception e)
            {
                Log.Warn(LogCategory.AppList, $"[wpr-messages] notification from {from} failed: {e.Message}");
                return false;
            }
        }

        /// <summary>The notification for one conversation's news. Public for tests.</summary>
        public static Notification Build(MessageAlert alert)
        {
            ChatMessage message = alert.Message;
            string body = message.Body.Replace('\n', ' ');
            if (!message.IsInvite && alert.Unread > 1) body += $" (+{alert.Unread - 1} more)";
            string? picture = null;
            if (message.IsInvite)
            {
                try { picture = GameIconPath?.Invoke(message.TitleId!); }
                catch (Exception) { picture = null; }
            }
            return new Notification
            {
                Title = alert.From, // an invite's body already says "Invited you to play ..."
                Body = body,
                ImagePath = picture,
                AttributionText = "WPR Hub",
                ActivationId = ActivationFor(alert.From),
            };
        }

        public static Notification BuildFriendRequest(string from) => new Notification
        {
            Title = from,
            Body = "Sent you a friend request.",
            AttributionText = "WPR Hub",
            ActivationId = ActivationFriends,
        };
    }
}
