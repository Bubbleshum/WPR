using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Media.Imaging;
using ReactiveUI;

using Microsoft.Xna.Framework.GamerServices;
using WPR.Common;
using WPR.Models;

namespace WPR.Platform.Windows.ViewModels
{
    public class AchievementGameItemViewModel : ViewModelBase
    {
        private readonly Application? _App;
        private readonly string _ProductId;
        private readonly IReadOnlyList<Achievement> _Achievements;
        private Bitmap? _Icon;

        public AchievementGameItemViewModel(string productId, Application? app, IReadOnlyList<Achievement> achievements, string name)
        {
            _ProductId = productId;
            _App = app;
            _Achievements = achievements;
            _Name = name;
        }

        public string ProductId => _ProductId;
        public Application? App => _App;
        public IReadOnlyList<Achievement> Achievements => _Achievements;

        /// <summary>
        /// Resolved by the page (AchievementsPageViewModel.LocalName): the bundled catalogue, then
        /// the install record, then WPR Hub. Settable because the hub's answer arrives after the
        /// list is shown.
        /// </summary>
        public string Name
        {
            get => _Name;
            set => this.RaiseAndSetIfChanged(ref _Name, value);
        }
        private string _Name;

        /// <summary>True while the name is only the product id, i.e. still worth asking WPR Hub about.</summary>
        public bool NameIsProductId => string.Equals(_Name, _ProductId, System.StringComparison.OrdinalIgnoreCase);
        public string Author => _App?.Author ?? "";

        /// <summary>Aggregates for this game, computed once by the shared roll-up. Both shells
        /// read the same numbers; four hand-written copies of this arithmetic used to exist.</summary>
        private WPR.Shell.AchievementTotals Totals => _Totals ??= WPR.Shell.AchievementRollup.Totalise(_Achievements);
        private WPR.Shell.AchievementTotals? _Totals;

        public int Total => Totals.Total;
        public int Earned => Totals.Earned;
        public int TotalScore => Totals.TotalScore;
        public int EarnedScore => Totals.EarnedScore;

        public string Progress => Totals.Progress;
        public double ProgressPercent => Totals.Percent;

        public Bitmap? Icon
        {
            get
            {
                if (_Icon != null) return _Icon;

                // Not _App.IconPath directly: that names a file inside the install folder, which
                // an uninstall deletes along with the row it came from, while this list
                // deliberately keeps showing the game. GameIconStore holds the fallback.
                string? relative = GameIconStore.Resolve(_ProductId, _App?.IconPath);
                if (string.IsNullOrEmpty(relative)) return null;

                try
                {
                    var iconPath = Configuration.Current!.DataPath(relative!);
                    using var fs = new FileStream(iconPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    _Icon = Bitmap.DecodeToWidth(fs, 64);
                }
                catch
                {
                }

                return _Icon;
            }
        }
    }
}
