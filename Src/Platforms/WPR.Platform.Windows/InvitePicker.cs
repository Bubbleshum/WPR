using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

using WPR.Common;
using WPR.Engine.Online;
using WPR.Online.Hub;
using WPR.Shell;

namespace WPR.Platform.Windows
{
    /// <summary>
    /// The game-invite friend picker (<see cref="GameInvites.Picker"/>), in the WPR Hub page's
    /// style: a black, topmost window over the game with the game's tile, a big light title, one
    /// row per friend (gamerpic, name, presence, an accent tick box) and invite / cancel. Esc or
    /// closing the window picks nobody.
    /// </summary>
    internal static class InvitePicker
    {
        private static readonly IBrush Subtle = new SolidColorBrush(Color.Parse("#99FFFFFF"));
        private static readonly IBrush Chrome = new SolidColorBrush(Color.Parse("#FF1F1F1F"));

        public static Task<IReadOnlyList<string>?> ShowAsync(string gameName, IReadOnlyList<InviteFriend> friends, string? emptyMessage)
        {
            Func<Task<IReadOnlyList<string>?>> show = () => Show(gameName, friends, emptyMessage);
            return Dispatcher.UIThread.CheckAccess() ? show() : Dispatcher.UIThread.InvokeAsync(show);
        }

        private static Task<IReadOnlyList<string>?> Show(string gameName, IReadOnlyList<InviteFriend> friends, string? emptyMessage)
        {
            var tcs = new TaskCompletionSource<IReadOnlyList<string>?>(TaskCreationOptions.RunContinuationsAsynchronously);
            IntPtr previousForeground = GetForegroundWindow();
            IBrush accent = Application.Current?.TryFindResource("WprAccentBrush", out object? found) == true && found is IBrush b
                ? b : Brushes.SteelBlue;
            HubOnline? hub = HubSetup.Current;
            var chosen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var w = new Window
            {
                Title = "Invite friends to " + gameName,
                Width = 460,
                Height = 600,
                CanResize = false,
                Topmost = true,
                ShowInTaskbar = false,
                Background = Brushes.Black,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
            };

            var root = new DockPanel { Margin = new Thickness(24, 20, 24, 16) };

            // Header: the game's tile beside "INVITE FRIENDS" / the game's name.
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new Thickness(0, 0, 0, 12) };
            if (GameArt(WprHostEnvironment.CurrentProductId) is { } art)
            {
                header.Children.Add(new Image { Source = art, Width = 64, Height = 64, Stretch = Stretch.UniformToFill });
            }
            var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titles.Children.Add(new TextBlock { Text = "INVITE FRIENDS", FontSize = 13, Foreground = accent, LetterSpacing = 1.2 });
            titles.Children.Add(new TextBlock
            {
                Text = gameName, FontSize = 30, FontWeight = FontWeight.Light, Foreground = Brushes.White,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 330,
            });
            header.Children.Add(titles);
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            // Buttons along the bottom.
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            bool canInvite = emptyMessage == null && friends.Count > 0;
            var invite = FlatButton("invite", accent);
            void Refresh()
            {
                invite.IsEnabled = chosen.Count > 0;
                invite.Opacity = chosen.Count > 0 ? 1 : 0.4;
                invite.Content = chosen.Count > 1 ? $"invite {chosen.Count}" : "invite";
            }
            if (canInvite)
            {
                invite.Click += (_, _) =>
                {
                    if (chosen.Count == 0) return;
                    tcs.TrySetResult(chosen.ToList());
                    w.Close();
                };
                buttons.Children.Add(invite);
                Refresh();
            }
            var cancel = FlatButton(canInvite ? "cancel" : "ok", canInvite ? Chrome : accent);
            cancel.Click += (_, _) => w.Close();
            buttons.Children.Add(cancel);
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);

