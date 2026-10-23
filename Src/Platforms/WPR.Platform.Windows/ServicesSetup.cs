using Microsoft.Xna.Framework.GamerServices;
using WPR.Platform.Windows.Input;
using WPR.Engine;
using WPR.SilverlightCompability;
using System.Linq;

namespace WPR.Platform.Windows
{
    public static class ServicesSetup
    {
        public static void Start()
        {
            // Everything this platform HAS is declared in one place — see WindowsPlatform. The
            // composition root turns that into registry writes; this head no longer knows which
            // registries exist. Runs at launcher startup, not per game: the slots it fills are
            // launcher-lifetime, and XnaBackend.Clear() on teardown deliberately leaves them alone
            // (clearing would leave the SECOND game launched without achievements or tilt).
            PlatformComposition.Apply(new WindowsPlatform());

            Guide.ShowInputBoxFunc = (title, description, defaultText) =>
                MessageBoxUtils.GetInputResult(title, description, defaultText);

            // XNA's Guide.BeginShowMessageBox: the game's own labels, its focus button as the default,
            // and the index pressed back, or -1 when the box was closed (Guide answers null then, as
            // XNA documents for Back). The icon is not drawn.
            Guide.ShowMessageBoxFunc = (title, description, buttonNames, focusButton, icon) =>
                MessageBoxUtils.ShowGameMessageBoxAsync(title, description, buttonNames.ToList(), focusButton);

            // Silverlight's blocking MessageBox.Show. The shim owns the WP7 button/result mapping
            // (WPR.WindowsCompability.MessageBox.Describe); this head only shows labelled buttons on
            // its UI thread and reports the index pressed, or -1 when the dialog was dismissed.
            WPR.WindowsCompability.MessageBox.Presenter = MessageBoxUtils.ShowGameMessageBoxAsync;
            WPR.Engine.Online.GameInvites.Picker = InvitePicker.ShowAsync;
            // A game invite's notification shows the game's tile.
            WPR.Shell.HubMessageNotifications.GameIconPath = titleId =>
                WPR.GameIconStore.Find(titleId) is { } relative ? WPR.Common.Configuration.Current?.DataPath(relative) : null;
        }
    }
}
