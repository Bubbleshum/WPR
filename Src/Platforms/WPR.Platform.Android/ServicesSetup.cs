using Microsoft.Xna.Framework.GamerServices;
using WPR.Engine;
using WPR.Common;
using System.Linq;

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

            // XNA's Guide.BeginShowMessageBox: the game's own labels, and the index pressed back, or
            // -1 when the dialog was closed with Back or a tap outside (Guide answers null then, as XNA
            // documents). The same AlertDialog as Silverlight's MessageBox.Show, which completes on
            // every exit; the old MessageBox.Avalonia-typed dialog never completed on Back.
            Guide.ShowMessageBoxFunc = (title, description, buttonNames, focusButton, icon) =>
                MessageBoxUtils.ShowGameMessageBoxAsync(title, description, buttonNames.ToList());

            // Silverlight's blocking MessageBox.Show. The shim owns the WP7 button/result mapping
            // (WPR.WindowsCompability.MessageBox.Describe); this head only shows labelled buttons on
            // its UI thread and reports the index pressed, or -1 when the dialog was dismissed.
            WPR.WindowsCompability.MessageBox.Presenter = MessageBoxUtils.ShowGameMessageBoxAsync;
            WPR.Engine.Online.GameInvites.Picker = Native.InvitePicker.ShowAsync;
            // A game invite's notification shows the game's tile.
            // A device the hub pushes to gets its pop-ups from the push, not the heartbeat.
            WPR.Shell.HubMessageNotifications.PushHandlesThisDevice = () =>
                Native.PushSetup.Registered(global::Android.App.Application.Context);
            WPR.Shell.HubMessageNotifications.GameIconPath = titleId =>
                WPR.GameIconStore.Find(titleId) is { } relative ? WPR.Common.Configuration.Current?.DataPath(relative) : null;
        }
    }
}
