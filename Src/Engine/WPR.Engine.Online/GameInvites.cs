using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace WPR.Engine.Online
{
    /// <summary>A WPR Hub friend the player can invite.</summary>
    /// <param name="Username">The friend's gamertag.</param>
    /// <param name="Online">Online now (not appearing offline).</param>
    /// <param name="Playing">The game they're in, when the hub knows it.</param>
    /// <param name="GamerpicUrl">Their hub gamerpic, for the picker's rows.</param>
    public sealed record InviteFriend(string Username, bool Online, string? Playing, string? GamerpicUrl = null);

    /// <summary>The hub half of game invites. WPR.Online.Hub implements it over friends and messages.</summary>
    public interface IGameInviteService
    {
        /// <summary>Signed in to WPR Hub, so invites can be sent at all.</summary>
        bool SignedIn { get; }

        Task<IReadOnlyList<InviteFriend>> GetFriendsAsync(CancellationToken ct);

        /// <summary>Sends one invite message to <paramref name="username"/>.</summary>
        Task SendAsync(string username, string titleId, string? titleName, CancellationToken ct);
    }

    /// <summary>
    /// What a player sees when a game asks to invite friends (XNA's <c>Guide.ShowGameInvite</c>):
    /// a list of WPR Hub friends to pick from, each sent a hub message of kind "invite". The
    /// friend's WPR shows it with a Play button.
    /// </summary>
    /// <remarks>
    /// <para>On WP7 this was an Xbox LIVE page over the game; it returned at once and the game
    /// kept running. Accepting an invite just started the game, and the friend found the inviter
    /// in the game's own lobby, which is all an invite needs to do here too. Pool Pro Online 3,
    /// the reference case, never reads an accepted invite.</para>
    ///
    /// <para>The picker is the platform's: a native dialog on Android, an Avalonia window on the
    /// desktop. It gets the friend list and returns the chosen gamertags, or null when the player
    /// backed out. Everything else (the hub calls, the log) is here, so both platforms behave the
    /// same. No picker, or not signed in: nothing is shown, and the game carries on as if the
    /// player had looked and come straight back.</para>
    /// </remarks>
    public static class GameInvites
    {
        /// <summary>
        /// Shows the friends to choose from. Arguments: the game's name, the friends (online
        /// first), and a message to show instead of the list when there is nothing to pick (null
        /// otherwise). Returns the chosen gamertags, or null if the player cancelled.
        /// </summary>
        public static Func<string, IReadOnlyList<InviteFriend>, string?, Task<IReadOnlyList<string>?>>? Picker { get; set; }

        /// <summary>
        /// Shows the picker and sends the invites. Returns when the picker has closed and the
        /// messages are sent (or failed); never throws. <paramref name="onClosed"/> runs exactly
        /// once, when the picker closes (before sending) or straight away when there is none.
        /// </summary>
        public static async Task<int> ShowAsync(string? titleId, string? titleName, Action? onClosed = null)
        {
            int closedOnce = 0;
            void Closed()
            {
                if (Interlocked.Exchange(ref closedOnce, 1) == 0)
                {
                    try { onClosed?.Invoke(); } catch (Exception ex) { Trace.WriteLine($"[wpr-invite] onClosed threw: {ex.Message}"); }
                }
            }

            var picker = Picker;
            IGameInviteService? hub = OnlineBackend.Invites;
            try
            {
                if (picker is null || string.IsNullOrEmpty(titleId))
                {
                    Trace.WriteLine("[wpr-invite] no invite picker on this platform; returning to the game.");
                    return 0;
                }

                string name = string.IsNullOrWhiteSpace(titleName) ? "this game" : titleName!;
                IReadOnlyList<InviteFriend> friends = Array.Empty<InviteFriend>();
                string? empty = null;
                if (hub is null || !hub.SignedIn)
                {
                    empty = "Sign in to WPR Hub to invite your friends.";
                }
                else
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        friends = Order(await hub.GetFriendsAsync(cts.Token).ConfigureAwait(false));
                        if (friends.Count == 0)
                        {
                            empty = "You have no WPR Hub friends yet. Add some from the hub page.";
                        }
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"[wpr-invite] could not load friends: {ex.Message}");
                        empty = "Couldn't reach WPR Hub. Check your connection and try again.";
                    }
                }

                IReadOnlyList<string>? chosen;
                try
                {
                    chosen = await picker(name, friends, empty).ConfigureAwait(false);
                }
                finally
                {
                    Closed();
                }

                // Nothing to pick from (signed out, no friends, hub down) means nothing to send.
                if (chosen is null || chosen.Count == 0 || hub is null || empty != null)
                {
                    Trace.WriteLine("[wpr-invite] picker closed, nobody invited.");
                    return 0;
                }

                int sent = 0;
                foreach (string username in chosen)
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await hub.SendAsync(username, titleId!, titleName, cts.Token).ConfigureAwait(false);
                        sent++;
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"[wpr-invite] invite to {username} failed: {ex.Message}");
                    }
                }
                Trace.WriteLine($"[wpr-invite] invited {sent} of {chosen.Count} friend(s) to {name}.");
                return sent;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[wpr-invite] failed: {ex}");
                return 0;
            }
            finally
            {
                Closed();
            }
        }

        /// <summary>Online friends first, then by name.</summary>
        public static IReadOnlyList<InviteFriend> Order(IReadOnlyList<InviteFriend> friends)
        {
            var list = new List<InviteFriend>(friends);
            list.Sort((a, b) =>
            {
                int byOnline = b.Online.CompareTo(a.Online);
                return byOnline != 0 ? byOnline : string.Compare(a.Username, b.Username, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }
    }
}
