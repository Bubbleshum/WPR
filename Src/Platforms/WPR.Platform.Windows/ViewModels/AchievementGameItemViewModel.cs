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

        public AchievementGameItemViewModel(string productId, Application? app, IReadOnlyList<Achievement> achievements)
        {
            _ProductId = productId;
            _App = app;
            _Achievements = achievements;
        }

        public string ProductId => _ProductId;
        public Application? App => _App;
        public IReadOnlyList<Achievement> Achievements => _Achievements;

        public string Name => _App?.Name ?? _ProductId;
        public string Author => _App?.Author ?? "";

        public int Total => _Achievements.Count;
        public int Earned => _Achievements.Count(a => a.IsEarned);
        public int TotalScore => _Achievements.Sum(a => a.GamerScore);
        public int EarnedScore => _Achievements.Where(a => a.IsEarned).Sum(a => a.GamerScore);

        public string Progress => $"{Earned} / {Total}";
        public double ProgressPercent => Total == 0 ? 0 : (Earned * 100.0 / Total);

        public Bitmap? Icon
        {
            get
            {
                if (_Icon != null) return _Icon;
                if (_App == null || string.IsNullOrEmpty(_App.IconPath)) return null;

                try
                {
                    var iconPath = Configuration.Current!.DataPath(_App.IconPath);
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
