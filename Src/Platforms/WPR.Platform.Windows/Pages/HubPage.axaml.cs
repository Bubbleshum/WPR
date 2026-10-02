using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

using WPR.Common;
using WPR.Online.Hub;
using WPR.Online.Hub.Client;
using WPR.Shell;

namespace WPR.Platform.Windows.Pages
{
    /// <summary>
    /// The WPR HUB tab: the player's profile, friend requests, friends with what they are playing,
    /// adding a friend, and the games the hub has on record. The Android twin is
    /// <c>Native/HubActivity</c>; both word things through <see cref="HubText"/>.
    /// </summary>
    /// <remarks>
    /// The navigator keeps one instance, so the page reloads every time it is shown rather than once:
    /// presence is only worth anything fresh.
    /// </remarks>
    public partial class HubPage : UserControl
    {
        private static readonly IBrush Subtle = new SolidColorBrush(Color.Parse("#BFFFFFFF"));
        private static readonly IBrush Tile = new SolidColorBrush(Color.Parse("#33FFFFFF"));

        private int _loadGeneration;
        private bool _busy;

        public HubPage()
        {
            InitializeComponent();

            Views.HubLogo.Apply(this.Get<Image>("hubLogoImage"), this.Get<TextBlock>("hubHeaderText"));

            this.Get<Button>("signInButton").Click += async (_, _) => await SignInAsync();
            this.Get<Button>("gamerpicButton").Click += async (_, _) => await ShowGamerpicsAsync();
            this.Get<Button>("refreshButton").Click += (_, _) => _ = LoadAsync();

            AttachedToVisualTree += (_, _) => _ = LoadAsync();
        }

        private static bool SignedIn => !string.IsNullOrEmpty(Configuration.Current?.HubAccessToken);

        private Window GetWindow() => VisualRoot as Window ?? throw new InvalidOperationException("The hub page has no window.");

        private async Task SignInAsync()
        {
            var window = new Views.SignInWindow(HubSetup.Current);
            await window.ShowDialog(GetWindow());
            await LoadAsync();
            if (window.WantsGamerpic) await ShowGamerpicsAsync();
        }

        private async Task ShowGamerpicsAsync()
        {
            var window = new Views.GamerpicWindow(HubSetup.Current);
            await window.ShowDialog(GetWindow());
            if (window.Changed) await LoadAsync();
        }

        private async Task LoadAsync()
        {
            int generation = ++_loadGeneration;
            HubOnline? hub = HubSetup.Current;
            StackPanel sections = this.Get<StackPanel>("sectionsPanel");
            sections.Children.Clear();

            bool signedIn = SignedIn && hub != null;
            this.Get<TextBlock>("nameText").Text = signedIn ? Configuration.Current!.HubUsername ?? "Signed in" : "Not signed in";
            this.Get<TextBlock>("summaryText").Text = signedIn ? "" : "Sign in to see your friends, what they're playing, and your games on the hub.";
            this.Get<Button>("signInButton").IsVisible = !signedIn;
            this.Get<Button>("gamerpicButton").IsVisible = signedIn;
            this.Get<Button>("refreshButton").IsVisible = signedIn;
            if (!signedIn)
            {
                this.Get<Image>("gamerpicImage").Source = null;
                SetStatus(hub == null ? "WPR Hub is not available right now. Try again later." : null);
                return;
            }

            SetStatus("Loading…");
            Task<Bitmap?> picture = Views.SignInWindow.LoadCurrentGamerpicAsync(hub);
            Task<FriendList?> friends = hub!.Social.GetFriendsAsync();
            Task<FriendRequests?> requests = hub.Social.GetFriendRequestsAsync();
            Task<HubGameList?> games = hub.Social.GetGamesAsync();

            try
            {
                await Task.WhenAll(friends, requests, games);
            }
            catch (Exception ex)
            {
                if (generation != _loadGeneration) return;
                if (!SignedIn) { _ = LoadAsync(); return; } // the token was revoked: show signed out
                SetStatus("Could not reach WPR Hub: " + ex.Message);
                return;
            }
            if (generation != _loadGeneration) return;

            SetStatus(null);
            this.Get<TextBlock>("summaryText").Text = HubText.Summary(games.Result);

            if (requests.Result is { Incoming.Count: > 0 } r) AddRequests(sections, hub, r.Incoming);
            AddFriends(sections, hub, friends.Result);
            AddFriendInput(sections, hub);
            if (requests.Result is { Outgoing.Count: > 0 } pending) AddOutgoing(sections, hub, pending.Outgoing);
            AddGames(sections, games.Result?.Games ?? Array.Empty<HubGame>());

            Bitmap? bitmap = await picture;
            if (generation == _loadGeneration) this.Get<Image>("gamerpicImage").Source = bitmap;
        }

