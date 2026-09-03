using Microsoft.Xna.Framework.GamerServices;
using WPR.Platform.Windows.Input;
using WPR.Backend.Direct3D11;
using WPR.Engine;
using WPR.SilverlightCompability;
using System.Linq;
using System.Windows;

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

            // Not a capability: this is the Silverlight framework's renderer, and the launcher is
            // the only layer that knows a concrete backend exists (ADR §2) — the framework sees
            // ISurfaceRendererBackend and nothing else, which is what keeps Vortice out of it.
            // Registered eagerly because DrawingSurfaceBackgroundGrid is constructed by the XAML
            // parser while a WP app loads, so the backend must already be in place. Idempotent
            // (assignment, not accumulation), so reconstructing MainWindowDesktop does not leak.
            SilverlightBackend.SurfaceRenderer = new Direct3D11SurfaceRendererBackend();

            Guide.ShowInputBoxFunc = async (title, description, defaultText) =>
            {
                return await MessageBoxUtils.GetInputResult(title, description, defaultText, false, true);
            };

            Guide.ShowMessageBoxFunc = async (title, description, buttonNames, currentActiveButton, icon) =>
            {
                MessageBox.Avalonia.Enums.Icon messageBoxIcon = MessageBox.Avalonia.Enums.Icon.None;
                switch (icon)
                {
                    case MessageBoxIcon.Error:
                        messageBoxIcon = MessageBox.Avalonia.Enums.Icon.Error;
                        break;

                    case MessageBoxIcon.Alert:
                    case MessageBoxIcon.Warning:
                        messageBoxIcon = MessageBox.Avalonia.Enums.Icon.Warning;
                        break;

                    default:
                        break;
                }

                var result = await MessageBoxUtils.GetMessageDialogResult(title, description, (buttonNames.Count() <= 1) 
                        ? MessageBox.Avalonia.Enums.ButtonEnum.Ok
                        : MessageBox.Avalonia.Enums.ButtonEnum.YesNo, 
                    messageBoxIcon, buttonNames, false, true);

                if (result == MessageBox.Avalonia.Enums.ButtonResult.None)
                {
                    return currentActiveButton;
                }

                return (result == MessageBox.Avalonia.Enums.ButtonResult.Ok) ? 0 :
                    (result == MessageBox.Avalonia.Enums.ButtonResult.Yes) ? 1 : 0;
            };

            System.Windows.MessageBox.ShowSimpleImpl = async (title, caption, button) =>
            {
                MessageBox.Avalonia.Enums.ButtonEnum buttonImpl = MessageBox.Avalonia.Enums.ButtonEnum.Ok;
                switch (button)
                {
                    //TODO: implement other buttons
                    /*case System.Windows.MessageBoxButton.OK:
                        buttonImpl = MessageBox.Avalonia.Enums.ButtonEnum.Ok;
                        break;

                    case System.Windows.MessageBoxButton.OKCancel:
                        buttonImpl = MessageBox.Avalonia.Enums.ButtonEnum.OkCancel;
                        break;

                    case System.Windows.MessageBoxButton.YesNoCancel:
                        buttonImpl = MessageBox.Avalonia.Enums.ButtonEnum.YesNoCancel;
                        break;

                    case System.Windows.MessageBoxButton.YesNo:
                        buttonImpl = MessageBox.Avalonia.Enums.ButtonEnum.YesNo;
                        break;*/

                    default:
                        break;
                }

                var result = await MessageBoxUtils.GetMessageDialogResult(title, caption, buttonImpl,
                    modalOnWindow: false, dispatchMain : true);

                switch (result)
                {
                    /*case MessageBox.Avalonia.Enums.ButtonResult.Ok:
                        return System.Windows.MessageBoxResult.OK;

                    case MessageBox.Avalonia.Enums.ButtonResult.Yes:
                        return System.Windows.MessageBoxResult.Yes;

                    case MessageBox.Avalonia.Enums.ButtonResult.No:
                        return System.Windows.MessageBoxResult.No;

                    case MessageBox.Avalonia.Enums.ButtonResult.Cancel:
                        return System.Windows.MessageBoxResult.Cancel;
                    */
                    default:
                        return default;//System.Windows.MessageBoxResult.None;
                }
            };
        }
    }
}
