using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

using WPR.Online.Hub;

namespace WPR.Platform.Windows.Views
{
    /// <summary>
    /// WPR Hub sign-in (and sign-up: the first sign-in makes the account): "Continue with GitHub /
    /// Microsoft / Google", then the link key, the hub's /link page opened at that provider, and a wait
    /// for the player to approve there. The flow itself is <see cref="HubAccountService"/>; this is
    /// only its face. Closing the window gives up, and the key then simply expires on the hub.
    /// </summary>
    public partial class SignInWindow : Window
    {
        private readonly HubOnline? _hub;
        private readonly CancellationTokenSource _cancel = new CancellationTokenSource();
        private readonly DispatcherTimer _countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private HubAccountService.SignIn? _signIn;
        private DateTimeOffset _expiresAt;

        /// <summary>True once the player signed in; the settings page refreshes on it.</summary>
        public bool SignedIn { get; private set; }

        /// <summary>True when the player asked to go straight on to the gamerpic picker.</summary>
        public bool WantsGamerpic { get; private set; }

        public SignInWindow() : this(null) { }

        public SignInWindow(HubOnline? hub)
        {
            _hub = hub;
            InitializeComponent();
            HubLogo.Apply(this.Get<Image>("logoImage"), this.Get<TextBlock>("eyebrowText"));

            this.Get<Button>("openButton").Click += (_, _) => OpenLink();
            this.Get<Button>("copyButton").Click += async (_, _) =>
            {
                if (_signIn == null || Clipboard == null) return;
                await Clipboard.SetTextAsync(_signIn.LinkKey);
                this.Get<Button>("copyButton").Content = "Copied";
            };
            this.Get<Button>("retryButton").Click += (_, _) => _ = RunAsync();
            this.Get<Button>("gamerpicButton").Click += (_, _) => { WantsGamerpic = true; Close(); };
            this.Get<Button>("closeButton").Click += (_, _) => Close();
            this.Get<Button>("otherDeviceButton").Click += (_, _) =>
            {
                if (_signIn == null) return;
                _provider = null;
                ShowWaiting();
            };

            _countdown.Tick += (_, _) => UpdateCountdown();
            Opened += (_, _) => _ = RunAsync();
            Closed += (_, _) =>
            {
                _countdown.Stop();
                _cancel.Cancel();
            };
        }

        private async Task RunAsync()
        {
            ShowSteps();
            if (_hub == null)
            {
                ShowFailed("WPR Hub is not available in this copy of WPR.");
                return;
            }

            try
            {
                _signIn = await _hub.Account.StartAsync(
                    WPR.Shell.HubSetup.ClientName($"Windows ({Environment.MachineName})"), _cancel.Token);
                if (_signIn == null)
                {
                    ShowFailed("Can't reach WPR Hub. Check your internet connection and try again.");
                    return;
                }

                this.Get<TextBlock>("keyText").Text = _signIn.LinkKey;
                this.Get<TextBlock>("linkPageText").Text = $"or go to {_signIn.LinkPage} on any device and type the key.";
                _expiresAt = DateTimeOffset.UtcNow + _signIn.ExpiresIn;
                _countdown.Start();
                UpdateCountdown();
                ShowProviders(_signIn);

                string? username = await _signIn.CompleteAsync(_cancel.Token);
                _countdown.Stop();

                if (username == null)
                {
                    ShowFailed("The sign-in was declined on the website, or the key expired before it was approved.");
                    return;
                }

                SignedIn = true;
                await ShowDoneAsync(username);
            }
            catch (OperationCanceledException)
            {
                // Window closed.
            }
            catch (Exception ex)
            {
                _countdown.Stop();
                WPR.Common.Log.Warn(WPR.Common.LogCategory.AppList, "[wpr-online] sign-in failed: " + ex);
                ShowFailed("Sign-in failed: " + ex.Message);
            }
        }

        /// <summary>The provider picked on the chooser, sent to the link page as a hint. Null: the plain page.</summary>
        private string? _provider;

