namespace WPR.Wp8Native
{
    /// <summary>
    /// XAudio2's voices: source voices that play, and submix/mastering voices whose volume and
    /// routing scale what reaches them.
    /// </summary>
    public sealed partial class XAudio2Runtime
    {
        /// <summary>Where source voices are played; null plays into silence.</summary>
        public IAudioOutput? Output { get; set; }

        private sealed class Voice
        {
            public long Address;
            public bool IsSource;
            public float Volume = 1f;
            public float Ratio = 1f;
            public long Output;              // destination voice; 0 = the mastering voice
            public long Callback;
            public bool Started;
            public SourceFormat? Format;
            public IAudioVoiceOutput? Out;
            public readonly List<QueuedBuffer> Queue = [];
            public int Fed;                  // host submissions so far
            public long SamplesPlayed;
            public float AppliedVolume = -1f;
            public float AppliedPitch = float.NaN;
        }

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

        /// <param name="Tag">1 PCM, 2 MS ADPCM, 3 IEEE float.</param>
        private sealed record SourceFormat(int Tag, int Channels, int SampleRate, int Bits, int BlockAlign, int SamplesPerBlock, short[] Coefficients)
        {
            /// <summary>Channels after conversion: the host plays mono or stereo.</summary>
            public int OutChannels => Math.Min(Channels, 2);

            public override string ToString() => $"tag {Tag} {Channels}ch {SampleRate} Hz {Bits}-bit align {BlockAlign}";
        }

        private readonly Dictionary<long, Voice> _voices = new();
        private int _buffersLogged;

        /// <summary>IXAudio2EngineCallback objects: OnProcessingPassStart 0, OnProcessingPassEnd 1, OnCriticalError 2 (no IUnknown).</summary>
        private readonly List<long> _engineCallbacks = [];
        private readonly List<(string Name, long Function, long[] Args)> _pendingCallbacks = [];

        /// <summary>Formats the game asked for that cannot be played, once each.</summary>
        public List<string> UnsupportedFormats { get; } = new();

        /// <summary>Callbacks made into the game.</summary>
        public int CallbacksMade { get; private set; }

        // IXAudio2Voice, with no IUnknown in front of it: GetVoiceDetails 0 through
        // DestroyVoice 18. IXAudio2SourceVoice continues at Start 19 and ends at
        // SetSourceSampleRate 28.
        private const int VoiceSlots = 19;

        private const int SourceVoiceSlots = 29;

        private const uint EndOfStreamFlag = 0x40;
        private const int InfiniteLoop = 255;

        private long BuildSourceVoice(long format, long callback, long sends)
        {
            Voice voice = new() { IsSource = true, Callback = callback, Format = ReadFormat(format) };
            voice.Address = CreateObject("IXAudio2SourceVoice", SourceVoiceSlots, hasUnknown: false, VoiceMethods(source: true));
            voice.Output = FirstSend(sends);
            _voices[voice.Address] = voice;

            string described = voice.Format?.ToString() ?? "unreadable format";
            _log.Add($"source voice 0x{voice.Address:X8}: {described}, callback 0x{callback:X8}");
            if (Output is not null && voice.Format is { } f)
            {
                if (f.Tag is 1 or 2 or 3 && f.Channels > 0 && f.SampleRate > 0 &&
                    (f.Tag != 1 || f.Bits is 8 or 16 or 24 or 32))
                {
                    voice.Out = Output.CreateVoice(f.SampleRate, f.OutChannels);
                }
                else if (!UnsupportedFormats.Contains(described))
                {
                    UnsupportedFormats.Add(described);
                }
            }

            return voice.Address;
        }

        private long BuildMixVoice(bool mastering, long sends)
        {
            Voice voice = new()
            {
                Address = CreateObject("IXAudio2Voice", VoiceSlots, hasUnknown: false, VoiceMethods(source: false)),
            };
            voice.Output = mastering ? 0 : FirstSend(sends);
            _voices[voice.Address] = voice;
            return voice.Address;
        }

        /// <summary>XAUDIO2_VOICE_SENDS { SendCount, XAUDIO2_SEND_DESCRIPTOR* { Flags, pOutputVoice } }.</summary>
        private long FirstSend(long sends)
        {
            if (sends == 0 || _emulator.ReadUInt32(sends, 0) == 0)
            {
                return 0;
            }

            long descriptors = _emulator.ReadUInt32(sends + 4, 0);
            return descriptors == 0 ? 0 : _emulator.ReadUInt32(descriptors + 4, 0);
        }

