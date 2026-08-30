using ReactiveUI;
using System.Threading.Tasks;
using WPR;
using WPR.Models;
using WPR.Common;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Xna.Framework.GamerServices;
using System.Linq;
using System.Threading;
using System.Reactive;
using Avalonia.Threading;
using System;
using System.Reactive.Linq;
using System.Collections.ObjectModel;
using System.IO;
using System.Diagnostics;

namespace WPR.Platform.Windows.ViewModels
{
    public class ApplicationListingPageViewModel : ViewModelBase
    {
        private string _SearchText;
        private ObservableCollection<ApplicationItemViewModel> _Applications;
        private ApplicationItemViewModel? _ChoosenApp;
        private InstallingAppViewModel? _Installing;
        private bool _IsViewingInstall;
        private ObservableCollection<AchievementItemViewModel> _Achievements = new();
        private int _EarnedCount;
        private int _TotalCount;
        private int _EarnedScore;
        private int _TotalScore;

        private readonly LibraryScanner _LibraryScanner;
        private readonly List<DiscoveredApplication> _Discovered = new List<DiscoveredApplication>();

        public ReactiveCommand<string, Unit> AppSearchCommand;


        public ReactiveCommand<ApplicationItemViewModel?, Unit> RunAppCommand;

        public Interaction<Application, bool> DeleteExistingAppInteraction;

        public ReactiveCommand<ApplicationItemViewModel?, Unit> DeleteAppCommand;

        public ReactiveCommand<Unit, Unit> ShowInstallProgressCommand;

        public event EventHandler<ApplicationItemViewModel>? InstallRequested;
        public event EventHandler<ApplicationItemViewModel>? EditRequested;


        public string SearchText
        {
            get { return _SearchText; }
            set { this.RaiseAndSetIfChanged(ref _SearchText, value); }
        }

        public ApplicationItemViewModel? ChoosenApp
        {
            get { return _ChoosenApp; }
            set
            {
                this.RaiseAndSetIfChanged(ref _ChoosenApp, value);
                if (value != null && value.IsInstalling)
                {
                    // Selecting the in-flight install item navigates to the
                    // install detail pane (showing the progress bar) instead
                    // of treating it like an ordinary app detail.
                    IsViewingInstall = true;
                }
                else if (value != null && _IsViewingInstall)
                {
                    // Selecting any other app implicitly leaves the install
                    // progress view.
                    IsViewingInstall = false;
                }
                this.RaisePropertyChanged(nameof(IsDetailEmpty));
                this.RaisePropertyChanged(nameof(IsDetailApp));
                _ = LoadAchievementsForChoosenAppAsync();
            }
        }

        public ObservableCollection<AchievementItemViewModel> Achievements
        {
            get => _Achievements;
            private set => this.RaiseAndSetIfChanged(ref _Achievements, value);
        }

        public int EarnedCount
        {
            get => _EarnedCount;
            private set
            {
                this.RaiseAndSetIfChanged(ref _EarnedCount, value);
                this.RaisePropertyChanged(nameof(ProgressLabel));
                this.RaisePropertyChanged(nameof(ProgressPercent));
            }
        }

        public int TotalCount
        {
            get => _TotalCount;
            private set
            {
                this.RaiseAndSetIfChanged(ref _TotalCount, value);
                this.RaisePropertyChanged(nameof(ProgressLabel));
                this.RaisePropertyChanged(nameof(ProgressPercent));
                this.RaisePropertyChanged(nameof(HasAchievements));
            }
        }

        public int EarnedScore
        {
            get => _EarnedScore;
            private set
            {
                this.RaiseAndSetIfChanged(ref _EarnedScore, value);
                this.RaisePropertyChanged(nameof(ScoreLabel));
            }
        }

        public int TotalScore
        {
            get => _TotalScore;
            private set
            {
                this.RaiseAndSetIfChanged(ref _TotalScore, value);
                this.RaisePropertyChanged(nameof(ScoreLabel));
            }
        }

        public bool HasAchievements => _TotalCount > 0;
        public string ProgressLabel => $"{_EarnedCount} / {_TotalCount}";
        public string ScoreLabel => $"{_EarnedScore} / {_TotalScore} G";
        public double ProgressPercent => _TotalCount == 0 ? 0 : _EarnedCount * 100.0 / _TotalCount;

