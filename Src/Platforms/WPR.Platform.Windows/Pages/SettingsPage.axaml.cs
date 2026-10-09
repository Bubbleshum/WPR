using WPR.Shell;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using WPR.Common;

using ReactiveUI;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Xna.Framework.GamerServices;
using MessageBox.Avalonia;

namespace WPR.Platform.Windows.Pages
{
    public partial class SettingsPage : UserControl
    {
        public SettingsPage()
        {
            InitializeComponent();

            WireHighlightColorPicker();
            WireDisplaySettings();
            WireOnlineSettings();

            TextBox pathTextBox = this.Get<TextBox>("dataStoragePathText");
            pathTextBox.Text = Configuration.Current.DataStorePath;

            Button pathChangeBtn = this.Get<Button>("dataStoragePathBrowse");
            pathChangeBtn.Click += async (obj, args) =>
            {
                string? resultFolder = await new OpenFolderDialog()
                {
                    Directory = Configuration.Current.DataStorePath
                }.ShowAsync(GetWindow());

                if (resultFolder != null)
                {
                    pathTextBox.Text = resultFolder;
                    Configuration.Current.DataStorePath = resultFolder;
                    Configuration.Current.Save();

                    var msgBox = MessageBoxManager.GetMessageBoxStandardWindow(
                        title: WPR.Shell.Resources.SuccessfullyChanged,
                        text: WPR.Shell.Resources.SuccessfullyChangedDataPathMsg,
                        icon: MessageBox.Avalonia.Enums.Icon.Success,
                        windowStartupLocation: WindowStartupLocation.CenterScreen);

                    await msgBox.ShowDialog(GetWindow());
                }
            };

            this.Get<Button>("restoreDefaultStoragePathBtn").Click += async (obj, args) =>
            {
                Configuration.Current.RestoreDefaultDataStoragePath();
                pathTextBox.Text = Configuration.Current.DataStorePath;

                Configuration.Current.Save();

                var msgBox = MessageBoxManager.GetMessageBoxStandardWindow(
                    title: WPR.Shell.Resources.SuccessfullyChanged,
                    text: WPR.Shell.Resources.SuccessfullyChangedDataPathMsg,
                    icon: MessageBox.Avalonia.Enums.Icon.Success,
                    windowStartupLocation: WindowStartupLocation.CenterScreen);

                await msgBox.ShowDialog(GetWindow());
            };

            TextBox libraryPathTextBox = this.Get<TextBox>("gameLibraryPathText");
            libraryPathTextBox.Text = Configuration.Current.GameLibraryPath ?? "";

            Button libraryPathBrowseBtn = this.Get<Button>("gameLibraryPathBrowse");
            libraryPathBrowseBtn.Click += async (obj, args) =>
            {
                string? resultFolder = await new OpenFolderDialog()
                {
                    Directory = Configuration.Current.GameLibraryPath ?? Configuration.Current.DataStorePath
                }.ShowAsync(GetWindow());

                if (resultFolder != null)
                {
                    libraryPathTextBox.Text = resultFolder;
                    Configuration.Current.GameLibraryPath = resultFolder;
                    Configuration.Current.Save();
                }
            };

            this.Get<Button>("clearGameLibraryPathBtn").Click += (obj, args) =>
            {
                libraryPathTextBox.Text = "";
                Configuration.Current.GameLibraryPath = null;
                Configuration.Current.Save();
            };
        }

        /// <summary>
        /// Fullscreen games. Read when a game's window is created, so it applies from the next
        /// launch; F11 / Alt+Enter in a game switches the running one and saves the same setting.
        /// </summary>
        private void WireDisplaySettings()
        {
            CheckBox fullscreen = this.Get<CheckBox>("gameFullscreenCheckBox");
            fullscreen.IsChecked = Configuration.Current.GameFullscreen;
            fullscreen.IsCheckedChanged += (_, _) =>
            {
                bool on = fullscreen.IsChecked == true;
                if (on == Configuration.Current.GameFullscreen) return;
                Configuration.Current.GameFullscreen = on;
                Configuration.Current.Save();
            };
        }

        /// <summary>
        /// The WPR Hub section. The hub is always the official one (its address is deliberately not
        /// editable). Server logging is read live by the hub module, so it applies immediately; switching server logging on also flushes anything a crash already queued
        /// (nothing is queued while it is off), and switching it off discards the queue.
        /// </summary>
        private void WireOnlineSettings()
        {
            CheckBox serverLogging = this.Get<CheckBox>("serverLoggingCheckBox");

            serverLogging.IsChecked = Configuration.Current.ServerLogging;

            serverLogging.IsCheckedChanged += (_, _) =>
            {
                bool on = serverLogging.IsChecked == true;
                if (on == Configuration.Current.ServerLogging) return;
                Configuration.Current.ServerLogging = on;
                Configuration.Current.Save();
                // Signed in, the hub goes by the account's setting, so keep it in step.
                _ = WPR.Shell.HubSetup.Current?.Account.SyncServerLoggingAsync();
                _ = WPR.Engine.Online.OnlineBackend.FlushAsync();
            };

            WireRatingCooldown();
            WireAccount();
        }

