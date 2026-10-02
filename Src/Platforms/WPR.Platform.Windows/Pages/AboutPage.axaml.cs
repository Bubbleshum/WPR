using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

using Avalonia;
using Avalonia.Controls;

using WPR.Common;

namespace WPR.Platform.Windows.Pages
{
    public partial class AboutPage : UserControl
    {
        private const string RepoUrl = "https://github.com/Bubbleshum/WPR";
        private const string CompatibilityUrl = "https://bubbleshum.github.io/WPR/";

        public AboutPage()
        {
            InitializeComponent();

            // Single source: $(WprVersion) in Src/Directory.Build.props -> InformationalVersion ->
            // AppVersion. Previously hardcoded here and in the window title, which had drifted.
            this.Get<TextBlock>("versionText").Text = AppVersion.TitleText;
            this.Get<TextBlock>("buildText").Text =
                $"DEVELOPER EDITION  ·  {RuntimeInformation.OSDescription}  ·  {RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}  ·  {RuntimeInformation.FrameworkDescription}";

            this.Get<Button>("githubButton").Click += (_, _) => Open(RepoUrl);
            this.Get<Button>("compatibilityButton").Click += (_, _) => Open(CompatibilityUrl);
            this.Get<Button>("issuesButton").Click += (_, _) => Open(RepoUrl + "/issues");
            this.Get<Button>("termsButton").Click += (_, _) => OpenHubPage("terms");
            this.Get<Button>("privacyButton").Click += (_, _) => OpenHubPage("privacy");
        }

        // The navigator keeps this page alive, so re-read the Hub address each time it is shown:
        // it can be changed in settings meanwhile.
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);

            Uri? hub = Configuration.Current?.HubUri;
            this.Get<TextBlock>("hubText").Text = hub == null
                ? "WPR Hub isn't available, so online features are off."
                : $"Accounts, friends, leaderboards and online play come from WPR Hub at {hub.Host}. Using them means agreeing to its terms.";
            this.Get<WrapPanel>("hubLinksPanel").IsVisible = hub != null;
            this.Get<TextBlock>("statusText").IsVisible = false;
        }

        /// <summary>The terms and privacy notice are the Hub's own pages, so a self-hosted Hub shows its own.</summary>
        private void OpenHubPage(string path)
        {
            Uri? hub = Configuration.Current?.HubUri;
            // Joined by hand: new Uri(hub, path) would drop the last segment of a Hub at https://host/wpr.
            if (hub != null) Open(hub.ToString().TrimEnd('/') + "/" + path);
        }

        private void Open(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                TextBlock status = this.Get<TextBlock>("statusText");
                status.Text = $"Could not open a browser ({ex.Message}). Go to {url} yourself.";
                status.IsVisible = true;
            }
        }
    }
}
