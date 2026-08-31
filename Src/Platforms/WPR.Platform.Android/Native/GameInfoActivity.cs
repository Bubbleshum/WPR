using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;

using WPR.Common;

namespace WPR.Platform.Android.Native
{
    /// <summary>
    /// Read-only diagnostics for one installed game, reached from the games list's
    /// long-press sheet.
    ///
    /// <para>The facts come from <see cref="GameDiagnostics"/>, which the Windows head's
    /// info dialog shares — only the presentation and the "environment" section below are
    /// this head's own. Change a fact there, not here.</para>
    ///
    /// <para>Every row copies its value on tap, and the last row copies the whole dump, so
    /// the contents can reach a bug report without a cable.</para>
    /// </summary>
    [Activity(
        Label = "info",
        Theme = "@style/WprTheme",
        ScreenOrientation = ScreenOrientation.Portrait,
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
    [Register("com.wpr.android.GameInfoActivity")]
    public class GameInfoActivity : Activity
    {
        public const string ExtraProductId = "ProductId";
        public const string ExtraGameName = "GameName";

        private InfoAdapter _Adapter = null!;
        private ListView _List = null!;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            // Android can recreate the process straight into this activity, so it cannot
            // assume the games list ran first.
            WprStartup.EnsureInitialized(this);

            SetContentView(Resource.Layout.activity_list_page);
            WpTheme.ApplySystemBars(this);

            FindViewById<TextView>(Resource.Id.appTitle)!.SetTextColor(WpTheme.Accent);
            FindViewById<TextView>(Resource.Id.pageTitle)!.Text = "info";

            string productId = GameDiagnostics.NormalizeProductId(Intent?.GetStringExtra(ExtraProductId));

            TextView subtitle = FindViewById<TextView>(Resource.Id.pageSubtitle)!;
            subtitle.Text = (Intent?.GetStringExtra(ExtraGameName) ?? productId).ToUpperInvariant();
            subtitle.Visibility = ViewStates.Visible;

            _List = FindViewById<ListView>(Resource.Id.pageList)!;
            _Adapter = new InfoAdapter(this);
            _List.Adapter = _Adapter;

            List<GameDiagnosticSection> sections = GameDiagnostics.Collect(productId);
            sections.Add(new GameDiagnosticSection("environment", Environment()));

            _Adapter.SetItems(Flatten(sections));

            _List.ItemClick += (_, e) => CopyRow(_Adapter[e.Position]);

            MeasureInstallFolderAsync(productId);
        }

        /// <summary>The platform half of the report — the part <see cref="GameDiagnostics"/> can't own.</summary>
        private List<GameDiagnosticField> Environment() => new List<GameDiagnosticField>
        {
            new GameDiagnosticField("data store", Configuration.Current!.DataStorePath ?? "(unset)"),
            new GameDiagnosticField("wpr", PackageVersion()),
            new GameDiagnosticField("android", $"{Build.VERSION.Release ?? "?"} (API {(int)Build.VERSION.SdkInt})"),
            new GameDiagnosticField("device", $"{Build.Manufacturer} {Build.Model}"),
            new GameDiagnosticField("abis", string.Join(", ", Build.SupportedAbis ?? new List<string>())),
        };

        /// <summary>Sections into flat list rows, with a trailing copy-everything row.</summary>
        private static List<InfoRow> Flatten(List<GameDiagnosticSection> sections)
        {
            var rows = new List<InfoRow>();

            foreach (GameDiagnosticSection section in sections)
            {
                rows.Add(InfoRow.Header(section.Title));
                rows.AddRange(section.Fields.Select(f => new InfoRow(f.Label, f.Value)));
            }

            rows.Add(InfoRow.Header("clipboard"));
            rows.Add(InfoRow.CopyAll());

            return rows;
        }

        /// <summary>
        /// Fill in the install folder row once the walk finishes. Deferred because
        /// content-heavy games run to thousands of files, which is more than a page load
        /// should be spending on the main thread.
        /// </summary>
        private void MeasureInstallFolderAsync(string productId)
        {
            Task.Run(() =>
            {
                string summary = GameDiagnostics.MeasureInstallFolder(productId);

                RunOnUiThread(() =>
                {
                    if (IsFinishing || IsDestroyed) return;
                    _Adapter.UpdateValue(GameDiagnostics.FolderContentsLabel, summary);
                });
            });
        }

        private void CopyRow(InfoRow row)
        {
            if (row.IsHeader) return;

            string text = row.IsCopyAll ? BuildDump() : row.Value;
            string what = row.IsCopyAll ? "everything" : row.Label;

            try
            {
                // The binding surfaces setPrimaryClip as a property, not a method.
                ClipboardManager? clipboard = (ClipboardManager?)GetSystemService(ClipboardService);
                if (clipboard == null) return;

                clipboard.PrimaryClip = ClipData.NewPlainText("wpr game info", text);
                Toast.MakeText(this, $"copied {what}", ToastLength.Short)!.Show();
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppList, $"Game info: clipboard copy failed: {ex.Message}");
            }
        }

        /// <summary>
        /// The whole page as plain text. Rebuilt from the rows rather than from the sections
        /// so it picks up the measured folder value, which lands after the page is built.
        /// </summary>
        private string BuildDump()
        {
            var sections = new List<GameDiagnosticSection>();
            var fields = new List<GameDiagnosticField>();
            string title = "";

            foreach (InfoRow row in _Adapter.Items)
            {
                if (row.IsCopyAll) continue;

                if (row.IsHeader)
                {
                    if (fields.Count > 0) sections.Add(new GameDiagnosticSection(title, fields.ToList()));
                    title = row.Label;
                    fields.Clear();
                }
                else
                {
                    fields.Add(new GameDiagnosticField(row.Label, row.Value));
                }
            }

            if (fields.Count > 0) sections.Add(new GameDiagnosticSection(title, fields));

            return GameDiagnostics.ToPlainText(sections);
        }

        private string PackageVersion()
        {
            try
            {
                PackageInfo? info = PackageManager?.GetPackageInfo(PackageName!, 0);
                if (!string.IsNullOrEmpty(info?.VersionName)) return info!.VersionName!;
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.Android, $"Game info: could not read package version: {ex.Message}");
            }

            return "unknown";
        }
    }

    /// <summary>One line of the info page: a label with its value, or a section break.</summary>
    internal sealed class InfoRow
    {
        public InfoRow(string label, string value)
        {
            Label = label;
            Value = value;
        }

        public string Label { get; }
        public string Value { get; set; }
        public bool IsHeader { get; private set; }
        public bool IsCopyAll { get; private set; }

        public static InfoRow Header(string title) => new InfoRow(title, "") { IsHeader = true };

        public static InfoRow CopyAll() =>
            new InfoRow("copy all", "tap to put this whole page on the clipboard") { IsCopyAll = true };
    }

    /// <summary>
    /// Two view types over <see cref="InfoRow"/> — section break, and label/value pair.
    /// A plain <see cref="BaseAdapter{T}"/> for the same reason the games list uses one:
    /// a few dozen static rows do not justify pulling in RecyclerView.
    /// </summary>
    internal sealed class InfoAdapter : BaseAdapter<InfoRow>
    {
        private const int TypeHeader = 0;
        private const int TypeValue = 1;

        private readonly LayoutInflater _Inflater;

        private List<InfoRow> _Items = new List<InfoRow>();

        public InfoAdapter(Context context)
        {
            _Inflater = LayoutInflater.From(context)!;
        }

        public IReadOnlyList<InfoRow> Items => _Items;

        public void SetItems(List<InfoRow> items)
        {
            _Items = items;
            NotifyDataSetChanged();
        }

        /// <summary>Replaces the value of the row carrying <paramref name="label"/>, if present.</summary>
        public void UpdateValue(string label, string value)
        {
            InfoRow? row = _Items.FirstOrDefault(r => !r.IsHeader && r.Label == label);
            if (row == null) return;

            row.Value = value;
            NotifyDataSetChanged();
        }

        public override int Count => _Items.Count;
        public override InfoRow this[int position] => _Items[position];
        public override long GetItemId(int position) => position;

        public override int ViewTypeCount => 2;
        public override int GetItemViewType(int position) => _Items[position].IsHeader ? TypeHeader : TypeValue;

        // Section breaks are not tappable; every other row copies on tap.
        public override bool AreAllItemsEnabled() => false;
        public override bool IsEnabled(int position) => !_Items[position].IsHeader;

        public override View GetView(int position, View? convertView, ViewGroup? parent)
        {
            InfoRow row = _Items[position];

            if (row.IsHeader)
            {
                View header = convertView ?? _Inflater.Inflate(Resource.Layout.item_info_header, parent, false)!;
                TextView title = header.FindViewById<TextView>(Resource.Id.infoHeader)!;
                title.Text = row.Label.ToUpperInvariant();
                title.SetTextColor(WpTheme.Accent);
                return header;
            }

            View view = convertView ?? _Inflater.Inflate(Resource.Layout.item_info, parent, false)!;
            view.FindViewById<TextView>(Resource.Id.infoLabel)!.Text = row.Label.ToUpperInvariant();
            view.FindViewById<TextView>(Resource.Id.infoValue)!.Text = row.Value;
            return view;
        }
    }
}
