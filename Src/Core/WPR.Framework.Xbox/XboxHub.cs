using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Windows.Foundation;
using WPR.WinRT;
using WPR.XboxLive;

namespace Microsoft.Xbox
{
    /// <summary>
    /// What every type in this facade shares: the host behind it, the signed-in player, and how
    /// an unsupported service fails.
    /// </summary>
    /// <remarks>
    /// The answers mirror the WP8 native runtime's (XboxRuntime/XboxData), so a native and a
    /// managed title see the same player: the same xuid for the signed-in player, a stable one for
    /// anyone else, a non-empty service token.
    /// </remarks>
    internal static class XboxHub
    {
        /// <summary>The native runtime's player xuid: a real-looking Xbox Live xuid plus "WPR".</summary>
        public const ulong PlayerXuid = 2533274790395904UL + 0x57505200UL;

        /// <summary>Microsoft.Xbox.Foundation.ErrorCode.ServerConnectionFailure.</summary>
        public const int ServerConnectionFailure = -2101608440;

        public static IXboxLiveHost? Host => XboxLiveBackend.Host;

        public static string Gamertag
        {
            get
            {
                try
                {
                    string? tag = Host?.Gamertag;
                    return string.IsNullOrWhiteSpace(tag) ? "Player" : tag;
                }
                catch (Exception ex)
                {
                    Log($"host gamertag threw {ex.GetType().Name}: {ex.Message}");
                    return "Player";
                }
            }
        }

        public static ulong XuidOf(string gamertag, bool isMe) =>
            isMe || string.Equals(gamertag, Gamertag, StringComparison.Ordinal) ? PlayerXuid : PlayerXuid + 1 + (uint)StableHash(gamertag);

        /// <summary>The player's gamer picture as a file URL, or empty for none (as the native runtime answers).</summary>
        public static string GamerPictureUrl
        {
            get
            {
                try
                {
                    string? path = Host?.GamerPicturePath;
                    return !string.IsNullOrEmpty(path) && System.IO.File.Exists(path) ? new Uri(path).AbsoluteUri : string.Empty;
                }
                catch
                {
                    return string.Empty;
                }
            }
        }

        /// <summary>
        /// A service WPR Hub does not provide (multiplayer, user messages, marketplace): the call
        /// fails as it did on a phone that could not reach Xbox Live, which titles handle.
        /// </summary>
        public static IAsyncAction Unavailable(string what)
        {
            Log($"{what}: not provided by WPR Hub; failing as an unreachable Xbox Live service");
            return WinRtAsync.Failed(ServiceUnavailable(what));
        }

        /// <inheritdoc cref="Unavailable(string)"/>
        public static IAsyncOperation<T> Unavailable<T>(string what)
        {
            Log($"{what}: not provided by WPR Hub; failing as an unreachable Xbox Live service");
            return WinRtAsync.Failed<T>(ServiceUnavailable(what));
        }

        /// <summary>
        /// A marketplace read (offers, receipts, balances): answered as a marketplace with nothing
        /// in it, not as an unreachable one. On a phone these reads succeeded at start-up, and a
        /// title's failure path can assume a screen that is not there yet: Gravity Guy 2's
        /// DLCManager raises its LastErrorChanged event with no subscriber (only the store page
        /// subscribes), and the NullReferenceException ended Game.Initialize before LoadContent.
        /// </summary>
        public static IAsyncOperation<T> EmptyMarketplace<T>(string what, T empty)
        {
            Log($"{what}: WPR Hub has no marketplace; answering an empty one");
            return WinRtAsync.FromResult(empty);
        }

        private static Exception ServiceUnavailable(string what) =>
            new InvalidOperationException($"Xbox LIVE service unavailable ({what}).") { HResult = ServerConnectionFailure };

        /// <summary>Run a host call on the thread pool; a host that throws fails the operation, never the game.</summary>
        public static IAsyncOperation<T> Run<T>(string what, Func<T> work) => WinRtAsync.Run(() =>
        {
            try
            {
                return work();
            }
            catch (Exception ex)
            {
                Log($"{what} threw {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        });

        /// <inheritdoc cref="Run{T}(string, Func{T})"/>
        public static IAsyncAction Run(string what, Action work) => WinRtAsync.Run(() =>
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                Log($"{what} threw {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        });

        public static IReadOnlyList<T> Page<T>(IReadOnlyList<T> all, uint skip, uint max)
        {
            int start = (int)Math.Min(skip, (uint)all.Count);
            int count = max == 0 ? all.Count - start : (int)Math.Min(max, (uint)(all.Count - start));
            var page = new T[count];
            for (int i = 0; i < count; i++) page[i] = all[start + i];
            return page;
        }

        private static int _logged;

        public static void Log(string message)
        {
            if (Interlocked.Increment(ref _logged) <= 500)
            {
                Trace.WriteLine("[wpr-xbox] " + message);
            }
        }

        private static int StableHash(string text)
        {
            unchecked
            {
                int hash = 17;
                foreach (char c in text) hash = (hash * 31) + c;
                return hash & 0x7FFFFFFF;
            }
        }
    }
}
