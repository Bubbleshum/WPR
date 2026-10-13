using System;
using System.Net;

namespace Microsoft.Phone.Net.NetworkInformation
{
    /// <summary>Shim for <c>Microsoft.Phone.Net.NetworkInformation.NameResolutionResult</c>.</summary>
    /// <remarks>
    /// What <see cref="DeviceNetworkInformation.ResolveHostNameAsync(DnsEndPoint, NameResolutionCallback, object)"/>
    /// hands back. WP7 titles read it almost exclusively as a connectivity probe — "What In The
    /// World?" resolves google.com and treats a null <see cref="NetworkInterface"/> as offline —
    /// so the interface is always filled, matching <see cref="DeviceNetworkInformation"/>'s
    /// optimistic "connected over Wi-Fi" answer.
    /// </remarks>
    public sealed class NameResolutionResult
    {
        internal NameResolutionResult(DnsEndPoint? hostName, IPEndPoint[] endPoints, NetworkError error,
            NetworkInterfaceInfo? networkInterface, object? asyncState)
        {
            HostName = hostName;
            IPEndPoints = endPoints;
            NetworkErrorCode = error;
            NetworkInterface = networkInterface;
            AsyncState = asyncState;
        }

        public DnsEndPoint? HostName { get; }
        public IPEndPoint[] IPEndPoints { get; }
        public NetworkError NetworkErrorCode { get; }
        public NetworkInterfaceInfo? NetworkInterface { get; }
        public object? AsyncState { get; }
    }
}
