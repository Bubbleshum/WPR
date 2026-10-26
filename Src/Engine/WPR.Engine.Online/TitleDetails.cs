using System.Threading.Tasks;

namespace WPR.Engine.Online
{
    /// <summary>
    /// What an installed package says about its game, offered to WPR Hub so its title pages fill in
    /// without an admin typing them: the name, publisher, developer, platform and the icon.
    /// </summary>
    /// <param name="TitleId">The product id, braced or not.</param>
    /// <param name="Platform">WPR Hub's platform name ("Windows Phone 7", "Windows Phone 8"…), from
    /// <c>WPR.Common.WindowsPhoneTarget.HubPlatformName</c>.</param>
    /// <param name="IconFile">An absolute path to the installed icon, read when the offer is sent.</param>
    public sealed record TitleDetails(
        string TitleId,
        string? Name,
        string? Publisher,
        string? Developer,
        string? Platform,
        string? IconFile);

    /// <summary>
    /// Offers installed games' details to the hub, which keeps only what it does not have yet.
    /// </summary>
    /// <remarks>
    /// <para>The install only queues (<see cref="Offer"/>); <see cref="OnlineBackend.FlushAsync"/>
    /// sends, so an install never waits on, or fails over, the network. Same consent as an install
    /// receipt: nothing is queued or sent with server logging off.</para>
    /// </remarks>
    public interface ITitleDetailsSync
    {
        /// <summary>Queue one game's details. Cheap, never throws, never touches the network.</summary>
        void Offer(TitleDetails details);

        /// <summary>Send what is queued, plus installed games never offered (a backfill). Never throws.</summary>
        Task FlushAsync();
    }
}
