namespace WPR.Wp8Native
{
    /// <summary>
    /// Conversion of uncompressed audio to what <see cref="IAudioOutput"/> plays: interleaved
    /// 16-bit PCM, one or two channels. Shared by the WASAPI render client and the media engine.
    /// </summary>
    internal static class PcmAudio
    {
        /// <summary>
        /// Converts <paramref name="frames"/> frames of PCM (8/16/24/32-bit) or IEEE float (32/64-bit)
        /// to 16-bit, keeping the first two channels (mono stays mono).
        /// </summary>
        public static byte[]? ToPcm16(ReadOnlySpan<byte> data, bool isFloat, int bits, int channels, int blockAlign)
        {
            if (channels <= 0 || blockAlign <= 0 || bits <= 0 || (isFloat && bits is not (32 or 64)) || (!isFloat && bits > 32))
            {
                return null;
            }

            int outChannels = Math.Min(channels, 2);
            int frames = data.Length / blockAlign;
            int bytesPerSample = bits / 8;
            if (bytesPerSample * channels > blockAlign)
            {
                return null;
            }

            byte[] output = new byte[frames * outChannels * 2];
            for (int f = 0; f < frames; f++)
            {
                for (int c = 0; c < outChannels; c++)
                {
                    ReadOnlySpan<byte> s = data.Slice((f * blockAlign) + (c * bytesPerSample), bytesPerSample);
                    short value = isFloat
                        ? FromFloat(bits == 32 ? BitConverter.ToSingle(s) : BitConverter.ToDouble(s))
                        : bits switch
                        {
                            8 => (short)((s[0] - 128) << 8),
                            16 => BitConverter.ToInt16(s),
                            24 => (short)(s[1] | (s[2] << 8)),
                            32 => (short)(BitConverter.ToInt32(s) >> 16),
                            _ => (short)0,
                        };
                    int at = ((f * outChannels) + c) * 2;
                    output[at] = (byte)value;
                    output[at + 1] = (byte)(value >> 8);
                }
            }

            return output;
        }

        private static short FromFloat(double v) => (short)Math.Clamp(Math.Round(v * 32767.0), -32768, 32767);

        /// <summary>An uncompressed RIFF/WAVE file, converted for playback.</summary>
        public sealed record Wave(int SampleRate, int Channels, byte[] Pcm16)
        {
            public int Frames => Pcm16.Length / (Channels * 2);

            public double Duration => (double)Frames / SampleRate;
        }

        /// <summary>Parses a RIFF/WAVE of PCM or float; null for anything else (compressed or not a WAVE).</summary>
        public static Wave? ReadWave(byte[] file)
        {
            if (file.Length < 12 || BitConverter.ToUInt32(file, 0) != 0x46464952 || BitConverter.ToUInt32(file, 8) != 0x45564157)
            {
                return null;
            }

            MediaFoundation.WaveFormat? format = null;
            int at = 12;
            while (at + 8 <= file.Length)
            {
                uint id = BitConverter.ToUInt32(file, at);
                int size = BitConverter.ToInt32(file, at + 4);
                if (size < 0 || at + 8L + size > file.Length)
                {
                    size = file.Length - at - 8;
                }

                if (id == 0x20746D66) // "fmt "
                {
                    format = MediaFoundation.WaveFormat.Read(file.AsSpan(at + 8, size).ToArray());
                }
                else if (id == 0x61746164 && format is not null) // "data"
                {
                    ushort tag = format.EffectiveTag;
                    if (tag is not (MediaFoundation.WaveFormat.Pcm or MediaFoundation.WaveFormat.Float) || format.SampleRate <= 0)
                    {
                        return null;
                    }

                    byte[]? pcm = ToPcm16(file.AsSpan(at + 8, size), tag == MediaFoundation.WaveFormat.Float, format.Bits, format.Channels, format.BlockAlign);
                    return pcm is null ? null : new Wave(format.SampleRate, Math.Min(format.Channels, 2), pcm);
                }

                at += 8 + size + (size & 1);
            }

            return null;
        }
    }
}