        // ------------------------------------------------------------------ sections

        private void AddRequests(StackPanel sections, HubOnline hub, IReadOnlyList<IncomingFriendRequest> incoming)
        {
            sections.Children.Add(Section($"FRIEND REQUESTS ({incoming.Count})"));
            foreach (IncomingFriendRequest request in incoming)
            {
                var accept = new Button { Content = "Accept" };
                var decline = new Button { Content = "Decline" };
                accept.Click += (_, _) => _ = ActAsync($"Accepted. {request.From} is now your friend.", () => hub.Social.AcceptAsync(request.Id));
                decline.Click += (_, _) => _ = ActAsync("Declined.", () => hub.Social.DeclineAsync(request.Id));
                sections.Children.Add(PersonRow(hub, request.GamerpicUrl, request.From, "wants to be your friend", Brushes.White, accept, decline));
            }
        }

        private void AddFriends(StackPanel sections, HubOnline hub, FriendList? list)
        {
            IReadOnlyList<Friend> friends = list?.Friends ?? Array.Empty<Friend>();
            int online = friends.Count(f => f.Presence.IsOnline);
            sections.Children.Add(Section(friends.Count == 0 ? "FRIENDS" : $"FRIENDS · {online} ONLINE"));

            if (friends.Count == 0)
            {
                sections.Children.Add(Hint("No friends yet. Add someone by their WPR Hub username below."));
                return;
            }

            IBrush accent = this.TryFindResource("WprAccentBrush", out object? found) && found is IBrush b ? b : Brushes.White;
            foreach (Friend friend in friends)
            {
                string line = HubText.Presence(friend.Presence);
                if (friend.UnreadMessages > 0) line += $" · {HubText.Plural(friend.UnreadMessages, "unread message")}";
                Control row = PersonRow(hub, friend.GamerpicUrl, friend.Username, line, friend.Presence.IsOnline ? accent : Subtle);
                if (!friend.Presence.IsOnline) row.Opacity = 0.7;

                var remove = new MenuItem { Header = $"Remove {friend.Username} from friends" };
                remove.Click += (_, _) => _ = ActAsync($"{friend.Username} removed.", () => hub.Social.RemoveFriendAsync(friend.Username));
                row.ContextMenu = new ContextMenu { ItemsSource = new[] { remove } };
                sections.Children.Add(row);
            }

            sections.Children.Add(Hint(list != null && friends.Count >= list.MaxFriends
                ? $"You've reached the limit of {list.MaxFriends} friends."
                : "Right-click a friend to remove them."));
        }

        private void AddFriendInput(StackPanel sections, HubOnline hub)
        {
            sections.Children.Add(Section("ADD A FRIEND"));
            var input = new TextBox { Watermark = "Username", MaxLength = 24, Width = 260 };
            var add = new Button { Content = "Send request" };

            async Task SendAsync()
            {
                string username = input.Text?.Trim() ?? "";
                if (username.Length == 0) return;
                string done = $"Request sent to {username}.";
                await ActAsync(done, async () =>
                {
                    FriendRequestResult result = await hub.Social.SendFriendRequestAsync(username);
                    if (result.Status == "accepted") done = $"{username} had already asked you, so you're now friends.";
                }, () => done);
            }

            add.Click += (_, _) => _ = SendAsync();
            input.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                _ = SendAsync();
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
            row.Children.Add(input);
            row.Children.Add(add);
            sections.Children.Add(row);
        }

        private void AddOutgoing(StackPanel sections, HubOnline hub, IReadOnlyList<OutgoingFriendRequest> outgoing)
        {
            sections.Children.Add(Section("WAITING FOR AN ANSWER"));
            foreach (OutgoingFriendRequest request in outgoing)
            {
                var cancel = new Button { Content = "Cancel" };
                cancel.Click += (_, _) => _ = ActAsync("Request cancelled.", () => hub.Social.CancelRequestAsync(request.Id));
                sections.Children.Add(PersonRow(hub, null, request.To, "sent " + HubText.Ago(request.SentAt), Subtle, cancel));
            }
        }

