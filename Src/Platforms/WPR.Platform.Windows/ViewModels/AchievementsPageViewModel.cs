using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Threading;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

using WPR;
using Microsoft.Xna.Framework.GamerServices;
using WPR.Common;
using WPR.Models;

namespace WPR.Platform.Windows.ViewModels
{
    public class AchievementsPageViewModel : ViewModelBase
    {
        private ObservableCollection<AchievementGameItemViewModel> _Games;
        private AchievementGameItemViewModel? _SelectedGame;
        private ObservableCollection<AchievementItemViewModel> _Achievements;
        private bool _IsLoading;

        public ObservableCollection<AchievementGameItemViewModel> Games
        {
            get => _Games;
            private set => this.RaiseAndSetIfChanged(ref _Games, value);
        }

        public AchievementGameItemViewModel? SelectedGame
        {
            get => _SelectedGame;
            set
            {
                this.RaiseAndSetIfChanged(ref _SelectedGame, value);
                RefreshAchievementsForSelectedGame();
                this.RaisePropertyChanged(nameof(HasSelection));
            }
        }

        public ObservableCollection<AchievementItemViewModel> Achievements
        {
            get => _Achievements;
            private set => this.RaiseAndSetIfChanged(ref _Achievements, value);
        }

        public bool IsLoading
        {
            get => _IsLoading;
            private set => this.RaiseAndSetIfChanged(ref _IsLoading, value);
        }

        public bool HasSelection => _SelectedGame != null;

        public AchievementsPageViewModel()
        {
            _Games = new ObservableCollection<AchievementGameItemViewModel>();
            _Achievements = new ObservableCollection<AchievementItemViewModel>();

            _ = LoadAsync();

            // A sign-in (or start-up) brought the account's achievements down from WPR Hub. The
            // page lives as long as the window, so the subscription does too.
            WPR.Shell.HubSetup.ProgressRestored += earned =>
                Dispatcher.UIThread.Post(() => _ = LoadAsync(_SelectedGame?.ProductId));
        }

        public async Task LoadAsync(string? selectProductId = null)
        {
            IsLoading = true;
            try
            {
                List<Achievement> all = await AchievementContext.Current!.Achievements!
                    .AsNoTracking()
                    .ToListAsync();

                Dictionary<string, Application> apps;
                try
                {
                    apps = ApplicationContext.Current.Applications!
                        .AsNoTracking()
                        .ToList()
                        .GroupBy(a => a.ProductId)
                        .ToDictionary(g => g.Key, g => g.First());
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ex] AchievementsPage: cannot load applications:\n{ex}");
                    apps = new Dictionary<string, Application>();
                }

                var grouped = WPR.Shell.AchievementRollup.ByProduct(all)
                    .Select(g =>
                    {
                        apps.TryGetValue(g.Key, out var app);
                        return new AchievementGameItemViewModel(g.Key, app, g.ToList(), LocalName(g.Key, app));
                    })
                    .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Games = new ObservableCollection<AchievementGameItemViewModel>(grouped);
                    SelectedGame = (selectProductId != null
                        ? grouped.FirstOrDefault(g => g.ProductId == selectProductId)
                        : null) ?? grouped.FirstOrDefault();
                });

                _ = FillNamesFromHubAsync(grouped);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ex] AchievementsPage: load failed:\n{ex}");
                Log.Error(LogCategory.GamerServices, $"AchievementsPage load failed:\n{ex}");
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>
        /// A game's name from what is on this machine, in the same order as the Android page: the
        /// bundled achievement catalogue first (its names are curated, where an install record can
        /// carry the raw manifest title), then the install record, then a name WPR Hub gave earlier.
        /// The product id only when none of them knows the game.
        /// </summary>
        private static string LocalName(string productId, Application? app) =>
            HardcodedAchievementCatalogue.GameName(productId)
            ?? (string.IsNullOrWhiteSpace(app?.Name) ? null : app!.Name)
            ?? WPR.Shell.HubTitleNames.Cached(productId)
            ?? productId;

        /// <summary>Asks WPR Hub for the games still showing a product id, and names them as answers arrive.</summary>
        private static async Task FillNamesFromHubAsync(IReadOnlyList<AchievementGameItemViewModel> games)
        {
            var unnamed = games.Where(g => g.NameIsProductId).ToList();
            if (unnamed.Count == 0) return;

            IReadOnlyDictionary<string, string> found = await WPR.Shell.HubTitleNames.FetchMissingAsync(unnamed.Select(g => g.ProductId));
            if (found.Count == 0) return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var game in unnamed)
                {
                    if (found.TryGetValue(game.ProductId, out string? name)) game.Name = name;
                }
            });
        }

        private void RefreshAchievementsForSelectedGame()
        {
            if (_SelectedGame == null)
            {
                Achievements = new ObservableCollection<AchievementItemViewModel>();
                return;
            }

            var items = WPR.Shell.AchievementRollup.InDisplayOrder(_SelectedGame.Achievements)
                .Select(a => new AchievementItemViewModel(a));

            Achievements = new ObservableCollection<AchievementItemViewModel>(items);
        }
    }
}
