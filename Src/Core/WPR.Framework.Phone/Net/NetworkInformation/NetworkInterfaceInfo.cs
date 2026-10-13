namespace Microsoft.Phone.Net.NetworkInformation
{
    /// <summary>Shim for <c>Microsoft.Phone.Net.NetworkInformation.NetworkInterfaceInfo</c>.</summary>
    /// <remarks>Describes the one interface WPR reports: a connected Wi-Fi adapter.</remarks>
    public sealed class NetworkInterfaceInfo
    {
        internal static readonly NetworkInterfaceInfo WiFi = new NetworkInterfaceInfo();

        private NetworkInterfaceInfo() { }

        public string InterfaceName => "Wi-Fi";
        public string Description => "Wi-Fi";
        public NetworkInterfaceType InterfaceType => NetworkInterfaceType.Wireless80211;
        public NetworkInterfaceSubType InterfaceSubtype => NetworkInterfaceSubType.WiFi;
        public NetworkInterfaceState InterfaceState => NetworkInterfaceState.Connected;
        public int Bandwidth => 54000;
        public NetworkCharacteristics Characteristics => NetworkCharacteristics.None;
    }

    /// <summary>Shim for <c>Microsoft.Phone.Net.NetworkInformation.NetworkInterfaceSubType</c>.</summary>
    public enum NetworkInterfaceSubType
    {
        Unknown = 0,
        Cellular_GPRS = 1,
        Cellular_1XRTT = 2,
        Cellular_EVDO = 3,
        Cellular_EDGE = 4,
        Cellular_3G = 5,
        Cellular_HSPA = 6,
        Cellular_EVDV = 7,
        Desktop_PassThru = 8,
        WiFi = 9,
        Cellular_LTE = 10,
        Cellular_EHRPD = 11,
    }

    /// <summary>Shim for <c>Microsoft.Phone.Net.NetworkInformation.NetworkInterfaceState</c>.</summary>
    public enum NetworkInterfaceState
    {
        Disconnected = 0,
        Connected = 1,
    }

    /// <summary>Shim for <c>Microsoft.Phone.Net.NetworkInformation.NetworkCharacteristics</c>.</summary>
    [System.Flags]
    public enum NetworkCharacteristics
    {
        None = 0,
        Roaming = 1,
    }

    /// <summary>Shim for <c>Microsoft.Phone.Net.NetworkInformation.NetworkError</c>.</summary>
    public enum NetworkError
    {
        Success = 0,
        WsaHostNotFound = 11001,
    }
}
