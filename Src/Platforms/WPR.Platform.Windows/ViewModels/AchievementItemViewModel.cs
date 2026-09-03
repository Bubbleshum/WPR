using System;
using System.IO;
using Avalonia.Media.Imaging;
using ReactiveUI;

using Microsoft.Xna.Framework.GamerServices;
using WPR.Common;

namespace WPR.Platform.Windows.ViewModels
{
    public class AchievementItemViewModel : ViewModelBase
    {
        private readonly Achievement _Achievement;
        private Bitmap? _Icon;

        public AchievementItemViewModel(Achievement achievement)
        {
            _Achievement = achievement;
        }

        public Achievement Model => _Achievement;

        /// <summary>Honours WP7 secret achievements: a secret one keeps its name hidden until
        /// earned. Until 2026-09-02 only the Android shell did this, so this page revealed the
        /// names and descriptions of unearned secret achievements.</summary>
        public string Name => WPR.Shell.AchievementRollup.DisplayName(_Achievement);
        public string Key => _Achievement.Key ?? "";
        public string Description => WPR.Shell.AchievementRollup.DescribeUnearnedSafely(_Achievement);
        public int GamerScore => _Achievement.GamerScore;
        public bool IsEarned => _Achievement.IsEarned;
        public bool IsLocked => !_Achievement.IsEarned;
        public string Status => _Achievement.IsEarned ? "Unlocked" : "Locked";
        public string EarnedDateText =>
            _Achievement.IsEarned && _Achievement.EarnedDateTime != default
                ? _Achievement.EarnedDateTime.ToString("MMM d, yyyy")
                : "";

        public Bitmap? Icon
        {
            get
            {
                if (_Icon != null) return _Icon;
                if (string.IsNullOrEmpty(_Achievement._IconPath)) return null;

                try
                {
                    var iconPath = Configuration.Current!.DataPath(_Achievement._IconPath);
                    using var fs = new FileStream(iconPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    _Icon = Bitmap.DecodeToWidth(fs, 96);
                }
                catch
                {
                }

                return _Icon;
            }
        }
    }
}
