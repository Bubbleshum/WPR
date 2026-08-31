using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

using WPR;
using WPR.Common;

namespace WPR.Platform.Windows.Views
{
    /// <summary>
    /// Read-only diagnostics for one installed game, opened from the library's Info action.
    ///
    /// <para>The facts come from <see cref="GameDiagnostics"/>, which the Android head's info
    /// activity shares — only the presentation and the "environment" section below are this
    /// head's own. Change a fact there, not here.</para>
    ///
    /// <para>Values are selectable and <c>Copy all</c> puts the whole report on the clipboard,
    /// because the point of the page is getting a product id or a path out of the app and into
    /// a bug report.</para>
    /// </summary>
    public partial class GameInfoDialog : Window
    {
        private List<GameDiagnosticSection> _Sections = new List<GameDiagnosticSection>();

        public GameInfoDialog()
        {
            InitializeComponent();

            this.Get<Button>("closeButton").Click += (_, __) => Close();
            this.Get<Button>("copyAllButton").Click += async (_, __) => await CopyAllAsync();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        /// <summary>
        /// Populate from a product id. Called by the host before <see cref="Window.ShowDialog"/>.
        ///
        /// <para>Runs on the UI thread on purpose: <c>ApplicationContext</c> and
        /// <c>AchievementContext</c> are shared single instances and not thread-safe, and the
        /// queries are a few local SQLite rows. The one slow fact — the install folder walk — is
        /// started in the background and folded in when it lands.</para>
        /// </summary>
        public void Load(string? productId, string? gameName)
        {
            string product = GameDiagnostics.NormalizeProductId(productId);

            this.Get<TextBlock>("gameNameText").Text = string.IsNullOrWhiteSpace(gameName)
                ? product
                : gameName;

            _Sections = GameDiagnostics.Collect(product);
            _Sections.Add(new GameDiagnosticSection("environment", Environment()));

            Render();
            MeasureInstallFolderAsync(product);
        }

        /// <summary>The platform half of the report — the part <see cref="GameDiagnostics"/> can't own.</summary>
        private static List<GameDiagnosticField> Environment() => new List<GameDiagnosticField>
        {
            new GameDiagnosticField("data store", Configuration.Current!.DataStorePath ?? "(unset)"),
            new GameDiagnosticField("wpr", AppVersion.Display),
            new GameDiagnosticField("os", System.Environment.OSVersion.VersionString),
            new GameDiagnosticField("runtime", System.Environment.Version.ToString()),
            new GameDiagnosticField("process", System.Environment.Is64BitProcess ? "64-bit" : "32-bit"),
        };

        /// <summary>
        /// Rebuild the bound list. Cheap enough to do wholesale when the folder measurement
        /// lands — the report is a few dozen rows, and the alternative (change notification on
        /// every field) would be plumbing for one mutable value.
        /// </summary>
        private void Render()
        {
            this.Get<ItemsControl>("sectionsList").ItemsSource = _Sections
                .Select(s => new SectionRow(
                    s.Title.ToUpperInvariant(),
                    s.Fields.Select(f => new FieldRow(f.Label.ToUpperInvariant(), f.Value)).ToList()))
                .ToList();
        }

        private void MeasureInstallFolderAsync(string productId)
        {
            _ = Task.Run(() =>
            {
                string summary = GameDiagnostics.MeasureInstallFolder(productId);

                Dispatcher.UIThread.Post(() =>
                {
                    // The dialog may already be closed; a stale post must not throw.
                    if (!IsVisible) return;

                    _Sections = _Sections
                        .Select(section => new GameDiagnosticSection(
                            section.Title,
                            section.Fields
                                .Select(field => field.Label == GameDiagnostics.FolderContentsLabel
                                    ? field with { Value = summary }
                                    : field)
                                .ToList()))
                        .ToList();

                    Render();
                });
            });
        }

        private async Task CopyAllAsync()
        {
            TextBlock status = this.Get<TextBlock>("copyStatusText");

            try
            {
                if (Clipboard == null)
                {
                    status.Text = "clipboard unavailable";
                    return;
                }

                await Clipboard.SetTextAsync(GameDiagnostics.ToPlainText(_Sections));
                status.Text = "copied";
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppList, $"Game info: clipboard copy failed: {ex.Message}");
                status.Text = "copy failed";
            }
        }

        private sealed record FieldRow(string Label, string Value);

        private sealed record SectionRow(string Title, IReadOnlyList<FieldRow> Fields);
    }
}
