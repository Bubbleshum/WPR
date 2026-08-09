using System;
using System.Collections.Generic;
using System.IO;

namespace Microsoft.Phone.Notification
{
    /// <summary>
    /// Shim for <c>Microsoft.Phone.Notification.HttpNotificationChannel</c>.
    ///
    /// WPR runs offline with no MPNS/WNS push backend, so this is a no-op channel: it never
    /// obtains a <see cref="ChannelUri"/> and never raises any of its events. The point is
    /// purely to make the TYPE LOAD. WP7 games that use push notifications create the channel
    /// from a static initializer during startup — EA's Battleship does
    /// <c>PushNotificationManager..cctor -&gt; new HttpNotificationChannel(...)</c> from inside
    /// <c>TiltCanvas..ctor</c>. Before this shim existed the CLR threw a TypeLoadException on
    /// the missing type, which cascaded into a TypeInitializationException that unwound out of
    /// the canvas ctor, left the game's <c>AppMidlet.canvas</c> null, and turned every
    /// subsequent frame into an NRE — a large black screen that ran for a few seconds and then
    /// crashed. A no-op channel that loads (Find returns null, the ctor succeeds, the events
    /// never fire) is enough for single-player to boot; online multiplayer over push simply
    /// won't function. Shim-implementation only — rebuild, no reinstall.
    /// </summary>
    public sealed class HttpNotificationChannel : IDisposable
    {
        public HttpNotificationChannel(string channelName)
        {
            ChannelName = channelName;
        }

        public HttpNotificationChannel(string channelName, string serviceName)
        {
            ChannelName = channelName;
            ServiceName = serviceName;
        }

        /// <summary>Always null — WPR has no notification backend to allocate a URI from.</summary>
        public Uri? ChannelUri => null;

        public string? ChannelName { get; }

        public string? ServiceName { get; }

        // Part of the public API WP7 games subscribe to (`channel.X += handler`); the
        // subscriptions must bind at load time, but nothing here ever raises them.
        public event EventHandler<NotificationChannelUriEventArgs>? ChannelUriUpdated;
        public event EventHandler<NotificationChannelErrorEventArgs>? ErrorOccurred;
        public event EventHandler<HttpNotificationEventArgs>? HttpNotificationReceived;
        public event EventHandler<NotificationEventArgs>? ShellToastNotificationReceived;

        /// <summary>No channel is ever registered in WPR, so Find never returns one.</summary>
        public static HttpNotificationChannel? Find(string channelName) => null;

        public void Open() { }
        public void Close() { }
        public void BindToShellToast() { }
        public void BindToShellTile() { }
        public void UnbindToShellToast() { }
        public void UnbindToShellTile() { }
        public void Dispose() { }
    }

    /// <summary>Shim for <c>Microsoft.Phone.Notification.HttpNotification</c> (a raw push payload).</summary>
    public sealed class HttpNotification
    {
        public Stream? Body { get; internal set; }

        public string? Headers { get; internal set; }
    }

    /// <summary>Shim for <c>Microsoft.Phone.Notification.HttpNotificationEventArgs</c>.</summary>
    public class HttpNotificationEventArgs : EventArgs
    {
        public HttpNotification Notification { get; internal set; } = new HttpNotification();
    }

    /// <summary>Shim for <c>Microsoft.Phone.Notification.NotificationEventArgs</c> (toast payload).</summary>
    public class NotificationEventArgs : EventArgs
    {
        public IDictionary<string, string> Collection { get; internal set; } = new Dictionary<string, string>();
    }

    /// <summary>Shim for <c>Microsoft.Phone.Notification.NotificationChannelErrorEventArgs</c>.</summary>
    public class NotificationChannelErrorEventArgs : EventArgs
    {
        public int ErrorCode { get; internal set; }

        public string? Message { get; internal set; }
    }

    /// <summary>Shim for <c>Microsoft.Phone.Notification.NotificationChannelUriEventArgs</c>.</summary>
    public class NotificationChannelUriEventArgs : EventArgs
    {
        public Uri? ChannelUri { get; internal set; }
    }
}
