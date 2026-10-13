// Fetches Bing's "Image of the Day" to a file the CPU rasteriser paints from.
// Real WP7 Panorama-based games (Minesweeper, Solitaire, Sudoku, Mahjong)
// overlay their UI on top of the Bing search app's daily wallpaper. The
// Panorama itself has Background="Transparent" in the WP toolkit's default
// template; the visible image comes from the Bing app underneath. We
// reproduce the look by painting the same Bing image behind the Panorama
// when its own Background isn't set.
//
// Caching: the fetched JPEG is stored under
//   %LocalAppData%\WPR\BingWallpaper\YYYY-MM-DD.jpg
// so we only hit the network once per day per app start.

using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace WPR.SilverlightCompability
{
    public static class BingWallpaper
    {
        private static bool _attempted;
        private static readonly object _gate = new object();

        private static volatile string? _cachedFile;

        /// <summary>
        /// The downloaded wallpaper's file, once it is on disk; null until then. Triggers a
        /// background fetch on first access. <c>SoftwareVisualRasteriser</c> decodes the file
        /// itself, and folds this path into its change signature so the frame the wallpaper
        /// arrives on repaints.
        /// </summary>
        public static string? CachedFilePath
        {
            get
            {
                EnsureFetchStarted();
                return _cachedFile;
            }
        }

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
                string cacheDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WPR", "BingWallpaper");
                Directory.CreateDirectory(cacheDir);

                string today = DateTime.UtcNow.ToString("yyyy-MM-dd");
                string cachedFile = System.IO.Path.Combine(cacheDir, today + ".jpg");

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

                _cachedFile = cachedFile;
            }
            catch
            {
                // Offline / blocked / network errors — leave the path null. The rasteriser
                // falls back to its dark-gray panorama background.
            }
        }
    }
}
