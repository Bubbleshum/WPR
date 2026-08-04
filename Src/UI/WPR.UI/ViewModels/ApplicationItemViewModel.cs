using Avalonia.Media.Imaging;
using System;
using System.IO;
using System.Reactive;
using System.Text.RegularExpressions;
using ReactiveUI;

using WPR;
using WPR.Common;
using WPR.Models;

namespace WPR.UI.ViewModels
{
    public class ApplicationItemViewModel : ViewModelBase
    {
        private readonly Application? _App;
        private readonly ApplicationPreview? _Preview;
        private readonly InstallingAppViewModel? _Installing;
        private readonly string? _XapFilePath;
        private Bitmap? _Icon;
        private IDisposable? _InstallingProgressSub;

        public int IconSize => 90;
        public int Height => 160;

        public ReactiveCommand<Unit, Unit> RunAppCommand { get; }
        public ReactiveCommand<Unit, Unit> UninstallAppCommand { get; }
        public ReactiveCommand<Unit, Unit> InstallAppCommand { get; }
        public ReactiveCommand<Unit, Unit> RepatchAppCommand { get; }
        public ReactiveCommand<Unit, Unit> EditAppCommand { get; }

        public event EventHandler<ApplicationItemViewModel>? UninstallRequested;
        public event EventHandler<ApplicationItemViewModel>? InstallRequested;
        public event EventHandler<ApplicationItemViewModel>? RepatchRequested;
        public event EventHandler<ApplicationItemViewModel>? EditRequested;

        public ApplicationItemViewModel(Application app)
        {
            _App = app;
            RunAppCommand = ReactiveCommand.Create(RunApp);
            UninstallAppCommand = ReactiveCommand.Create(UninstallApp);
            InstallAppCommand = ReactiveCommand.Create(() => { });
            RepatchAppCommand = ReactiveCommand.Create(RepatchApp);
            EditAppCommand = ReactiveCommand.Create(EditApp);
        }

        public ApplicationItemViewModel(string xapFilePath, ApplicationPreview preview)
        {
            _XapFilePath = xapFilePath;
            _Preview = preview;
            RunAppCommand = ReactiveCommand.Create(() => { });
            UninstallAppCommand = ReactiveCommand.Create(() => { });
            InstallAppCommand = ReactiveCommand.Create(InstallApp);
            RepatchAppCommand = ReactiveCommand.Create(() => { });
            EditAppCommand = ReactiveCommand.Create(() => { });
        }

        /// <summary>
        /// Construct a library list entry representing an in-flight install.
        /// Replaces the discovered "available" entry for the same product while
        /// the install is running. The entry renders with a progress bar and
        /// (via the listing view-model's selection handler) navigates to the
        /// install detail pane on click.
        /// </summary>
        public ApplicationItemViewModel(InstallingAppViewModel installing)
        {
            _Installing = installing;
            RunAppCommand = ReactiveCommand.Create(() => { });
            UninstallAppCommand = ReactiveCommand.Create(() => { });
            InstallAppCommand = ReactiveCommand.Create(() => { });
            RepatchAppCommand = ReactiveCommand.Create(() => { });
            EditAppCommand = ReactiveCommand.Create(() => { });

            // Re-raise PropertyChanged for our Progress when the installer ticks.
            _InstallingProgressSub = installing.WhenAnyValue(i => i.Progress)
                .Subscribe(_ => this.RaisePropertyChanged(nameof(Progress)));
        }

        internal Application? App => _App;

        public Application? Model => _App;
        public ApplicationPreview? Preview => _Preview;
        public InstallingAppViewModel? Installing => _Installing;
        public string? XapFilePath => _XapFilePath;

        public bool IsInstalled => _App != null;
        public bool IsAvailable => _App == null && _Installing == null;

        /// <summary>
        /// Whether the user may edit this game's name/description/etc. False for games that ship a
        /// hardcoded catalogue (<c>Database/Achievements/&lt;productId&gt;/</c>): the catalogue is the
        /// authoritative source for their metadata (it backs both the window title and the game-page
        /// description), so an edit would be silently overridden and is disallowed.
        /// </summary>
        public bool IsEditable => IsInstalled && !HardcodedAchievementCatalogue.HasCatalogue(ProductId ?? "");

        /// <summary>
        /// True when this game has no hardcoded achievement catalogue under
        /// <c>Database/Achievements/&lt;productId&gt;/</c>. Drives the game-page "Game ID" field,
        /// which surfaces the product id so an as-yet-uncatalogued game can be looked up and added.
        /// </summary>
        public bool IsNotInCatalogue => !HardcodedAchievementCatalogue.HasCatalogue(ProductId ?? "");
        public bool IsInstalling => _Installing != null;
        public int Progress => _Installing?.Progress ?? 0;

        public string? Name =>
            HardcodedAchievementCatalogue.GameName(ProductId ?? "")
            ?? _App?.Name ?? PreviewName ?? _Installing?.Name;

        /// <summary>
        /// Display name for a pre-install (discovered) entry when it has no catalogue: the cleaned
        /// XAP file name rather than the manifest <c>&lt;App Title&gt;</c>, which for many WP games is a
        /// resource ref (<c>@AppResLib.dll,-100</c>) or a truncated internal name. Falls back to the
        /// manifest title only if a name can't be derived from the file path. Null for non-preview VMs.
        /// </summary>
        private string? PreviewName
        {
            get
            {
                if (_Preview == null) return null;
                return CleanXapFileName(_XapFilePath)
                    ?? (string.IsNullOrWhiteSpace(_Preview.Name) ? null : _Preview.Name);
            }
        }

