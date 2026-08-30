using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;

using Microsoft.Xna.Framework.GamerServices;

using WPR.Common;

namespace WPR.Platform.Android.Native
{
    /// <summary>
    /// Shared bitmap cache for the achievement views. Both adapters decode small PNGs off
    /// the same data store, and a null entry is a remembered failure so a missing icon is
    /// not re-probed on every fling frame.
    /// </summary>
    internal sealed class IconCache
    {
        private readonly Dictionary<string, Bitmap?> _Cache = new Dictionary<string, Bitmap?>(StringComparer.OrdinalIgnoreCase);

        public Bitmap? Get(string? relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) return null;
            if (_Cache.TryGetValue(relativePath, out Bitmap? cached)) return cached;

            Bitmap? bitmap = null;
            try
            {
                string full = Configuration.Current!.DataPath(relativePath);
                if (File.Exists(full)) bitmap = BitmapFactory.DecodeFile(full);
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.GamerServices, $"Could not decode icon {relativePath}: {ex.Message}");
            }

            _Cache[relativePath] = bitmap;
            return bitmap;
        }
    }

    /// <summary>One game's achievement totals, for the roll-up list.</summary>
    internal sealed class AchievementGameEntry
    {
        public AchievementGameEntry(string productId, string name, string? iconPath, IReadOnlyList<Achievement> achievements)
        {
            ProductId = productId;
            Name = name;
            IconPath = iconPath;
            Total = achievements.Count;
            Earned = achievements.Count(a => a.IsEarned);
            TotalScore = achievements.Sum(a => a.GamerScore);
            EarnedScore = achievements.Where(a => a.IsEarned).Sum(a => a.GamerScore);
        }

        public string ProductId { get; }
        public string Name { get; }
        public string? IconPath { get; }
        public int Total { get; }
        public int Earned { get; }
        public int TotalScore { get; }
        public int EarnedScore { get; }

        public int Percent => Total == 0 ? 0 : (int)Math.Round(Earned * 100.0 / Total);
        public string Summary => $"{Earned}/{Total}  ·  {EarnedScore}/{TotalScore} G";
    }

    internal sealed class AchievementGameAdapter : BaseAdapter<AchievementGameEntry>
    {
        private readonly LayoutInflater _Inflater;
        private readonly IconCache _Icons = new IconCache();
        private List<AchievementGameEntry> _Items = new List<AchievementGameEntry>();

        public AchievementGameAdapter(Context context)
        {
            _Inflater = LayoutInflater.From(context)!;
        }

        public override AchievementGameEntry this[int position] => _Items[position];
        public override int Count => _Items.Count;
        public override long GetItemId(int position) => position;

        public void SetItems(IEnumerable<AchievementGameEntry> items)
        {
            _Items = items.ToList();
            NotifyDataSetChanged();
        }

        public override View GetView(int position, View? convertView, ViewGroup? parent)
        {
            View view = convertView ?? _Inflater.Inflate(Resource.Layout.item_achievement_game, parent, false)!;
            AchievementGameEntry entry = _Items[position];

            view.FindViewById<TextView>(Resource.Id.gameName)!.Text = entry.Name;
            view.FindViewById<TextView>(Resource.Id.gameProgressText)!.Text = entry.Summary;

            ProgressBar bar = view.FindViewById<ProgressBar>(Resource.Id.gameProgressBar)!;
            bar.Progress = entry.Percent;
            WpTheme.ApplyProgress(bar);

            ImageView icon = view.FindViewById<ImageView>(Resource.Id.gameIcon)!;
            Bitmap? bitmap = _Icons.Get(entry.IconPath);
            if (bitmap != null) icon.SetImageBitmap(bitmap);
            else icon.SetImageResource(Resource.Drawable.wp_tile_placeholder);

            return view;
        }
    }

    internal sealed class AchievementAdapter : BaseAdapter<Achievement>
    {
        private readonly LayoutInflater _Inflater;
        private readonly IconCache _Icons = new IconCache();
        private List<Achievement> _Items = new List<Achievement>();

        public AchievementAdapter(Context context)
        {
            _Inflater = LayoutInflater.From(context)!;
        }

        public override Achievement this[int position] => _Items[position];
        public override int Count => _Items.Count;
        public override long GetItemId(int position) => _Items[position].Id;

        public void SetItems(IEnumerable<Achievement> items)
        {
            _Items = items.ToList();
            NotifyDataSetChanged();
        }

        public override View GetView(int position, View? convertView, ViewGroup? parent)
        {
            View view = convertView ?? _Inflater.Inflate(Resource.Layout.item_achievement, parent, false)!;
            Achievement achievement = _Items[position];

            TextView name = view.FindViewById<TextView>(Resource.Id.achievementName)!;
            TextView description = view.FindViewById<TextView>(Resource.Id.achievementDescription)!;
            TextView score = view.FindViewById<TextView>(Resource.Id.achievementScore)!;
            ImageView icon = view.FindViewById<ImageView>(Resource.Id.achievementIcon)!;

            // Secret achievements hid their name and description until earned. Honour that —
            // the row still shows the gamerscore so the total adds up.
            bool hidden = !achievement.IsEarned && !achievement.DisplayBeforeEarned;

            name.Text = hidden ? "hidden achievement" : achievement.Name;
            description.Text = hidden
                ? "revealed when you earn it"
                : (string.IsNullOrWhiteSpace(achievement.Description) ? achievement.HowToEarn : achievement.Description);
            score.Text = $"{achievement.GamerScore} G";

            // Locked entries are dimmed rather than hidden, the way the Xbox hub did it.
            float alpha = achievement.IsEarned ? 1f : 0.45f;
            name.Alpha = alpha;
            description.Alpha = alpha;
            score.Alpha = alpha;
            icon.Alpha = alpha;

            Bitmap? bitmap = hidden ? null : _Icons.Get(achievement._IconPath);
            if (bitmap != null) icon.SetImageBitmap(bitmap);
            else icon.SetImageResource(Resource.Drawable.ic_wp_lock);

            score.SetTextColor(achievement.IsEarned ? WpTheme.Accent : WpTheme.Foreground);

            return view;
        }
    }
}
