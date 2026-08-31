using Microsoft.Xna.Framework.GamerServices;
using WPR.Platform.Android.Input;
using System.Linq;
using System.Windows;

namespace WPR.Platform.Android
{
    public static class ServicesSetup
    {
        public static void Start()
        {
            // Stage 5e: supply the GamerServices achievement store. WPR.Framework.Xna holds only
            // the IAchievementStore seam now, so without this registration a game reaches sign-in
            // and plays but reports no achievements. Registered once here, at launcher startup —
            // NOT per game — because XnaBackend.Clear() runs on each game teardown and would
            // otherwise leave the second game launched without a store.
            WPR.Xna.Rhi.XnaBackend.SetAchievements(new WPR.Database.Achievements.EfAchievementStore());

            // Supply this head's motion sensors — the device's real accelerometer. The WP7
            // Accelerometer shim in Microsoft.Devices.Sensors sees only ISensorProvider, so the
            // hardware code (and its Xamarin.Essentials dependency) stays in this head. Start()
            // runs in the launcher process AND again in GameActivity's :game process, which needs
            // its own registration because no static crosses that boundary.
            WPR.Sensors.SensorBackend.SetProvider(new AndroidSensorProvider());

            // Supply the install-time audio transcoder. WP7 XNA titles ship their soundtracks as
            // .wma and the song backend decodes Ogg Vorbis only, so ApplicationInstaller transcodes
            // at install time — through FFmpegKit's JNI entry point here, because FFMpegCore (the
            // Windows half) spawns an ffmpeg process and an APK has none to spawn. Without this
            // registration the install FAILS with ConvertFailed rather than silently producing a
            // mute game, which is what used to happen.
            WPR.Core.AudioTranscoderBackend.SetTranscoder(new Audio.FFmpegKitAudioTranscoder());

            // Replace FAudio's song player with Android's own MediaPlayer. FAudio's XNA_Song
            // decodes a full second of Vorbis per buffer with a queue depth of one, refilled from
            // OnBufferEnd — so once per second the voice is starved while the audio thread decodes,
            // which is audible on a phone as a click exactly once per second. A factory rather than
            // a direct XnaBackend.SetMedia call because that slot is composed per game launch by
            // FnaGameHost and cleared on teardown; see MediaBackendOverride.
            WPR.Backend.FNA.MediaBackendOverride.SetFactory(() => new Audio.AndroidMediaBackend());

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
