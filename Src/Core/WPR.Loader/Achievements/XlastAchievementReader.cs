using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WPR.Achievements
{
    /// <summary>One achievement in an XLAST configuration: the key a game awards and its Xbox id.</summary>
    public sealed record XlastAchievement(string Key, uint XboxId);

    /// <summary>
    /// Reads <c>Microsoft.Xna.Framework.XlastConfiguration</c>, the resource XNA's content
    /// pipeline embeds in a GamerServices game's assembly. It is generated from the title's Xbox
    /// LIVE submission, so its keys are exactly the strings the game passes to
    /// <c>SignedInGamer.AwardAchievement</c>, and its ids are the numbers WP8 Xbox titles unlock by.
    /// </summary>
    /// <remarks>
    /// <para>Layout:</para>
    /// <code>
    /// [optional] int32 magic = -1 ; int32 version = 1
    /// int32 nStrings ; nStrings * BinaryWriter string (7-bit length, UTF-8)
    /// int32 nAlt     ; nAlt     * BinaryWriter string         (0 in most titles)
    /// int32 nPairs   ; nPairs   * (int32 stringIndex, int32 xboxId)
    /// ... a tail that varies by SDK revision
    /// </code>
    /// <para>The string table is achievements followed by the title's leaderboards; the pair table
    /// is what selects the achievements. Its indices run 0..n-1. WP8-era SDKs put fixed-size records
    /// rather than strings in the "alt" section, so when the documented layout does not give such
    /// a pair table, the bytes after the strings are searched for one. Keys are taken verbatim:
    /// trailing spaces are sometimes in the game's key too.</para>
    /// </remarks>
    public static class XlastAchievementReader
    {
        public const string ResourceName = "Microsoft.Xna.Framework.XlastConfiguration";

        /// <summary>The achievements in one resource's bytes, or null when they do not parse.</summary>
        public static IReadOnlyList<XlastAchievement>? Parse(byte[] data)
        {
            try
            {
                using BinaryReader reader = new(new MemoryStream(data, writable: false), Encoding.UTF8);
                int first = reader.ReadInt32();
                int count;
                if (first == -1)
                {
                    reader.ReadInt32(); // version
                    count = reader.ReadInt32();
                }
                else
                {
                    count = first;
                }
                if (count is <= 0 or > 4096) return null;

                List<string> strings = new(count);
                for (int i = 0; i < count; i++) strings.Add(reader.ReadString());
                long afterStrings = reader.BaseStream.Position;

                // The documented layout first.
                List<(int Index, uint Id)>? pairs = null;
                try
                {
                    int alt = reader.ReadInt32();
                    if (alt is >= 0 and < 4096)
                    {
                        for (int i = 0; i < alt; i++) reader.ReadString();
                        pairs = ReadPairs(reader, count, 1);
                    }
                }
                catch (Exception)
                {
                    pairs = null;
                }

                // Else search the bytes after the strings for a count n then n pairs indexed 0..n-1
                // (at least two, so a stray "1, 0" in the records is not taken for a table).
                if (pairs is null)
                {
                    for (long at = afterStrings; at + 4 <= data.Length && pairs is null; at++)
                    {
                        reader.BaseStream.Position = at;
                        try { pairs = ReadPairs(reader, count, 2); }
                        catch (Exception) { pairs = null; }
                    }
                }
                if (pairs is null || pairs.Count == 0) return null;

                List<XlastAchievement> result = new(pairs.Count);
                foreach ((int index, uint id) in pairs)
                {
                    if (string.IsNullOrEmpty(strings[index])) return null;
                    result.Add(new XlastAchievement(strings[index], id));
                }
                return result;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>A pair table at the reader's position, or null when there is none.</summary>
        private static List<(int, uint)>? ReadPairs(BinaryReader reader, int stringCount, int minimum)
        {
            int n = reader.ReadInt32();
            if (n < minimum || n > stringCount) return null;
            if (reader.BaseStream.Length - reader.BaseStream.Position < n * 8L) return null;
            List<(int, uint)> pairs = new(n);
            for (int i = 0; i < n; i++)
            {
                int index = reader.ReadInt32();
                uint id = reader.ReadUInt32();
                if (index != i) return null;
                pairs.Add((index, id));
            }
            return pairs;
        }
    }
}