        /// <summary>How often "how did it run?" may be asked: the shared choices, saved on change.</summary>
        private void WireRatingCooldown()
        {
            ComboBox box = this.Get<ComboBox>("ratingCooldownComboBox");
            var choices = WPR.Shell.GameRatingPrompt.CooldownChoices;
            box.ItemsSource = choices.Select(c => char.ToUpperInvariant(c.Label[0]) + c.Label[1..]).ToList();

            int current = Configuration.Current.RatingPromptCooldownMinutes;
            int index = choices.Select(c => c.Minutes).ToList().IndexOf(current);
            box.SelectedIndex = index >= 0 ? index : choices.Select(c => c.Minutes).ToList().IndexOf(Configuration.DefaultRatingPromptCooldownMinutes);

            box.SelectionChanged += (_, _) =>
            {
                if (box.SelectedIndex < 0) return;
                int minutes = choices[box.SelectedIndex].Minutes;
                if (minutes == Configuration.Current.RatingPromptCooldownMinutes) return;
                Configuration.Current.RatingPromptCooldownMinutes = minutes;
                Configuration.Current.Save();
            };
        }

        /// <summary>
        /// The WPR Hub account row. Signing in happens in <see cref="Views.SignInWindow"/> and picking
        /// a picture in <see cref="Views.GamerpicWindow"/>; this only shows who is signed in.
        /// </summary>
        private void WireAccount()
        {
            Views.HubLogo.Apply(this.Get<Image>("hubLogoImage"), this.Get<TextBlock>("onlineHeaderText"));

            TextBlock name = this.Get<TextBlock>("accountNameText");
            TextBlock status = this.Get<TextBlock>("accountStatusText");
            Image picture = this.Get<Image>("accountGamerpicImage");
            Button account = this.Get<Button>("accountButton");
            Button gamerpic = this.Get<Button>("gamerpicButton");

            bool SignedIn() => !string.IsNullOrEmpty(Configuration.Current.HubAccessToken);

            async Task RefreshAsync()
            {
                bool signedIn = SignedIn();
                name.Text = signedIn ? Configuration.Current.HubUsername ?? "Signed in" : "Not signed in";
                status.Text = signedIn
                    ? "Signed in to WPR Hub. Your scores go to the leaderboards."
                    : "Sign in to put your scores on the leaderboards and choose a gamerpic.";
                account.Content = signedIn ? "Sign out" : "Sign in";
                gamerpic.IsVisible = signedIn;
                if (!signedIn) { picture.Source = null; return; }

                // Refreshing also catches a device signed out on the website.
                picture.Source = await Views.SignInWindow.LoadCurrentGamerpicAsync(WPR.Shell.HubSetup.Current);
                if (!SignedIn()) await RefreshAsync();
                else name.Text = Configuration.Current.HubUsername ?? name.Text;
            }

            async Task ShowGamerpicsAsync()
            {
                var window = new Views.GamerpicWindow(WPR.Shell.HubSetup.Current);
                await window.ShowDialog(GetWindow());
                if (window.Changed) await RefreshAsync();
            }

            _ = RefreshAsync();

            gamerpic.Click += async (_, _) => await ShowGamerpicsAsync();

            account.Click += async (_, _) =>
            {
                var hub = WPR.Shell.HubSetup.Current;
                if (SignedIn())
                {
                    account.IsEnabled = false;
                    if (hub != null) await hub.Account.SignOutAsync();
                    account.IsEnabled = true;
                    await RefreshAsync();
                    return;
                }

                var window = new Views.SignInWindow(hub);
                await window.ShowDialog(GetWindow());
                await RefreshAsync();
                if (window.WantsGamerpic) await ShowGamerpicsAsync();
            };
        }


        /// <summary>
        /// Populate the highlight-color combo with the WP7 accent palette,
        /// seed selection from <see cref="Configuration.AccentColor"/>, and on
        /// change persist the chosen hex back to configuration plus update the
        /// preview swatch. The actual phone theme reads this on next game launch
        /// (in <c>WPR.WindowsCompability.Application</c>'s ctor); we don't apply
        /// it live to running games because <c>PhoneTheme</c>-keyed brushes get
        /// captured into individual element <c>Style</c>s during XAML load.
        /// </summary>
        private void WireHighlightColorPicker()
        {
            var combo = this.Get<ComboBox>("highlightColorBox");
            var swatch = this.Get<Border>("highlightColorSwatch");
            combo.ItemsSource = WP7AccentColors.Presets;

            string? saved = Configuration.Current.AccentColor;
            WP7AccentColor initial = WP7AccentColors.Presets
                .FirstOrDefault(c => string.Equals(c.Hex, saved, StringComparison.OrdinalIgnoreCase))
                ?? WP7AccentColors.Default;
            combo.SelectedItem = initial;
            swatch.Background = initial.Brush;

            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is not WP7AccentColor pick) return;
                Configuration.Current.AccentColor = pick.Hex;
                Configuration.Current.Save();
                swatch.Background = pick.Brush;
            };
        }

        Window GetWindow() => VisualRoot as Window ?? throw new NullReferenceException("Invalid Owner");
    }
}
