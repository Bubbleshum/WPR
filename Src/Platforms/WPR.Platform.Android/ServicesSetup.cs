using Microsoft.Xna.Framework.GamerServices;
using WPR.Engine;
using WPR.Common;
using System.Linq;
using System.Windows;

namespace WPR.Platform.Android
{
    public static class ServicesSetup
    {
        public static void Start()
        {
            // Everything this platform HAS is declared in one place — see AndroidPlatform, which
            // is meant to be read against the Windows head's WindowsPlatform. The composition root
            // turns that into registry writes; this head no longer knows which registries exist.
            //
            // Runs in the launcher process AND again in GameActivity's :game process — no static
            // crosses that boundary, so both need it. Applying twice is safe by construction: every
            // registry underneath is set-by-assignment, and the audio module stack de-duplicates by
            // module name.
            //
            // Application context, never an activity: what this builds outlives any one screen, and
            // holding an activity in the :game process would pin it for the whole game run.
            PlatformComposition.Apply(new AndroidPlatform(
                global::Android.App.Application.Context,
                Configuration.Current?.DataStorePath));

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
