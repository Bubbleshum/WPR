using WPR.Shell;
using Avalonia.Controls;
using Avalonia.ReactiveUI;
using WPR.Platform.Windows.ViewModels;
using WPR.Platform.Windows.Views;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using System.Reactive.Linq;
using WPR.Models;
using WPR.Common;
using Avalonia.Platform.Storage;
using Avalonia.Interactivity;

namespace WPR.Platform.Windows.Pages
{
    public partial class ApplicationListingPage : ReactiveUserControl<ApplicationListingPageViewModel>
    {
        private List<FilePickerFileType> AppInstallFileFilters;

        public ApplicationListingPage()
        {
            InitializeComponent();
            ApplicationListingPageViewModel vm = new ApplicationListingPageViewModel();
            DataContext = vm;

            AppInstallFileFilters = new List<FilePickerFileType>
            {
                new FilePickerFileType("XAP file")
                {
                    Patterns = new List<string> { "*.xap" }
                },
                new FilePickerFileType("All files")
                {
                    Patterns = new List<string> { "*.*" }
                }
            };

            // Persistent handler so both the "+" button and the per-item
            // Install command (for library-discovered XAPs) share the same prompt.
            vm.DeleteExistingAppInteraction.RegisterHandler(context =>
                Dispatcher.UIThread.InvokeAsync(async () =>
            {
                Application app = context.Input;
                var msgResult = await MessageBoxUtils.GetMessageDialogResult(
                    title: WPR.Shell.Resources.ApplicationAlreadyInstalled,
                    text: String.Format(WPR.Shell.Resources.ApplicationAlreadyInstalledDescription, app.Name),
                    icon: MessageBox.Avalonia.Enums.Icon.Question,
                    buttons: MessageBox.Avalonia.Enums.ButtonEnum.YesNo);

                context.SetOutput(msgResult == MessageBox.Avalonia.Enums.ButtonResult.Yes);
            }));

            // Both uninstall entry points (the game pane and the list's context menu) go through
            // the view-model, so both get this prompt.
            vm.UninstallInteraction.RegisterHandler(context =>
                Dispatcher.UIThread.InvokeAsync(async () =>
            {
                Application app = context.Input;
                var msgResult = await MessageBoxUtils.GetMessageDialogResult(
                    title: "Uninstall",
                    text: $"Uninstall {app.Name}?\n\n"
                        + "Do you also want to delete its save data?\n\n"
                        + "Yes: uninstall and permanently delete all save data (progress, high scores, "
                        + "settings). This cannot be undone.\n"
                        + "No: uninstall but keep the save data, so it comes back if you reinstall.\n"
                        + "Cancel: do nothing.\n\n"
                        + "Achievements are kept either way.",
                    icon: MessageBox.Avalonia.Enums.Icon.Question,
                    buttons: MessageBox.Avalonia.Enums.ButtonEnum.YesNoCancel);

                context.SetOutput(msgResult switch
                {
                    MessageBox.Avalonia.Enums.ButtonResult.Yes => UninstallChoice.DeleteSaveData,
                    MessageBox.Avalonia.Enums.ButtonResult.No => UninstallChoice.KeepSaveData,
                    // Cancel, or the window closed with the X.
                    _ => UninstallChoice.Cancel,
                });
            }));

            vm.InstallRequested += OnDiscoveredAppInstallRequested;
            vm.EditRequested += OnAppEditRequested;
            vm.InfoRequested += OnAppInfoRequested;
            vm.ControlsRequested += OnAppControlsRequested;
            vm.ClearDataRequested += OnAppClearDataRequested;

            this.Get<Button>("addNewAppButton").Click += AddNewAppButton_Click;

            var appListBox = this.Get<ListBox>("appListBox");
            appListBox.DoubleTapped += (_, _) =>
            {
                if (ViewModel?.ChoosenApp != null)
                {
                    ApplicationLaunchRequest.Ask(ViewModel.ChoosenApp.Model);
                }
            };
        }

