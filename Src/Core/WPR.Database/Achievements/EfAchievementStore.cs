using System.Collections.Generic;
using System.Linq;
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
        public async Task<IReadOnlyList<Achievement>> GetForProductAsync(string productId) =>
            await AchievementContext.Current.Achievements!
                .Where(x => x.OwnProductId == productId)
                .ToListAsync();

        public async Task<IReadOnlyList<Achievement>> GetByKeyAsync(string productId, string achievementKey) =>
            await AchievementContext.Current.Achievements!
                .Where(x => (x.OwnProductId == productId) && (x.Key == achievementKey))
                .ToListAsync();

        public Task<int> CountForProductAsync(string productId) =>
            AchievementContext.Current.Achievements!
                .CountAsync(x => x.OwnProductId == productId);

        public async Task<AchievementTotals> GetEarnedTotalsAsync()
        {
            // Two aggregates over the same filtered set, exactly as Gamer.BeginGetProfile did
            // inline before the seam (CountAsync + SumAsync over .Where(a => a.IsEarned)).
            var earned = AchievementContext.Current.Achievements!.Where(a => a.IsEarned);
            return new AchievementTotals(
                await earned.CountAsync(),
                await earned.SumAsync(a => a.GamerScore));
        }

        public Task SaveChangesAsync() => AchievementContext.Current.SaveChangesAsync();
    }
}
