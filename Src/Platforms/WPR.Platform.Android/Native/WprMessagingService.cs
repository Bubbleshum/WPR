using System;
using System.Collections.Generic;

using Android.App;
using Firebase.Messaging;

using WPR.Common;
using WPR.Engine.Notifications;
using WPR.Shell;

namespace WPR.Platform.Android.Native
{
    /// <summary>
    /// Receives WPR Hub's pushes (see <see cref="PushSetup"/>) and shows each as a notification.
    /// They are data messages, so this runs for every one, WPR open or closed, and the notification
    /// is built here: the per-kind switches in settings apply, and an open conversation gets none.
    /// </summary>
    [Service(Exported = false)]
    [IntentFilter(new[] { "com.google.firebase.MESSAGING_EVENT" })]
    public class WprMessagingService : FirebaseMessagingService
    {
        public override void OnCreate()
        {
            // A push that arrives with WPR closed starts the process for this service alone, so
            // Firebase has to be set up from the saved settings before the base class needs it.
            PushSetup.EnsureFirebase(this);
            base.OnCreate();
        }

        public override void OnNewToken(string token)
        {
            base.OnNewToken(token);
            _ = PushSetup.RegisterTokenAsync(this, token);
        }

        public override void OnMessageReceived(RemoteMessage message)
        {
            base.OnMessageReceived(message);
            try
            {
                // The rest of WPR may not be running: configuration (the switches) and the
                // notification manager first, as the launcher would.
                WprStartup.EnsureInitialized(this);
                if (NotificationBackend.Manager == null) ServicesSetup.Start();

                var data = new Dictionary<string, string>();
                foreach (var pair in message.Data) data[pair.Key] = pair.Value;
                bool shown = HubMessageNotifications.ShowPush(data);
                Log.Info(LogCategory.AppList, $"[wpr-push] {(data.TryGetValue("kind", out string? kind) ? kind : "?")} from "
                    + $"{(data.TryGetValue("sender", out string? from) ? from : "?")}: {(shown ? "shown" : "not shown")}");
            }
            catch (Exception e)
            {
                Log.Warn(LogCategory.AppList, "[wpr-push] could not show a push: " + e.Message);
            }
        }
    }
}
