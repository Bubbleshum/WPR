using System;
using System.IO;

using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace WPR.Platform.Windows.Views
{
    /// <summary>
    /// The WPR Hub logo, for every hub screen (sign-in, gamerpics, the hub page, the settings account row).
    /// </summary>
    /// <remarks>
    /// The desktop uses only the white version, <c>Images/white_wpr_hub.png</c> (Android also has the
    /// colour <c>Images/wprhub.png</c> for its black pages). This head copies it beside the exe as
    /// <c>Assets/wpr-hub-logo.png</c> when it exists (see the csproj). Loaded from
    /// disk rather than as an <c>avares://</c> resource so a checkout without the file still builds:
    /// the screens then show their plain "WPR HUB" text header instead.
    /// </remarks>
    internal static class HubLogo
    {
        private static Bitmap? _cached;
        private static bool _tried;

        public static Bitmap? Load()
        {
            if (_tried) return _cached;
            _tried = true;
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, "Assets", "wpr-hub-logo.png");
                if (File.Exists(path)) _cached = new Bitmap(path);
            }
            catch (Exception)
            {
                _cached = null;
            }
            return _cached;
        }

        /// <summary>Shows the logo in <paramref name="image"/>, hiding <paramref name="textFallback"/>; or the reverse when there is no logo.</summary>
        public static void Apply(Image image, Control? textFallback = null)
        {
            Bitmap? logo = Load();
            image.Source = logo;
            image.IsVisible = logo != null;
            if (textFallback != null) textFallback.IsVisible = logo == null;
        }
    }
}
