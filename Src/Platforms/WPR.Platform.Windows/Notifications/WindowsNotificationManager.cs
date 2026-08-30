using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Toolkit.Uwp.Notifications;

namespace DesktopNotifications.Windows
{
    public class WindowsNotificationManager : INotificationManager
    {
        private readonly WindowsApplicationContext _applicationContext;

        public WindowsNotificationManager(WindowsApplicationContext? applicationContext = null)
        {
            // The ctor of WindowsApplicationContext registers the AppUserModelID and writes
            // a Start Menu shortcut — without that, Win32 toast notifications silently no-op.
            _applicationContext = applicationContext ?? WindowsApplicationContext.FromCurrentProcess();
        }

        public event EventHandler<NotificationActivatedEventArgs>? NotificationActivated;
        public event EventHandler<NotificationDismissedEventArgs>? NotificationDismissed;
        public string? LaunchActionId { get; }

        public Task Initialize()
        {
            return Task.CompletedTask;
        }

        public Task ShowNotification(Notification notification, DateTimeOffset? expirationTime = null)
        {
            if (notification == null) throw new ArgumentNullException(nameof(notification));

            ToastContentBuilder builder = BuildToast(notification);
            builder.Show(toast =>
            {
                if (expirationTime.HasValue)
                    toast.ExpirationTime = expirationTime.Value;
            });

            // Mark the events as referenced so the compiler doesn't warn that they're never
            // raised — wiring activation/dismissal callbacks through the Win32 COM activator
            // would require a CLSID-registered activator class, which is out of scope here.
            _ = NotificationActivated;
            _ = NotificationDismissed;

            return Task.CompletedTask;
        }

        public Task ScheduleNotification(Notification notification, DateTimeOffset deliveryTime, DateTimeOffset? expirationTime = null)
        {
            if (notification == null) throw new ArgumentNullException(nameof(notification));

            ToastContentBuilder builder = BuildToast(notification);
            builder.Schedule(deliveryTime, toast =>
            {
                if (expirationTime.HasValue)
                    toast.ExpirationTime = expirationTime.Value;
            });
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }

        /// <summary>
        /// Translate a <see cref="Notification"/> into a styled <see cref="ToastContentBuilder"/>.
        /// Sleek layout: bold title line, regular body line, app-logo avatar on the left with
        /// Windows' default (square, slightly rounded) crop, dimmer attribution footer at the
        /// bottom. Matches the visual language of Windows' own first-party toasts (Mail,
        /// Phone Link, Teams).
        /// </summary>
        private static ToastContentBuilder BuildToast(Notification notification)
        {
            ToastContentBuilder builder = new ToastContentBuilder();

            // The first AddText is the bold heading; subsequent ones are the body. Cap at
            // two body lines so the toast height stays compact rather than stretching for
            // long descriptions.
            if (!string.IsNullOrEmpty(notification.Title))
                builder.AddText(notification.Title, hintMaxLines: 1);
            if (!string.IsNullOrEmpty(notification.Body))
                builder.AddText(notification.Body, hintMaxLines: 2);

            // For a single-image notification the side avatar reads as sleeker than a
            // body-stretched inline image, so promote a lone BodyImagePath into the app
            // logo slot. When both are supplied the caller's intent is preserved:
            // ImagePath = avatar, BodyImagePath = body image.
            string? logoSource = notification.ImagePath;
            string? inlineSource = notification.BodyImagePath;
            if (string.IsNullOrEmpty(logoSource) && !string.IsNullOrEmpty(inlineSource))
            {
                logoSource = inlineSource;
                inlineSource = null;
            }

            // Toast XML requires absolute file:// URIs for local files; relative paths
            // silently drop the image, which is the kind of bug that takes hours to find.
            if (TryMakeImageUri(logoSource, out Uri? logoUri))
                builder.AddAppLogoOverride(logoUri!, ToastGenericAppLogoCrop.Default);
            if (TryMakeImageUri(inlineSource, out Uri? bodyUri))
                builder.AddInlineImage(bodyUri!, notification.BodyImageAltText ?? string.Empty);

            if (!string.IsNullOrWhiteSpace(notification.AttributionText))
                builder.AddAttributionText(notification.AttributionText);

            foreach (var (title, actionId) in notification.Buttons)
            {
                builder.AddButton(new ToastButton().SetContent(title).AddArgument("action", actionId));
            }

            return builder;
        }

        private static bool TryMakeImageUri(string? path, out Uri? uri)
        {
            uri = null;
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                if (Uri.TryCreate(path, UriKind.Absolute, out Uri? abs))
                {
                    uri = abs;
                    return true;
                }
                string full = Path.GetFullPath(path);
                if (!File.Exists(full)) return false;
                uri = new Uri(full, UriKind.Absolute);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