        private SourceFormat? ReadFormat(long address)
        {
            if (address == 0)
            {
                return null;
            }

            byte[] header = _emulator.ReadMemory(address, 18);
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
                tag = BitConverter.ToUInt16(_emulator.ReadMemory(address + 24, 2), 0);
            }
            else if (tag == 2 && extra >= 4)
            {
                // ADPCMWAVEFORMAT: wSamplesPerBlock, wNumCoef, then the coefficient pairs.
                byte[] adpcm = _emulator.ReadMemory(address + 18, 4);
                samplesPerBlock = BitConverter.ToUInt16(adpcm, 0);
                int count = Math.Min((int)BitConverter.ToUInt16(adpcm, 2), 32);
                byte[] raw = _emulator.ReadMemory(address + 22, count * 4);
                coefficients = new short[count * 2];
                Buffer.BlockCopy(raw, 0, coefficients, 0, raw.Length);
            }

            return new SourceFormat(tag, channels, rate, bits, align, samplesPerBlock, coefficients);
        }

        private Dictionary<int, (string, Action)> VoiceMethods(bool source)
        {
            Dictionary<int, (string, Action)> methods = new()
            {
                [0] = ("GetVoiceDetails", () =>
                {
                    // XAUDIO2_VOICE_DETAILS: CreationFlags, ActiveFlags, InputChannels, InputSampleRate.
                    long details = Arg(1);
                    if (details != 0)
                    {
                        SourceFormat? f = VoiceAt(Arg(0))?.Format;
                        _emulator.WriteUInt32(details + 0, 0);
                        _emulator.WriteUInt32(details + 4, 0);
                        _emulator.WriteUInt32(details + 8, (uint)(f?.Channels ?? 2));
                        _emulator.WriteUInt32(details + 12, (uint)(f?.SampleRate ?? 44100));
                    }

                    Return(HResultOk);
                }),
                [1] = ("SetOutputVoices", () =>
                {
                    if (VoiceAt(Arg(0)) is { } v)
                    {
                        v.Output = FirstSend(Arg(1));
                    }

                    Return(HResultOk);
                }),
                // SetVolume(float Volume, UINT32 OperationSet): the volume is in s0.
                [12] = ("SetVolume", () =>
                {
                    if (VoiceAt(Arg(0)) is { } v)
                    {
                        v.Volume = _frame.FloatArg(0);
                    }

                    Return(HResultOk);
                }),
                [13] = ("GetVolume", () =>
                {
                    if (Arg(1) != 0)
                    {
                        _emulator.WriteSingle(Arg(1), VoiceAt(Arg(0))?.Volume ?? 1f);
                    }

                    Return(HResultOk);
                }),
                [18] = ("DestroyVoice", () =>
                {
                    if (VoiceAt(Arg(0)) is { } v)
                    {
                        v.Out?.Dispose();
                        _voices.Remove(v.Address);
                        // A callback object that no voice uses any more may be freed next.
                        if (v.Callback != 0 && !_voices.Values.Any(o => o.Callback == v.Callback))
                        {
                            _pendingCallbacks.RemoveAll(c => c.Args[0] == v.Callback);
                        }
                    }

                    Return(HResultOk);
                }),
            };

            if (!source)
            {
                return methods;
            }

            methods[19] = ("Start", () =>
            {
                if (VoiceAt(Arg(0)) is { } v)
                {
                    v.Started = true;
                    Feed(v);
                    v.Out?.Play();
                }

                Return(HResultOk);
            });
            methods[20] = ("Stop", () =>
            {
                if (VoiceAt(Arg(0)) is { } v)
                {
                    v.Started = false;
                    v.Out?.Pause();
                }

                Return(HResultOk);
            });
            methods[21] = ("SubmitSourceBuffer", () =>
            {
                BuffersSubmitted++;
                if (VoiceAt(Arg(0)) is { } v && Arg(1) != 0)
                {
                    Enqueue(v, Arg(1));
                }

                Return(HResultOk);
            });
            methods[22] = ("FlushSourceBuffers", () =>
            {
                if (VoiceAt(Arg(0)) is { } v)
                {
                    // Everything queued ends now and is reported ended at the next pump.
                    foreach (QueuedBuffer b in v.Queue)
                    {
                        b.Stage = 3;
                        b.LastSeq = 0;
                    }

                    v.Out?.Flush();
                }

                Return(HResultOk);
            });
            methods[23] = ("Discontinuity", () => Return(HResultOk));
            methods[24] = ("ExitLoop", () =>
            {
                if (VoiceAt(Arg(0)) is { } v)
                {
                    foreach (QueuedBuffer b in v.Queue)
                    {
                        b.ExitLoop = true;
                    }
                }

                Return(HResultOk);
            });
            methods[25] = ("GetState", () =>
            {
                // XAUDIO2_VOICE_STATE: pCurrentBufferContext, BuffersQueued, SamplesPlayed (64-bit).
                long state = Arg(1);
                if (state != 0)
                {
                    Voice? v = VoiceAt(Arg(0));
                    if (v is not null)
                    {
                        Retire(v);
                    }

                    _emulator.WriteUInt32(state, (uint)(v?.Queue.Count > 0 ? v.Queue[0].Context : 0));
                    _emulator.WriteUInt32(state + 4, (uint)(v?.Queue.Count ?? 0));
                    _emulator.WriteUInt64(state + 8, (ulong)(v?.SamplesPlayed ?? 0));
                }

                Return(HResultOk);
            });
            // SetFrequencyRatio(float Ratio, UINT32 OperationSet).
            methods[26] = ("SetFrequencyRatio", () =>
            {
                if (VoiceAt(Arg(0)) is { } v)
                {
                    v.Ratio = _frame.FloatArg(0);
                }

                Return(HResultOk);
            });
            methods[27] = ("GetFrequencyRatio", () =>
            {
                if (Arg(1) != 0)
                {
                    _emulator.WriteSingle(Arg(1), VoiceAt(Arg(0))?.Ratio ?? 1f);
                }

                Return(HResultOk);
            });
            methods[28] = ("SetSourceSampleRate", () => Return(HResultOk));
            return methods;
        }

