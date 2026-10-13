using System;

namespace Microsoft.Phone.Net.NetworkInformation
{
    /// <summary>
    /// Shim for <c>Microsoft.Phone.Net.NetworkInformation.DeviceNetworkInformation</c>.
    /// </summary>
    /// <remarks>
    /// Reports a connected device. WPR runs on a desktop or a phone that is, in practice, online,
    /// and the alternative default is worse than optimistic: WP7 titles gate their leaderboard,
    /// social and "more games" paths on <see cref="IsNetworkAvailable"/> and a hard <c>false</c>
    /// would hide those permanently rather than let them try and fail. A request that then cannot
    /// reach anything surfaces as the ordinary network exception the game already handles — which
    /// is the common case anyway, since these titles' back ends are long dead.
    /// </remarks>
    public static class DeviceNetworkInformation
    {
        public static bool IsNetworkAvailable => true;

        /// <summary>Reported as Wi-Fi so a title does not treat traffic as metered cellular.</summary>
        public static bool IsWiFiEnabled => true;

        public static bool IsCellularDataEnabled => false;

        public static bool IsCellularDataRoamingEnabled => false;

        public static string? CellularMobileOperator => null;

        /// <summary>
        /// Never raised: nothing here can observe a change, and WP7 titles subscribe to it only
        /// to re-check <see cref="IsNetworkAvailable"/>, which never changes either.
        /// </summary>
        public static event EventHandler<NetworkNotificationEventArgs>? NetworkAvailabilityChanged;

        /// <summary>
        /// Resolves a host name. Always reports failure by invoking the callback with a null
        /// result — WP7's own contract for an unresolvable name, so callers already handle it.
        /// </summary>
        public static void ResolveHostNameAsync(object hostName, Action<object?> callback, object? state)
        {
            callback?.Invoke(null);
        }

        /// <summary>
        /// WP7's real signature. Answers at once, on the calling thread, with a result whose
        /// <see cref="NameResolutionResult.NetworkInterface"/> is the reported Wi-Fi adapter and
        /// no addresses — consistent with <see cref="IsNetworkAvailable"/>. Synchronous because
        /// the callers are connectivity probes that wait a few milliseconds for the answer ("What
        /// In The World?" waits 50 ms and then reads the result), and a real DNS round trip would
        /// miss that window and read as "offline".
        /// </summary>
        public static void ResolveHostNameAsync(System.Net.DnsEndPoint endPoint, NameResolutionCallback callback, object? context)
        {
            callback?.Invoke(new NameResolutionResult(
                endPoint, Array.Empty<System.Net.IPEndPoint>(), NetworkError.Success,
                NetworkInterfaceInfo.WiFi, context));
        }
    }

    /// <summary>
    /// Shim for <c>Microsoft.Phone.Net.NetworkInformation.NetworkNotificationEventArgs</c>.
    /// </summary>
    public class NetworkNotificationEventArgs : EventArgs
    {
        public NetworkNotificationType NotificationType { get; set; }
    }

    /// <summary>
    /// Shim for <c>Microsoft.Phone.Net.NetworkInformation.NetworkNotificationType</c>.
    /// </summary>
    public enum NetworkNotificationType
    {
        InterfaceConnected = 0,
        InterfaceDisconnected = 1,
        CharacteristicUpdate = 2,
    }
}
