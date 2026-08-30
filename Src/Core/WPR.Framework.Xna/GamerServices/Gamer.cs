using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using System.Linq;

using WPR.Common;

namespace Microsoft.Xna.Framework.GamerServices
{
    public abstract class Gamer : IDisposable
    {
        internal static SignedInGamerCollection _SignedInGamers;

        private LeaderboardWriter _LeaderboardWriter;
        private String _GamerTag;

        internal Gamer()
        {
            _LeaderboardWriter = new LeaderboardWriter();
            _GamerTag = Configuration.Current.GamerTag ?? "HarryDirk";
        }

        static Gamer()
        {
            _SignedInGamers = new SignedInGamerCollection(new List<SignedInGamer>{
                new SignedInGamer() { PlayerIndex = PlayerIndex.One }
            });
        }

        public IAsyncResult BeginGetProfile(AsyncCallback callback, object asyncState)
        {
            return Task.Run(async () =>
            {
                GamerProfile profile = new GamerProfile();
                // Stage 5e: totals now come from the registered achievement store rather than a
                // DbContext this assembly owns. A host that ships without WPR.Database registers
                // nothing, which reads as "nothing earned" instead of throwing — the same
                // degradation the "no catalogue rows for this product" path already had.
                var totals = WPR.Xna.Rhi.XnaBackend.Achievements is { } store
                    ? await store.GetEarnedTotalsAsync()
                    : default;

                profile.TotalAchievements = totals.EarnedCount;
                profile.GamerScore = totals.GamerScore;
                profile.GamerZone = GamerZone.Underground;
                profile.Region = System.Globalization.RegionInfo.CurrentRegion;
                profile.Reputation = 100.0f;
                profile.Motto = "";

                if (callback != null)
                {
                    TaskCompletionSource<GamerProfile> source = new TaskCompletionSource<GamerProfile>(asyncState);
                    source.SetResult(profile);

                    callback(source.Task);
                }

                return profile;
            });
        }

        public GamerProfile EndGetProfile(IAsyncResult result)
        {
            return (result as Task<GamerProfile>)!.GetAwaiter().GetResult();
        }

        public GamerProfile GetProfile() => EndGetProfile(BeginGetProfile(null, null));

        public override string ToString()
        {
            return Gamertag;
        }

        public static IAsyncResult BeginGetPartnerToken(
          string audienceUri,
          AsyncCallback callback,
          object asyncState)
        {
            return StubUtils.ForeverTask;
        }

        // Synchronous partner-token fetch. WPR has no Xbox LIVE partner-service backend,
        // so there is no real token to return. Battleship's XBOXLive.getPartnerToken() calls
        // this statically (Gamer.GetPartnerToken(audienceUri)) from RESTRequest._start() on a
        // background thread; without the method the game threw MissingMethodException and the
        // whole process died. Return an empty token — the caller (ExtractTokenData) tolerates
        // it (parses to nothing, yields ""), wraps it in an empty SAML assertion, and the
        // ensuing REST call to the unreachable partner service fails gracefully in its own
        // guarded async callback. Online partner features won't work offline; single-player is
        // unaffected.
        public static string GetPartnerToken(string audienceUri) => "";

        // The async half of the above. BeginGetPartnerToken hands back StubUtils.ForeverTask, so
        // this is only ever reached by a game that polls IsCompleted rather than blocking — but
        // it has to EXIST, because the reference is resolved when the calling method is JITted,
        // not when it runs. Skulls of the Shogun (AngelXNA.Net.NetAsyncGameManager) and Crimson
        // Dragon (Microsoft.Phone.Marketplace.GamerContext) both reference it. Same empty token
        // as the synchronous path, for the same reason.
        public static string EndGetPartnerToken(IAsyncResult result) => "";

        /// <summary>
        /// Look a gamer up by tag. WPR has no LIVE directory to query, so the only tag that can
        /// resolve is the local one; anything else returns null, which is what the real API does
        /// for a gamertag that isn't a friend. Skulls of the Shogun calls this from its gamer-card
        /// dialog.
        /// </summary>
        public static Gamer GetFromGamertag(string gamertag)
        {
            if (string.IsNullOrEmpty(gamertag)) return null;

            foreach (SignedInGamer gamer in _SignedInGamers)
            {
                if (string.Equals(gamer.Gamertag, gamertag, StringComparison.OrdinalIgnoreCase))
                    return gamer;
            }

            return null;
        }

        public string Gamertag
        {
            get => _GamerTag;
            set => _GamerTag = value;
        }

        public string DisplayName => _GamerTag;

        public void Dispose()
        {
            IsDisposed = true;
        }

        public bool IsDisposed
        {
            get;
            set;
        }

        public static SignedInGamerCollection SignedInGamers
        {
            get
            {
                return _SignedInGamers;
            }
        }

        public object Tag
        {
            get;
            set;
        }

        public LeaderboardWriter LeaderboardWriter
        {
            get => _LeaderboardWriter;
        }
    }
}
