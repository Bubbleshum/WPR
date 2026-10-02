using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

using WPR.Online.Hub.Client;

namespace WPR.Platform.Windows.Views
{
    /// <summary>
    /// "How did it run?" after a game, now and again (the rules are <c>WPR.Shell.GameRatingPrompt</c>'s).
    /// One button per hub rating, best first, then "Don't ask about this game" and "Ask me later".
    /// <see cref="Chosen"/> is the rating key, or null when the player did not answer (postponed, or
    /// <see cref="Declined"/>). The Android twin is a list dialog in
    /// <c>GameLauncher.MaybeAskRating</c>.
    /// </summary>
    internal sealed class RateGameWindow : Window
    {
        public string? Chosen { get; private set; }

        public string? ChosenLabel { get; private set; }

        /// <summary>"Don't ask about this game". Closing the window any other way without a rating postpones it.</summary>
        public bool Declined { get; private set; }

        public RateGameWindow(string gameName)
        {
            Title = "How did it run?";
            Width = 460;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var panel = new StackPanel { Margin = new Thickness(24, 20), Spacing = 8 };
            panel.Children.Add(new TextBlock
            {
                Text = $"How did {gameName} run?",
                FontSize = 22,
                FontWeight = FontWeight.Light,
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(new TextBlock
            {
                Text = "Your rating goes on WPR Hub's compatibility page, so other players know what to expect. We only ask now and again.",
                FontSize = 12,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            });

            foreach (var (key, label, meaning) in CompatibilityRatings.All)
            {
                var text = new StackPanel { Spacing = 1 };
                text.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.SemiBold });
                text.Children.Add(new TextBlock { Text = meaning, FontSize = 11, Opacity = 0.75, TextWrapping = TextWrapping.Wrap });
                var button = new Button
                {
                    Content = text,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(12, 8),
                };
                string chosenKey = key, chosenLabel = label;
                button.Click += (_, _) => { Chosen = chosenKey; ChosenLabel = chosenLabel; Close(); };
                panel.Children.Add(button);
            }

            // "Ask me later" is also what closing the window does: a player who did not get a good feel
            // for the game yet is asked again the next time they finish it.
            var later = new Button { Content = "Ask me later" };
            later.Classes.Add("ghost");
            later.Click += (_, _) => Close();
            var decline = new Button { Content = "Don't ask about this game" };
            decline.Classes.Add("ghost");
            decline.Click += (_, _) => { Declined = true; Close(); };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            buttons.Children.Add(decline);
            buttons.Children.Add(later);
            panel.Children.Add(buttons);

            Content = panel;
        }
    }
}
