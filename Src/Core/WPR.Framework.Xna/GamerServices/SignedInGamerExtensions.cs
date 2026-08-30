using System;
using System.Collections.Generic;

namespace Microsoft.Xna.Framework.GamerServices
{
    /// <summary>
    /// Shim for <c>Microsoft.Xna.Framework.GamerServices.SignedInGamerExtensions</c>.
    ///
    /// On WP7 this shipped in a separate <c>Microsoft.Xna.Framework.GamerServicesExtensions</c>
    /// assembly and let a title grant Xbox avatar items as rewards. WPR has no avatar service and
    /// no LIVE backend, so every award silently succeeds and every status query reports "already
    /// awarded" — that is the quiet path through a game's reward code, whereas throwing would
    /// surface an error dialog for a purely cosmetic feature.
    ///
    /// <c>ApplicationPatcher</c> already rescopes references to the GamerServicesExtensions
    /// assembly onto this one (ApplicationPatcher.cs:1709), so the type only has to exist here.
    /// Crimson Dragon: Side Story reaches it from <c>DracoWP.MyGamerService.StartAwardAvatarAssets</c>.
    /// </summary>
    public static class SignedInGamerExtensions
    {
        // The real API takes string[]; games bind that exact signature, and an IEnumerable<string>
        // overload does NOT satisfy a memberref for the array form (member resolution is by exact
        // signature, not by assignability). Crimson Dragon calls the array overload, so both
        // shapes are declared.
        public static IAsyncResult BeginAwardAvatarAssets(this SignedInGamer gamer, string[] assetKeys, AsyncCallback callback, object asyncState)
        {
            return Completed(callback, asyncState);
        }

        public static IAsyncResult BeginAwardAvatarAssets(this SignedInGamer gamer, IEnumerable<string> assetKeys, AsyncCallback callback, object asyncState)
        {
            return Completed(callback, asyncState);
        }

        public static void AwardAvatarAssets(this SignedInGamer gamer, string[] assetKeys)
        {
        }

        public static IAsyncResult BeginGetAvatarAssetStatus(this SignedInGamer gamer, string[] assetKeys, AsyncCallback callback, object asyncState)
        {
            return Completed(callback, asyncState);
        }

        public static IDictionary<string, bool> GetAvatarAssetStatus(this SignedInGamer gamer, string[] assetKeys)
        {
            return GetAvatarAssetStatus(gamer, (IEnumerable<string>)assetKeys);
        }

        public static void EndAwardAvatarAssets(this SignedInGamer gamer, IAsyncResult result)
        {
        }

        public static void AwardAvatarAssets(this SignedInGamer gamer, IEnumerable<string> assetKeys)
        {
        }

        public static IAsyncResult BeginGetAvatarAssetStatus(this SignedInGamer gamer, IEnumerable<string> assetKeys, AsyncCallback callback, object asyncState)
        {
            return Completed(callback, asyncState);
        }

        /// <summary>
        /// Reports every requested asset as already awarded, so a game that awards-if-missing
        /// does nothing rather than retrying forever.
        /// </summary>
        public static IDictionary<string, bool> EndGetAvatarAssetStatus(this SignedInGamer gamer, IAsyncResult result)
        {
            return new Dictionary<string, bool>();
        }

        public static IDictionary<string, bool> GetAvatarAssetStatus(this SignedInGamer gamer, IEnumerable<string> assetKeys)
        {
            var map = new Dictionary<string, bool>();
            if (assetKeys != null)
            {
                foreach (string key in assetKeys)
                {
                    if (key != null) map[key] = true;
                }
            }
            return map;
        }

        /// <summary>
        /// An IAsyncResult that is already finished, so a caller that blocks on
        /// <see cref="IAsyncResult.AsyncWaitHandle"/> or polls
        /// <see cref="IAsyncResult.IsCompleted"/> proceeds immediately instead of hanging.
        /// The callback is invoked inline, matching the "completed synchronously" contract.
        /// </summary>
        private static IAsyncResult Completed(AsyncCallback callback, object state)
        {
            var result = new SyncResult(state);
            callback?.Invoke(result);
            return result;
        }

        private sealed class SyncResult : IAsyncResult
        {
            private readonly Lazy<System.Threading.WaitHandle> _Handle;

            public SyncResult(object state)
            {
                AsyncState = state;
                _Handle = new Lazy<System.Threading.WaitHandle>(
                    () => new System.Threading.ManualResetEvent(true));
            }

            public object AsyncState { get; }
            public System.Threading.WaitHandle AsyncWaitHandle => _Handle.Value;
            public bool CompletedSynchronously => true;
            public bool IsCompleted => true;
        }
    }
}
