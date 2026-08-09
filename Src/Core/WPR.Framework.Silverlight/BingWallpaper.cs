// Fetches Bing's "Image of the Day" and exposes it as an Avalonia Bitmap.
// Real WP7 Panorama-based games (Minesweeper, Solitaire, Sudoku, Mahjong)
// overlay their UI on top of the Bing search app's daily wallpaper. The
// Panorama itself has Background="Transparent" in the WP toolkit's default
// template; the visible image comes from the Bing app underneath. We
// reproduce the look by painting the same Bing image behind the Panorama
// when its own Background isn't set.
//
// Caching: the fetched JPEG is stored under
//   %LocalAppData%\WPR\BingWallpaper\YYYY-MM-DD.jpg
// so we only hit the network once per day per app start. The Avalonia Bitmap
// is held in a static field; subsequent panorama renders reuse the loaded
// instance.

using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace WPR.SilverlightCompability
{
    public static class BingWallpaper
    {
        private static Avalonia.Media.Imaging.Bitmap? _cached;
        private static bool _attempted;
        private static readonly object _gate = new object();

        /// <summary>
        /// The cached wallpaper bitmap if it's been fetched. Triggers an async
        /// background fetch on first access; returns null until the fetch
        /// finishes (caller should re-render when ready — see <see cref="Ready"/>).
        /// </summary>
        public static Avalonia.Media.Imaging.Bitmap? Bitmap
        {
            get
            {
                EnsureFetchStarted();
                return _cached;
            }
        }

        /// <summary>Fired (on the thread-pool thread that completed the fetch) when
        /// <see cref="Bitmap"/> first becomes available. Renderer subscribes so it
        /// can request a repaint when the wallpaper lands.</summary>
        public static event Action? Ready;

        private static void EnsureFetchStarted()
        {
            lock (_gate)
            {
                if (_attempted) return;
                _attempted = true;
            }
            _ = Task.Run(FetchAsync);
        }

        private static async Task FetchAsync()
        {
            try
            {
                string cacheDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WPR", "BingWallpaper");
                Directory.CreateDirectory(cacheDir);

                string today = DateTime.UtcNow.ToString("yyyy-MM-dd");
                string cachedFile = Path.Combine(cacheDir, today + ".jpg");

                if (!File.Exists(cachedFile))
                {
                    using var http = new HttpClient();
                    http.Timeout = TimeSpan.FromSeconds(8);
                    // Returns JSON describing today's wallpaper; pull the image URL
                    // (relative, prepend www.bing.com).
                    string json = await http.GetStringAsync(
                        "https://www.bing.com/HPImageArchive.aspx?format=js&idx=0&n=1&mkt=en-US");
                    // Naive parse — avoid pulling a JSON dependency just for two fields.
                    int urlStart = json.IndexOf("\"url\":\"", StringComparison.Ordinal);
                    if (urlStart < 0) return;
                    urlStart += "\"url\":\"".Length;
                    int urlEnd = json.IndexOf('"', urlStart);
                    if (urlEnd < 0) return;
                    string urlPath = json.Substring(urlStart, urlEnd - urlStart);
                    if (urlPath.Length == 0) return;
                    string imageUrl = urlPath.StartsWith("http")
                        ? urlPath
                        : "https://www.bing.com" + urlPath;

                    byte[] bytes = await http.GetByteArrayAsync(imageUrl);
                    await File.WriteAllBytesAsync(cachedFile, bytes);
                }

                // Load the JPEG into an Avalonia Bitmap on the dispatcher thread —
                // Bitmap construction must happen where the GPU resources can be
                // touched safely. For non-UI loading we read into a memory stream
                // and let the Bitmap copy out; works on any thread.
                using var fs = File.OpenRead(cachedFile);
                _cached = new Avalonia.Media.Imaging.Bitmap(fs);
                try { Ready?.Invoke(); }
                catch { /* don't propagate UI repaint exceptions out of the fetcher */ }
            }
            catch
            {
                // Offline / blocked / network errors — leave _cached null. Renderer
                // falls back to its dark-gray panorama background.
            }
        }
    }
}
