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

        public string Name => _Achievement.Name ?? "";
        public string Key => _Achievement.Key ?? "";
        public string Description =>
            string.IsNullOrEmpty(_Achievement.Description) ? _Achievement.HowToEarn ?? "" : _Achievement.Description;
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
