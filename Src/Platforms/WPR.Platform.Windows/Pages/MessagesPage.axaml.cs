using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

using WPR.Common;
using WPR.Online.Hub;
using WPR.Online.Hub.Client;
using WPR.Shell;

namespace WPR.Platform.Windows.Pages
{
    /// <summary>
    /// The MESSAGES tab: WPR Hub direct messages. The hub only carries messages between friends,
    /// so typing a username that is not a friend offers a friend request instead. The open
    /// conversation is polled with <c>?after=&lt;last id&gt;</c> while the page is showing, since
    /// the hub has no push channel; that is also when what is shown gets marked read.
    /// </summary>
    public partial class MessagesPage : UserControl
    {
        private static readonly IBrush Subtle = new SolidColorBrush(Color.Parse("#BFFFFFFF"));
        private static readonly IBrush Tile = new SolidColorBrush(Color.Parse("#33FFFFFF"));
        private static readonly IBrush Theirs = new SolidColorBrush(Color.Parse("#2AFFFFFF"));

        private readonly DispatcherTimer _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        private readonly HashSet<long> _shown = new HashSet<long>();
        private string? _with;
        private long _newestId;
        private bool _polling;
        private bool _sending;
        private int _openGeneration;

        public MessagesPage()
        {
            InitializeComponent();

            this.Get<Button>("messageButton").Click += (_, _) => _ = MessageTypedAsync();
            this.Get<Button>("requestButton").Click += (_, _) => _ = RequestAsync(Typed(), left: true);
            this.Get<TextBox>("toTextBox").KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                _ = MessageTypedAsync();
            };
            this.Get<Button>("sendButton").Click += (_, _) => _ = SendAsync();
            this.Get<TextBox>("bodyTextBox").KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
                e.Handled = true;
                _ = SendAsync();
            };
            this.Get<Button>("refriendButton").Click += (_, _) => { if (_with != null) _ = RequestAsync(_with, left: false); };