        /// <summary>
        /// Open the read-only diagnostics dialog. Mirrors the Android head's info activity;
        /// both read their facts from <see cref="GameDiagnostics"/>.
        /// </summary>
        private async void OnAppInfoRequested(object? sender, ApplicationItemViewModel appItem)
        {
            if (appItem?.Model == null) return;

            try
            {
                var dialog = new GameInfoDialog();
                dialog.Load(appItem.ProductId, appItem.Name);
                await dialog.ShowDialog(GetWindow());
            }
            catch (Exception ex)
            {
                await MessageBoxUtils.ShowSelectableErrorAsync(
                    title: WPR.Shell.Resources.AppRunError,
                    body: ex.ToString());
            }
        }

        /// <summary>
        /// Open the per-game key-to-touch binding editor. Per game because the bindings live in the
        /// game's install folder and name coordinates on its screen; the global Controls page keeps
        /// the tilt keys and the Back key, which are game-independent.
        /// </summary>
        private async void OnAppControlsRequested(object? sender, ApplicationItemViewModel appItem)
        {
            if (appItem?.Model == null) return;

            string? productId = appItem.ProductId;
            if (string.IsNullOrEmpty(productId)) return;

            try
            {
                string folder = System.IO.Path.Combine(
                    Configuration.Current!.DataPath(WPR.Models.Application.DataStoreFolder), productId);

                var dialog = new GameControlsDialog();
                dialog.Load(appItem.Name ?? productId, folder);
                await dialog.ShowDialog(GetWindow());
            }
            catch (Exception ex)
            {
                await MessageBoxUtils.ShowSelectableErrorAsync(
                    title: WPR.Shell.Resources.AppRunError,
                    body: ex.ToString());
            }
        }

        /// <summary>
        /// Erase every file this game has saved, after a warning. The escape hatch for a game that
        /// no longer starts because of something in its saves — see
        /// <see cref="WPR.WindowsCompability.PerGameIsolatedStorage.ClearGameData"/>.
        /// </summary>
        private async void OnAppClearDataRequested(object? sender, ApplicationItemViewModel appItem)
        {
            if (appItem?.Model == null) return;

            string? productId = appItem.ProductId;
            if (string.IsNullOrEmpty(productId)) return;
            string name = appItem.Name ?? productId;

            // A game patched before v38 still reads the old shared store, so clearing its own store
            // would appear to do nothing. The desktop never repatches by itself, so say so.
            if (appItem.Model.PatchedVersion < WPR.ApplicationPatcher.Version)
            {
                await MessageBoxUtils.GetMessageDialogResult(
                    title: "Repatch first",
                    text: $"{name} was patched by an older version of WPR, so it still uses the save "
                        + "data shared by every game. Click Repatch, then clear its data.",
                    icon: MessageBox.Avalonia.Enums.Icon.Info);
                return;
            }

            var answer = await MessageBoxUtils.GetMessageDialogResult(
                title: "Clear game data",
                text: $"This permanently deletes ALL save data for {name}: progress, high scores, "
                    + "settings and unlocked content. It cannot be undone.\n\n"
                    + "Achievements are not affected. Close the game before continuing.\n\n"
                    + "Clear the data?",
                icon: MessageBox.Avalonia.Enums.Icon.Warning,
                buttons: MessageBox.Avalonia.Enums.ButtonEnum.YesNo);
            if (answer != MessageBox.Avalonia.Enums.ButtonResult.Yes) return;

            try
            {
                string installsRoot = Configuration.Current!.DataPath(WPR.Models.Application.DataStoreFolder);
                await Task.Run(() => WPR.WindowsCompability.PerGameIsolatedStorage.ClearGameData(productId, installsRoot));
                await MessageBoxUtils.GetMessageDialogResult(
                    title: "Clear game data",
                    text: $"Save data for {name} was deleted. It will start as if newly installed.");
            }
            catch (Exception ex)
            {
                await MessageBoxUtils.ShowSelectableErrorAsync(
                    title: "Could not clear game data",
                    body: $"Some files could not be deleted — if {name} is running, close it and try again.\n\n{ex}");
            }
        }