        private async Task LoadAchievementsForChoosenAppAsync()
        {
            string? productId = _ChoosenApp?.ProductId;
            if (string.IsNullOrEmpty(productId))
            {
                Achievements = new ObservableCollection<AchievementItemViewModel>();
                EarnedCount = 0;
                TotalCount = 0;
                EarnedScore = 0;
                TotalScore = 0;
                return;
            }

            try
            {
                List<Achievement> rows = await AchievementContext.Current!.Achievements!
                    .AsNoTracking()
                    .Where(a => a.OwnProductId == productId)
                    .ToListAsync();

                var ordered = rows
                    .OrderByDescending(a => a.IsEarned)
                    .ThenByDescending(a => a.GamerScore)
                    .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(a => new AchievementItemViewModel(a))
                    .ToList();

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Achievements = new ObservableCollection<AchievementItemViewModel>(ordered);
                    TotalCount = rows.Count;
                    EarnedCount = rows.Count(r => r.IsEarned);
                    TotalScore = rows.Sum(r => r.GamerScore);
                    EarnedScore = rows.Where(r => r.IsEarned).Sum(r => r.GamerScore);
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ex] ApplicationListingPage: failed to load achievements for {productId}:\n{ex}");
                Log.Error(LogCategory.GamerServices,
                    $"ApplicationListingPage achievement load failed for {productId}:\n{ex}");
            }
        }

        public CancellationTokenSource? CancelSource { get; set; }

        public InstallingAppViewModel? Installing
        {
            get { return _Installing; }
            set
            {
                this.RaiseAndSetIfChanged(ref _Installing, value);
                // Focus the install pane on start; release it on completion.
                IsViewingInstall = value != null;
                this.RaisePropertyChanged(nameof(IsDetailEmpty));
                this.RaisePropertyChanged(nameof(IsDetailApp));
                this.RaisePropertyChanged(nameof(IsDetailInstalling));
                // Rebuild the library list so the discovered "available" entry
                // for this product is replaced by an "installing" entry — and
                // restored to "available" once the install completes / cancels.
                UpdateApplications();
            }
        }

        public bool IsViewingInstall
        {
            get { return _IsViewingInstall; }
            private set
            {
                this.RaiseAndSetIfChanged(ref _IsViewingInstall, value);
                this.RaisePropertyChanged(nameof(IsDetailEmpty));
                this.RaisePropertyChanged(nameof(IsDetailApp));
                this.RaisePropertyChanged(nameof(IsDetailInstalling));
            }
        }

        public bool IsDetailEmpty => _ChoosenApp == null && !_IsViewingInstall;
        public bool IsDetailApp => _ChoosenApp != null && !_IsViewingInstall;
        public bool IsDetailInstalling => _IsViewingInstall && _Installing != null;

        public ObservableCollection<ApplicationItemViewModel> Applications {
            get { return _Applications; }
            set
            {
                this.RaiseAndSetIfChanged(ref _Applications, value);
                this.RaisePropertyChanged(nameof(ShowEmptyHint));
            }
        }

        public bool ShowEmptyHint => Applications.Count == 0;

