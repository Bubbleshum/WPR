using Microsoft.Xna.Framework.GamerServices;
using WPR.Platform.Windows.Input;
using WPR.Backend.Direct3D11;
using WPR.SilverlightCompability;
using System.Linq;
using System.Windows;

namespace WPR.Platform.Windows
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

            // Compose the Direct3D11 backend into the Silverlight framework. The launcher is the
            // only layer that knows which concrete backend exists (ADR §2) — the framework sees
            // ISurfaceRendererBackend and nothing else, which is what keeps Vortice out of it.
            //
            // Registering here rather than lazily is deliberate: DrawingSurfaceBackgroundGrid is
            // constructed by the XAML parser while a WP app loads, so the backend has to already
            // be in place. Start() runs from the launcher window's ctor, long before any app is
            // launched, and is idempotent (assignment, not accumulation) — MainWindowDesktop can
            // be reconstructed without leaking.
            SilverlightBackend.SurfaceRenderer = new Direct3D11SurfaceRendererBackend();

            // Supply this head's motion sensors. A desktop PC has none, so the provider reads
            // the keyboard emulator the Controls page binds — the WP7 Accelerometer shim in
            // Microsoft.Devices.Sensors sees only ISensorProvider and never learns which it got.
            // Registered once here, not per game, for the same reason as the achievement store:
            // nothing re-runs Start() between launches. The per-game state that DOES need
            // clearing is the subscriber list, which ApplicationLaunch drops through
            // ISensorProvider.ResetForNewLaunch.
            WPR.Sensors.SensorBackend.SetProvider(new WindowsSensorProvider());

            // Supply the install-time audio transcoder — FFMpegCore over the ffmpeg.exe this head
            // ships beside the executable. WP7 XNA titles ship .wma soundtracks and the song
            // backend decodes Ogg Vorbis only, so ApplicationInstaller transcodes at install time.
            // This is the behaviour desktop already had; it moved out of WPR.Loader and behind
            // IAudioTranscoder on 2026-08-31 so that Android could have a working half too.
            WPR.Core.AudioTranscoderBackend.SetTranscoder(new Audio.FFMpegCoreAudioTranscoder());

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
