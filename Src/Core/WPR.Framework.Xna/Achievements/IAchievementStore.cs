using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Xna.Framework.GamerServices;

namespace WPR.Xna.Achievements
{
	/// <summary>Aggregate totals behind <c>GamerProfile</c> — earned count and summed score.</summary>
	public readonly record struct AchievementTotals(int EarnedCount, int GamerScore);

	/// <summary>
	/// The persistence seam behind the XNA GamerServices achievement API — ADR §1.4 / Stage 5e.
	///
	/// <para>GamerServices used to reach straight into an EF Core <c>AchievementContext</c>, which
	/// meant <c>WPR.Framework.Xna</c> — the assembly patched games bind — carried EF Core, Sqlite
	/// and SQLitePCLRaw. This interface is what let those references leave: the framework states
	/// what it needs, and <c>WPR.Database</c> supplies it.</para>
	///
	/// <para><b>Why this lives here and not in <c>WPR.Abstractions</c></b> — the same correction
	/// 5c-0 and 5c-5 both had to make. The vocabulary is <see cref="Achievement"/>, a game-facing
	/// XNA type defined in this assembly, so an interface in <c>WPR.Abstractions</c> would force
	/// <c>Abstractions → WPR.Framework.Xna</c> while the framework consumes the seam — a cycle.
	/// Co-locating the contract with its vocabulary is the established fix (see
	/// <c>WPR.Xna.Rhi</c>). A DTO-based interface in Abstractions was the alternative; it was
	/// rejected because it would break entity tracking, which the award path depends on.</para>
	///
	/// <para>Implementations return live, <em>tracked</em> entities: callers mutate what they get
	/// back and then call <see cref="SaveChangesAsync"/>. That is exactly how the pre-seam code
	/// behaved against the shared DbContext singleton, and preserving it is why the interface is
	/// shaped around queries + an explicit save rather than a single Award operation — the
	/// diagnostics, the toast notification and the "more than one row with this key" warning all
	/// stay in GamerServices, where they belong.</para>
	/// </summary>
	public interface IAchievementStore
	{
		/// <summary>Every achievement row seeded for <paramref name="productId"/>. May be empty
		/// when the game has no catalogue under <c>Database/Achievements/&lt;productId&gt;/</c>.</summary>
		Task<IReadOnlyList<Achievement>> GetForProductAsync(string productId);

		/// <summary>The rows for one achievement key. More than one is a catalogue authoring bug;
		/// the caller logs it.</summary>
		Task<IReadOnlyList<Achievement>> GetByKeyAsync(string productId, string achievementKey);

		/// <summary>How many rows exist for a product — diagnostics only, for the case where an
		/// award fires but no row matches the key.</summary>
		Task<int> CountForProductAsync(string productId);

		/// <summary>Earned count and summed gamerscore across every product.</summary>
		Task<AchievementTotals> GetEarnedTotalsAsync();

		/// <summary>Persists mutations made to entities handed out by this store.</summary>
		Task SaveChangesAsync();
	}
}
