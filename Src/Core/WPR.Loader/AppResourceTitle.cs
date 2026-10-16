using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace WPR
{
    /// <summary>
    /// Resolves a WP8 manifest title of the form <c>@AppResLib.dll,-100</c>: a reference into the
    /// string table of a resource-only DLL the package ships (the localised-title mechanism of the
    /// WP8 SDK's project template). Microsoft Minesweeper, MONOPOLY Millionaire and Asphalt
    /// Overdrive install with that text as their name otherwise.
    /// </summary>
    /// <remarks>
    /// The DLL is an ordinary PE with an <c>RT_STRING</c> table: string <c>id</c> lives in block
    /// <c>id / 16 + 1</c> at index <c>id % 16</c>, each entry a UTF-16 length followed by that many
    /// characters. The neutral DLL is read; the per-language <c>.mui</c> files beside it are not.
    /// Anything that does not parse leaves the title as it was.
    /// </remarks>
    public static class AppResourceTitle
    {
        public static string Resolve(ZipArchive archive, string title)
        {
            if (!TryParse(title, out string dll, out int id))
            {
                return title;
            }

            ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(e => e.FullName.Equals(dll, StringComparison.OrdinalIgnoreCase))
                ?? archive.Entries.FirstOrDefault(e => e.Name.Equals(dll, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                return title;
            }

            try
            {
                using Stream stream = entry.Open();
                using MemoryStream copy = new();
                stream.CopyTo(copy);
                return ReadString(copy.ToArray(), id) is { Length: > 0 } text ? text.Trim() : title;
            }
            catch (Exception)
            {
                return title;
            }
        }

        private static bool TryParse(string title, out string dll, out int id)
        {
            dll = "";
            id = 0;
            string trimmed = title.Trim();
            if (!trimmed.StartsWith('@'))
            {
                return false;
            }

            int comma = trimmed.LastIndexOf(',');
            if (comma < 2 || !int.TryParse(trimmed[(comma + 1)..].Trim(), out int value))
            {
                return false;
            }

            dll = trimmed[1..comma].Trim();
            id = Math.Abs(value);
            return dll.Length > 0;
        }

        /// <summary>String <paramref name="id"/> of the image's RT_STRING table, or null.</summary>
        internal static string? ReadString(byte[] image, int id)
        {
            int pe = BitConverter.ToInt32(image, 0x3C);
            if (pe <= 0 || pe + 24 > image.Length || BitConverter.ToUInt32(image, pe) != 0x4550)
            {
                return null;
            }

            int sections = BitConverter.ToUInt16(image, pe + 6);
            int optional = BitConverter.ToUInt16(image, pe + 20);
            int magic = BitConverter.ToUInt16(image, pe + 24);
            int dataDirectories = pe + 24 + (magic == 0x20B ? 112 : 96);
            uint resourceRva = BitConverter.ToUInt32(image, dataDirectories + (2 * 8));
            if (resourceRva == 0)
            {
                return null;
            }

            // Section table: map the resource RVA to a file offset.
            int table = pe + 24 + optional;
            int rsrc = -1;
            uint rsrcRva = 0;
            for (int i = 0; i < sections; i++)
            {
                int s = table + (i * 40);
                uint va = BitConverter.ToUInt32(image, s + 12);
                uint size = Math.Max(BitConverter.ToUInt32(image, s + 8), BitConverter.ToUInt32(image, s + 16));
                if (resourceRva >= va && resourceRva < va + size)
                {
                    rsrcRva = va;
                    rsrc = (int)BitConverter.ToUInt32(image, s + 20);
                }
            }

            if (rsrc < 0)
            {
                return null;
            }

            int root = rsrc + (int)(resourceRva - rsrcRva);
            int? FindChild(int directory, int wantedId)
            {
                int named = BitConverter.ToUInt16(image, directory + 12);
                int ids = BitConverter.ToUInt16(image, directory + 14);
                for (int i = 0; i < named + ids; i++)
                {
                    int e = directory + 16 + (i * 8);
                    uint name = BitConverter.ToUInt32(image, e);
                    uint offset = BitConverter.ToUInt32(image, e + 4);
                    if ((name & 0x80000000) == 0 && name == wantedId)
                    {
                        return (int)offset;
                    }
                }

                return null;
            }

            int? FirstChild(int directory)
            {
                int named = BitConverter.ToUInt16(image, directory + 12);
                int ids = BitConverter.ToUInt16(image, directory + 14);
                return named + ids == 0 ? null : (int)BitConverter.ToUInt32(image, directory + 16 + 4);
            }

            // Type RT_STRING (6) -> name (block) -> first language -> data entry.
            if (FindChild(root, 6) is not { } types || (types & 0x80000000) == 0 ||
                FindChild(root + (types & 0x7FFFFFFF), (id / 16) + 1) is not { } block || (block & 0x80000000) == 0 ||
                FirstChild(root + (block & 0x7FFFFFFF)) is not { } language || (language & 0x80000000) != 0)
            {
                return null;
            }

            int dataEntry = root + language;
            uint dataRva = BitConverter.ToUInt32(image, dataEntry);
            int at = rsrc + (int)(dataRva - rsrcRva);
            for (int index = 0; index < 16; index++)
            {
                int length = BitConverter.ToUInt16(image, at);
                at += 2;
                if (index == id % 16)
                {
                    return length == 0 ? null : Encoding.Unicode.GetString(image, at, length * 2);
                }

                at += length * 2;
            }

            return null;
        }
    }
}
