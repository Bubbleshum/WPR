using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using WPR.Common;

namespace WPR.Shell
{
    /// <summary>A published WPR release, as the GitHub Releases API describes it.</summary>
    /// <param name="Version">The version with the tag's leading <c>v</c> removed, e.g. <c>0.1.06</c>.</param>
    /// <param name="PageUrl">The release's page on GitHub.</param>
    /// <param name="ApkUrl">The <c>.apk</c> asset, or null if the release has none.</param>
    /// <param name="InstallerUrl">The Windows <c>.exe</c> installer asset, or null.</param>
    public sealed record AppRelease(string Version, string Name, string PageUrl, string? ApkUrl, string? InstallerUrl);

    /// <summary>
    /// Looks for a newer WPR release on GitHub. Shared by both heads; only showing the result is theirs.
    /// </summary>
    /// <remarks>
    /// <para><b>Source:</b> <c>/releases/latest</c> of the project repository, which already leaves
    /// out drafts and pre-releases, so a pre-release is never offered as an update.</para>
    /// <para><b>Rules, so this never nags:</b> at most one request per <see cref="CheckInterval"/>
    /// unless forced (the About page forces it), and <see cref="ShouldNotify"/> answers true once per
    /// release: after a release has been announced it is still shown on the About page, but not
    /// announced again. Both are remembered in <c>&lt;DataStore&gt;/Online/update-check.json</c>, not
    /// in config.json, for the same reason as the rating prompts' file: config.json is rewritten whole
    /// on every save.</para>
    /// <para>The automatic check is turned off by <see cref="Configuration.UpdateCheckEnabled"/>; a forced
    /// one (the player asking) still runs. Every failure (offline,
    /// rate-limited, malformed reply) reads as "nothing new"; the check is never worth an error.</para>
    /// </remarks>
    public static class AppUpdates
    {
        public const string RepoUrl = "https://github.com/Bubbleshum/WPR";
        private const string LatestReleaseApi = "https://api.github.com/repos/Bubbleshum/WPR/releases/latest";

        public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);

        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        /// <summary>The newest release seen by the last successful check, whether or not it is newer.</summary>
        public static AppRelease? LastKnown => Load().Latest;

        /// <summary>True when the most recent request to GitHub got no usable answer.</summary>
        public static bool LastCheckFailed => Load().LastCheckFailed;

        /// <summary>
        /// The newest release if it is newer than <paramref name="currentVersion"/>, otherwise null.
        /// Without <paramref name="force"/> this answers from the remembered result until
        /// <see cref="CheckInterval"/> has passed.
        /// </summary>
        public static async Task<AppRelease?> CheckAsync(string currentVersion, bool force = false, CancellationToken token = default)
        {
            // The setting turns off the automatic check only; asking by hand (force) still works.
            if (!force && Configuration.Current?.UpdateCheckEnabled == false) return null;

            await Gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                State state = Load();
                bool due = force || state.LastCheckedUtc is not { } last || DateTime.UtcNow - last >= CheckInterval;
                if (due)
                {
                    AppRelease? latest = await FetchLatestAsync(token).ConfigureAwait(false);
                    state.LastCheckedUtc = DateTime.UtcNow;
                    state.LastCheckFailed = latest == null;
                    if (latest != null) state.Latest = latest;
                    Save(state);
                    Log.Info(LogCategory.Common, $"[wpr-update] latest release {latest?.Version ?? "(could not be read)"}, running {currentVersion}");
                }

                return state.Latest is { } known && IsNewer(known.Version, currentVersion) ? known : null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn(LogCategory.Common, $"[wpr-update] check failed: {ex.Message}");
                return null;
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>True the first time it is asked about <paramref name="release"/>; false after that.</summary>
        public static bool ShouldNotify(AppRelease release)
        {
            State state = Load();
            if (string.Equals(state.NotifiedVersion, release.Version, StringComparison.OrdinalIgnoreCase)) return false;
            state.NotifiedVersion = release.Version;
            Save(state);
            return true;
        }

        /// <summary>
        /// Whether <paramref name="candidate"/> is a later version than <paramref name="current"/>.
        /// Compared number by number, so <c>0.1.10</c> is later than <c>0.1.09</c> and the tags'
        /// zero padding (<c>0.1.06</c>) means nothing. An unreadable version is never newer.
        /// </summary>
        public static bool IsNewer(string candidate, string current) => CompareVersions(candidate, current) > 0;

        /// <summary>Negative, zero or positive, as <paramref name="a"/> is earlier, equal or later than <paramref name="b"/>.</summary>
        public static int CompareVersions(string a, string b)
        {
            int[]? x = Parse(a), y = Parse(b);
            if (x == null || y == null) return 0;
            for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
            {
                int l = i < x.Length ? x[i] : 0, r = i < y.Length ? y[i] : 0;
                if (l != r) return l.CompareTo(r);
            }
            return 0;
        }

        private static int[]? Parse(string version)
        {
            string v = version.Trim().TrimStart('v', 'V');
            int cut = v.IndexOfAny(new[] { '-', '+', ' ' });
            if (cut >= 0) v = v.Substring(0, cut);
            string[] parts = v.Split('.');
            int[] numbers = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], out numbers[i])) return null;
            }
            return numbers;
        }

        private static async Task<AppRelease?> FetchLatestAsync(CancellationToken token)
        {
            using HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            // GitHub refuses API requests without a User-Agent.
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WPR", "1.0"));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using HttpResponseMessage response = await http.GetAsync(LatestReleaseApi, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warn(LogCategory.Common, $"[wpr-update] GitHub answered {(int)response.StatusCode}");
                return null;
            }

            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            string? tag = root.TryGetProperty("tag_name", out JsonElement t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag) || Parse(tag!) == null) return null;
            string version = tag!.Trim().TrimStart('v', 'V');

            string name = root.TryGetProperty("name", out JsonElement n) && n.GetString() is { Length: > 0 } s ? s : "WPR " + version;
            string page = root.TryGetProperty("html_url", out JsonElement h) && h.GetString() is { Length: > 0 } p
                ? p : $"{RepoUrl}/releases/tag/{tag}";

            string? apk = null, installer = null;
            if (root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement asset in assets.EnumerateArray())
                {
                    string? file = asset.TryGetProperty("name", out JsonElement an) ? an.GetString() : null;
                    string? url = asset.TryGetProperty("browser_download_url", out JsonElement au) ? au.GetString() : null;
                    if (file == null || url == null) continue;
                    if (apk == null && file.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)) apk = url;
                    if (installer == null && file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) installer = url;
                }
            }

            return new AppRelease(version, name, page, apk, installer);
        }

        // ------------------------------------------------------------------ state

        private sealed class State
        {
            public DateTime? LastCheckedUtc { get; set; }
            public AppRelease? Latest { get; set; }
            public string? NotifiedVersion { get; set; }
            public bool LastCheckFailed { get; set; }
        }

        private static string? FilePath() =>
            Configuration.Current == null ? null : Path.Combine(Configuration.Current.DataPath("Online"), "update-check.json");

        private static State Load()
        {
            try
            {
                string? path = FilePath();
                if (path != null && File.Exists(path))
                {
                    return JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? new State();
                }
            }
            catch (Exception ex)
            {
                // A torn file costs one extra check, nothing more.
                Log.Warn(LogCategory.Common, $"[wpr-update] could not read update-check.json: {ex.Message}");
            }
            return new State();
        }

        private static void Save(State state)
        {
            try
            {
                string? path = FilePath();
                if (path == null) return;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(state));
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.Common, $"[wpr-update] could not write update-check.json: {ex.Message}");
            }
        }
    }
}
