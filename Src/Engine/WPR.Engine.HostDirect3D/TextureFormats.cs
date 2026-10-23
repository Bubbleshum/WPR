namespace WPR.HostDirect3D
{
    /// <summary>DXGI formats as the host stores them: RGBA8 for everything uncompressed, BC blocks as they are.</summary>
    internal static unsafe class TextureFormats
    {
        /// <summary>1/2/3 for BC1/BC2/BC3 (the replay's TextureImage.BlockFormat), else 0.</summary>
        public static int BlockFormat(int format) => format switch
        {
            70 or 71 or 72 => 1,
            73 or 74 or 75 => 2,
            76 or 77 or 78 => 3,
            _ => 0,
        };

        public static int BytesPerPixel(int format) => format switch
        {
            1 or 2 or 3 or 4 => 16,
            10 or 11 or 12 or 13 or 14 or 15 or 16 or 17 or 18 => 8,
            85 or 86 or 115 or 48 or 49 or 50 or 51 or 52 or 53 or 54 or 55 or 56 or 57 or 58 or 59 => 2,
            60 or 61 or 62 or 63 or 64 or 65 => 1,
            _ => 4,
        };

        public static int RowBytes(int format, int width)
        {
            int block = BlockFormat(format);
            return block != 0 ? Math.Max(1, (width + 3) / 4) * (block == 1 ? 8 : 16) : width * BytesPerPixel(format);
        }

        /// <summary>Bytes a vertex element of this format takes, for AppendAligned offsets.</summary>
        public static int ElementSize(int format) => format switch
        {
            >= 1 and <= 4 => 16,
            >= 5 and <= 8 => 12,
            >= 9 and <= 22 => 8,
            >= 23 and <= 47 => 4,
            >= 48 and <= 59 => 2,
            >= 60 and <= 65 => 1,
            _ => 4,
        };

        /// <summary>Converts a region of rows at <paramref name="pitch"/> into the RGBA copy (R first). False when the format is not decoded.</summary>
        public static bool ToRgba(int format, byte* bits, int pitch, int left, int top, int right, int bottom, byte[] rgba, int width)
        {
            for (int y = top; y < bottom; y++)
            {
                byte* row = bits + ((y - top) * pitch);
                for (int x = left; x < right; x++)
                {
                    int i = x - left;
                    int o = ((y * width) + x) * 4;
                    byte r, g, b, a;
                    switch (format)
                    {
                        case 27 or 28 or 29 or 30 or 31:
                            r = row[(i * 4) + 0]; g = row[(i * 4) + 1]; b = row[(i * 4) + 2]; a = row[(i * 4) + 3];
                            break;
                        case 87 or 90 or 91:
                            b = row[(i * 4) + 0]; g = row[(i * 4) + 1]; r = row[(i * 4) + 2]; a = row[(i * 4) + 3];
                            break;
                        case 88 or 92 or 93:
                            b = row[(i * 4) + 0]; g = row[(i * 4) + 1]; r = row[(i * 4) + 2]; a = 255;
                            break;
                        case 85:
                        {
                            int v = ((ushort*)row)[i];
                            r = (byte)(((v >> 11) & 31) * 255 / 31); g = (byte)(((v >> 5) & 63) * 255 / 63); b = (byte)((v & 31) * 255 / 31); a = 255;
                            break;
                        }

                        case 86:
                        {
                            int v = ((ushort*)row)[i];
                            r = (byte)(((v >> 10) & 31) * 255 / 31); g = (byte)(((v >> 5) & 31) * 255 / 31); b = (byte)((v & 31) * 255 / 31); a = (byte)((v >> 15) != 0 ? 255 : 0);
                            break;
                        }

                        case 115:
                        {
                            int v = ((ushort*)row)[i];
                            b = (byte)((v & 15) * 17); g = (byte)(((v >> 4) & 15) * 17); r = (byte)(((v >> 8) & 15) * 17); a = (byte)(((v >> 12) & 15) * 17);
                            break;
                        }

                        case 61 or 60 or 62 or 63 or 64:
                            r = g = b = row[i]; a = 255;
                            break;
                        case 65:
                            r = g = b = 0; a = row[i];
                            break;
                        case 49 or 48 or 50 or 51 or 52:
                            r = row[i * 2]; g = row[(i * 2) + 1]; b = 0; a = 255;
                            break;
                        default:
                            return false;
                    }

                    rgba[o] = r; rgba[o + 1] = g; rgba[o + 2] = b; rgba[o + 3] = a;
                }
            }

            return true;
        }
    }
}
