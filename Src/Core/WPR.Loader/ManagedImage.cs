using System;
using System.IO;

namespace WPR
{
    /// <summary>
    /// Whether a file is a managed assembly, read from its headers without throwing.
    /// </summary>
    /// <remarks>
    /// A WP8 package mixes managed assemblies with native ARM DLLs (the game's own component,
    /// SharpDX's interop, Xbox Live's). Handing a native one to Cecil or <c>PEReader</c> throws
    /// <see cref="BadImageFormatException"/>, and even when that is caught the run's
    /// compatibility observer sees it first and reports it to WPR Hub as a bad assembly: GTA:
    /// San Andreas sent sixteen of them each time its page was prepared. Asking the headers
    /// first costs a few reads and throws nothing.
    /// </remarks>
    internal static class ManagedImage
    {
        /// <summary>True when <paramref name="path"/> is a PE image with a CLI (COR20) header.</summary>
        public static bool IsManaged(string path)
        {
            try
            {
                using FileStream stream = File.OpenRead(path);
                using var reader = new BinaryReader(stream);
                if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5A4D) // "MZ"
                {
                    return false;
                }

                stream.Position = 0x3C;
                uint peOffset = reader.ReadUInt32();
                if (peOffset > stream.Length - 24)
                {
                    return false;
                }

                stream.Position = peOffset;
                if (reader.ReadUInt32() != 0x00004550) // "PE\0\0"
                {
                    return false;
                }

                stream.Position = peOffset + 4 + 16; // SizeOfOptionalHeader in the COFF header
                ushort optionalSize = reader.ReadUInt16();
                long optional = peOffset + 24;
                if (optionalSize < 2 || optional + optionalSize > stream.Length)
                {
                    return false;
                }

                stream.Position = optional;
                ushort magic = reader.ReadUInt16();

                // The data directories start at 96 (PE32) or 112 (PE32+) into the optional
                // header; the CLI header is the fifteenth, eight bytes each.
                long directories = magic switch { 0x10B => 96, 0x20B => 112, _ => -1 };
                long cli = directories + (14 * 8);
                if (directories < 0 || cli + 8 > optionalSize)
                {
                    return false;
                }

                stream.Position = optional + cli;
                return reader.ReadUInt32() != 0 && reader.ReadUInt32() != 0;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
