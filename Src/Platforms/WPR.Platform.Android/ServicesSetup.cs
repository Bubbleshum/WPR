using Microsoft.Xna.Framework.GamerServices;
using WPR.Engine;
using WPR.Common;
using System.Linq;
using System.Windows;

namespace WPR.Platform.Android
{
    public static class ServicesSetup
    {
        private static bool _Started;

        /// <summary>
        /// Runs <see cref="Start"/> unless this process has already composed the platform. For the
        /// launcher screens other than Start: Android can recreate the process straight into any
        /// of them (typically sign-in, after the browser it opened pushed WPR out of memory), and
        /// without this nothing was ever composed there, so the hub was missing ("WPR Hub is not
        /// available in this copy of WPR") along with the transcoder, notifications and vibration.
        /// A hub that failed to compose because configuration was not loaded yet is retried.
        /// </summary>
        public static void EnsureStarted()
        {
            if (_Started && WPR.Shell.HubSetup.Current != null) return;
            Start();
        }

        public static void Start()
        {
            _Started = true;

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
