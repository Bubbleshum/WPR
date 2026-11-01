using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.IO;
using System;

using WPR.Common;

namespace Microsoft.Xna.Framework.GamerServices
{
    public class Achievement
    {
        public Achievement()
        {
        }

        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        [Key, Column(Order = 0)]
        public int Id { get; set; }

        public string _IconPath { get; set; }

        public string Description { get; set; }

        public bool DisplayBeforeEarned { get; set; }

        public DateTime EarnedDateTime { get; set; }

        public bool EarnedOnline { get; set; }

        public int GamerScore { get; set; }

        public string HowToEarn { get; set; }

        public bool IsEarned { get; set; }

        public string Key { get; set; }

        public string Name { get; set; }

        public string OwnProductId { get; set; }

        public Stream GetPicture()
        {
            // An achievement with no icon (a key-only catalogue row, or one created for a key the
            // catalogue does not list) still has a picture: on the phone every achievement had one,
            // and games build their lists with Texture2D.FromStream(GetPicture()) per row.
            if (string.IsNullOrEmpty(_IconPath) || Configuration.Current is null ||
                !File.Exists(Configuration.Current.DataPath(_IconPath)))
            {
                return new MemoryStream(PlaceholderPicture.Value, writable: false);
            }


            // Read-only + FileShare.ReadWrite: the unlock toast displays the same
            // icon PNG and holds a handle on it, so a concurrent GetPicture() that
            // asked for the default ReadWrite access threw "being used by another
            // process". Requesting only read and sharing fully avoids the collision.
            Stream res = new FileStream(
                Configuration.Current!.DataPath(_IconPath),
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return res;
        }

        /// <summary>A 64x64 grey square as PNG, the size of a WP7 achievement picture.</summary>
        internal static readonly Lazy<byte[]> PlaceholderPicture = new Lazy<byte[]>(() => SolidPng(64, 64, 0x55, 0x55, 0x55));

        private static byte[] SolidPng(int width, int height, byte r, byte g, byte b)
        {
            byte[] raw = new byte[height * (1 + width * 3)];
            for (int y = 0, o = 0; y < height; y++)
            {
                raw[o++] = 0; // filter: none
                for (int x = 0; x < width; x++) { raw[o++] = r; raw[o++] = g; raw[o++] = b; }
            }
            MemoryStream idat = new MemoryStream();
            using (var z = new System.IO.Compression.ZLibStream(idat, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            {
                z.Write(raw, 0, raw.Length);
            }

            MemoryStream png = new MemoryStream();
            png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
            byte[] header = new byte[13];
            WriteBigEndian(header, 0, width);
            WriteBigEndian(header, 4, height);
            header[8] = 8; // bit depth
            header[9] = 2; // colour type: RGB
            WriteChunk(png, "IHDR", header);
            WriteChunk(png, "IDAT", idat.ToArray());
            WriteChunk(png, "IEND", Array.Empty<byte>());
            return png.ToArray();
        }

        private static void WriteChunk(Stream s, string type, byte[] data)
        {
            byte[] length = new byte[4];
            WriteBigEndian(length, 0, data.Length);
            s.Write(length);
            byte[] body = new byte[4 + data.Length];
            System.Text.Encoding.ASCII.GetBytes(type, 0, 4, body, 0);
            Buffer.BlockCopy(data, 0, body, 4, data.Length);
            s.Write(body);
            byte[] crc = new byte[4];
            WriteBigEndian(crc, 0, (int)Crc32(body));
            s.Write(crc);
        }

        private static void WriteBigEndian(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        private static uint Crc32(byte[] data)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte d in data)
            {
                crc ^= d;
                for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
            return ~crc;
        }
    }
}