        public void UpdateApplicationList(string text)
        {
            try
            {
                string filter = (text ?? "").ToLower();

                // Materialise the DB rows first, then filter/sort on the view-model's resolved
                // Name — for catalogue games that's the curated catalogue name, which can't be
                // expressed in SQL. Keeps search + ordering consistent with what's displayed.
                List<ApplicationItemViewModel> installed = ApplicationContext.Current.Applications!
                    .ToList()
                    .Select(app => new ApplicationItemViewModel(app))
                    .Where(vm => (vm.Name ?? "").ToLower().Contains(filter))
                    .OrderBy(vm => (vm.Name ?? "").ToLower())
                    .ToList();

                HashSet<string> installedProductIds = new HashSet<string>(
                    installed.Where(i => !string.IsNullOrEmpty(i.ProductId)).Select(i => i.ProductId!),
                    StringComparer.OrdinalIgnoreCase);

                // Suppress the discovered entry for whatever's installing right
                // now — we'll insert an installing-mode entry in its place below
                // so the library list shows one row per product rather than
                // duplicating "available" + "installing" for the same app.
                string? installingProductId = _Installing?.ProductId;

                List<ApplicationItemViewModel> discovered = _Discovered
                    .Where(d => !installedProductIds.Contains(d.Preview.ProductId))
                    .Where(d => installingProductId == null
                        || !string.Equals(d.Preview.ProductId, installingProductId, StringComparison.OrdinalIgnoreCase))
                    .Select(d => new ApplicationItemViewModel(d.XapFilePath, d.Preview))
                    .Where(vm => (vm.Name ?? "").ToLower().Contains(filter))
                    .OrderBy(vm => (vm.Name ?? "").ToLower())
                    .ToList();

                IEnumerable<ApplicationItemViewModel> combined = installed.Concat(discovered);

                // Splice in an installing item. It's not in either source list
                // (installed = DB rows, discovered = un-installing scanner) so
                // it lives alongside both. Put it at the top of the available
                // section by ordering it after installed items.
                if (_Installing != null
                    && (_Installing.Name ?? "").ToLower().Contains(filter))
                {
                    var installingItem = new ApplicationItemViewModel(_Installing);
                    combined = installed.Concat(new[] { installingItem }).Concat(discovered);
                }

                _ChoosenApp = null;

                Applications =
                    new ObservableCollection<ApplicationItemViewModel>(combined);

                foreach (ApplicationItemViewModel appItem in Applications)
                {
                    appItem.UninstallRequested += OnAppUninstallRequested;
                    appItem.InstallRequested += OnAppInstallRequested;
                    appItem.RepatchRequested += OnAppRepatchRequested;
                    appItem.EditRequested += OnAppEditRequested;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine( $"[ex] ApplicationListingViewModel: " +
                    $"Unable to query application database with exception:\n {ex}");
                Log.Error(LogCategory.AppList,
                    $"Unable to query application database with exception:\n {ex}");
                Applications = new ObservableCollection<ApplicationItemViewModel>();
            }
        }

        private void OnAppUninstallRequested(object? sender, ApplicationItemViewModel appItem)
        {
            _ = DeleteApplicationAsync(appItem);
        }

        private void OnAppInstallRequested(object? sender, ApplicationItemViewModel appItem)
        {
            // Surface to the page, which owns the dialogs (delete-existing prompt,
            // result message box) and the file/stream lifecycle for the install flow.
            InstallRequested?.Invoke(this, appItem);
        }

        private void OnAppRepatchRequested(object? sender, ApplicationItemViewModel appItem)
        {
            _ = RepatchApplicationAsync(appItem);
        }

        private void OnAppEditRequested(object? sender, ApplicationItemViewModel appItem)
        {
            // The page owns dialogs (modal Window lifetime, MainWindow as owner).
            // Bubble up rather than constructing the dialog from the VM.
            EditRequested?.Invoke(this, appItem);
        }

        /// <summary>
        /// Apply user-edited metadata onto the EF-tracked Application row and
        /// persist. The item VM's underlying <see cref="Application"/> reference
        /// is the same tracked instance returned by the initial query, so direct
        /// property writes plus SaveChangesAsync are enough — no Update() call
        /// needed. Pushes property-changed for the displayed fields so the hero
        /// and list refresh without rebuilding the collection (which would clear
        /// the user's current selection).
        /// </summary>
        public async Task SaveApplicationEditAsync(
            ApplicationItemViewModel appItem,
            string name,
            string description,
            string author,
            string publisher,
            string version)
        {
            if (appItem?.Model == null) return;

            Application app = appItem.Model;
            app.Name = name ?? string.Empty;
            app.Description = description ?? string.Empty;
            app.Author = author ?? string.Empty;
            app.Publisher = publisher ?? string.Empty;
            app.Version = version ?? string.Empty;

            try
            {
                await ApplicationContext.Current.SaveChangesAsync();
                appItem.NotifyEdited();
                // Detail pane binds via ChoosenApp.Name etc. — appItem.NotifyEdited
                // raises those. ChoosenApp itself doesn't need to flip; the
                // bindings re-evaluate when the inner property-changed fires.
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ex] ApplicationListingViewModel: failed to save edit for {app.ProductId}:\n{ex}");
                Log.Error(LogCategory.AppList,
                    $"Failed to save edited metadata for {app.ProductId}:\n{ex}");
                throw;
            }
        }

        /// <summary>
        /// Re-run the patcher on an already-installed app without re-extracting from the
        /// XAP. Cheaper than a full uninstall + reinstall when the only thing that changed
        /// in WPR is the patcher's redirect table — and avoids the file-lock pitfalls of
        /// blowing away the install folder while a previous launch's AssemblyLoadContext
        /// is still finalising.
        /// </summary>
        private async Task RepatchApplicationAsync(ApplicationItemViewModel appItem)
        {
            if (appItem?.Model == null) return;

            CancelSource = new CancellationTokenSource();
            Installing = new InstallingAppViewModel(
                new ApplicationPreview
                {
                    Name = appItem.Model.Name,
                    Author = appItem.Model.Author,
                    Publisher = appItem.Model.Publisher,
                    Description = appItem.Model.Description,
                    Version = appItem.Model.Version,
                    ProductId = appItem.Model.ProductId,
                    ApplicationType = appItem.Model.ApplicationType,
                },
                () => CancelSource?.Cancel());

            try
            {
                await ApplicationInstaller.RepatchAsync(
                    appItem.Model,
                    progress => Dispatcher.UIThread.Post(() =>
                    {
                        if (Installing != null) Installing.Progress = progress;
                    }),
                    CancelSource.Token);
            }
            finally
            {
                Installing = null;
                UpdateApplications();
            }
        }

