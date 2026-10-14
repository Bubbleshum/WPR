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
    /// Star Wars II, Amazing Alex) configured their boards on the service rather than in the SPA;
    /// their gameconfig has no StatsViews and this answers nothing for them.</para>
    /// </remarks>
    public sealed class XboxTitleConfig
    {
        /// <summary>One leaderboard: its id, the XLAST's name for it, its en-US display name and
        /// how its rating column aggregates (Max, Min, Sum, Last).</summary>
        public sealed record Board(uint Id, string FriendlyName, string DisplayName, string RatingColumn, string Aggregation);

        public static readonly XboxTitleConfig Empty = new([], null);

        private XboxTitleConfig(IReadOnlyList<Board> boards, string? source)
        {
            Boards = boards;
            Source = source;
        }

        public IReadOnlyList<Board> Boards { get; }

        /// <summary>The file the configuration came from, or null when none was found.</summary>
        public string? Source { get; }

        public Board? Find(uint id) => Boards.FirstOrDefault(b => b.Id == id);

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
                        return new XboxTitleConfig(ParseBoards(xml), file.Name);
                    }
                }
            }
            catch (Exception)
            {
                // A configuration nobody can read is the same as none: the board list stays empty.
            }

            return Empty;
        }

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
