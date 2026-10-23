namespace WPR.Wp8Native
{
    /// <summary>
    /// A source voice's format, read from a WAVEFORMATEX (plus the ADPCM and extensible tails).
    /// </summary>
    /// <param name="Tag">1 PCM, 2 MS ADPCM, 3 IEEE float.</param>
    public sealed record XAudio2Format(int Tag, int Channels, int SampleRate, int Bits, int BlockAlign, int SamplesPerBlock, short[] Coefficients)
    {
        /// <summary>Channels after conversion: the host plays mono or stereo.</summary>
        public int OutChannels => Math.Min(Channels, 2);

        /// <summary>Whether <see cref="XAudio2Pcm.Decode"/> can turn it into 16-bit PCM.</summary>
        public bool IsPlayable => Tag is 1 or 2 or 3 && Channels > 0 && SampleRate > 0 && (Tag != 1 || Bits is 8 or 16 or 24 or 32);

        public override string ToString() => $"tag {Tag} {Channels}ch {SampleRate} Hz {Bits}-bit align {BlockAlign}";

        /// <summary>
        /// Reads a WAVEFORMATEX. <paramref name="read"/> answers bytes at an offset from the
        /// structure's start, however the caller reaches its memory.
        /// </summary>
        public static XAudio2Format Read(Func<int, int, byte[]> read)
        {
            byte[] header = read(0, 18);
            int tag = BitConverter.ToUInt16(header, 0);
            int channels = BitConverter.ToUInt16(header, 2);
            int rate = (int)BitConverter.ToUInt32(header, 4);
            int align = BitConverter.ToUInt16(header, 12);
            int bits = BitConverter.ToUInt16(header, 14);
            int extra = BitConverter.ToUInt16(header, 16);
            int samplesPerBlock = 0;
            short[] coefficients = [];

            if (tag == 0xFFFE && extra >= 22)
            {
                // WAVEFORMATEXTENSIBLE: the real tag is the first word of SubFormat.
                tag = BitConverter.ToUInt16(read(24, 2), 0);
            }
            else if (tag == 2 && extra >= 4)
            {
                // ADPCMWAVEFORMAT: wSamplesPerBlock, wNumCoef, then the coefficient pairs.
                byte[] adpcm = read(18, 4);
                samplesPerBlock = BitConverter.ToUInt16(adpcm, 0);
                int count = Math.Min((int)BitConverter.ToUInt16(adpcm, 2), 32);
                byte[] raw = read(22, count * 4);
                coefficients = new short[count * 2];
                Buffer.BlockCopy(raw, 0, coefficients, 0, raw.Length);
            }

            return new XAudio2Format(tag, channels, rate, bits, align, samplesPerBlock, coefficients);
        }
    }

    /// <summary>
    /// A source voice's queue of XAUDIO2_BUFFERs, fed to an <see cref="IAudioVoiceOutput"/> a chunk
    /// at a time: head, loop region (repeated), tail. Shared by the WP8 native runtime (buffers
    /// read from guest memory) and the host Direct3D/XAudio2 a managed title's SharpDX calls.
    /// </summary>
    public sealed class XAudio2BufferQueue(XAudio2Format? format, IAudioVoiceOutput? output)
    {
        public const uint EndOfStreamFlag = 0x40;
        public const int InfiniteLoop = 255;

        // IXAudio2VoiceCallback, no IUnknown: OnStreamEnd 2, OnBufferStart 3, OnBufferEnd 4.
        public const int OnStreamEnd = 2;
        public const int OnBufferStart = 3;
        public const int OnBufferEnd = 4;

        private sealed class QueuedBuffer
        {
            public long Context;
            public bool EndOfStream;
            public byte[] Head = [];
            public byte[]? Loop;
            public byte[] Tail = [];
            public bool Infinite;
            public int LoopsLeft;
            public bool ExitLoop;
            public int Stage;                // 0 head, 1 loop, 2 tail, 3 all fed
            public int LastSeq;
            public bool Started;
            public long Frames;
        }

        private readonly List<QueuedBuffer> _queue = [];
        private int _fed;                    // host submissions so far

        public XAudio2Format? Format => format;

        public IAudioVoiceOutput? Output => output;

        /// <summary>Buffers submitted and not yet reported ended.</summary>
        public int Count => _queue.Count;

        /// <summary>The context of the buffer playing now, or 0.</summary>
        public long CurrentContext => _queue.Count > 0 ? _queue[0].Context : 0;

        /// <summary>Frames of buffers that have ended.</summary>
        public long SamplesPlayed { get; private set; }

        /// <summary>
        /// Queues one XAUDIO2_BUFFER. <paramref name="audio"/> is the buffer's bytes in the voice's
        /// format, copied by the caller (the game may reuse its memory once the buffer ends); null
        /// or empty queues a silent buffer that ends at the next retire.
        /// </summary>
        public void Enqueue(uint flags, byte[]? audio, long playBegin, long playLength, long loopBegin, long loopLength, int loopCount, long context)
        {
            QueuedBuffer buffer = new()
            {
                Context = context,
                EndOfStream = (flags & EndOfStreamFlag) != 0,
            };

            if (output is not null && format is { } f && audio is { Length: > 0 })
            {
                short[] pcm = XAudio2Pcm.Decode(f, audio);
                int ch = f.OutChannels;
                long total = pcm.Length / ch;
                long playEnd = playLength == 0 ? total : Math.Min(total, playBegin + playLength);
                playBegin = Math.Min(playBegin, playEnd);
                buffer.Frames = playEnd - playBegin;

                if (loopCount > 0)
                {
                    long loopEnd = loopLength == 0 ? playEnd : Math.Min(playEnd, loopBegin + loopLength);
                    loopBegin = Math.Clamp(loopBegin, playBegin, loopEnd);
                    buffer.Head = Slice(pcm, ch, playBegin, loopEnd);
                    buffer.Loop = Slice(pcm, ch, loopBegin, loopEnd);
                    buffer.Tail = Slice(pcm, ch, loopEnd, playEnd);
                    buffer.Infinite = loopCount == InfiniteLoop;
                    buffer.LoopsLeft = loopCount;
                    buffer.Loop = Repeat(buffer.Loop, ch, f.SampleRate, buffer.Infinite ? int.MaxValue : loopCount, out int per);
                    if (!buffer.Infinite)
                    {
                        buffer.LoopsLeft = (loopCount + per - 1) / per;
                    }
                }
                else
                {
                    buffer.Head = Slice(pcm, ch, playBegin, playEnd);
                }
            }
            else
            {
                // Silent: nothing to feed, so the buffer ends at the next retire.
                buffer.Stage = 3;
            }

            _queue.Add(buffer);
        }

        /// <summary>Everything queued ends now and is reported ended at the next retire.</summary>
        public void Flush()
        {
            foreach (QueuedBuffer b in _queue)
            {
                b.Stage = 3;
                b.LastSeq = 0;
            }

            output?.Flush();
        }

        /// <summary>Loops in the queued buffers play out their current pass and then stop looping.</summary>
        public void ExitLoop()
        {
            foreach (QueuedBuffer b in _queue)
            {
                b.ExitLoop = true;
            }
        }

        /// <summary>
        /// Keeps up to three chunks queued on the host. <paramref name="callback"/> receives
        /// (callback slot, buffer context) for each OnBufferStart due.
        /// </summary>
        public void Feed(Action<int, long> callback)
        {
            if (output is null)
            {
                return;
            }

            foreach (QueuedBuffer b in _queue)
            {
                while (b.Stage < 3 && _fed - output.Completed < 3)
                {
                    byte[]? chunk = NextChunk(b);
                    if (chunk is null || chunk.Length == 0)
                    {
                        continue;
                    }

                    output.Submit(chunk);
                    _fed++;
                    b.LastSeq = _fed;
                    if (!b.Started)
                    {
                        b.Started = true;
                        callback(OnBufferStart, b.Context);
                    }
                }

                if (b.Stage < 3)
                {
                    return;
                }
            }
        }

        /// <summary>
        /// Removes buffers the host has finished with. <paramref name="callback"/> receives
        /// (slot, context) for each OnBufferEnd, and (OnStreamEnd, 0) after an end-of-stream buffer.
        /// </summary>
        public void Retire(Action<int, long> callback)
        {
            int completed = output?.Completed ?? int.MaxValue;
            while (_queue.Count > 0)
            {
                QueuedBuffer b = _queue[0];
                if (b.Stage < 3 || completed < b.LastSeq)
                {
                    break;
                }

                _queue.RemoveAt(0);
                SamplesPlayed += b.Frames;
                callback(OnBufferEnd, b.Context);
                if (b.EndOfStream)
                {
                    callback(OnStreamEnd, 0);
                }
            }
        }

        /// <summary>A loop region short enough to underrun between frames is played as several repeats per chunk.</summary>
        private static byte[] Repeat(byte[] loop, int channels, int rate, int maxRepeats, out int repeats)
        {
            int minimum = rate * channels * 2 / 5;   // 200 ms
            repeats = loop.Length == 0 ? 1 : Math.Clamp((minimum + loop.Length - 1) / loop.Length, 1, Math.Min(maxRepeats, 256));
            if (repeats == 1)
            {
                return loop;
            }

            byte[] joined = new byte[loop.Length * repeats];
            for (int i = 0; i < repeats; i++)
            {
                Buffer.BlockCopy(loop, 0, joined, i * loop.Length, loop.Length);
            }

            return joined;
        }

        private static byte[] Slice(short[] pcm, int channels, long fromFrame, long toFrame)
        {
            int count = (int)Math.Max(0, toFrame - fromFrame) * channels;
            byte[] bytes = new byte[count * 2];
            Buffer.BlockCopy(pcm, (int)fromFrame * channels * 2, bytes, 0, bytes.Length);
            return bytes;
        }

        private static byte[]? NextChunk(QueuedBuffer b)
        {
            switch (b.Stage)
            {
                case 0:
                    b.Stage = b.Loop is null ? 2 : 1;
                    return b.Head;
                case 1:
                    if (!b.ExitLoop && (b.Infinite || b.LoopsLeft > 0))
                    {
                        b.LoopsLeft--;
                        return b.Loop;
                    }

                    b.Stage = 2;
                    return null;
                default:
                    b.Stage = 3;
                    return b.Tail;
            }
        }
    }

    /// <summary>Conversion of an XAudio2 source format to 16-bit PCM, mono or stereo.</summary>
    public static class XAudio2Pcm
    {
        public static short[] Decode(XAudio2Format f, byte[] data) => f.Tag switch
        {
            2 => Downmix(DecodeAdpcm(f, data), f.Channels),
            3 => Downmix(FromFloat(data), f.Channels),
            _ => Downmix(FromPcm(data, f.Bits), f.Channels),
        };

        private static short[] FromPcm(byte[] data, int bits)
        {
            int width = bits / 8;
            short[] samples = new short[data.Length / width];
            for (int i = 0, at = 0; i < samples.Length; i++, at += width)
            {
                samples[i] = width switch
                {
                    1 => (short)((data[at] - 128) << 8),
                    2 => BitConverter.ToInt16(data, at),
                    3 => (short)(data[at + 1] | (data[at + 2] << 8)),
                    _ => (short)(BitConverter.ToInt32(data, at) >> 16),
                };
            }

            return samples;
        }

        private static short[] FromFloat(byte[] data)
        {
            short[] samples = new short[data.Length / 4];
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = (short)Math.Clamp(BitConverter.ToSingle(data, i * 4) * 32767f, -32768f, 32767f);
            }

            return samples;
        }

        /// <summary>More than two channels keep the first two (front left and right).</summary>
        private static short[] Downmix(short[] samples, int channels)
        {
            if (channels <= 2)
            {
                return samples;
            }

            int frames = samples.Length / channels;
            short[] stereo = new short[frames * 2];
            for (int i = 0; i < frames; i++)
            {
                stereo[i * 2] = samples[i * channels];
                stereo[(i * 2) + 1] = samples[(i * channels) + 1];
            }

            return stereo;
        }

        private static readonly int[] AdpcmAdaptation = [230, 230, 230, 230, 307, 409, 512, 614, 768, 614, 512, 409, 307, 230, 230, 230];

        private static readonly short[] AdpcmDefaultCoefficients = [256, 0, 512, -256, 0, 0, 192, 64, 240, 0, 460, -208, 392, -232];

        /// <summary>Microsoft ADPCM: per block, a predictor, delta and two seed samples per channel, then nibbles.</summary>
        private static short[] DecodeAdpcm(XAudio2Format f, byte[] data)
        {
            int channels = f.Channels;
            int align = f.BlockAlign;
            if (channels is < 1 or > 2 || align <= 7 * channels)
            {
                return [];
            }

            int perBlock = f.SamplesPerBlock > 0 ? f.SamplesPerBlock : (((align - (7 * channels)) * 8) / (4 * channels)) + 2;
            short[] coefficients = f.Coefficients.Length >= 14 ? f.Coefficients : AdpcmDefaultCoefficients;
            int blocks = data.Length / align;
            short[] output = new short[blocks * perBlock * channels];
            int[] c1 = new int[2], c2 = new int[2], delta = new int[2], s1 = new int[2], s2 = new int[2];
            int written = 0;

            for (int block = 0; block < blocks; block++)
            {
                int at = block * align;
                for (int c = 0; c < channels; c++)
                {
                    int predictor = Math.Min(data[at + c], (coefficients.Length / 2) - 1);
                    c1[c] = coefficients[predictor * 2];
                    c2[c] = coefficients[(predictor * 2) + 1];
                }

                at += channels;
                for (int c = 0; c < channels; c++, at += 2)
                {
                    delta[c] = BitConverter.ToInt16(data, at);
                }

                for (int c = 0; c < channels; c++, at += 2)
                {
                    s1[c] = BitConverter.ToInt16(data, at);
                }

                for (int c = 0; c < channels; c++, at += 2)
                {
                    s2[c] = BitConverter.ToInt16(data, at);
                }

                for (int c = 0; c < channels; c++)
                {
                    output[written++] = (short)s2[c];
                }

                for (int c = 0; c < channels; c++)
                {
                    output[written++] = (short)s1[c];
                }

                int nibbles = (perBlock - 2) * channels;
                for (int n = 0; n < nibbles; n++)
                {
                    int c = n % channels;
                    int b = data[at + (n / 2)];
                    int nibble = (n & 1) == 0 ? b >> 4 : b & 0x0F;
                    int signed = nibble >= 8 ? nibble - 16 : nibble;
                    int predicted = ((s1[c] * c1[c]) + (s2[c] * c2[c])) / 256;
                    int sample = Math.Clamp(predicted + (signed * delta[c]), short.MinValue, short.MaxValue);
                    s2[c] = s1[c];
                    s1[c] = sample;
                    delta[c] = Math.Max(16, (AdpcmAdaptation[nibble] * delta[c]) / 256);
                    output[written++] = (short)sample;
                }
            }

            return written == output.Length ? output : output[..written];
        }
    }
}