        private void AddGames(StackPanel sections, IReadOnlyList<HubGame> games)
        {
            sections.Children.Add(Section("GAMES"));
            if (games.Count == 0)
            {
                sections.Children.Add(Hint("Nothing yet. Playtime and achievements show up here once you've played while signed in."));
                return;
            }

            Dictionary<string, WPR.Models.Application> installed = InstalledByTitle();
            foreach (HubGame game in games)
            {
                installed.TryGetValue(game.TitleId, out WPR.Models.Application? app);

                var art = new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(3), ClipToBounds = true, Background = Tile };
                if (app != null && LoadTileArt(app) is { } bitmap) art.Child = new Image { Source = bitmap, Stretch = Stretch.UniformToFill };

                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2, Margin = new Thickness(14, 0, 0, 0) };
                text.Children.Add(new TextBlock { Text = app?.Name ?? game.Name, FontSize = 18, FontWeight = FontWeight.Light, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis });
                text.Children.Add(new TextBlock { Text = HubText.GameLine(game), FontSize = 12, Foreground = Subtle });

                var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
                DockPanel.SetDock(art, Dock.Left);
                row.Children.Add(art);
                row.Children.Add(text);
                sections.Children.Add(row);
            }
        }

        /// <summary>Installed games keyed the way the hub keys titles, so a row can show local tile art.</summary>
        private static Dictionary<string, WPR.Models.Application> InstalledByTitle()
        {
            var map = new Dictionary<string, WPR.Models.Application>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (WPR.Models.Application app in WPR.Models.ApplicationContext.Current.Applications!.ToList())
                    if (!string.IsNullOrEmpty(app.ProductId)) map[TitleIds.Normalize(app.ProductId)] = app;
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppList, "hub page could not read installed games: " + ex.Message);
            }
            return map;
        }

        private static Bitmap? LoadTileArt(WPR.Models.Application app)
        {
            try
            {
                string? relative = GameIconStore.Resolve(app.ProductId, app.IconPath);
                if (string.IsNullOrWhiteSpace(relative)) return null;
                string full = Configuration.Current!.DataPath(relative!);
                return File.Exists(full) ? new Bitmap(full) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ------------------------------------------------------------------ actions

        /// <summary>Run one hub action, report it, and reload. One at a time.</summary>
        private async Task ActAsync(string done, Func<Task> action, Func<string>? doneLate = null)
        {
            if (_busy) return;
            _busy = true;
            SetStatus("Working…");
            string message;
            try
            {
                await action();
                message = doneLate?.Invoke() ?? done;
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
            await LoadAsync();
            SetStatus(message);
        }

        // ------------------------------------------------------------------ building blocks

        private Control PersonRow(HubOnline hub, string? gamerpicUrl, string name, string line, IBrush lineBrush, params Control[] trailing)
        {
            var image = new Image { Stretch = Stretch.UniformToFill };
            var picture = new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(3), ClipToBounds = true, Background = Tile, Child = image };
            if (!string.IsNullOrEmpty(gamerpicUrl)) _ = LoadImageAsync(hub, image, gamerpicUrl!);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2, Margin = new Thickness(14, 0, 8, 0) };
            text.Children.Add(new TextBlock { Text = name, FontSize = 18, FontWeight = FontWeight.Light, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = line, FontSize = 12, Foreground = lineBrush, TextTrimming = TextTrimming.CharacterEllipsis });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            foreach (Control c in trailing) buttons.Children.Add(c);

            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6), Background = Brushes.Transparent };
            DockPanel.SetDock(picture, Dock.Left);
            DockPanel.SetDock(buttons, Dock.Right);
            row.Children.Add(picture);
            row.Children.Add(buttons);
            row.Children.Add(text);
            return row;
        }

        private static async Task LoadImageAsync(HubOnline hub, Image target, string url)
        {
            byte[]? bytes = await hub.Account.GetImageAsync(url);
            if (bytes == null) return;
            try { target.Source = new Bitmap(new MemoryStream(bytes)); }
            catch (Exception) { /* not an image; leave the placeholder */ }
        }

        private static TextBlock Section(string title) => new TextBlock
        {
            Text = title,
            FontSize = 12,
            LetterSpacing = 1.5,
            Foreground = Subtle,
            Margin = new Thickness(0, 22, 0, 4),
        };

        private static TextBlock Hint(string text) => new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = Subtle,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };

        private void SetStatus(string? text)
        {
            TextBlock status = this.Get<TextBlock>("statusText");
            status.Text = text ?? "";
            status.IsVisible = !string.IsNullOrEmpty(text);
        }
    }
}
