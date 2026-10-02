using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

using WPR.Online.Hub;
using WPR.Online.Hub.Client;

namespace WPR.Platform.Windows.Views
{
    /// <summary>
    /// Pick a WPR Hub gamerpic. The catalog is admin-curated on the hub; reward pictures are
    /// listed too, locked until the milestone that unlocks them is earned, and say what that is.
    /// Choosing one also makes it the in-game gamer picture (see <c>HubSetup.GamerpicChanged</c>).
    /// </summary>
    public partial class GamerpicWindow : Window
    {
        private const double Tile = 88;

        private readonly HubOnline? _hub;
        private readonly Dictionary<long, Border> _tiles = new Dictionary<long, Border>();
        private long? _currentId;
        private bool _busy;

        public bool Changed { get; private set; }

        public GamerpicWindow() : this(null) { }

        public GamerpicWindow(HubOnline? hub)
        {
            _hub = hub;
            InitializeComponent();
            HubLogo.Apply(this.Get<Image>("logoImage"));
            this.Get<Button>("closeButton").Click += (_, _) => Close();
            Opened += (_, _) => _ = LoadAsync();
        }

        private async Task LoadAsync()
        {
            TextBlock status = this.Get<TextBlock>("statusText");
            if (_hub == null) { status.Text = "WPR Hub is not available."; return; }

            GamerpicCatalog? catalog;
            try
            {
                catalog = await _hub.Account.GetGamerpicsAsync();
                _currentId = (await _hub.Account.RefreshAsync())?.Gamerpic?.Id;
            }
            catch (Exception ex)
            {
                status.Text = "Could not load gamerpics: " + ex.Message;
                return;
            }

            if (catalog == null) { status.Text = "Sign in to choose a gamerpic."; return; }
            if (catalog.Gamerpics.Count == 0) { status.Text = "There are no gamerpics on this hub yet."; return; }

            StackPanel panel = this.Get<StackPanel>("catalogPanel");
            foreach (var group in catalog.Gamerpics.GroupBy(g => string.IsNullOrEmpty(g.Category) ? "Gamerpics" : g.Category!))
            {
                panel.Children.Add(new TextBlock { Text = group.Key.ToUpperInvariant(), Classes = { "eyebrow" } });
                WrapPanel wrap = new WrapPanel();
                foreach (GamerpicInfo pic in group) wrap.Children.Add(BuildTile(pic, catalog.DefaultId));
                panel.Children.Add(wrap);
            }

            _currentId ??= catalog.DefaultId;
            Highlight();
            status.Text = "Pick one. Locked pictures are unlocked by milestones.";
        }

        private Control BuildTile(GamerpicInfo pic, long? defaultId)
        {
            Image image = new Image { Stretch = Stretch.UniformToFill, Width = Tile, Height = Tile };
            _ = LoadImageAsync(image, pic.Url);

            Grid content = new Grid { Width = Tile, Height = Tile };
            content.Children.Add(image);
            if (pic.Locked)
            {
                image.Opacity = 0.3;
                content.Children.Add(new TextBlock
                {
                    Text = "\U0001F512", FontSize = 26,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                });
            }

            Border frame = new Border
            {
                Child = content, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(3),
                CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };

            string tip = pic.Name + (pic.Id == defaultId ? " (default)" : "");
            if (pic.Locked && pic.UnlockedBy is { Count: > 0 } how)
                tip += "\nLocked: " + string.Join("; or ", how.Select(u => u.Requirement));
            ToolTip.SetTip(frame, tip);

            frame.PointerPressed += async (_, _) => await ChooseAsync(pic);
            _tiles[pic.Id] = frame;
            return frame;
        }

        private async Task ChooseAsync(GamerpicInfo pic)
        {
            TextBlock status = this.Get<TextBlock>("statusText");
            if (_busy || _hub == null) return;
            if (pic.Locked)
            {
                status.Text = pic.UnlockedBy is { Count: > 0 } how
                    ? $"\"{pic.Name}\" is locked. {string.Join(" Or: ", how.Select(u => u.Requirement))}"
                    : $"\"{pic.Name}\" is locked.";
                return;
            }
            if (pic.Id == _currentId) return;

            _busy = true;
            status.Text = $"Setting \"{pic.Name}\"…";
            try
            {
                await _hub.Account.ChooseGamerpicAsync(pic.Id);
                _currentId = pic.Id;
                Changed = true;
                Highlight();
                status.Text = $"Your gamerpic is now \"{pic.Name}\".";
            }
            catch (Exception ex)
            {
                status.Text = "Could not set it: " + ex.Message;
            }
            finally
            {
                _busy = false;
            }
        }

        private void Highlight()
        {
            IBrush accent = this.FindResource("WprAccentBrush") as IBrush ?? Brushes.DeepSkyBlue;
            foreach (var (id, frame) in _tiles)
                frame.BorderBrush = id == _currentId ? accent : Brushes.Transparent;
        }

        private async Task LoadImageAsync(Image target, string url)
        {
            if (_hub == null) return;
            byte[]? bytes = await _hub.Account.GetImageAsync(url);
            if (bytes == null) return;
            try { target.Source = new Bitmap(new MemoryStream(bytes)); }
            catch (Exception) { /* a bad image leaves an empty tile */ }
        }
    }
}
