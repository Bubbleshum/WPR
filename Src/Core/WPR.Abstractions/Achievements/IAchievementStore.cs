using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WPR.Abstractions.Achievements;

/// <summary>One achievement's persisted state.</summary>
public readonly record struct AchievementRecord(
    string Key,
    string Name,
    string Description,
    int GamerScore,
    bool Unlocked);

/// <summary>
/// The achievements backend — the hardcoded catalogue + SQLite DB lifted out of the
/// GamerServices framework project into a Runtime service (Stage 5e; that code is
/// confirmed FNA-free). The XNA GamerServices API surface and the UI view models
/// consume this instead of reaching into the DB directly.
///
/// <para>Not yet implemented: the live code still uses <c>AchievementContext</c> directly.
/// (The TrueAchievements web scraper this once also covered was removed 2026-08-07.)</para>
/// </summary>
public interface IAchievementStore
{
    Task<IReadOnlyList<AchievementRecord>> GetAchievementsAsync(
        string productId, CancellationToken cancellationToken = default);

    Task UnlockAsync(
        string productId, string achievementKey, CancellationToken cancellationToken = default);
}
