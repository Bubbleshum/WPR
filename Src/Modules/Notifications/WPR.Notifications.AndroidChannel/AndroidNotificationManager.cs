#if __ANDROID__
using WPR.Engine.Notifications;
/* `Notification` is ambiguous in this file: Android.App has one too, and the old
 * `namespace DesktopNotifications.Android` used to disambiguate it by accident — the enclosing
 * namespace won. With the namespaces sorted onto WPR.Notifications.AndroidChannel that accident
 * is gone, so say which one explicitly. Android's is still reachable as Android.App.Notification. */
using Notification = WPR.Engine.Notifications.Notification;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Android.App;
using Android.Content;

using AndroidX.Core.App;

using Android.Graphics;
using Android.Media;
using Android.Net;
using Android.OS;
using Android.Provider;

namespace WPR.Notifications.AndroidChannel
{
    public class AndroidNotificationManager : INotificationManager
    {
        /// <summary>
        /// Null — this head never launches from a notification action, so there is no id to
        /// report. Deliberately not a <c>NotImplementedException</c>: the property is part of the
        /// <see cref="INotificationManager"/> surface and a host that probes it would take the
        /// process down for a feature that simply isn't offered.
        /// </summary>
        public string? LaunchActionId => null;

        public event EventHandler<NotificationActivatedEventArgs>? NotificationActivated;
        public event EventHandler<NotificationDismissedEventArgs>? NotificationDismissed;

        private NotificationManagerCompat _NofManagerCompat;

        private int _NofId = 0;
        private int _NofChannelCounter = 0;

        /* NOT renamed with the namespace (2026-09-01). This is the channel's user-visible name and
         * it pairs with the "DesktopNotificationChannelV10_*" channel IDs below, which Android
         * persists per install along with whatever importance/sound the user has set on them.
         * Changing either string orphans the existing channels and silently drops those settings. */
        private static string _ChannelName = "DesktopNotifications";

        private Context _Context;

        /// <summary>
        /// The status-bar glyph, as a drawable resource id from the HOST app.
        ///
        /// <para>Passed in rather than referenced directly: it is app branding, and this module
        /// has no business naming another assembly's generated <c>Resource.Drawable</c>. It used
        /// to be <c>WPR.Platform.Android.Resource.Drawable.ic_stat_wpr</c>, which is exactly the
        /// head dependency that stopped this being a module.</para>
        ///
        /// <para>Android keeps only this drawable's ALPHA channel and repaints every visible pixel
        /// in the system tint, so it must be a white-on-transparent glyph. A full-colour bitmap
        /// becomes a solid white blob.</para>
        /// </summary>
        private readonly int _SmallIconResourceId;
        private Dictionary<string, string> _NofChannels;

        /// <param name="smallIconResourceId">Host drawable for the status bar - see
        /// <see cref="_SmallIconResourceId"/>.</param>
        public AndroidNotificationManager(Context context, int smallIconResourceId)
        {
            _NofChannels = new Dictionary<string, string>();

            _Context = context;
            _SmallIconResourceId = smallIconResourceId;
            _NofManagerCompat = NotificationManagerCompat.From(_Context);
        }

        private string GetNotificationChannelForAudio(string audioPath)
        {
            string audioPathNormalized = audioPath;

            if (audioPathNormalized != null)
            {
                if (global::Android.Net.Uri.Parse(audioPathNormalized) == null)
                {
                    audioPathNormalized = "";
                }
            }
            
            if (_NofChannels.ContainsKey(audioPathNormalized!))
            {
                return _NofChannels[audioPathNormalized!];
            }

            var _NofChannel = new NotificationChannel($"DesktopNotificationChannelV10_{_NofChannelCounter++}",
                _ChannelName, NotificationImportance.Max);

            _NofChannel.Description = _ChannelName;

            if (audioPathNormalized != "")
            {
                AudioAttributes att = new AudioAttributes.Builder()!
                        .SetUsage(AudioUsageKind.Notification)!
                        .SetContentType(AudioContentType.Speech)!
                        .Build()!;

                _NofChannel.SetSound(global::Android.Net.Uri.Parse(audioPathNormalized), att);
            }

            _NofManagerCompat.CreateNotificationChannel(_NofChannel);
            _NofChannels.Add(audioPathNormalized!, _NofChannel.Id!);

            return _NofChannel.Id!;
        }

        public void Dispose()
        {
            foreach (var channel in _NofChannels.Values)
            {
                _NofManagerCompat.DeleteNotificationChannel(channel);
            }
        }

        public Task Initialize()
        {
            return Task.CompletedTask;
        }

        public Task ScheduleNotification(Notification notification, DateTimeOffset deliveryTime, DateTimeOffset? expirationTime = null)
        {
            throw new NotImplementedException();
        }

        public Task ShowNotification(Notification notification, DateTimeOffset? expirationTime = null)
        {
            global::Android.Net.Uri? uriSound = RingtoneManager.GetActualDefaultRingtoneUri(_Context, RingtoneType.Notification);
            string? pathComplete = (uriSound != null) ? uriSound.Path : null;
            if (notification.SoundUri != null)
            {
                pathComplete = $"android.resource://{_Context.ApplicationInfo!.PackageName}/raw/{notification.SoundUri.ToLower()}";
            }

            NotificationCompat.Builder builder = (Build.VERSION.SdkInt >= BuildVersionCodes.O) ?
                new NotificationCompat.Builder(_Context, GetNotificationChannelForAudio(pathComplete ?? "")) :
                new NotificationCompat.Builder(_Context);

            builder.SetContentTitle(notification.Title)
                .SetContentText(notification.Body)
                .SetDefaults(NotificationCompat.DefaultVibrate)
                .SetPriority(NotificationCompat.PriorityMax);

            // Alpha-only glyph supplied by the host — see _SmallIconResourceId for why it is not
            // referenced directly, and why a full-colour bitmap here becomes a solid white blob.
            builder.SetSmallIcon(_SmallIconResourceId);

            // The achievement art belongs in the large icon, which is a real colour bitmap.
            if (notification.ImagePath != null)
            {
                Bitmap? icon = BitmapFactory.DecodeFile(notification.ImagePath);
                if (icon != null)
                {
                    builder.SetLargeIcon(icon);
                }
            }

            if (expirationTime != null)
            {
                long duration = (long)(expirationTime - DateTime.Now)!.Value.TotalMilliseconds;
                builder.SetTimeoutAfter(duration);
            }

            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                if (pathComplete != null)
                {
                    builder.SetSound(global::Android.Net.Uri.Parse(pathComplete));
                }
            }

            _NofManagerCompat.Notify(_NofId++, builder.Build());
            return Task.CompletedTask;
        }
    }
}
#endif