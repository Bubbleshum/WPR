using System.Collections.Generic;
using System.Threading.Tasks;

namespace WPR.Engine.Online
{
    /// <summary>One achievement definition offered to WPR Hub.</summary>
    /// <param name="NameFromKey">True when nothing named it and <see cref="Name"/> is only the key
    /// made readable: the hub then keeps its own name, if it has one.</param>
    /// <param name="IconFile">An absolute path to the icon (PNG or JPEG), read when the offer is sent.</param>
    public sealed record OfferedAchievement(
        string Key,
        string Name,
        bool NameFromKey,
        string? Description,
        int Points,
        bool Secret,
        string? IconFile);

    /// <summary>
    /// A game's achievement catalogue as WPR has it (shipped with WPR or generated from the
    /// installed package), offered to WPR Hub so the hub's title gets definitions and icons.
    /// </summary>
    /// <param name="TitleId">The product id, braced or not.</param>
    /// <param name="Source">Where the catalogue came from: <c>bundled</c> or <c>package</c>.</param>
    public sealed record TitleAchievements(string TitleId, string Source, IReadOnlyList<OfferedAchievement> Achievements);

    /// <summary>
    /// Offers games' achievement definitions and icons to the hub, which keeps only what it lacks.
    /// </summary>
    /// <remarks>
    /// Same shape and consent as <see cref="ITitleDetailsSync"/>: the install (or the catalogue
    /// seeding beside it) only queues; <see cref="OnlineBackend.FlushAsync"/> sends. Nothing is
    /// queued or sent with server logging off, and nothing here can fail an install.
    /// </remarks>
    public interface IAchievementDefinitionsSync
    {
        /// <summary>Queue one game's catalogue. Cheap, never throws, never touches the network.</summary>
        void Offer(TitleAchievements catalogue);

        /// <summary>Send what is queued, plus installed games never offered (a backfill). Never throws.</summary>
        Task FlushAsync();
    }
}
