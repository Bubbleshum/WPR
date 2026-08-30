using Avalonia.Controls;

namespace WPR.Platform.Windows.Pages
{
    public partial class AboutPage : UserControl
    {
        public AboutPage()
        {
            InitializeComponent();

            // Single source: $(WprVersion) in Src/Directory.Build.props -> InformationalVersion ->
            // AppVersion. Previously hardcoded here and in the window title, which had drifted.
            VersionLabel.Content = $"{AppVersion.TitleText} :: DEVELOPER EDITION ::";
        }
    }
}
