using System;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Xbox.Foundation;
using Windows.Foundation;
using WPR.WinRT;

namespace Microsoft.Xbox
{
    /// <summary>
    /// Shim for <c>Microsoft.Xbox.XboxLIVEService</c>: sign-in is the WPR Hub account.
    /// </summary>
    /// <remarks>
    /// <para><b>SignInAsync always succeeds at once</b>, as the player WPR knows: the WPR Hub
    /// username when signed in, the guest gamertag otherwise (Configuration.EffectiveGamerTag,
    /// through the host). Scores and unlocks made as the guest wait on the device and go up under
    /// the account at hub sign-in, which is the same rule XNA titles follow, so a game never needs
    /// to show Xbox Live's sign-in flow.</para>
    /// <para><b>SignedOut is never raised.</b> Signing in or out of WPR Hub happens in the
    /// launcher, never under a running game, and SignOutAsync from the game does not sign the
    /// player out of the hub.</para>
    /// </remarks>
    public static class XboxLIVEService
    {
        private static readonly EventRegistrationTokenTable<EventHandler<SignedOutEventArgs>> _signedOut = new();
        private static ServiceClient? _serviceClient;

        public static IAsyncOperation<UserIdentity> SignInAsync()
        {
            UserIdentity identity = SignedInUserIdentity;
            XboxHub.Log($"SignInAsync -> {identity.Gamertag}");
            return WinRtAsync.FromResult(identity);
        }

        public static IAsyncAction SignOutAsync()
        {
            XboxHub.Log("SignOutAsync (the WPR Hub account stays signed in)");
            return WinRtAsync.Completed();
        }

        public static EventRegistrationToken add_SignedOut(EventHandler<SignedOutEventArgs> handler) => _signedOut.AddEventHandler(handler);

        public static void remove_SignedOut(EventRegistrationToken cookie) => _signedOut.RemoveEventHandler(cookie);

        public static UserIdentity SignedInUserIdentity => new(XboxHub.PlayerXuid, XboxHub.Gamertag);

        public static ServiceClient ServiceClient => _serviceClient ??= new ServiceClient("WPR Hub");

        public static void InvalidateCacheGroup(CacheGroup eGroup)
        {
        }

        public static IAsyncAction ShowGamesApplicationAsync(LaunchAction eAction)
        {
            XboxHub.Log($"ShowGamesApplicationAsync({eAction}): there is no Games hub; nothing shown");
            return WinRtAsync.Completed();
        }
    }
}

namespace Microsoft.Xbox.Foundation
{
    public interface IUserIdentity
    {
        ulong Xuid { get; }
        string Gamertag { get; }
    }

    /// <summary>Shim for <c>Microsoft.Xbox.Foundation.UserIdentity</c>.</summary>
    public sealed class UserIdentity : IUserIdentity
    {
        public UserIdentity(ulong xuid, string gamertag)
        {
            Xuid = xuid;
            Gamertag = gamertag ?? string.Empty;
        }

        public ulong Xuid { get; }

        public string Gamertag { get; }
    }

    public interface ISignedOutEventArgs
    {
        Exception? ErrorCode { get; }
    }

    /// <summary>Shim for <c>Microsoft.Xbox.Foundation.SignedOutEventArgs</c>; never raised (see XboxLIVEService).</summary>
    public sealed class SignedOutEventArgs : ISignedOutEventArgs
    {
        internal SignedOutEventArgs(Exception? errorCode) => ErrorCode = errorCode;

        public Exception? ErrorCode { get; }
    }

    /// <summary>Shim for <c>Microsoft.Xbox.Foundation.ServiceClient</c>.</summary>
    public sealed class ServiceClient
    {
        private readonly EventRegistrationTokenTable<EventHandler<SignedOutEventArgs>> _signedOut = new();

        public ServiceClient(string targetName)
        {
        }

        public IAsyncAction SignInAsync() => WinRtAsync.Completed();

        public IAsyncAction SignOutAsync() => WinRtAsync.Completed();

        public EventRegistrationToken add_SignedOut(EventHandler<SignedOutEventArgs> handler) => _signedOut.AddEventHandler(handler);

        public void remove_SignedOut(EventRegistrationToken cookie) => _signedOut.RemoveEventHandler(cookie);

        public UserIdentity SignedInUserIdentity => XboxLIVEService.SignedInUserIdentity;

        /// <summary>Non-empty, as the native runtime answers: titles treat an empty ticket as signed out.</summary>
        public string Token => "WPR-OFFLINE-TOKEN";

        public uint TitleId => 0;
    }
}