        private Voice? VoiceAt(long address) => _voices.GetValueOrDefault(address);

        // ------------------------------------------------------------------------------
        // Buffers
        // ------------------------------------------------------------------------------

        /// <summary>
        /// XAUDIO2_BUFFER { Flags, AudioBytes, pAudioData, PlayBegin, PlayLength, LoopBegin,
        /// LoopLength, LoopCount, pContext }. Copied now: the game may reuse the memory once the
        /// buffer ends, and by then it has been converted.
        /// </summary>
        private void Enqueue(Voice v, long descriptor)
        {
            byte[] d = _emulator.ReadMemory(descriptor, 36);
            uint flags = BitConverter.ToUInt32(d, 0);
            int bytes = (int)BitConverter.ToUInt32(d, 4);
            long data = BitConverter.ToUInt32(d, 8);
            long playBegin = BitConverter.ToUInt32(d, 12);
            long playLength = BitConverter.ToUInt32(d, 16);
            long loopBegin = BitConverter.ToUInt32(d, 20);
            long loopLength = BitConverter.ToUInt32(d, 24);
            int loopCount = (int)BitConverter.ToUInt32(d, 28);
            if (_buffersLogged++ < 12)
            {
                _log.Add($"buffer on 0x{v.Address:X8}: {bytes} bytes at 0x{data:X8} flags 0x{flags:X} play {playBegin}+{playLength} loop {loopBegin}+{loopLength} x{loopCount} ctx 0x{BitConverter.ToUInt32(d, 32):X8}");
            }

            QueuedBuffer buffer = new()
            {
                Context = BitConverter.ToUInt32(d, 32),
                EndOfStream = (flags & EndOfStreamFlag) != 0,
            };

            if (v.Out is not null && v.Format is { } f && data != 0 && bytes > 0)
            {
                short[] pcm = Decode(f, _emulator.ReadMemory(data, bytes));
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
                // Silent: nothing to feed, so the buffer ends at the next pump.
                buffer.Stage = 3;
            }

            v.Queue.Add(buffer);
            Feed(v);
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

        /// <summary>Keeps up to three chunks queued on the host for each voice.</summary>
        private void Feed(Voice v)
        {
            if (v.Out is null)
            {
                return;
            }

            foreach (QueuedBuffer b in v.Queue)
            {
                while (b.Stage < 3 && v.Fed - v.Out.Completed < 3)
                {
                    byte[]? chunk = NextChunk(b);
                    if (chunk is null || chunk.Length == 0)
                    {
                        continue;
                    }

                    v.Out.Submit(chunk);
                    v.Fed++;
                    b.LastSeq = v.Fed;
                    if (!b.Started)
                    {
                        b.Started = true;
                        QueueCallback(v, 3, "OnBufferStart", b.Context);
                    }
                }

                if (b.Stage < 3)
                {
                    return;
                }
            }
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

        /// <summary>Removes buffers the host has finished with, queueing their callbacks.</summary>
        private void Retire(Voice v)
        {
            int completed = v.Out?.Completed ?? int.MaxValue;
            while (v.Queue.Count > 0)
            {
                QueuedBuffer b = v.Queue[0];
                if (b.Stage < 3 || completed < b.LastSeq)
                {
                    break;
                }

                v.Queue.RemoveAt(0);
                v.SamplesPlayed += b.Frames;
                QueueCallback(v, 4, "OnBufferEnd", b.Context);
                if (b.EndOfStream)
                {
                    QueueCallback(v, 2, "OnStreamEnd");
                }
            }
        }

        // IXAudio2VoiceCallback, no IUnknown: OnVoiceProcessingPassStart 0, OnVoiceProcessingPassEnd 1,
        // OnStreamEnd 2, OnBufferStart 3, OnBufferEnd 4, OnLoopEnd 5, OnVoiceError 6.
        private void QueueCallback(Voice v, int slot, string name, params long[] args)
        {
            if (v.Callback == 0)
            {
                return;
            }

            long function = _emulator.ReadUInt32(_emulator.ReadUInt32(v.Callback, 0) + (slot * 4), 0);
            if (_emulator.IsExecutableCode(function))
            {
                _pendingCallbacks.Add(($"IXAudio2VoiceCallback::{name}", function, [v.Callback, .. args]));
            }
        }

        // ------------------------------------------------------------------------------
        // The per-frame pump
        // ------------------------------------------------------------------------------

        /// <summary>
        /// Once per frame, on the main guest thread from inside a trap: feeds the host, retires
        /// finished buffers, applies volume and pitch, and makes the callbacks that are due.
        /// </summary>
        /// <returns>
        /// True if it has taken over the return path (it is calling into the game) and will run
        /// <paramref name="continueWith"/> itself; false if there was nothing to call.
        /// </returns>
        public bool Pump(Action continueWith)
        {
            // A real engine runs a processing pass every 10 ms and tells registered engine
            // callbacks either side of it; a game with its own mixer (Angry Birds) fills its
            // voice from OnProcessingPassStart. One pass a frame is enough while it keeps a
            // few buffers ahead.
            QueueEngineCallback(0, "OnProcessingPassStart");

            foreach (Voice v in _voices.Values.ToArray())
            {
                if (!v.IsSource)
                {
                    continue;
                }

                Retire(v);
                Feed(v);
                if (v.Out is { } output)
                {
                    float volume = Math.Clamp(EffectiveVolume(v), 0f, 1f);
                    if (Math.Abs(volume - v.AppliedVolume) > 0.001f)
                    {
                        v.AppliedVolume = volume;
                        output.SetVolume(volume);
                    }

                    float pitch = Math.Clamp(MathF.Log2(Math.Max(v.Ratio, 0.0005f)), -1f, 1f);
                    if (!(Math.Abs(pitch - v.AppliedPitch) < 0.001f))
                    {
                        v.AppliedPitch = pitch;
                        output.SetPitch(pitch);
                    }
                }
            }

            QueueEngineCallback(1, "OnProcessingPassEnd");
            if (_pendingCallbacks.Count == 0)
            {
                return false;
            }

            var calls = _pendingCallbacks.ToArray();
            _pendingCallbacks.Clear();
            RunCallbacks(calls, 0, continueWith);
            return true;
        }

        private void QueueEngineCallback(int slot, string name)
        {
            foreach (long callback in _engineCallbacks)
            {
                long function = _emulator.ReadUInt32(_emulator.ReadUInt32(callback, 0) + (slot * 4), 0);
                if (_emulator.IsExecutableCode(function))
                {
                    _pendingCallbacks.Add(($"IXAudio2EngineCallback::{name}", function, [callback]));
                }
            }
        }

        private void RunCallbacks((string Name, long Function, long[] Args)[] calls, int index, Action continueWith)
        {
            if (index >= calls.Length)
            {
                continueWith();
                return;
            }

            CallbacksMade++;
            var (name, function, args) = calls[index];
            _emulator.CallEmulated(name, function, args, () => RunCallbacks(calls, index + 1, continueWith), recycle: true);
        }

        /// <summary>The voice's own volume times every mix voice it is routed through.</summary>
        private float EffectiveVolume(Voice v)
        {
            float volume = v.Volume;
            long next = v.Output != 0 ? v.Output : _masteringVoice;
            for (int depth = 0; depth < 8 && next != 0 && _voices.TryGetValue(next, out Voice? mix); depth++)
            {
                volume *= mix.Volume;
                next = mix.Output != 0 ? mix.Output : (next == _masteringVoice ? 0 : _masteringVoice);
            }

            return volume;
        }

        // ------------------------------------------------------------------------------
        // Conversion to 16-bit PCM, mono or stereo
        // ------------------------------------------------------------------------------

        private static short[] Decode(SourceFormat f, byte[] data) => f.Tag switch
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
        private static short[] DecodeAdpcm(SourceFormat f, byte[] data)
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
