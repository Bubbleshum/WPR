using System;
using System.Threading.Tasks;

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

using AndroidX.Core.App;

using WPR.Shell;

namespace WPR.Platform.Android.Native
{
    /// <summary>
    /// Tells the player when a newer WPR is out: a notification the first time a release is seen,
    /// and the update row on the About page from then on. The rules (how often, which release, once
    /// per release) are all <see cref="AppUpdates"/>'; this is only the Android half.
    /// </summary>
    /// <remarks>
    /// Checked from the Start screen's resume, which is cheap because <see cref="AppUpdates"/> only
    /// asks GitHub every twelve hours. The notification opens <see cref="AboutActivity"/>, whose
    /// update row downloads the APK in the browser; installing it is then the system's own
    /// "install over the top", which is how every release has been installed so far. Without the
    /// POST_NOTIFICATIONS grant the notification is silently dropped and the About row still works.
    /// </remarks>
    internal static class UpdateNotifier
    {
        private const string ChannelId = "wpr_updates";
        private const int NotificationId = 0x57505255; // "WPRU", clear of the achievement ids

        /// <summary>This install's version, as the package manager reports it (e.g. <c>0.1.06</c>).</summary>
        public static string InstalledVersion(Context context)
        {
            try
            {
                PackageInfo? info = context.PackageManager?.GetPackageInfo(context.PackageName!, 0);
                if (!string.IsNullOrEmpty(info?.VersionName)) return info!.VersionName!;
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("WPR", $"Could not read package version: {ex.Message}");
            }
            return "unknown";
        }

        /// <summary>Checks in the background, and notifies if a release not yet announced is newer.</summary>
        public static void CheckInBackground(Context context)
        {
            Context app = context.ApplicationContext ?? context;
            string current = InstalledVersion(app);
            _ = Task.Run(async () =>
            {
                try
                {
                    AppRelease? update = await AppUpdates.CheckAsync(current).ConfigureAwait(false);
                    if (update != null && AppUpdates.ShouldNotify(update)) Notify(app, update, current);
                }
                catch (Exception ex)
                {
                    global::Android.Util.Log.Warn("WPR", "update check failed: " + ex.Message);
                }
            });
        }

        private static void Notify(Context context, AppRelease update, string current)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                NotificationChannel channel = new NotificationChannel(ChannelId, "WPR updates", NotificationImportance.Default)
                {
                    Description = "when a new version of WPR is available",
                };
                ((NotificationManager)context.GetSystemService(Context.NotificationService)!).CreateNotificationChannel(channel);
            }

            Intent open = new Intent(context, typeof(AboutActivity));
            open.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
            PendingIntent? tap = PendingIntent.GetActivity(context, 0, open,
                PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

            Notification notification = new NotificationCompat.Builder(context, ChannelId)
                .SetSmallIcon(Resource.Drawable.ic_stat_wpr)
                .SetContentTitle($"WPR {update.Version} is available")
                .SetContentText($"you have {current}. tap to see what's new and download it.")
                .SetContentIntent(tap)
                .SetAutoCancel(true)
                .SetPriority(NotificationCompat.PriorityDefault)
                .Build()!;

            try
            {
                NotificationManagerCompat.From(context).Notify(NotificationId, notification);
            }
            catch (Exception ex)
            {
                // SecurityException without the permission on some OEM builds; the About row remains.
                global::Android.Util.Log.Warn("WPR", "update notification failed: " + ex.Message);
            }
        }
    }
}
