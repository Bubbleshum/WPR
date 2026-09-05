using System;
using System.Collections.Generic;
using System.Linq;

using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;

using WprApplication = WPR.Models.Application;

namespace WPR.Platform.Android.Native
{
    /// <summary>One installed game, flattened for display.</summary>
    internal sealed class GameEntry
    {
        public GameEntry(WprApplication model)
        {
            Model = model;

            // The curated catalogue name wins over the manifest title: many WP XAPs store a
            // resource reference ("@AppResLib.dll,-100") or an internal codename there.
            Name = HardcodedAchievementCatalogue.GameName(model.ProductId ?? "")
                   ?? (string.IsNullOrWhiteSpace(model.Name) ? model.ProductId : model.Name)
                   ?? "unknown";

            string type = model.ApplicationType.ToString().ToLowerInvariant();
            Subtitle = string.IsNullOrWhiteSpace(model.Version) ? type : $"{type}  ·  {model.Version}";
        }

        public WprApplication Model { get; }
        public string Name { get; }
        public string Subtitle { get; }
        public string? ProductId => Model.ProductId;
    }

    /// <summary>
    /// Rows for the games list. A plain <see cref="BaseAdapter{T}"/> over a
    /// <see cref="ListView"/> rather than RecyclerView: the library is a few dozen rows at
    /// most, and RecyclerView would mean adding an AndroidX package the head does not
    /// otherwise need.
    /// </summary>
    internal sealed class GameListAdapter : BaseAdapter<GameEntry>
    {
        private readonly Context _Context;
        private readonly LayoutInflater _Inflater;
        private readonly Dictionary<string, Bitmap?> _IconCache = new Dictionary<string, Bitmap?>(StringComparer.OrdinalIgnoreCase);

        private List<GameEntry> _Items = new List<GameEntry>();

        public GameListAdapter(Context context)
        {
            _Context = context;
            _Inflater = LayoutInflater.From(context)!;
        }

        public override GameEntry this[int position] => _Items[position];
        public override int Count => _Items.Count;
        public override long GetItemId(int position) => _Items[position].Model.Id;

        public void SetItems(IEnumerable<GameEntry> items)
        {
            _Items = items.ToList();
            NotifyDataSetChanged();
        }

        public override View GetView(int position, View? convertView, ViewGroup? parent)
        {
            View view = convertView ?? _Inflater.Inflate(Resource.Layout.item_game, parent, false)!;
            GameEntry entry = _Items[position];

            view.FindViewById<TextView>(Resource.Id.gameName)!.Text = entry.Name;
            view.FindViewById<TextView>(Resource.Id.gameSubtitle)!.Text = entry.Subtitle;

            ImageView icon = view.FindViewById<ImageView>(Resource.Id.gameIcon)!;
            Bitmap? bitmap = ResolveIcon(entry);
            if (bitmap != null)
            {
                icon.SetImageBitmap(bitmap);
            }
            else
            {
                icon.SetImageResource(Resource.Drawable.wp_tile_placeholder);
            }

            return view;
        }

        /// <summary>
        /// The tile art the installer extracted from the XAP. Cached per product because
        /// <see cref="GetView"/> runs on every fling frame; a null cache entry is a remembered
        /// failure, so a game with a broken icon is not re-decoded forever.
        ///
        /// <para>The decode itself is <see cref="GameTileArt"/>, shared with
        /// <see cref="GameShortcuts"/> — only the caching is this adapter's own concern.</para>
        /// </summary>
        private Bitmap? ResolveIcon(GameEntry entry)
        {
            string key = entry.ProductId ?? entry.Name;
            if (_IconCache.TryGetValue(key, out Bitmap? cached)) return cached;

            Bitmap? bitmap = GameTileArt.Decode(entry.Model);

            _IconCache[key] = bitmap;
            return bitmap;
        }
    }
}
