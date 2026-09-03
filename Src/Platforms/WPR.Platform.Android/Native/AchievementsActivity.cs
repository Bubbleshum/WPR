using System;
using System.Collections.Generic;
using System.Linq;

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;

using Microsoft.EntityFrameworkCore;
using Microsoft.Xna.Framework.GamerServices;

using WPR.Common;
using WPR.Models;

namespace WPR.Platform.Android.Native
{
    /// <summary>
    /// The achievements page, in two modes over one layout.
    ///
    /// <list type="bullet">
    ///   <item>No <see cref="ExtraProductId"/> — a roll-up of every game that has a
    ///     catalogue, with earned counts and gamerscore. Tapping one drills in.</item>
    ///   <item>With <see cref="ExtraProductId"/> — that game's achievement list.</item>
    /// </list>
    ///
    /// <para>One activity rather than two because the chrome, the empty state and the data
    /// source are identical; only the row template and the query differ.</para>
    /// </summary>
    [Activity(
        Label = "achievements",
        Theme = "@style/WprTheme",
        ScreenOrientation = ScreenOrientation.Portrait,
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
    [Register("com.wpr.android.AchievementsActivity")]
    public class AchievementsActivity : Activity
    {
        public const string ExtraProductId = "ProductId";
        public const string ExtraGameName = "GameName";

        private ListView _List = null!;
        private TextView _EmptyState = null!;
        private TextView _Subtitle = null!;

        private string? _ProductId;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            WprStartup.EnsureInitialized(this);

            SetContentView(Resource.Layout.activity_list_page);
            WpTheme.ApplySystemBars(this);

            FindViewById<TextView>(Resource.Id.appTitle)!.SetTextColor(WpTheme.Accent);

            _List = FindViewById<ListView>(Resource.Id.pageList)!;
            _EmptyState = FindViewById<TextView>(Resource.Id.emptyState)!;
            _Subtitle = FindViewById<TextView>(Resource.Id.pageSubtitle)!;

            _ProductId = Intent?.GetStringExtra(ExtraProductId);

            if (string.IsNullOrEmpty(_ProductId)) LoadGameRollup();
            else LoadGameDetail(_ProductId!);
        }

        private void LoadGameRollup()
        {
            FindViewById<TextView>(Resource.Id.pageTitle)!.Text = "achievements";

            List<AchievementGameEntry> entries = new List<AchievementGameEntry>();
            try
            {
                List<Achievement> all = AchievementContext.Current!.Achievements!
                    .AsNoTracking()
                    .ToList();

                Dictionary<string, WPR.Models.Application> apps = LoadApplicationsByProduct();

                entries = all
                    .GroupBy(a => a.OwnProductId ?? "")
                    .Where(group => !string.IsNullOrEmpty(group.Key))
                    .Select(group =>
                    {
                        apps.TryGetValue(group.Key, out WPR.Models.Application? app);
                        string name = HardcodedAchievementCatalogue.GameName(group.Key)
                                      ?? app?.Name
                                      ?? group.Key;
                        return new AchievementGameEntry(group.Key, name, app?.IconPath, group.ToList());
                    })
                    .OrderByDescending(entry => entry.Earned)
                    .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                Log.Error(LogCategory.GamerServices, $"Achievement roll-up failed:\n{ex}");
            }

            int earned = entries.Sum(entry => entry.Earned);
            int score = entries.Sum(entry => entry.EarnedScore);
            _Subtitle.Text = $"{earned} EARNED  ·  {score} G";
            _Subtitle.Visibility = entries.Count == 0 ? ViewStates.Gone : ViewStates.Visible;

            AchievementGameAdapter adapter = new AchievementGameAdapter(this);
            adapter.SetItems(entries);
            _List.Adapter = adapter;

            _List.ItemClick += (_, e) =>
            {
                AchievementGameEntry entry = adapter[e.Position];
                Intent intent = new Intent(this, typeof(AchievementsActivity));
                intent.PutExtra(ExtraProductId, entry.ProductId);
                intent.PutExtra(ExtraGameName, entry.Name);
                StartActivity(intent);
            };

            ShowEmptyIfNeeded(entries.Count,
                "no achievements yet. install a game with a catalogue and its achievements appear here, locked, before you have earned any.");
        }

        private void LoadGameDetail(string productId)
        {
            string title = Intent?.GetStringExtra(ExtraGameName)
                           ?? HardcodedAchievementCatalogue.GameName(productId)
                           ?? "achievements";

            FindViewById<TextView>(Resource.Id.pageTitle)!.Text = title.ToLowerInvariant();

            List<Achievement> achievements = new List<Achievement>();
            try
            {
                achievements = AchievementContext.Current!.Achievements!
                    .AsNoTracking()
                    .Where(a => a.OwnProductId == productId)
                    .ToList();
                achievements = WPR.Shell.AchievementRollup.InDisplayOrder(achievements).ToList();
            }
            catch (Exception ex)
            {
                Log.Error(LogCategory.GamerServices, $"Achievement load failed for {productId}:\n{ex}");
            }

            _Subtitle.Text = WPR.Shell.AchievementRollup.Totalise(achievements).Summary;
            _Subtitle.Visibility = achievements.Count == 0 ? ViewStates.Gone : ViewStates.Visible;

            AchievementAdapter adapter = new AchievementAdapter(this);
            adapter.SetItems(achievements);
            _List.Adapter = adapter;

            ShowEmptyIfNeeded(achievements.Count, "this game has no achievement catalogue yet.");
        }

        private Dictionary<string, WPR.Models.Application> LoadApplicationsByProduct()
        {
            try
            {
                // Fully qualified: Activity inherits an ApplicationContext property from
                // ContextWrapper, which otherwise wins over the EF context type.
                return WPR.Models.ApplicationContext.Current.Applications!
                    .AsNoTracking()
                    .ToList()
                    .Where(app => !string.IsNullOrEmpty(app.ProductId))
                    .GroupBy(app => app.ProductId!)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.GamerServices, $"Could not read the application table for achievement names: {ex.Message}");
                return new Dictionary<string, WPR.Models.Application>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private void ShowEmptyIfNeeded(int count, string message)
        {
            bool empty = count == 0;
            _EmptyState.Text = message;
            _EmptyState.Visibility = empty ? ViewStates.Visible : ViewStates.Gone;
            _List.Visibility = empty ? ViewStates.Gone : ViewStates.Visible;
        }
    }
}
