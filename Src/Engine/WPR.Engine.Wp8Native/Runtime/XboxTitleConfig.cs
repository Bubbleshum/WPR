using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace WPR.Wp8Native
{
    /// <summary>
    /// A WP8 Xbox title's own configuration - its leaderboards - read from the Xbox SPA its package
    /// ships, which is what Xbox Live served <c>ILeaderboardService::GetLeaderboardsAsync</c> from.
    /// </summary>
    /// <remarks>
    /// <para>The SPA is an <c>XDBF</c> table whose <c>XSRC</c> section holds the gzipped UTF-16
    /// <c>&lt;titleid&gt;.original.gameconfig</c>: the title's XLAST, with a
    /// <c>&lt;StatsView id friendlyName stringId viewType="Leaderboard"&gt;</c> per board, its columns
    /// and their aggregation, and the en-US <c>LocalizedString</c> naming it. It sits in
    /// <c>xbl.dll</c> (the Angry Birds titles, MONOPOLY Millionaire), the managed app
    /// (<c>MC4Interop.dll</c>, <c>SparkApplicationXaml.dll</c>) or a <c>.spa</c>, so every top-level
    /// module is searched, smallest first, and only files carrying <c>XDBF</c>.</para>
    ///
    /// <para>Without a board list the Angry Birds titles' leaderboard screens stop on "Sorry, we're
    /// not sure what happened" before asking for a single board. Some titles (Seasons, Star Wars,
    /// Star Wars II) configured their boards on the service rather than in the SPA: their
    /// gameconfig has no StatsViews. For those the boards come from <see cref="ServiceBoards"/>,
    /// keyed by the gameconfig's Xbox title id, the same key the service used.</para>
    /// </remarks>
    public sealed class XboxTitleConfig
    {
        /// <summary>One leaderboard: its id, the XLAST's name for it, its en-US display name and
        /// how its rating column aggregates (Max, Min, Sum, Last).</summary>
        public sealed record Board(uint Id, string FriendlyName, string DisplayName, string RatingColumn, string Aggregation);

        public static readonly XboxTitleConfig Empty = new([], null, null);

        private XboxTitleConfig(IReadOnlyList<Board> boards, string? source, uint? titleId)
        {
            Boards = boards;
            Source = source;
            TitleId = titleId;
        }

        public IReadOnlyList<Board> Boards { get; }

        /// <summary>The gameconfig's Xbox title id (<c>titleId="0x314C1392"</c>), when it has one.</summary>
        public uint? TitleId { get; }

        /// <summary>The file the configuration came from, or null when none was found.</summary>
        public string? Source { get; }

        public Board? Find(uint id) => Boards.FirstOrDefault(b => b.Id == id);

        /// <summary>
        /// What WPR Hub should call a board: its en-US display name, or, where that is only the
        /// game's internal identifier (Seasons' and Star Wars' service boards, which draw their own
        /// artwork instead), that identifier made readable: <c>TOTALSCOREEPISODE3</c> is
        /// "Total Score, Episode 3", <c>HOTH_1_LBNAME</c> "Hoth 1". Null for an unknown board.
        /// </summary>
        public string? HubName(uint id) => Find(id) is { } board ? HubName(board) : null;

        public static string HubName(Board board)
        {
            string name = board.DisplayName.Trim();
            if (name.Length == 0 || !IsIdentifier(name))
            {
                return name.Length == 0 ? board.FriendlyName : name;
            }

            // An identifier: drop the decorations Rovio puts round its board names, then words.
            string text = name;
            if (text.EndsWith("_LBNAME", StringComparison.Ordinal)) text = text[..^"_LBNAME".Length];
            if (text.StartsWith("LEADERBOARD_", StringComparison.Ordinal)) text = text["LEADERBOARD_".Length..];

            System.Text.RegularExpressions.Match episode = System.Text.RegularExpressions.Regex.Match(text, @"^TOTALSCOREEPISODE(\d+)$");
            if (episode.Success) return "Total Score, Episode " + episode.Groups[1].Value;
            if (text == "TOTALSCORE") return "Total Score";

            IEnumerable<string> words = text.Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Length <= 3 && !w.All(char.IsDigit) ? w : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant());
            string readable = string.Join(' ', words);
            return readable.Length == 0 ? name : readable;
        }

        /// <summary>An upper-case identifier (<c>TOTALSCORE</c>, <c>HOTH_1_LBNAME</c>) rather than a display name.</summary>
        private static bool IsIdentifier(string name) =>
            name.All(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_') && name.Any(char.IsLetter);

        /// <summary>Reads the first gameconfig found among the package's top-level modules. Never throws.</summary>
        public static XboxTitleConfig Load(string packageRoot)
        {
            try
            {
                if (!Directory.Exists(packageRoot))
                {
                    return Empty;
                }

                IEnumerable<FileInfo> candidates = new DirectoryInfo(packageRoot).EnumerateFiles()
                    .Where(f => f.Extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                                f.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                                f.Extension.Equals(".spa", StringComparison.OrdinalIgnoreCase))
                    .Where(f => f.Length is > 0 and < 96L * 1024 * 1024)
                    .OrderBy(f => f.Length);

                foreach (FileInfo file in candidates)
                {
                    byte[] bytes = File.ReadAllBytes(file.FullName);
                    if (FindGameConfig(bytes) is { } xml)
                    {
                        return FromGameConfig(xml, file.Name);
                    }
                }
            }
            catch (Exception)
            {
                // A configuration nobody can read is the same as none: the board list stays empty.
            }

            return Empty;
        }

        /// <summary>
        /// The configuration a gameconfig describes: its own StatsViews, or, when it has none and
        /// its title id is in <see cref="ServiceBoards"/>, the boards the service defined.
        /// </summary>
        public static XboxTitleConfig FromGameConfig(string xml, string? source)
        {
            uint? titleId = ParseTitleId(xml);
            IReadOnlyList<Board> boards = ParseBoards(xml);
            if (boards.Count == 0 && titleId is { } id && ServiceBoards.TryGetValue(id, out IReadOnlyList<Board>? known))
            {
                return new XboxTitleConfig(known, $"{source}, boards from WPR's table for title 0x{id:X8}", titleId);
            }

            return new XboxTitleConfig(boards, source, titleId);
        }

        /// <summary>The <c>titleId</c> attribute of the <c>GameConfigProject</c>, or null.</summary>
        public static uint? ParseTitleId(string xml)
        {
            try
            {
                // GameConfigProject is the root of a bare gameconfig and a child of
                // XboxLiveSubmissionProject in the one an SPA carries.
                XElement? project = XDocument.Parse(xml).Descendants().FirstOrDefault(e => e.Name.LocalName == "GameConfigProject");
                string? text = ((string?)project?.Attribute("titleId"))?.Trim();
                if (text is null)
                {
                    return null;
                }

                return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? uint.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out uint hex) ? hex : null
                    : uint.TryParse(text, out uint dec) ? dec : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Boards of titles that defined them on Xbox Live rather than in their SPA, keyed by Xbox
        /// title id.
        /// </summary>
        /// <remarks>
        /// <para>The service is gone, so these were recovered from the games. Each of these Rovio
        /// titles builds an id-to-name map in its exe (<c>movw/movt rN, "TOTALSCORE"; movs r3, #id;
        /// bl insert</c>, the same shape as its achievement table) and its leaderboard menu shows a
        /// board only when <c>GetLeaderboardsAsync</c> returned its id: the Lua menu walks
        /// <c>leaderboardOrder</c> (<c>scripts/xboxlive/leaderboards.lua</c>) and skips a name the
        /// native map did not fill. An id the game does not know is ignored, so an entry here must
        /// be right, but a missing one only hides that board. Seasons and Star Wars label their
        /// buttons with their own artwork, so their display names are the game's internal ones;
        /// Star Wars II draws the display name, so it gets real ones.</para>
        ///
        /// <para>Star Wars II needs its table only for TOTALSCORE (id 0): it posts its other sixteen
        /// boards at every start, which <c>IXboxLiveHost.KnownLeaderboards</c> already covers.</para>
        /// </remarks>
        public static readonly IReadOnlyDictionary<uint, IReadOnlyList<Board>> ServiceBoards = new Dictionary<uint, IReadOnlyList<Board>>
        {
            // Angry Birds Seasons: TOTALSCORE, then one board per episode.
            [0x314C1392] = Rovio(
                (0, "TOTALSCORE"), (224, "TOTALSCOREEPISODE1"), (208, "TOTALSCOREEPISODE2"), (192, "TOTALSCOREEPISODE3"),
                (176, "TOTALSCOREEPISODE4"), (160, "TOTALSCOREEPISODE5"), (144, "TOTALSCOREEPISODE6"), (128, "TOTALSCOREEPISODE7"),
                (112, "TOTALSCOREEPISODE8"), (96, "TOTALSCOREEPISODE9"), (80, "TOTALSCOREEPISODE10"), (64, "TOTALSCOREEPISODE11"),
                (48, "TOTALSCOREEPISODE12"), (32, "TOTALSCOREEPISODE13"), (16, "TOTALSCOREEPISODE14"), (240, "TOTALSCOREEPISODE15"),
                (256, "TOTALSCOREEPISODE16"), (272, "TOTALSCOREEPISODE17"), (288, "TOTALSCOREEPISODE18"), (304, "TOTALSCOREEPISODE19"),
                (320, "TOTALSCOREEPISODE20")),

            // Angry Birds Star Wars.
            [0x314C138A] = Rovio(
                (32, "TOTALSCORE"), (0, "TATOIINE_1_LBNAME"), (16, "DEATHSTAR_1_LBNAME"), (64, "HOTH_1_LBNAME"),
                (80, "BESPIN_1_LBNAME"), (96, "BOBAFETT_1_LBNAME"), (112, "ENDOR_1_LBNAME"), (128, "DEATHSTAR_2_LBNAME"),
                (48, "IAP_1_LBNAME")),

            // Angry Birds Star Wars II. Unlike the two above, its menu draws the board's
            // LeaderboardName as the button label: TOTALSCORE's as it is, and a chapter board's with
            // its last 18 (Bird Side) or 17 (Pork Side) characters cut off (LeaderboardDialog.lua,
            // string.sub(displayName, 1, -19 / -18)). The service's names therefore ended in a side
            // suffix of exactly those lengths, which " - Light Side Best" and " - Dark Side Best"
            // are. Chapter names are the game's own (TEXTS_BASIC).
            [0x314C1396] = Rovio(
                (0, "LEADERBOARD_TOTALSCORE", "Total Score"),
                (16, "LEADERBOARD_NABOOINVASIONLIGHT", Light("Naboo Invasion")), (32, "LEADERBOARD_NABOOINVASIONDARK", Dark("Naboo Invasion")),
                (48, "LEADERBOARD_ESCAPETOTATOOINELIGHT", Light("Escape to Tatooine")), (64, "LEADERBOARD_ESCAPETOTATOOINEDARK", Dark("Escape to Tatooine")),
                (80, "LEADERBOARD_BONUSLIGHT", Light("Bonus")), (96, "LEADERBOARD_BONUSDARK", Dark("Bonus")),
                (112, "LEADERBOARD_BATTLEOFNABOOLIGHT", Light("Battle of Naboo")), (128, "LEADERBOARD_BATTLEOFNABOODARK", Dark("Battle of Naboo")),
                (144, "LEADERBOARD_RISEOFTHECLONESLIGHT", Light("Rise of the Clones")), (160, "LEADERBOARD_RISEOFTHECLONESDARK", Dark("Rise of the Clones")),
                (176, "LEADERBOARD_MASTERYOURDESTINYLIGHT", Light("Master Your Destiny")), (192, "LEADERBOARD_MASTERYOURDESTINYDARK", Dark("Master Your Destiny")),
                (208, "LEADERBOARD_REBELSLIGHT", Light("Rebels")), (224, "LEADERBOARD_REBELSDARK", Dark("Rebels")),
                (240, "LEADERBOARD_REVENGEOFTHEPORKLIGHT", Light("Revenge of the Pork")), (256, "LEADERBOARD_REVENGEOFTHEPORKDARK", Dark("Revenge of the Pork"))),
        };

        private static string Light(string chapter) => chapter + " - Light Side Best";

        private static string Dark(string chapter) => chapter + " - Dark Side Best";

        /// <summary>A Rovio board list: every one ranks a score, highest first.</summary>
        private static IReadOnlyList<Board> Rovio(params (uint Id, string Name)[] boards) =>
            boards.Select(b => new Board(b.Id, b.Name, b.Name, "Score", "Max")).ToList();

        /// <summary>A Rovio board list whose display names differ from the game's own.</summary>
        private static IReadOnlyList<Board> Rovio(params (uint Id, string Name, string Display)[] boards) =>
            boards.Select(b => new Board(b.Id, b.Name, b.Display, "Score", "Max")).ToList();

        /// <summary>The gameconfig XML inside an SPA-carrying module, or null.</summary>
        public static string? FindGameConfig(byte[] bytes)
        {
            if (IndexOf(bytes, "XDBF"u8, 0) < 0)
            {
                return null;
            }

            for (int at = IndexOf(bytes, [0x1F, 0x8B, 0x08], 0); at >= 0; at = IndexOf(bytes, [0x1F, 0x8B, 0x08], at + 1))
            {
                string? text = TryGunzip(bytes, at);
                if (text is not null && text.Contains("<GameConfigProject", StringComparison.Ordinal))
                {
                    return text;
                }
            }

            return null;
        }

        /// <summary>The leaderboard StatsViews of a gameconfig.</summary>
        public static IReadOnlyList<Board> ParseBoards(string xml)
        {
            XDocument doc = XDocument.Parse(xml);
            Dictionary<string, string> strings = new(StringComparer.Ordinal);
            foreach (XElement s in doc.Descendants().Where(e => e.Name.LocalName == "LocalizedString"))
            {
                string? id = (string?)s.Attribute("id");
                XElement? english = s.Elements().FirstOrDefault(t => t.Name.LocalName == "Translation" && (string?)t.Attribute("locale") == "en-US")
                                    ?? s.Elements().FirstOrDefault(t => t.Name.LocalName == "Translation");
                if (id is not null && english is not null)
                {
                    strings[id] = english.Value.Trim();
                }
            }

            List<Board> boards = [];
            foreach (XElement view in doc.Descendants().Where(e => e.Name.LocalName == "StatsView"))
            {
                if (!string.Equals((string?)view.Attribute("viewType") ?? "Leaderboard", "Leaderboard", StringComparison.OrdinalIgnoreCase) ||
                    !uint.TryParse((string?)view.Attribute("id"), out uint id))
                {
                    continue;
                }

                string friendly = (string?)view.Attribute("friendlyName") ?? id.ToString();
                string display = strings.TryGetValue((string?)view.Attribute("stringId") ?? string.Empty, out string? name) ? name : friendly;

                // The rating is the column that is neither the rank nor the gamertag: attribute ids
                // 65535 and 65533 are those two, 65534 is the conventional rating column.
                XElement? rating = view.Descendants().Where(e => e.Name.LocalName == "Field")
                    .OrderByDescending(f => (string?)f.Attribute("attributeId") == "65534")
                    .FirstOrDefault(f => (string?)f.Attribute("attributeId") is not ("65535" or "65533"));
                string column = rating is not null && strings.TryGetValue((string?)rating.Attribute("stringId") ?? string.Empty, out string? c)
                    ? c
                    : "Score";
                string aggregation = (string?)rating?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Aggregation")?.Attribute("type") ?? "Max";
                boards.Add(new Board(id, friendly, display, column, aggregation));
            }

            return boards;
        }

        private static string? TryGunzip(byte[] bytes, int offset)
        {
            try
            {
                using var source = new MemoryStream(bytes, offset, bytes.Length - offset, writable: false);
                using var gzip = new GZipStream(source, CompressionMode.Decompress);
                using var output = new MemoryStream();
                byte[] buffer = new byte[64 * 1024];
                try
                {
                    int read;
                    while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, read);
                        if (output.Length > 8 * 1024 * 1024)
                        {
                            return null;
                        }
                    }
                }
                catch (InvalidDataException) when (output.Length > 0)
                {
                    // The stream sits inside a PE: whatever follows the gzip member is not another
                    // member, and the decoder says so after handing over the whole first one.
                }

                byte[] data = output.ToArray();
                if (data.Length < 64)
                {
                    return null;
                }

                // UTF-16 with a BOM in every SPA seen; a UTF-8 one would be read as garbage and
                // simply not contain the root element.
                return data[0] == 0xFF && data[1] == 0xFE ? Encoding.Unicode.GetString(data, 2, data.Length - 2)
                     : data[0] == 0xEF && data[1] == 0xBB ? Encoding.UTF8.GetString(data, 3, data.Length - 3)
                     : Encoding.Unicode.GetString(data);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle, int start)
        {
            if (start >= haystack.Length)
            {
                return -1;
            }

            int found = haystack.AsSpan(start).IndexOf(needle);
            return found < 0 ? -1 : start + found;
        }
    }
}
