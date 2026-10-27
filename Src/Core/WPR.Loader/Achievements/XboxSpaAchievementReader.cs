using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace WPR.Achievements
{
    /// <summary>One achievement as the title's Xbox SPA describes it.</summary>
    /// <param name="Id">The Xbox achievement id: what a WP8 title unlocks by, and the XLAST pair id.</param>
    /// <param name="HowToEarn">The text shown before it is earned (XLAST's <c>unachievedStringId</c>).</param>
    /// <param name="Secret">Hidden until earned (<c>showUnachieved="false"</c>).</param>
    public sealed record SpaAchievement(
        uint Id, string FriendlyName, string? Name, string? Description, string? HowToEarn,
        int GamerScore, bool Secret, uint ImageId);

    /// <summary>A title's Xbox SPA: its achievements and the images they name.</summary>
    public sealed class XboxSpa
    {
        private readonly Dictionary<ulong, byte[]> _images;

        internal XboxSpa(string source, string? titleName, IReadOnlyList<SpaAchievement> achievements, Dictionary<ulong, byte[]> images)
        {
            Source = source;
            TitleName = titleName;
            Achievements = achievements;
            _images = images;
        }

        /// <summary>The package file the SPA came from.</summary>
        public string Source { get; }

        public string? TitleName { get; }

        public IReadOnlyList<SpaAchievement> Achievements { get; }

        /// <summary>An image's bytes (a PNG in every SPA seen), or null.</summary>
        public byte[]? Image(uint imageId) => _images.TryGetValue(imageId, out byte[]? bytes) ? bytes : null;
    }

    /// <summary>
    /// Reads the Xbox SPA a title ships (in <c>xbl.dll</c>, the game's own module or a
    /// <c>.spa</c>): an <c>XDBF</c> table whose namespace 2 holds the images by id, and an
    /// <c>XSRC</c> section with the gzipped UTF-16 gameconfig, i.e. the XLAST with every
    /// <c>&lt;Achievement id cred imageId titleStringId descriptionStringId unachievedStringId&gt;</c>
    /// and the en-US <c>LocalizedString</c>s they name.
    /// </summary>
    /// <remarks>
    /// The gameconfig search duplicates <c>WPR.Wp8Native.XboxTitleConfig.FindGameConfig</c>, which
    /// reads leaderboards from the same document: WPR.Loader does not reference the WP8 runtime.
    /// </remarks>
    public static class XboxSpaAchievementReader
    {
        /// <summary>The first SPA among the package's top-level modules that has achievements. Never throws.</summary>
        public static XboxSpa? Find(IPackageFiles package)
        {
            try
            {
                IEnumerable<string> candidates = package.Paths
                    .Where(p => !p.Contains('/'))
                    .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                                p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                                p.EndsWith(".spa", StringComparison.OrdinalIgnoreCase))
                    .Where(p => package.Length(p) is > 0 and < 96L * 1024 * 1024)
                    .OrderBy(package.Length);
                foreach (string path in candidates)
                {
                    if (Read(package.ReadAll(path), path) is { Achievements.Count: > 0 } spa) return spa;
                }
            }
            catch (Exception)
            {
                // An SPA nobody can read is the same as none.
            }
            return null;
        }

        /// <summary>The SPA in one module's bytes, or null.</summary>
        public static XboxSpa? Read(byte[] bytes, string source)
        {
            int xdbf = bytes.AsSpan().IndexOf("XDBF"u8);
            if (xdbf < 0) return null;

            string? xml = null;
            for (int at = IndexOf(bytes, [0x1F, 0x8B, 0x08], xdbf); at >= 0 && xml is null; at = IndexOf(bytes, [0x1F, 0x8B, 0x08], at + 1))
            {
                string? text = TryGunzip(bytes, at);
                if (text is not null && text.Contains("<GameConfigProject", StringComparison.Ordinal)) xml = text;
            }
            if (xml is null) return null;

            (string? title, List<SpaAchievement> achievements) = ParseGameConfig(xml);
            return new XboxSpa(source, title, achievements, ReadImages(bytes, xdbf));
        }

        /// <summary>The achievements of a gameconfig, in its order, and the title's name.</summary>
        public static (string? TitleName, List<SpaAchievement> Achievements) ParseGameConfig(string xml)
        {
            XDocument doc = XDocument.Parse(xml);
            Dictionary<string, string> strings = new(StringComparer.Ordinal);
            string? titleName = null;
            foreach (XElement s in doc.Descendants().Where(e => e.Name.LocalName == "LocalizedString"))
            {
                string? id = (string?)s.Attribute("id");
                XElement? english = s.Elements().FirstOrDefault(t => t.Name.LocalName == "Translation" && (string?)t.Attribute("locale") == "en-US")
                                    ?? s.Elements().FirstOrDefault(t => t.Name.LocalName == "Translation");
                if (id is null || english is null) continue;
                strings[id] = english.Value.Trim();
                if ((string?)s.Attribute("friendlyName") == "X_STRINGID_TITLENAME") titleName = english.Value.Trim();
            }

            string? Text(XElement e, string attribute) =>
                strings.TryGetValue((string?)e.Attribute(attribute) ?? "", out string? t) && t.Length > 0 ? t : null;

            List<SpaAchievement> result = new();
            HashSet<uint> seen = new();
            foreach (XElement a in doc.Descendants().Where(e => e.Name.LocalName == "Achievement"))
            {
                if (!uint.TryParse((string?)a.Attribute("id"), out uint id) || !seen.Add(id)) continue;
                int.TryParse((string?)a.Attribute("cred"), out int cred);
                uint.TryParse((string?)a.Attribute("imageId"), out uint image);
                bool secret = string.Equals((string?)a.Attribute("showUnachieved"), "false", StringComparison.OrdinalIgnoreCase);
                result.Add(new SpaAchievement(id, (string?)a.Attribute("friendlyName") ?? id.ToString(),
                    Text(a, "titleStringId"), Text(a, "descriptionStringId"), Text(a, "unachievedStringId"),
                    Math.Max(0, cred), secret, image));
            }
            return (titleName, result);
        }

        /// <summary>
        /// Namespace-2 (image) entries of the XDBF table. Big-endian: a 24-byte header (magic,
        /// version, entry table length, entry count, free table length, free count), 18-byte
        /// entries (u16 namespace, u64 id, u32 offset, u32 length), 8-byte free entries, then data.
        /// </summary>
        private static Dictionary<ulong, byte[]> ReadImages(byte[] bytes, int xdbf)
        {
            Dictionary<ulong, byte[]> images = new();
            try
            {
                ReadOnlySpan<byte> b = bytes;
                uint tableLength = BinaryPrimitives.ReadUInt32BigEndian(b[(xdbf + 8)..]);
                uint count = BinaryPrimitives.ReadUInt32BigEndian(b[(xdbf + 12)..]);
                uint freeLength = BinaryPrimitives.ReadUInt32BigEndian(b[(xdbf + 16)..]);
                if (count > tableLength || tableLength > 100_000 || freeLength > 100_000) return images;
                long data = xdbf + 24L + tableLength * 18L + freeLength * 8L;
                for (int i = 0; i < count; i++)
                {
                    int entry = xdbf + 24 + i * 18;
                    if (entry + 18 > bytes.Length) break;
                    ushort ns = BinaryPrimitives.ReadUInt16BigEndian(b[entry..]);
                    ulong id = BinaryPrimitives.ReadUInt64BigEndian(b[(entry + 2)..]);
                    uint offset = BinaryPrimitives.ReadUInt32BigEndian(b[(entry + 10)..]);
                    uint length = BinaryPrimitives.ReadUInt32BigEndian(b[(entry + 14)..]);
                    if (ns != 2 || length == 0 || data + offset + length > bytes.Length) continue;
                    images[id] = b.Slice((int)(data + offset), (int)length).ToArray();
                }
            }
            catch (Exception)
            {
                // Images are a bonus; the achievements stand without them.
            }
            return images;
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
                        if (output.Length > 8 * 1024 * 1024) return null;
                    }
                }
                catch (InvalidDataException) when (output.Length > 0)
                {
                    // Whatever follows the gzip member inside a PE is not another member.
                }

                byte[] data = output.ToArray();
                if (data.Length < 64) return null;
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
            if (start >= haystack.Length) return -1;
            int found = haystack.AsSpan(start).IndexOf(needle);
            return found < 0 ? -1 : start + found;
        }
    }
}