        private async void OnAppEditRequested(object? sender, ApplicationItemViewModel appItem)
        {
            if (appItem?.Model == null) return;

            var dialog = new EditApplicationDialog();
            dialog.SetInitialValues(appItem.Model);

            EditApplicationResult? result;
            try
            {
                result = await dialog.ShowDialogAsync(GetWindow());
            }
            catch (Exception ex)
            {
                await MessageBoxUtils.ShowSelectableErrorAsync(
                    title: WPR.Shell.Resources.AppRunError,
                    body: ex.ToString());
                return;
            }

            if (result == null) return;

            try
            {
                await ViewModel!.SaveApplicationEditAsync(
                    appItem,
                    name: result.Name,
                    description: result.Description,
                    author: result.Author,
                    publisher: result.Publisher,
                    version: result.Version);
            }
            catch (Exception ex)
            {
                await MessageBoxUtils.ShowSelectableErrorAsync(
                    title: WPR.Shell.Resources.AppRunError,
                    body: ex.ToString());
            }
        }

        private async void AddNewAppButton_Click(object? sender, RoutedEventArgs e)
        {
            var result = await GetStorageProvider().OpenFilePickerAsync(new FilePickerOpenOptions()
            {
                Title = "Choose XAP file",
                FileTypeFilter = AppInstallFileFilters
            });

            if (result == null || result.Count < 1) return;
            var file = result[0];

            ApplicationPreview? preview;
            using (var previewStream = await file.OpenReadAsync())
            {
                preview = ApplicationInstaller.ReadPreview(previewStream);
            }

            if (preview == null)
            {
                await MessageBoxUtils.GetMessageDialogResult(
                    title: WPR.Shell.Resources.InstallationFailed,
                    text: LocaleUtils.GetDisplayName(ApplicationInstallError.InvalidManifestFiles),
                    icon: MessageBox.Avalonia.Enums.Icon.Error);
                return;
            }

            await RunInstallAsync(async () => await file.OpenReadAsync(), preview);
        }

        private async void OnDiscoveredAppInstallRequested(object? sender, ApplicationItemViewModel appItem)
        {
            string? xapPath = appItem.XapFilePath;
            if (string.IsNullOrEmpty(xapPath) || !File.Exists(xapPath))
            {
                await MessageBoxUtils.GetMessageDialogResult(
                    title: WPR.Shell.Resources.InstallationFailed,
                    text: LocaleUtils.GetDisplayName(ApplicationInstallError.MissingManifestFiles),
                    icon: MessageBox.Avalonia.Enums.Icon.Error);
                return;
            }

            ApplicationPreview? preview = appItem.Preview;
            if (preview == null)
            {
                using FileStream previewStream = new FileStream(xapPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                preview = ApplicationInstaller.ReadPreview(previewStream);
            }

            if (preview == null)
            {
                await MessageBoxUtils.GetMessageDialogResult(
                    title: WPR.Shell.Resources.InstallationFailed,
                    text: LocaleUtils.GetDisplayName(ApplicationInstallError.InvalidManifestFiles),
                    icon: MessageBox.Avalonia.Enums.Icon.Error);
                return;
            }

            await RunInstallAsync(
                () => Task.FromResult<Stream>(new FileStream(xapPath, FileMode.Open, FileAccess.Read, FileShare.Read)),
                preview);
        }

        private async Task RunInstallAsync(Func<Task<Stream>> openStream, ApplicationPreview preview)
        {
            ApplicationInstallError err = await ViewModel!.InstallAsync(openStream, preview);

            if (err != ApplicationInstallError.None && err != ApplicationInstallError.Canceled)
            {
                await MessageBoxUtils.GetMessageDialogResult(
                    title: WPR.Shell.Resources.InstallationFailed,
                    text: LocaleUtils.GetDisplayName(err),
                    icon: MessageBox.Avalonia.Enums.Icon.Error);
            }

            ViewModel!.UpdateApplicationList(ViewModel!.SearchText);
        }

        Window GetWindow() => VisualRoot as Window ?? throw new NullReferenceException("Invalid Owner");
        TopLevel GetTopLevel() => VisualRoot as TopLevel ?? throw new NullReferenceException("Invalid Owner");
        IStorageProvider GetStorageProvider() => GetTopLevel().StorageProvider;
    }
}
