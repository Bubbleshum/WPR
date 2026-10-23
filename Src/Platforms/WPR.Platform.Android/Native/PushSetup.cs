using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

using Android.Content;
using Android.Gms.Tasks;
using Firebase;
using Firebase.Messaging;

using WPR.Common;
using WPR.Online.Hub;
using WPR.Online.Hub.Client;
using WPR.Shell;

namespace WPR.Platform.Android.Native
{
    /// <summary>
    /// WPR Hub push notifications through Firebase. Signed in, the launcher asks the hub for the
    /// Firebase client settings, starts Firebase with them, and registers the token Firebase gives
    /// the device; from then on the hub pushes new messages, game invites and friend requests, and
    /// <see cref="WprMessagingService"/> shows them, WPR closed or not.
    /// </summary>
    /// <remarks>
    /// <para>No google-services.json is built in. The settings are saved in the app's private
    /// files (<c>push-config.json</c>), because a push that arrives with WPR closed starts the
    /// process with nothing else running, and Firebase must be set up before it can deliver it.</para>
    ///
    /// <para><c>push-registered</c> beside it holds the token the hub has. While it exists the
    /// heartbeat's own alerts stand down on this device (<see cref="HubMessageNotifications.PushHandlesThisDevice"/>),
    /// so nothing pops twice. Signing out deletes the Firebase token, which the hub then drops the
    /// next time it tries it, so the old account's messages stop.</para>
    /// </remarks>
    internal static class PushSetup
    {
        private static bool _started;

        private static string ConfigFile(Context context) => Path.Combine(context.FilesDir!.AbsolutePath, "push-config.json");

        private static string RegisteredFile(Context context) => Path.Combine(context.FilesDir!.AbsolutePath, "push-registered");

        /// <summary>The hub pushes to this device (for <see cref="HubMessageNotifications.PushHandlesThisDevice"/>).</summary>
        public static bool Registered(Context context) => File.Exists(RegisteredFile(context));

        /// <summary>Starts Firebase from the saved settings if it isn't running. False when there are none yet.</summary>
        public static bool EnsureFirebase(Context context)
        {
            try
            {
                if (FirebaseApp.GetApps(context).Count > 0) return true;
                string file = ConfigFile(context);
                if (!File.Exists(file)) return false;
                AndroidPushConfig? config = JsonSerializer.Deserialize<AndroidPushConfig>(File.ReadAllText(file));
                if (config == null) return false;
                var options = new FirebaseOptions.Builder()
                    .SetApiKey(config.ApiKey)
                    .SetApplicationId(config.AppId)
                    .SetProjectId(config.ProjectId);
                if (!string.IsNullOrEmpty(config.SenderId)) options.SetGcmSenderId(config.SenderId);
                FirebaseApp.InitializeApp(context, options.Build());
                return true;
            }
            catch (Exception e)
            {
                Log.Warn(LogCategory.AppList, "[wpr-push] Firebase did not start: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// Once per launcher process, signed in: fetch the settings, start Firebase, register the
        /// token. Signed out: forget the device. Never throws.
        /// </summary>
        public static async System.Threading.Tasks.Task StartAsync(Context context)
        {
            bool signedIn = !string.IsNullOrEmpty(Configuration.Current?.HubAccessToken);
            if (!signedIn)
            {
                if (Registered(context)) SignedOut(context);
                return;
            }
            if (_started) return;
            _started = true;
            try
            {
                HubOnline? hub = HubSetup.Current;
                AndroidPushConfig? config = hub == null ? null : await hub.Push.GetAndroidConfigAsync();
                if (config == null)
                {
                    Log.Info(LogCategory.AppList, "[wpr-push] the hub has no push set up; notifications come from the heartbeat while WPR runs");
                    _started = false; // ask again next time the Start screen shows
                    return;
                }
                string json = JsonSerializer.Serialize(config);
                string file = ConfigFile(context);
                if (!File.Exists(file) || File.ReadAllText(file) != json) File.WriteAllText(file, json);
                if (!EnsureFirebase(context)) return;

                FirebaseMessaging.Instance.GetToken().AddOnCompleteListener(new CompleteListener(task =>
                {
                    if (task.IsSuccessful && task.Result?.ToString() is { Length: > 0 } token)
                    {
                        _ = RegisterTokenAsync(context, token);
                    }
                    else
                    {
                        Log.Warn(LogCategory.AppList, "[wpr-push] no Firebase token: " + task.Exception?.Message);
                    }
                }));
            }
            catch (Exception e)
            {
                _started = false;
                Log.Warn(LogCategory.AppList, "[wpr-push] setup failed: " + e.Message);
            }
        }

        /// <summary>Hands the hub this device's token (a new one, or the current one again).</summary>
        public static async System.Threading.Tasks.Task RegisterTokenAsync(Context context, string token)
        {
            HubOnline? hub = HubSetup.Current;
            if (hub == null || string.IsNullOrEmpty(Configuration.Current?.HubAccessToken)) return;
            if (await hub.Push.RegisterAsync(token, "android"))
            {
                File.WriteAllText(RegisteredFile(context), token);
            }
        }

        private static void SignedOut(Context context)
        {
            try
            {
                File.Delete(RegisteredFile(context));
                if (EnsureFirebase(context)) FirebaseMessaging.Instance.DeleteToken();
                Log.Info(LogCategory.AppList, "[wpr-push] signed out: this device's push token deleted");
            }
            catch (Exception e)
            {
                Log.Warn(LogCategory.AppList, "[wpr-push] could not delete the push token: " + e.Message);
            }
            _started = false;
        }

        private sealed class CompleteListener : Java.Lang.Object, IOnCompleteListener
        {
            private readonly Action<global::Android.Gms.Tasks.Task> _done;
            public CompleteListener(Action<global::Android.Gms.Tasks.Task> done) => _done = done;
            public void OnComplete(global::Android.Gms.Tasks.Task task) => _done(task);
        }
    }
}
