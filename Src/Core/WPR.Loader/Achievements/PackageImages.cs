using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace WPR.Achievements
{
    /// <summary>
    /// Finds an achievement's icon among the package's own images by name: a PNG, JPEG or
    /// uncompressed texture XNB whose file name is the key (or a name the game keeps beside the
    /// key, such as an icon-name array parallel to its key array), with an "unlocked" variant
    /// preferred over a "locked" one. Returns PNG or JPEG bytes.
    /// </summary>
    public sealed class PackageImages
    {
        private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".xnb"];
        private static readonly string[] LockedWords = ["lock", "grey", "gray", "off", "disabled", "unearned", "notachieved"];
        private static readonly string[] StateWords = ["open", "unlocked", "unlock", "on", "earned", "achieved", "icon", "ach", "achievement", "small", "big", "large"];

        private readonly IPackageFiles _package;
        private readonly List<(string Path, string Normal, string Stem)> _images;

        public PackageImages(IPackageFiles package)
        {
            _package = package;
            _images = package.Paths
                .Where(p => Extensions.Any(e => p.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                .Where(p => package.Length(p) is > 0 and < 2L * 1024 * 1024)
                .Select(p => (p, Normalise(Path.GetFileNameWithoutExtension(p)), Stem(Path.GetFileNameWithoutExtension(p))))
                .ToList();
        }

        /// <summary>An icon named for one of <paramref name="hints"/>, as PNG/JPEG bytes, or null.</summary>
        public byte[]? Find(IEnumerable<string> hints)
        {
            foreach (string hint in hints)
            {
                string normal = Normalise(hint);
                string stem = Stem(hint);
                if (normal.Length < 5) continue;
                List<(string Path, string Normal, string Stem)> matches = _images
                    .Where(i => i.Normal == normal || i.Stem == normal || (stem.Length >= 4 && i.Stem == stem))
                    .OrderBy(i => LockedWords.Any(w => i.Normal.Contains(w, StringComparison.Ordinal) && !normal.Contains(w, StringComparison.Ordinal)) ? 1 : 0)
                    .ThenBy(i => i.Path.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                    .ToList();
                foreach (var match in matches)
                {
                    if (LockedWords.Any(w => match.Normal.Contains(w, StringComparison.Ordinal) && !normal.Contains(w, StringComparison.Ordinal)) &&
                        matches.Count > 1)
                    {
                        continue;
                    }
                    if (Load(match.Path) is { } bytes) return bytes;
                }
            }
            return null;
        }

        private byte[]? Load(string path)
        {
            try
            {
                byte[] bytes = _package.ReadAll(path);
                if (path.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase)) return XnbTexture.ToPng(bytes);
                return IconFits(bytes) ? bytes : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>A PNG or JPEG of icon size (16..512 px a side for a PNG; any JPEG up to 256 KB).</summary>
        public static bool IconFits(byte[] bytes)
        {
            if (bytes.Length > 256 * 1024) return false;
            if (bytes.Length > 24 && bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G')
            {
                int w = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16));
                int h = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20));
                return w is >= 16 and <= 512 && h is >= 16 and <= 512 && Math.Max(w, h) <= 2 * Math.Min(w, h);
            }
            return bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;
        }

        private static string Normalise(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        /// <summary>The name without state words (<c>_open</c>, <c>_lock</c>, <c>icon_</c>) round it.</summary>
        private static string Stem(string name)
        {
            List<string> parts = name.ToLowerInvariant()
                .Split(['_', '-', ' ', '.'], StringSplitOptions.RemoveEmptyEntries).ToList();
            while (parts.Count > 1 && (StateWords.Contains(parts[^1]) || LockedWords.Contains(parts[^1]))) parts.RemoveAt(parts.Count - 1);
            while (parts.Count > 1 && (StateWords.Contains(parts[0]) || LockedWords.Contains(parts[0]))) parts.RemoveAt(0);
            return Normalise(string.Concat(parts));
        }
    }

    /// <summary>
    /// A texture XNB (XNA 4, uncompressed) to PNG: <c>SurfaceFormat.Color</c> and DXT1/3/5,
    /// un-premultiplied (XNA content is premultiplied). Null for anything else.
    /// </summary>
    public static class XnbTexture
    {
        public static byte[]? ToPng(byte[] xnb)
        {
            try
            {
                if (xnb.Length < 10 || xnb[0] != 'X' || xnb[1] != 'N' || xnb[2] != 'B') return null;
                if ((xnb[5] & 0x80) != 0) return null; // LZX-compressed
                using BinaryReader r = new(new MemoryStream(xnb), Encoding.UTF8);
                r.BaseStream.Position = 10;
                int readers = Read7Bit(r);
                bool texture = false;
                for (int i = 0; i < readers; i++)
                {
                    texture |= r.ReadString().StartsWith("Microsoft.Xna.Framework.Content.Texture2DReader", StringComparison.Ordinal);
                    r.ReadInt32();
                }
                if (!texture || readers != 1) return null;
                Read7Bit(r); // shared resources
                if (Read7Bit(r) != 1) return null; // the asset's reader index
                int format = r.ReadInt32();
                int width = r.ReadInt32();
                int height = r.ReadInt32();
                r.ReadInt32(); // mip count
                int size = r.ReadInt32();
                if (width is < 16 or > 512 || height is < 16 or > 512) return null;
                byte[] data = r.ReadBytes(size);
                byte[]? rgba = format switch
                {
                    0 when size >= width * height * 4 => data,
                    4 => Dxt.Decode(data, width, height, 1),
                    5 => Dxt.Decode(data, width, height, 3),
                    6 => Dxt.Decode(data, width, height, 5),
                    _ => null,
                };
                if (rgba is null) return null;
                for (int p = 0; p + 3 < width * height * 4; p += 4)
                {
                    int a = rgba[p + 3];
                    if (a is 0 or 255) continue;
                    rgba[p] = (byte)Math.Min(255, rgba[p] * 255 / a);
                    rgba[p + 1] = (byte)Math.Min(255, rgba[p + 1] * 255 / a);
                    rgba[p + 2] = (byte)Math.Min(255, rgba[p + 2] * 255 / a);
                }
                return Png.Encode(rgba, width, height);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static int Read7Bit(BinaryReader r)
        {
            int value = 0, shift = 0;
            byte b;
            do
            {
                b = r.ReadByte();
                value |= (b & 0x7F) << shift;
                shift += 7;
            }
            while ((b & 0x80) != 0 && shift < 35);
            return value;
        }
    }

    /// <summary>BC1/BC2/BC3 to RGBA.</summary>
    internal static class Dxt
    {
        public static byte[] Decode(byte[] data, int width, int height, int kind)
        {
            byte[] rgba = new byte[width * height * 4];
            int blockSize = kind == 1 ? 8 : 16;
            int offset = 0;
            Span<int> r = stackalloc int[4], g = stackalloc int[4], b = stackalloc int[4], a = stackalloc int[4];
            Span<byte> alpha = stackalloc byte[16];
            Span<int> table = stackalloc int[8];
            for (int by = 0; by < (height + 3) / 4; by++)
            {
                for (int bx = 0; bx < (width + 3) / 4; bx++, offset += blockSize)
                {
                    if (offset + blockSize > data.Length) return rgba;
                    int colour = kind == 1 ? offset : offset + 8;
                    ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(colour));
                    ushort c1 = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(colour + 2));
                    uint bits = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(colour + 4));
                    Expand(c0, out r[0], out g[0], out b[0]);
                    Expand(c1, out r[1], out g[1], out b[1]);
                    a[0] = a[1] = a[2] = a[3] = 255;
                    if (c0 > c1 || kind != 1)
                    {
                        r[2] = (2 * r[0] + r[1]) / 3; g[2] = (2 * g[0] + g[1]) / 3; b[2] = (2 * b[0] + b[1]) / 3;
                        r[3] = (r[0] + 2 * r[1]) / 3; g[3] = (g[0] + 2 * g[1]) / 3; b[3] = (b[0] + 2 * b[1]) / 3;
                    }
                    else
                    {
                        r[2] = (r[0] + r[1]) / 2; g[2] = (g[0] + g[1]) / 2; b[2] = (b[0] + b[1]) / 2;
                        r[3] = g[3] = b[3] = 0; a[3] = 0;
                    }

                    if (kind == 3)
                    {
                        ulong ab = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset));
                        for (int i = 0; i < 16; i++) alpha[i] = (byte)(((ab >> (4 * i)) & 0xF) * 17);
                    }
                    else if (kind == 5)
                    {
                        int a0 = data[offset], a1 = data[offset + 1];
                        ulong ab = 0;
                        for (int i = 0; i < 6; i++) ab |= (ulong)data[offset + 2 + i] << (8 * i);
                        table[0] = a0; table[1] = a1;
                        if (a0 > a1) for (int i = 1; i < 7; i++) table[i + 1] = ((7 - i) * a0 + i * a1) / 7;
                        else
                        {
                            for (int i = 1; i < 5; i++) table[i + 1] = ((5 - i) * a0 + i * a1) / 5;
                            table[6] = 0; table[7] = 255;
                        }
                        for (int i = 0; i < 16; i++) alpha[i] = (byte)table[(int)((ab >> (3 * i)) & 7)];
                    }

                    for (int i = 0; i < 16; i++)
                    {
                        int x = bx * 4 + (i & 3), y = by * 4 + (i >> 2);
                        if (x >= width || y >= height) continue;
                        int index = (int)((bits >> (2 * i)) & 3);
                        int p = (y * width + x) * 4;
                        rgba[p] = (byte)r[index]; rgba[p + 1] = (byte)g[index]; rgba[p + 2] = (byte)b[index];
                        rgba[p + 3] = kind == 1 ? (byte)a[index] : alpha[i];
                    }
                }
            }
            return rgba;
        }

        private static void Expand(ushort c, out int r, out int g, out int b)
        {
            r = ((c >> 11) & 31) * 255 / 31;
            g = ((c >> 5) & 63) * 255 / 63;
            b = (c & 31) * 255 / 31;
        }
    }

    /// <summary>A minimal RGBA8 PNG writer (no filtering).</summary>
    internal static class Png
    {
        private static readonly uint[] Crc = Enumerable.Range(0, 256).Select(n =>
        {
            uint c = (uint)n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            return c;
        }).ToArray();

        public static byte[] Encode(byte[] rgba, int width, int height)
        {
            using MemoryStream raw = new();
            for (int y = 0; y < height; y++)
            {
                raw.WriteByte(0);
                raw.Write(rgba, y * width * 4, width * 4);
            }
            using MemoryStream compressed = new();
            using (ZLibStream z = new(compressed, CompressionLevel.Optimal, leaveOpen: true)) raw.WriteTo(z);

            using MemoryStream png = new();
            png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
            byte[] header = new byte[13];
            BinaryPrimitives.WriteInt32BigEndian(header, width);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
            header[8] = 8; header[9] = 6;
            Chunk(png, "IHDR", header);
            Chunk(png, "IDAT", compressed.ToArray());
            Chunk(png, "IEND", []);
            return png.ToArray();
        }

        private static void Chunk(Stream s, string type, byte[] data)
        {
            Span<byte> four = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(four, data.Length);
            s.Write(four);
            byte[] typeBytes = Encoding.ASCII.GetBytes(type);
            s.Write(typeBytes);
            s.Write(data);
            uint crc = 0xFFFFFFFF;
            foreach (byte b in typeBytes.Concat(data)) crc = Crc[(crc ^ b) & 0xFF] ^ (crc >> 8);
            BinaryPrimitives.WriteUInt32BigEndian(four, crc ^ 0xFFFFFFFF);
            s.Write(four);
        }
    }
}
