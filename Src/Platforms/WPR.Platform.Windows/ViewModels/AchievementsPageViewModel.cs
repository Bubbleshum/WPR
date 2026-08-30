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

                var grouped = all
                    .GroupBy(a => a.OwnProductId ?? "")
                    .Where(g => !string.IsNullOrEmpty(g.Key))
                    .Select(g =>
                    {
                        apps.TryGetValue(g.Key, out var app);
                        return new AchievementGameItemViewModel(g.Key, app, g.ToList());
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

        private void RefreshAchievementsForSelectedGame()
        {
            if (_SelectedGame == null)
            {
                Achievements = new ObservableCollection<AchievementItemViewModel>();
                return;
            }

            var items = _SelectedGame.Achievements
                .OrderByDescending(a => a.IsEarned)
                .ThenByDescending(a => a.GamerScore)
                .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .Select(a => new AchievementItemViewModel(a));

            Achievements = new ObservableCollection<AchievementItemViewModel>(items);
        }
    }
}
