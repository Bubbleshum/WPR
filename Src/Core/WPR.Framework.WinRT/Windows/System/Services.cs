using System;
using global::System.Collections.Generic;
using global::System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Foundation;
using WPR.WinRT;

namespace Windows.Networking.Connectivity
{
    /// <summary>Shim for <c>Windows.Networking.Connectivity.NetworkCostType</c>.</summary>
    public enum NetworkCostType
    {
        Unknown = 0,
        Unrestricted = 1,
        Fixed = 2,
        Variable = 3,
    }

    /// <summary>Shim for <c>Windows.Networking.Connectivity.NetworkConnectivityLevel</c>.</summary>
    public enum NetworkConnectivityLevel
    {
        None = 0,
        LocalAccess = 1,
        ConstrainedInternetAccess = 2,
        InternetAccess = 3,
    }

    /// <summary>Shim for <c>Windows.Networking.Connectivity.NetworkStatusChangedEventHandler</c>.</summary>
    public delegate void NetworkStatusChangedEventHandler(object sender);

    /// <summary>Shim for <c>Windows.Networking.Connectivity.ConnectionCost</c>: an unmetered connection.</summary>
    public sealed class ConnectionCost
    {
        internal ConnectionCost()
        {
        }

        public NetworkCostType NetworkCostType => NetworkCostType.Unrestricted;

        public bool Roaming => false;

        public bool OverDataLimit => false;

        public bool ApproachingDataLimit => false;
    }

    /// <summary>Shim for <c>Windows.Networking.Connectivity.ConnectionProfile</c>: the host's connection.</summary>
    public sealed class ConnectionProfile
    {
        internal ConnectionProfile()
        {
        }

        public string ProfileName => "WPR";

        public ConnectionCost GetConnectionCost() => new();

        public NetworkConnectivityLevel GetNetworkConnectivityLevel() =>
            global::System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable() ? NetworkConnectivityLevel.InternetAccess : NetworkConnectivityLevel.None;
    }

    /// <summary>
    /// Shim for <c>Windows.Networking.Connectivity.NetworkInformation</c>. The status-changed
    /// event is kept but never raised.
    /// </summary>
    public static class NetworkInformation
    {
        private static EventRegistrationTokenTable<NetworkStatusChangedEventHandler>? _changed;

        /// <summary>The host's connection, or null when it has none (what a phone with no network answered).</summary>
        public static ConnectionProfile? GetInternetConnectionProfile() =>
            global::System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable() ? new ConnectionProfile() : null;

        public static EventRegistrationToken add_NetworkStatusChanged(NetworkStatusChangedEventHandler handler) =>
            EventRegistrationTokenTable<NetworkStatusChangedEventHandler>.GetOrCreateEventRegistrationTokenTable(ref _changed).AddEventHandler(handler);

        public static void remove_NetworkStatusChanged(EventRegistrationToken token) => _changed?.RemoveEventHandler(token);
    }
}

namespace Windows.System
{
    /// <summary>Shim for <c>Windows.System.Launcher</c>: links go to the platform's opener (<see cref="WinRtHost.OpenUri"/>).</summary>
    public static class Launcher
    {
        public static IAsyncOperation<bool> LaunchUriAsync(Uri uri)
        {
            bool opened = false;
            try { opened = uri != null && WinRtHost.OpenUri?.Invoke(uri) == true; }
            catch (Exception) { }

            return WinRtAsync.FromResult(opened);
        }
    }
}

namespace Windows.Security.Authentication.OnlineId
{
    /// <summary>Shim for <c>Windows.Security.Authentication.OnlineId.OnlineIdServiceTicketRequest</c>.</summary>
    public sealed class OnlineIdServiceTicketRequest
    {
        public OnlineIdServiceTicketRequest(string service)
            : this(service, string.Empty)
        {
        }

        public OnlineIdServiceTicketRequest(string service, string policy)
        {
            Service = service;
            Policy = policy;
        }

        public string Service { get; }

        public string Policy { get; }
    }

    /// <summary>Shim for <c>Windows.Security.Authentication.OnlineId.OnlineIdServiceTicket</c>.</summary>
    public sealed class OnlineIdServiceTicket
    {
        internal OnlineIdServiceTicket()
        {
        }

        public string Value => string.Empty;

        public int ErrorCode => 0;

        public OnlineIdServiceTicketRequest? Request => null;
    }

    /// <summary>Shim for <c>Windows.Security.Authentication.OnlineId.UserIdentity</c>.</summary>
    public sealed class UserIdentity
    {
        internal UserIdentity()
        {
        }

        public IReadOnlyList<OnlineIdServiceTicket> Tickets => Array.Empty<OnlineIdServiceTicket>();

        public string Id => string.Empty;

        public string SafeCustomerId => string.Empty;

        public string SignInName => string.Empty;

        public string FirstName => string.Empty;

        public string LastName => string.Empty;

        public bool IsBetaAccount => false;

        public bool IsConfirmedPC => false;
    }

    /// <summary>
    /// Shim for <c>Windows.Security.Authentication.OnlineId.UserAuthenticationOperation</c>.
    /// WPR has no Microsoft account: every request fails, as an unreachable service did.
    /// </summary>
    public sealed class UserAuthenticationOperation : TaskAsyncInfo, IAsyncOperation<UserIdentity>
    {
        private AsyncOperationCompletedHandler<UserIdentity>? _completed;

        internal UserAuthenticationOperation()
            : base(Task.FromException<UserIdentity>(new UnauthorizedAccessException("No Microsoft account is available.")), null)
        {
        }

        public AsyncOperationCompletedHandler<UserIdentity>? get_Completed() => _completed;

        public void put_Completed(AsyncOperationCompletedHandler<UserIdentity>? handler)
        {
            _completed = handler;
            if (handler != null)
            {
                OnCompletedSet(() => handler(this, Status));
            }
        }

        public UserIdentity GetResults()
        {
            ThrowIfNotSucceeded();
            return ((Task<UserIdentity>)Task).Result;
        }
    }

    /// <summary>Shim for <c>Windows.Security.Authentication.OnlineId.OnlineIdAuthenticator</c>.</summary>
    public sealed class OnlineIdAuthenticator
    {
        public bool CanSignOut => false;

        public Guid AuthenticatedSafeCustomerId => Guid.Empty;

        public UserAuthenticationOperation AuthenticateUserAsync(OnlineIdServiceTicketRequest request) => new();

        public IAsyncAction SignOutUserAsync() => WinRtAsync.Completed();
    }
}