            // The list, or why there is none.
            var list = new StackPanel();
            if (!canInvite)
            {
                list.Children.Add(new TextBlock
                {
                    Text = emptyMessage ?? "You have no WPR Hub friends yet.", FontSize = 18, FontWeight = FontWeight.Light,
                    Foreground = Subtle, TextWrapping = TextWrapping.Wrap,
                });
            }
            else
            {
                string? lastSection = null;
                foreach (InviteFriend friend in friends)
                {
                    string section = friend.Online ? "online" : "offline";
                    if (section != lastSection)
                    {
                        list.Children.Add(new TextBlock { Text = section, FontSize = 13, Foreground = Subtle, LetterSpacing = 1, Margin = new Thickness(0, lastSection == null ? 0 : 18, 0, 6) });
                        lastSection = section;
                    }
                    list.Children.Add(FriendRow(hub, friend, accent, chosen, Refresh));
                }
            }
            root.Children.Add(new ScrollViewer { Content = list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
            w.Content = root;

            w.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    w.Close();
                }
            };
            // Closed covers Esc, the title bar's close button, Alt+F4 and WPR shutting down.
            w.Closed += (_, _) =>
            {
                tcs.TrySetResult(null);
                if (previousForeground != IntPtr.Zero)
                {
                    try { SetForegroundWindow(previousForeground); } catch { /* best effort */ }
                }
            };

            w.Show();
            w.Activate();
            return tcs.Task;
        }

        private static Control FriendRow(HubOnline? hub, InviteFriend friend, IBrush accent, HashSet<string> chosen, Action changed)
        {
            var picture = new Image { Width = 56, Height = 56, Stretch = Stretch.UniformToFill };
            var pictureFrame = new Border { Width = 56, Height = 56, Background = Chrome, Child = picture };
            if (hub != null && !string.IsNullOrEmpty(friend.GamerpicUrl))
            {
                _ = LoadImageAsync(hub, picture, friend.GamerpicUrl!);
            }

            string line = friend.Playing is { Length: > 0 } playing ? "playing " + playing : friend.Online ? "online" : "offline";
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 8, 0) };
            text.Children.Add(new TextBlock { Text = friend.Username, FontSize = 22, FontWeight = FontWeight.Light, Foreground = Brushes.White });
            text.Children.Add(new TextBlock { Text = line, FontSize = 13, Foreground = friend.Online ? accent : Subtle, TextTrimming = TextTrimming.CharacterEllipsis });

            var tick = new Border { Width = 26, Height = 26, BorderThickness = new Thickness(2), VerticalAlignment = VerticalAlignment.Center };
            void Paint()
            {
                bool on = chosen.Contains(friend.Username);
                tick.Background = on ? accent : Brushes.Transparent;
                tick.BorderBrush = on ? accent : Brushes.White;
            }
            Paint();

            var row = new DockPanel { Margin = new Thickness(0, 6), Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand) };
            DockPanel.SetDock(pictureFrame, Dock.Left);
            DockPanel.SetDock(tick, Dock.Right);
            row.Children.Add(pictureFrame);
            row.Children.Add(tick);
            row.Children.Add(text);
            row.PointerPressed += (_, _) =>
            {
                if (!chosen.Remove(friend.Username)) chosen.Add(friend.Username);
                Paint();
                changed();
            };
            return row;
        }

        private static Button FlatButton(string label, IBrush background) => new Button
        {
            Content = label,
            MinWidth = 110,
            Padding = new Thickness(18, 8),
            Background = background,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };

        private static async Task LoadImageAsync(HubOnline hub, Image target, string url)
        {
            try
            {
                byte[]? bytes = await hub.Account.GetImageAsync(url);
                if (bytes == null) return;
                using var stream = new MemoryStream(bytes);
                target.Source = new Bitmap(stream);
            }
            catch (Exception)
            {
                // a missing picture leaves the grey square
            }
        }

        private static Bitmap? GameArt(string? productId)
        {
            if (string.IsNullOrEmpty(productId)) return null;
            try
            {
                string? relative = GameIconStore.Find(productId);
                string? full = string.IsNullOrWhiteSpace(relative) ? null : Configuration.Current!.DataPath(relative!);
                return full != null && File.Exists(full) ? new Bitmap(full) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
