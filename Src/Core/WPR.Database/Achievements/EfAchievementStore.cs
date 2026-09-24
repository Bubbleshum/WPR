using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Xna.Framework.GamerServices;
using WPR.Xna.Achievements;

namespace WPR.Database.Achievements
{
    /// <summary>
    /// EF Core implementation of the GamerServices achievement seam — Stage 5e, ADR §1.4.
    ///
    /// <para>This is the half that used to live inside <c>WPR.Framework.Xna</c>. Moving it here
    /// is what let that project drop its EF Core / Sqlite / SQLitePCLRaw references and go back
    /// to being the dependency-free leaf the XNA type system is supposed to be — which matters
    /// because it is the assembly patched games bind directly.</para>
    ///
    /// <para>Every method delegates to the same <see cref="AchievementContext.Current"/> singleton
    /// the pre-seam code used, so entity tracking, the shared-connection behaviour and the
    /// ConcurrencyDetector race that <c>SignedInGamer._SignInGate</c> guards are all unchanged.
    /// The point of this step was to move the dependency, not to change how the data behaves.</para>
    /// </summary>
    public sealed class EfAchievementStore : IAchievementStore
    {
        /// <summary>
        /// Serialises every operation against <see cref="AchievementContext.Current"/>.
        ///
        /// <para>That context is a process-wide singleton, and a <c>DbContext</c> is explicitly
        /// NOT thread-safe: EF Core builds its model lazily on first use, and a second thread
        /// entering while the first is inside <c>OnModelCreating</c> gets
        /// <c>"An attempt was made to use the model while it was being created"</c>. Nothing above
        /// this class serialises the callers — <see cref="Microsoft.Xna.Framework.GamerServices.SignedInGamer.BeginGetAchievements"/>
        /// and <c>BeginAwardAchievement</c> each start their own <c>Task.Run</c>, so a title that
        /// asks twice in one frame (iStunt 2 does, from its menu build) races itself.</para>
        ///
        /// <para>A lock rather than a context per call, deliberately: the award path mutates
        /// tracked entities and then calls <see cref="SaveChangesAsync"/> through this same store,
        /// so the entity tracking has to stay shared. Contention is irrelevant here — these are a
        /// handful of queries per launch, never a per-frame path.</para>
        ///
        /// <para>Static, because the thing being guarded is static. A second
        /// <c>EfAchievementStore</c> instance would otherwise reintroduce the race.</para>
        /// </summary>
        private static readonly SemaphoreSlim _Gate = new SemaphoreSlim(1, 1);

        public async Task<IReadOnlyList<Achievement>> GetForProductAsync(string productId)
        {
            await _Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await AchievementContext.Current.Achievements!
                    .Where(x => x.OwnProductId == productId)
                    .ToListAsync().ConfigureAwait(false);
            }
            finally { _Gate.Release(); }
        }

        public async Task<IReadOnlyList<Achievement>> GetByKeyAsync(string productId, string achievementKey)
        {
            await _Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await AchievementContext.Current.Achievements!
                    .Where(x => (x.OwnProductId == productId) && (x.Key == achievementKey))
                    .ToListAsync().ConfigureAwait(false);
            }
            finally { _Gate.Release(); }
        }

        public async Task<int> CountForProductAsync(string productId)
        {
            await _Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await AchievementContext.Current.Achievements!
                    .CountAsync(x => x.OwnProductId == productId).ConfigureAwait(false);
            }
            finally { _Gate.Release(); }
        }

        public async Task<AchievementTotals> GetEarnedTotalsAsync()
        {
            await _Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                // Two aggregates over the same filtered set, exactly as Gamer.BeginGetProfile did
                // inline before the seam (CountAsync + SumAsync over .Where(a => a.IsEarned)).
                var earned = AchievementContext.Current.Achievements!.Where(a => a.IsEarned);
                return new AchievementTotals(
                    await earned.CountAsync().ConfigureAwait(false),
                    await earned.SumAsync(a => a.GamerScore).ConfigureAwait(false));
            }
            finally { _Gate.Release(); }
        }

        public async Task SaveChangesAsync()
        {
            await _Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await AchievementContext.Current.SaveChangesAsync().ConfigureAwait(false);
            }
            finally { _Gate.Release(); }
        }
    }
}
