#if WPR_D3D11
using System;
using System.Collections.Generic;
using System.IO;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Cheap-and-cheerful PNG extractor for GameMaker Studio <c>game.win</c> files.
    ///
    /// The .win file is a chunked container (FORM root, with TXTR / SPRT / AUDO sub-chunks)
    /// containing the compiled game data: scripts, rooms, sprites, sounds. We don't have a
    /// full .win parser — instead we scan the raw bytes for PNG signatures and extract every
    /// embedded PNG. That gets us the texture atlases (the big sprite sheets) plus a handful
    /// of small UI sprites and font glyphs. Good enough for "show real game art".
    ///
    /// Future per-app renderers that want sprite-accurate rendering should integrate a
    /// proper .win parser (e.g. UndertaleModLib) so they can map sprite IDs to atlas regions.
    /// </summary>
    internal static class GameMakerAssetExtractor
    {
        // PNG header: 89 50 4E 47 0D 0A 1A 0A
        private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        /// <summary>
        /// Returns the bytes of every PNG embedded in the file at <paramref name="winPath"/>,
        /// in order of appearance. Bytes for each PNG include the signature and IEND. Empty
        /// list on any I/O error or invalid file.
        /// </summary>
        public static List<byte[]> ExtractPngs(string winPath)
        {
            var result = new List<byte[]>();
            if (!File.Exists(winPath)) return result;

            byte[] data;
            try { data = File.ReadAllBytes(winPath); }
            catch { return result; }

            int i = 0;
            while (i < data.Length - 8)
            {
                int sig = FindPngSignature(data, i);
                if (sig < 0) break;

                // PNG body: signature + chunks. Each chunk = 4-byte length (BE) + 4-byte type
                // + length bytes of data + 4-byte CRC. Final chunk type is "IEND".
                int p = sig + 8;
                int? endIndex = null;
                while (p + 12 <= data.Length)
                {
                    int chunkLen = ReadIntBigEndian(data, p);
                    if (chunkLen < 0 || p + 8 + chunkLen + 4 > data.Length) break;
                    bool isIend = data[p + 4] == 'I' && data[p + 5] == 'E' &&
                                   data[p + 6] == 'N' && data[p + 7] == 'D';
                    p += 8 + chunkLen + 4; // length(4) + type(4) + data + CRC(4)
                    if (isIend) { endIndex = p; break; }
                }

                if (endIndex.HasValue)
                {
                    int len = endIndex.Value - sig;
                    var pngBytes = new byte[len];
                    Buffer.BlockCopy(data, sig, pngBytes, 0, len);
                    result.Add(pngBytes);
                    i = endIndex.Value;
                }
                else
                {
                    // Couldn't find IEND — bail and continue past the bad signature.
                    i = sig + 8;
                }
            }

            return result;
        }

        private static int FindPngSignature(byte[] data, int start)
        {
            int end = data.Length - PngSignature.Length;
            for (int i = start; i <= end; i++)
            {
                bool match = true;
                for (int j = 0; j < PngSignature.Length; j++)
                {
                    if (data[i + j] != PngSignature[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }

        private static int ReadIntBigEndian(byte[] data, int offset)
        {
            return (data[offset] << 24) | (data[offset + 1] << 16) |
                   (data[offset + 2] << 8) | data[offset + 3];
        }
    }
}
#endif
