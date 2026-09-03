using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework.GamerServices;

namespace WPR.Shell
{
    /// <summary>
    /// The arithmetic and ordering behind an achievements list, shared by both launcher shells.
    ///
    /// <para>Every member here existed twice or more before 2026-09-02 — the four aggregates in
    /// four places, the sort comparator in three, the description fallback in two — and the copies
    /// had already drifted apart in ways the duplication hid. Extracting them is not only
    /// de-duplication; it settles which behaviour is correct:</para>
    ///
    /// <list type="bullet">
    /// <item>The two shells sorted games differently (one by name, one by earned-count then name).</item>
    /// <item>Only the Android shell masked <b>secret</b> achievements, so the desktop achievements
    /// page revealed the name and description of achievements the player had not earned — the one
    /// thing WP7's secret flag exists to prevent. <see cref="DescribeUnearnedSafely"/> is now the
    /// only way either shell renders an achievement's text.</item>
    /// <item>The percentage was a <c>double</c> on one side and a rounded <c>int</c> on the other,
    /// so the same game could read 66% and 67%. Both are offered here, from one expression.</item>
    /// </list>
    ///
    /// <para><b>No UI types.</b> These are pure functions over <see cref="Achievement"/>; the
    /// shells wrap them in a ViewModel or an Adapter. Note <see cref="Achievement"/> is a
    /// game-facing type the patcher rescopes into WPR.Framework.Xna, which is why the reference
    /// runs that way and cannot be inverted.</para>
    /// </summary>
    public static class AchievementRollup
    {
        /// <summary>Shown in place of a secret achievement's name until it is earned.</summary>
        public const string HiddenName = "hidden achievement";

        /// <summary>Shown in place of a secret achievement's description until it is earned.</summary>
        public const string HiddenDescription = "revealed when you earn it";

        /// <summary>
        /// Totals for one game's achievement set. A single pass, so a shell binding six properties
        /// does not re-enumerate the list six times the way the property-per-aggregate shape did.
        /// </summary>
        public static AchievementTotals Totalise(IEnumerable<Achievement> achievements)
        {
            if (achievements == null) throw new ArgumentNullException(nameof(achievements));

            int total = 0, earned = 0, totalScore = 0, earnedScore = 0;
            foreach (Achievement a in achievements)
            {
                total += 1;
                totalScore += a.GamerScore;
                if (a.IsEarned)
                {
                    earned += 1;
                    earnedScore += a.GamerScore;
                }
            }

            return new AchievementTotals(total, earned, totalScore, earnedScore);
        }

        /// <summary>
        /// The order an achievement list is shown in: earned first, then by gamerscore descending,
        /// then by name. Matches the Xbox hub, and was already written identically in three places.
        /// </summary>
        public static IOrderedEnumerable<Achievement> InDisplayOrder(IEnumerable<Achievement> achievements)
        {
            if (achievements == null) throw new ArgumentNullException(nameof(achievements));

            return achievements
                .OrderByDescending(a => a.IsEarned)
                .ThenByDescending(a => a.GamerScore)
                .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True while this achievement's name and description must stay hidden — WP7's secret
        /// achievements, which reveal themselves only once earned. The gamerscore is never hidden,
        /// so the totals still add up for the player.
        /// </summary>
        public static bool IsSecretAndUnearned(Achievement achievement)
        {
            if (achievement == null) throw new ArgumentNullException(nameof(achievement));
            return !achievement.IsEarned && !achievement.DisplayBeforeEarned;
        }

        /// <summary>
        /// The name to render, honouring secrecy. Use this rather than <c>Achievement.Name</c>
        /// anywhere a list is drawn.
        /// </summary>
        public static string DisplayName(Achievement achievement) =>
            IsSecretAndUnearned(achievement) ? HiddenName : (achievement.Name ?? "");

        /// <summary>
        /// The description to render, honouring secrecy and falling back to <c>HowToEarn</c> when
        /// a catalogue entry carries no description — which many of the extracted catalogues do.
        /// </summary>
        public static string DescribeUnearnedSafely(Achievement achievement)
        {
            if (IsSecretAndUnearned(achievement)) return HiddenDescription;

            return string.IsNullOrWhiteSpace(achievement.Description)
                ? achievement.HowToEarn ?? ""
                : achievement.Description;
        }

        /// <summary>
        /// Groups a flat achievement set by owning product. Null and empty product ids are dropped:
        /// they cannot be matched to a catalogue row, and one desktop copy of this used to hand
        /// them to <c>ToDictionary</c> and throw the whole page into a catch that degraded to an
        /// empty list.
        /// </summary>
        public static IEnumerable<IGrouping<string, Achievement>> ByProduct(IEnumerable<Achievement> achievements)
        {
            if (achievements == null) throw new ArgumentNullException(nameof(achievements));

            return achievements
                .Where(a => !string.IsNullOrEmpty(a.OwnProductId))
                .GroupBy(a => a.OwnProductId!, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Aggregates for one game's achievement set. See <see cref="AchievementRollup.Totalise"/>.</summary>
    public readonly struct AchievementTotals
    {
        public AchievementTotals(int total, int earned, int totalScore, int earnedScore)
        {
            Total = total;
            Earned = earned;
            TotalScore = totalScore;
            EarnedScore = earnedScore;
        }

        public int Total { get; }
        public int Earned { get; }
        public int TotalScore { get; }
        public int EarnedScore { get; }

        /// <summary>Completion as a fraction of achievements earned, 0-100. Zero for an empty set.</summary>
        public double Percent => Total == 0 ? 0 : Earned * 100.0 / Total;

        /// <summary>
        /// <see cref="Percent"/> rounded, for shells that want an int progress bar. Derived from
        /// the same expression so the two shells can no longer disagree by a percentage point.
        /// </summary>
        public int PercentRounded => (int)Math.Round(Percent);

        /// <summary>"3 / 12" — the earned-of-total pair.</summary>
        public string Progress => $"{Earned} / {Total}";

        /// <summary>"3/12  ·  60/200 G" — progress plus gamerscore, for a one-line subtitle.</summary>
        public string Summary => $"{Earned}/{Total}  ·  {EarnedScore}/{TotalScore} G";
    }
}
