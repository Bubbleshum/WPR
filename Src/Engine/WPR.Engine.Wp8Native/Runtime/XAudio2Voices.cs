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
            public XAudio2Format? Format;
            public IAudioVoiceOutput? Out;
            public XAudio2BufferQueue Buffers = new(null, null);
            public float AppliedVolume = -1f;
            public float AppliedPitch = float.NaN;
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
                if (f.IsPlayable)
                {
                    voice.Out = Output.CreateVoice(f.SampleRate, f.OutChannels);
                }
                else if (!UnsupportedFormats.Contains(described))
                {
                    UnsupportedFormats.Add(described);
                }
            }

            voice.Buffers = new XAudio2BufferQueue(voice.Format, voice.Out);
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

        private XAudio2Format? ReadFormat(long address) =>
            address == 0 ? null : XAudio2Format.Read((offset, count) => _emulator.ReadMemory(address + offset, count));

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
                        XAudio2Format? f = VoiceAt(Arg(0))?.Format;
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
                // Everything queued ends now and is reported ended at the next pump.
                VoiceAt(Arg(0))?.Buffers.Flush();

                Return(HResultOk);
            });
            methods[23] = ("Discontinuity", () => Return(HResultOk));
            methods[24] = ("ExitLoop", () =>
            {
                VoiceAt(Arg(0))?.Buffers.ExitLoop();
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

                    _emulator.WriteUInt32(state, (uint)(v?.Buffers.CurrentContext ?? 0));
                    _emulator.WriteUInt32(state + 4, (uint)(v?.Buffers.Count ?? 0));
                    _emulator.WriteUInt64(state + 8, (ulong)(v?.Buffers.SamplesPlayed ?? 0));
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
            long context = BitConverter.ToUInt32(d, 32);
            if (_buffersLogged++ < 12)
            {
                _log.Add($"buffer on 0x{v.Address:X8}: {bytes} bytes at 0x{data:X8} flags 0x{flags:X} play {BitConverter.ToUInt32(d, 12)}+{BitConverter.ToUInt32(d, 16)} loop {BitConverter.ToUInt32(d, 20)}+{BitConverter.ToUInt32(d, 24)} x{BitConverter.ToUInt32(d, 28)} ctx 0x{context:X8}");
            }

            byte[]? audio = v.Out is not null && v.Format is not null && data != 0 && bytes > 0
                ? _emulator.ReadMemory(data, bytes)
                : null;
            v.Buffers.Enqueue(flags, audio, BitConverter.ToUInt32(d, 12), BitConverter.ToUInt32(d, 16),
                BitConverter.ToUInt32(d, 20), BitConverter.ToUInt32(d, 24), (int)BitConverter.ToUInt32(d, 28), context);
            Feed(v);
        }

        private static readonly string[] CallbackNames =
            ["OnVoiceProcessingPassStart", "OnVoiceProcessingPassEnd", "OnStreamEnd", "OnBufferStart", "OnBufferEnd", "OnLoopEnd", "OnVoiceError"];

        /// <summary>Keeps up to three chunks queued on the host for each voice.</summary>
        private void Feed(Voice v) =>
            v.Buffers.Feed((slot, context) => QueueCallback(v, slot, CallbackNames[slot], context));

        /// <summary>Removes buffers the host has finished with, queueing their callbacks.</summary>
        private void Retire(Voice v) =>
            v.Buffers.Retire((slot, context) =>
            {
                if (slot == XAudio2BufferQueue.OnStreamEnd)
                {
                    QueueCallback(v, slot, CallbackNames[slot]);
                }
                else
                {
                    QueueCallback(v, slot, CallbackNames[slot], context);
                }
            });

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
        /// <summary>
        /// Raised at the top of every <see cref="Pump"/>, i.e. once per frame of the main loop: the
        /// clock the other audio paths (WASAPI, the media engine) advance on. Handlers must not
        /// call into the image.
        /// </summary>
        public event Action? FrameTick;

        public bool Pump(Action continueWith)
        {
            FrameTick?.Invoke();
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
    }
}