            _poll.Tick += (_, _) => _ = PollAsync();
            AttachedToVisualTree += (_, _) =>
            {
                _ = LoadConversationsAsync();
                if (_with != null) _poll.Start();
            };
            DetachedFromVisualTree += (_, _) => _poll.Stop();
        }

        private static bool SignedIn => !string.IsNullOrEmpty(Configuration.Current?.HubAccessToken) && HubSetup.Current != null;

        private string Typed() => this.Get<TextBox>("toTextBox").Text?.Trim() ?? "";

        // ------------------------------------------------------------------ left: conversations

        private async Task LoadConversationsAsync()
        {
            StackPanel panel = this.Get<StackPanel>("conversationsPanel");
            panel.Children.Clear();
            HubOnline? hub = HubSetup.Current;
            if (!SignedIn || hub == null)
            {
                panel.Children.Add(Hint("Sign in to WPR Hub (on the HUB tab) to message your friends."));
                return;
            }

            ConversationList? conversations;
            FriendList? friends;
            try
            {
                Task<ConversationList?> c = hub.Social.GetConversationsAsync();
                Task<FriendList?> f = hub.Social.GetFriendsAsync();
                await Task.WhenAll(c, f);
                conversations = c.Result;
                friends = f.Result;
            }
            catch (Exception ex)
            {
                panel.Children.Add(Hint("Could not reach WPR Hub: " + ex.Message));
                return;
            }

            panel.Children.Clear();
            IReadOnlyList<Conversation> list = conversations?.Conversations ?? Array.Empty<Conversation>();
            panel.Children.Add(Section("CONVERSATIONS"));
            if (list.Count == 0) panel.Children.Add(Hint("No messages yet."));
            foreach (Conversation c in list)
            {
                string preview = (c.LastMessage.Mine ? "You: " : "") + OneLine(c.LastMessage.Body);
                panel.Children.Add(Row(hub, c.GamerpicUrl, c.With, $"{HubText.Ago(c.LastMessage.SentAt)} · {preview}",
                    c.Unread > 0 ? c.Unread.ToString() : c.IsFriend ? null : "not friends", c.Unread > 0,
                    () => _ = OpenAsync(c.With)));
            }

            var talked = new HashSet<string>(list.Select(c => c.With), StringComparer.OrdinalIgnoreCase);
            List<Friend> fresh = (friends?.Friends ?? Array.Empty<Friend>()).Where(f => !talked.Contains(f.Username)).ToList();
            if (fresh.Count > 0)
            {
                panel.Children.Add(Section("FRIENDS"));
                foreach (Friend f in fresh)
                    panel.Children.Add(Row(hub, f.GamerpicUrl, f.Username, HubText.Presence(f.Presence), null, f.Presence.IsOnline,
                        () => _ = OpenAsync(f.Username)));
            }
        }

        private async Task MessageTypedAsync()
        {
            string username = Typed();
            HubOnline? hub = HubSetup.Current;
            if (username.Length == 0 || hub == null || !SignedIn) return;

            SetLeftStatus($"Looking for {username}…");
            FriendList? friends;
            try { friends = await hub.Social.GetFriendsAsync(); }
            catch (Exception ex) { SetLeftStatus(ex.Message); return; }

            Friend? friend = friends?.Friends.FirstOrDefault(f => string.Equals(f.Username, username, StringComparison.OrdinalIgnoreCase));
            if (friend != null)
            {
                SetLeftStatus(null);
                this.Get<TextBox>("toTextBox").Text = "";
                await OpenAsync(friend.Username);
                return;
            }

            SetLeftStatus($"You can only message friends. Press \"Send friend request\" to ask {username}; you can message them once they accept.");
        }

        private async Task RequestAsync(string username, bool left)
        {
            HubOnline? hub = HubSetup.Current;
            if (username.Length == 0 || hub == null || !SignedIn) return;
            void Say(string text) { if (left) SetLeftStatus(text); else SetThreadStatus(text); }

            try
            {
                FriendRequestResult result = await hub.Social.SendFriendRequestAsync(username);
                if (left) this.Get<TextBox>("toTextBox").Text = "";
                Say(result.Status == "accepted"
                    ? $"{username} had already asked you, so you're now friends. You can message them."
                    : $"Friend request sent to {username}. You can message them once they accept.");
                if (result.Status == "accepted") await LoadConversationsAsync();
            }
            catch (Exception ex)
            {
                Say(ex.Message);
            }
        }

        // ------------------------------------------------------------------ right: one conversation

        private async Task OpenAsync(string username)
        {
            HubOnline? hub = HubSetup.Current;
            if (hub == null) return;
            int generation = ++_openGeneration;

            _with = username;
            _newestId = 0;
            _shown.Clear();
            this.Get<StackPanel>("threadPanel").Children.Clear();
            this.Get<TextBlock>("withText").Text = username;
            SetThreadStatus("Loading…");

            MessageThread? thread;
            try { thread = await hub.Social.GetThreadAsync(username); }
            catch (Exception ex) { if (generation == _openGeneration) SetThreadStatus(ex.Message); return; }
            if (generation != _openGeneration || thread == null) return;

            _with = thread.With;
            this.Get<TextBlock>("withText").Text = thread.With;
            ApplyFriendship(thread.IsFriend);
            SetThreadStatus(thread.Messages.Count == 0
                ? (thread.IsFriend ? "No messages yet. Say hello." : null)
                : thread.HasMore ? "Showing the latest messages." : null);
            Append(thread.Messages);
            _ = hub.Social.MarkReadAsync(thread.With, _newestId == 0 ? null : _newestId);
            _poll.Start();
            this.Get<TextBox>("bodyTextBox").Focus();
        }

        private async Task PollAsync()
        {
            HubOnline? hub = HubSetup.Current;
            if (_polling || hub == null || _with == null || !SignedIn) return;
            _polling = true;
            string with = _with;
            try
            {
                MessageThread? thread = await hub.Social.GetThreadAsync(with, after: _newestId == 0 ? null : _newestId);
                if (thread == null || with != _with) return;
                ApplyFriendship(thread.IsFriend);
                if (thread.Messages.Count == 0) return;
                bool incoming = thread.Messages.Any(m => !m.Mine && !_shown.Contains(m.Id));
                Append(thread.Messages);
                if (incoming) _ = hub.Social.MarkReadAsync(with, _newestId);
            }
            catch (Exception)
            {
                // Try again next tick.
            }
            finally
            {
                _polling = false;
            }
        }

        private async Task SendAsync()
        {
            HubOnline? hub = HubSetup.Current;
            TextBox box = this.Get<TextBox>("bodyTextBox");
            string body = box.Text?.Trim() ?? "";
            if (hub == null || _with == null || body.Length == 0 || _sending) return;

            _sending = true;
            this.Get<Button>("sendButton").IsEnabled = false;
            try
            {
                ChatMessage sent = await hub.Social.SendMessageAsync(_with, body);
                box.Text = "";
                SetThreadStatus(null);
                Append(new[] { sent });
            }
            catch (HubException ex) when (ex.ErrorCode == "not_friends")
            {
                ApplyFriendship(false);
                SetThreadStatus("You can only message friends.");
            }
            catch (Exception ex)
            {
                SetThreadStatus("Not sent: " + ex.Message);
            }
            finally
            {
                _sending = false;
                this.Get<Button>("sendButton").IsEnabled = true;
            }
        }

        private void ApplyFriendship(bool isFriend)
        {
            this.Get<StackPanel>("composePanel").IsVisible = isFriend;
            this.Get<StackPanel>("notFriendsPanel").IsVisible = !isFriend;
        }

        private void Append(IEnumerable<ChatMessage> messages)
        {
            StackPanel panel = this.Get<StackPanel>("threadPanel");
            HubOnline? hub = HubSetup.Current;
            bool added = false;
            foreach (ChatMessage m in messages)
            {
                if (!_shown.Add(m.Id)) continue;
                panel.Children.Add(Bubble(hub, m));
                _newestId = Math.Max(_newestId, m.Id);
                added = true;
            }
            if (added) Dispatcher.UIThread.Post(() => this.Get<ScrollViewer>("threadScroll").ScrollToEnd(), DispatcherPriority.Background);
        }

        private Control Bubble(HubOnline? hub, ChatMessage message)
        {
            IBrush accent = this.TryFindResource("WprAccentBrush", out object? found) && found is IBrush b ? b : Brushes.SteelBlue;
            var body = new SelectableTextBlock { Text = message.Body, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, FontSize = 14 };
            string when = HubText.Ago(message.SentAt) + (message.Mine && message.ReadAt != null ? " · read" : "");
            var stack = new StackPanel { Spacing = 2 };
            stack.Children.Add(new Border { Background = message.Mine ? accent : Theirs, CornerRadius = new CornerRadius(3), Padding = new Thickness(10, 6), Child = body });
            stack.Children.Add(new TextBlock { Text = when, FontSize = 10, Foreground = Subtle, HorizontalAlignment = message.Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left });

            var wrap = new Border
            {
                Child = stack,
                MaxWidth = 460,
                HorizontalAlignment = message.Mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            };

            var delete = new MenuItem { Header = "Delete for me" };
            delete.Click += async (_, _) =>
            {
                if (hub == null) return;
                try
                {
                    await hub.Social.DeleteMessageAsync(message.Id);
                    this.Get<StackPanel>("threadPanel").Children.Remove(wrap);
                    SetThreadStatus($"Deleted for you. {_with} still has their copy.");
                }
                catch (Exception ex) { SetThreadStatus(ex.Message); }
            };
            var items = new List<MenuItem> { delete };
            if (!message.Mine)
            {
                var report = new MenuItem { Header = "Report" };
                foreach (string reason in new[] { MessageReportReasons.Harassment, MessageReportReasons.Spam, MessageReportReasons.Inappropriate, MessageReportReasons.Other })
                {
                    foreach (bool block in new[] { false, true })
                    {
                        var item = new MenuItem { Header = char.ToUpperInvariant(reason[0]) + reason[1..] + (block ? ", and block them" : "") };
                        item.Click += async (_, _) =>
                        {
                            if (hub == null) return;
                            try
                            {
                                await hub.Social.ReportMessageAsync(message.Id, reason, block);
                                SetThreadStatus(block ? $"Reported, and {_with} is blocked." : "Reported. Thanks for letting us know.");
                                if (block) ApplyFriendship(false);
                            }
                            catch (Exception ex) { SetThreadStatus(ex.Message); }
                        };
                        report.Items.Add(item);
                    }
                }
                items.Add(report);
            }
            wrap.ContextMenu = new ContextMenu { ItemsSource = items };
            return wrap;
        }

        // ------------------------------------------------------------------ building blocks

        private Control Row(HubOnline hub, string? gamerpicUrl, string name, string line, string? badge, bool highlight, Action open)
        {
            IBrush accent = this.TryFindResource("WprAccentBrush", out object? found) && found is IBrush b ? b : Brushes.White;
            var image = new Image { Stretch = Stretch.UniformToFill };
            var picture = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(3), ClipToBounds = true, Background = Tile, Child = image };
            if (!string.IsNullOrEmpty(gamerpicUrl)) _ = LoadImageAsync(hub, image, gamerpicUrl!);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1, Margin = new Thickness(10, 0, 6, 0) };
            text.Children.Add(new TextBlock { Text = name, FontSize = 16, FontWeight = FontWeight.Light, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = line, FontSize = 11, Foreground = highlight ? accent : Subtle, TextTrimming = TextTrimming.CharacterEllipsis });

            var row = new DockPanel { Margin = new Thickness(0, 3), Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand) };
            DockPanel.SetDock(picture, Dock.Left);
            row.Children.Add(picture);
            if (badge != null)
            {
                var tag = new TextBlock { Text = badge, FontSize = highlight ? 18 : 10, Foreground = highlight ? accent : Subtle, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(tag, Dock.Right);
                row.Children.Add(tag);
            }
            row.Children.Add(text);
            row.PointerReleased += (_, e) => { if (e.InitialPressMouseButton == MouseButton.Left) open(); };
            return row;
        }

        private static async Task LoadImageAsync(HubOnline hub, Image target, string url)
        {
            byte[]? bytes = await hub.Account.GetImageAsync(url);
            if (bytes == null) return;
            try { target.Source = new Bitmap(new MemoryStream(bytes)); }
            catch (Exception) { /* leave the placeholder */ }
        }

        private static string OneLine(string text)
        {
            string flat = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
            return flat.Length > 60 ? flat[..60] + "…" : flat;
        }

        private static TextBlock Section(string title) => new TextBlock
        {
            Text = title, FontSize = 11, LetterSpacing = 1.5, Foreground = Subtle, Margin = new Thickness(0, 14, 0, 4),
        };

        private static TextBlock Hint(string text) => new TextBlock
        {
            Text = text, FontSize = 12, Foreground = Subtle, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
        };

        private void SetLeftStatus(string? text)
        {
            TextBlock status = this.Get<TextBlock>("leftStatusText");
            status.Text = text ?? "";
            status.IsVisible = !string.IsNullOrEmpty(text);
        }

        private void SetThreadStatus(string? text)
        {
            TextBlock status = this.Get<TextBlock>("threadStatusText");
            status.Text = text ?? "";
            status.IsVisible = !string.IsNullOrEmpty(text);
        }
    }
}