        /// <summary>One "Continue with …" button per sign-in the hub offers; the first is the accented one.</summary>
        private void ShowProviders(HubAccountService.SignIn signIn)
        {
            StackPanel buttons = this.Get<StackPanel>("providerButtons");
            buttons.Children.Clear();
            bool first = true;
            foreach (var provider in signIn.Providers)
            {
                var button = new Button
                {
                    Content = "Continue with " + provider.Label,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                    Padding = new Avalonia.Thickness(16, 10),
                };
                button.Classes.Add(first ? "play" : "ghost");
                string key = provider.Key;
                button.Click += (_, _) =>
                {
                    _provider = key;
                    ShowWaiting();
                    OpenLink();
                };
                buttons.Children.Add(button);
                first = false;
            }
            this.Get<TextBlock>("chooserStatusText").IsVisible = false;
        }

        /// <summary>From the chooser to the key and the wait for approval.</summary>
        private void ShowWaiting()
        {
            this.Get<StackPanel>("chooserPanel").IsVisible = false;
            this.Get<StackPanel>("stepsPanel").IsVisible = true;
        }

        private void OpenLink()
        {
            if (_signIn == null) return;
            try
            {
                string url = _provider == null ? _signIn.LinkUrl : _signIn.LinkUrlFor(_provider);
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                this.Get<TextBlock>("statusText").Text = $"Could not open a browser ({ex.Message}). Go to {_signIn.LinkUrl} yourself.";
            }
        }

        private void UpdateCountdown()
        {
            TimeSpan left = _expiresAt - DateTimeOffset.UtcNow;
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            this.Get<TextBlock>("statusText").Text =
                $"Waiting for you to approve in the browser. The key expires in {(int)left.TotalMinutes}:{left.Seconds:00}.";
        }

        private void ShowSteps()
        {
            this.Get<StackPanel>("chooserPanel").IsVisible = true;
            this.Get<StackPanel>("providerButtons").Children.Clear();
            this.Get<TextBlock>("chooserStatusText").Text = "Getting ready…";
            this.Get<TextBlock>("chooserStatusText").IsVisible = true;
            this.Get<StackPanel>("stepsPanel").IsVisible = false;
            this.Get<Border>("donePanel").IsVisible = false;
            this.Get<Border>("failedPanel").IsVisible = false;
            this.Get<Button>("retryButton").IsVisible = false;
            this.Get<Button>("gamerpicButton").IsVisible = false;
            this.Get<Button>("closeButton").Content = "Cancel";
            this.Get<Button>("copyButton").Content = "Copy";
            this.Get<TextBlock>("keyText").Text = "····-····";
            this.Get<TextBlock>("statusText").Text = "Asking WPR Hub for a key…";
            this.Get<TextBlock>("titleText").Text = "Sign in";
        }

        private void ShowFailed(string message)
        {
            this.Get<StackPanel>("chooserPanel").IsVisible = false;
            this.Get<StackPanel>("stepsPanel").IsVisible = false;
            this.Get<Border>("failedPanel").IsVisible = true;
            this.Get<TextBlock>("failedText").Text = message;
            this.Get<Button>("retryButton").IsVisible = true;
            this.Get<Button>("closeButton").Content = "Close";
        }

        private async Task ShowDoneAsync(string username)
        {
            this.Get<StackPanel>("chooserPanel").IsVisible = false;
            this.Get<StackPanel>("stepsPanel").IsVisible = false;
            this.Get<Border>("donePanel").IsVisible = true;
            this.Get<TextBlock>("titleText").Text = "You're in";
            this.Get<TextBlock>("usernameText").Text = username;
            this.Get<Button>("gamerpicButton").IsVisible = true;
            this.Get<Button>("closeButton").Content = "Done";

            this.Get<Image>("gamerpicImage").Source = await LoadCurrentGamerpicAsync(_hub);
        }

        /// <summary>The account's gamerpic as the hub module saved it, or null.</summary>
        internal static async Task<Bitmap?> LoadCurrentGamerpicAsync(HubOnline? hub)
        {
            if (hub == null) return null;
            // RefreshAsync downloads the gamerpic to the module's folder as a side effect.
            var account = await hub.Account.RefreshAsync();
            byte[]? bytes = await hub.Account.GetImageAsync(account?.Gamerpic?.Url);
            if (bytes == null) return null;
            try { return new Bitmap(new MemoryStream(bytes)); }
            catch (Exception) { return null; }
        }
    }
}