        /// <summary>
        /// Derive a display name from a XAP file path by dropping the extension and a trailing
        /// version token (e.g. <c>" v1.2.0.0"</c>, <c>"_1.1.5.0"</c>) and tidying separators. Mirrors
        /// the naming used when the achievement catalogue was seeded from these same file names.
        /// </summary>
        private static string? CleanXapFileName(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            string name;
            try { name = Path.GetFileNameWithoutExtension(path); }
            catch { return null; }
            if (string.IsNullOrEmpty(name)) return null;

            // Strip a trailing version token: a 'v'-prefixed version ("v1.2.0.0") or a dotted one
            // ("1.0.3.0"). A bare single trailing digit is left alone so titles like "Asphalt 5"
            // or "Connect 4" (files with no version suffix) keep their number.
            name = Regex.Replace(name, @"[\s_]+(v\d[\d.]*|\d+(\.\d+)+)$", "", RegexOptions.IgnoreCase);
            name = name.Replace('_', ' ');
            name = Regex.Replace(name, @"\s+", " ").Trim();
            return name.Length == 0 ? null : name;
        }
        public string? Author => _App?.Author ?? _Preview?.Author ?? _Installing?.Author;
        public string? Publisher => _App?.Publisher ?? _Preview?.Publisher ?? _Installing?.Publisher;
        public string? Description =>
            HardcodedAchievementCatalogue.GameDescription(ProductId ?? "")
            ?? _App?.Description ?? _Preview?.Description ?? _Installing?.Description;
        public string? Version => _App?.Version ?? _Preview?.Version ?? _Installing?.Version;
        public string? ProductId => _App?.ProductId ?? _Preview?.ProductId ?? _Installing?.ProductId;
        public ApplicationType? ApplicationType => _App?.ApplicationType ?? _Preview?.ApplicationType ?? _Installing?.ApplicationType;

        /// <summary>
        /// Single uppercase eyebrow label for the detail hero. Prefers the
        /// runtime type name ("SILVERLIGHT" / "XNA" / "MODERNNATIVE") when it's
        /// resolvable from either the installed Application or the preview's
        /// manifest, and falls back to "AVAILABLE TO INSTALL" only when there
        /// is genuinely no type information (e.g. corrupt manifest). Lets the
        /// XAML bind one TextBlock instead of juggling visibility conditions.
        /// </summary>
        public string TypeLabel
        {
            get
            {
                if (IsInstalling) return "INSTALLING";
                var t = ApplicationType;
                if (t.HasValue) return t.Value.ToString().ToUpperInvariant();
                return IsInstalled ? "" : "AVAILABLE TO INSTALL";
            }
        }
        public DateTime? InstalledTime => _App?.InstalledTime;

        public string Tooltip
        {
            get
            {
                string name = Name ?? "";
                string desc = Description ?? "";
                if (!IsInstalled) name += "  (available)";
                return desc.Length == 0 ? name : $"{name}\n\n{desc}";
            }
        }

        public Bitmap? Icon
        {
            get
            {
                if (_Icon == null)
                {
                    try
                    {
                        if (_App != null)
                        {
                            string iconpath = Configuration.Current!.DataPath(_App.IconPath);
                            using FileStream fs = new FileStream(iconpath, FileMode.Open, FileAccess.Read, FileShare.Read);
                            _Icon = Bitmap.DecodeToWidth(fs, IconSize);
                        }
                        else if (_Preview?.IconBytes != null)
                        {
                            using MemoryStream ms = new MemoryStream(_Preview.IconBytes);
                            _Icon = Bitmap.DecodeToWidth(ms, IconSize);
                        }
                        else if (_Installing?.Icon != null)
                        {
                            // The InstallingAppViewModel already decoded an icon
                            // from preview bytes; reuse it instead of re-decoding.
                            _Icon = _Installing.Icon;
                        }
                    }
                    catch { }
                }

                return _Icon;
            }
        }

        private void RunApp()
        {
            if (_App != null) WPR.UI.ApplicationLaunchRequest.Ask(_App);
        }

        private void UninstallApp()
        {
            UninstallRequested?.Invoke(this, this);
        }

        private void InstallApp()
        {
            InstallRequested?.Invoke(this, this);
        }

        private void RepatchApp()
        {
            RepatchRequested?.Invoke(this, this);
        }

        private void EditApp()
        {
            // Catalogue-managed games own their metadata; never open the edit dialog for them
            // even if a command somehow fires (the UI entry points are hidden via IsEditable).
            if (!IsEditable) return;
            EditRequested?.Invoke(this, this);
        }

        /// <summary>
        /// Push PropertyChanged for the user-editable detail fields so the
        /// listing and hero pane refresh after an edit dialog writes new
        /// values onto the underlying <see cref="Application"/>. Only the
        /// installed entry path is editable — the preview/installing constructors
        /// have no DB row to update.
        /// </summary>
        public void NotifyEdited()
        {
            this.RaisePropertyChanged(nameof(Name));
            this.RaisePropertyChanged(nameof(Description));
            this.RaisePropertyChanged(nameof(Author));
            this.RaisePropertyChanged(nameof(Publisher));
            this.RaisePropertyChanged(nameof(Version));
            this.RaisePropertyChanged(nameof(Tooltip));
        }
    }
}