        public void UpdateApplications()
        {
            UpdateApplicationList(SearchText);
        }

        public ApplicationListingPageViewModel()
        {
            _SearchText = "";
            Applications = new ObservableCollection<ApplicationItemViewModel>();
            DeleteExistingAppInteraction = new Interaction<Application, bool>();

            // In ApplicationListingPageViewModel constructor
            var deleteEnabled = this.WhenAnyValue(x => x.ChoosenApp).Select(x => x != null && x.IsInstalled);
            DeleteAppCommand = ReactiveCommand.CreateFromTask<ApplicationItemViewModel>(DeleteApplicationAsync, deleteEnabled);
            RunAppCommand = ReactiveCommand.CreateFromTask<ApplicationItemViewModel>(RunApplicationAsync, deleteEnabled);

            ShowInstallProgressCommand = ReactiveCommand.Create(() =>
            {
                if (_Installing != null) IsViewingInstall = true;
            });

            this.WhenAnyValue(v => v.SearchText)
                .Throttle(TimeSpan.FromMilliseconds(20))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(text => UpdateApplicationList(text));

            _LibraryScanner = new LibraryScanner();
            _LibraryScanner.Discovered += OnLibraryDiscovered;
            _LibraryScanner.Removed += OnLibraryRemoved;
            _LibraryScanner.Path = Configuration.Current?.GameLibraryPath;

            // Initial scan: synchronous so the first paint shows discovered items.
            // The folder is local and small in practice; fine to do on the UI thread.
            foreach (DiscoveredApplication entry in _LibraryScanner.ScanOnce())
            {
                _Discovered.Add(entry);
            }

            Configuration.GameLibraryPathChanged += OnConfigurationLibraryPathChanged;

            UpdateApplicationList(SearchText);
        }

        private void OnConfigurationLibraryPathChanged(object? sender, string? newPath)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _Discovered.Clear();
                _LibraryScanner.Path = newPath;
                foreach (DiscoveredApplication entry in _LibraryScanner.ScanOnce())
                {
                    _Discovered.Add(entry);
                }
                UpdateApplications();
            });
        }

        private void OnLibraryDiscovered(object? sender, DiscoveredApplication entry)
        {
            Dispatcher.UIThread.Post(() =>
            {
                int idx = _Discovered.FindIndex(d =>
                    string.Equals(d.XapFilePath, entry.XapFilePath, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) _Discovered[idx] = entry;
                else _Discovered.Add(entry);
                UpdateApplications();
            });
        }

        private void OnLibraryRemoved(object? sender, string path)
        {
            Dispatcher.UIThread.Post(() =>
            {
                int removed = _Discovered.RemoveAll(d =>
                    string.Equals(d.XapFilePath, path, StringComparison.OrdinalIgnoreCase));
                if (removed > 0) UpdateApplications();
            });
        }

            public async Task<ApplicationInstallError> InstallAsync(
                Func<Task<Stream>> openStream,
                ApplicationPreview preview)
            {
                CancelSource = new CancellationTokenSource();
                Installing = new InstallingAppViewModel(preview, () => CancelSource?.Cancel());

                try
                {
                    using Stream stream = await openStream();
                    return await ApplicationInstaller.Install(
                        stream,
                        progress => Dispatcher.UIThread.Post(() =>
                        {
                            if (Installing != null) Installing.Progress = progress;
                        }),
                        app => DeleteExistingAppInteraction.Handle(app),
                        CancelSource.Token);
                }
                finally
                {
                    Installing = null;
                }
            }

            private async Task RunApplicationAsync(ApplicationItemViewModel app) {
                if (app?.Model != null) ApplicationLaunchRequest.Ask(app.Model);
            }

            private async Task DeleteApplicationAsync(ApplicationItemViewModel app) {
                if (app?.Model == null) return;
                // Uninstall = remove DB row AND best-effort delete the install folder.
                // Before this, uninstall only removed the row, leaving %LocalAppData%\WPR\AppData\<id>
                // on disk. If a previous launch had any DLL still locked (leaked ALC,
                // antivirus, etc.) and the user tried to reinstall, the extract would
                // crash with "process cannot access the file." The installer retries
                // through transient locks via GC.Collect + backoff.
                await ApplicationInstaller.UninstallAsync(app.Model);
                Applications.Remove(app);
                UpdateApplications();
            }
    }
}
