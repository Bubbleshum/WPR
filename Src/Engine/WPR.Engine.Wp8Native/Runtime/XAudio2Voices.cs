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

                    // A voice with a callback hears of a finished buffer at the pump, so its buffers
                    // leave the queue there too, with their OnBufferEnd: on a phone the two happen
                    // together on the audio thread. Retired here, a finished buffer left the count a
                    // frame before its OnBufferEnd woke anyone, and a thread woken by something else
                    // saw room it was never told about. Asphalt 7's streamer, woken by its own exit
                    // code, refilled into a lock the exiting thread held and the game never closed.
                    // A voice nobody calls back is polled, so its count stays current here.
                    if (v is { Callback: 0 })
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
        /// Raised at the top of every pass: once per frame of the main loop, and from the engine
        /// thread while the frame loop is stalled. The clock the other audio paths (WASAPI, the
        /// media engine) advance on. Handlers must not call into the image.
        /// </summary>
        public event Action? FrameTick;

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
            EnsureEngineThread();
            var calls = CollectPass();
            if (calls.Length == 0)
            {
                return false;
            }

            RunCallbacks(calls, 0, continueWith);
            return true;
        }

        /// <summary>
        /// One processing pass, on whichever guest thread runs it: feeds the host, retires finished
        /// buffers, applies volume and pitch, and returns the callbacks now due (to be run on that
        /// thread). Calls nothing in the image itself.
        /// </summary>
        private (string Name, long Function, long[] Args)[] CollectPass()
        {
            FrameTick?.Invoke();
            // A real engine runs a processing pass every 10 ms and tells registered engine
            // callbacks either side of it; a game with its own mixer (Angry Birds) fills its
            // voice from OnProcessingPassStart. One pass a frame is enough while it keeps a
            // few buffers ahead.
            QueueEngineCallback(0, "OnProcessingPassStart");
            double lead = PassLead();

            foreach (Voice v in _voices.Values.ToArray())
            {
                if (!v.IsSource)
                {
                    continue;
                }

                Retire(v);
                Feed(v);
                if (v.Started)
                {
                    QueuePassCallbacks(v, lead);
                }

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
            var calls = _pendingCallbacks.ToArray();
            _pendingCallbacks.Clear();
            return calls;
        }

        // ------------------------------------------------------------------------------
        // The engine thread: passes while the frame loop is stalled
        // ------------------------------------------------------------------------------

        /// <summary>
        /// <c>WPR_WP8_AUDIO_THREAD=0</c> leaves every pass to the frame loop, as before the engine
        /// thread existed, to rule it out.
        /// </summary>
        private static readonly bool EngineThreadEnabled =
            Environment.GetEnvironmentVariable("WPR_WP8_AUDIO_THREAD") != "0";

        /// <summary>
        /// How long without a pass before the engine thread runs one: longer than a 30 fps frame
        /// (33 ms), so normal play keeps every pass on the frame loop, and short enough for a WASAPI
        /// stream that holds only about 100 ms. 60 ms left N.O.V.A. 3's stream (event-driven,
        /// 50 ms chunks) running dry in every second-long loading stall: +26 underruns over the
        /// campaign load at 60 ms, 0 at 40. <c>WPR_WP8_AUDIO_PASS_MS</c> overrides it.
        /// </summary>
        private static readonly long EnginePassDueTicks = System.Diagnostics.Stopwatch.Frequency
            * (int.TryParse(Environment.GetEnvironmentVariable("WPR_WP8_AUDIO_PASS_MS"), out int ms) && ms > 0 ? ms : 40) / 1000;

        private int _engineThread;          // 0 = not yet, -1 = given up, else its guest thread id
        private bool _enginePassRunning;

        /// <summary>Passes the engine thread ran because the frame loop had not (loading screens).</summary>
        public int EnginePasses { get; private set; }

        /// <summary>
        /// Whether another audio path the frame tick drives is in use (the media engine, WASAPI),
        /// so a title with no XAudio2 voice of its own still gets an engine thread.
        /// </summary>
        public Func<bool>? OtherAudioActive { get; set; }

        /// <summary>
        /// Starts the engine thread the first time a title has sound to keep going. Called from the
        /// frame pump, inside a trap on the main thread.
        /// </summary>
        /// <remarks>
        /// Every pass used to run at the frame loop's turn on the main thread. A loading screen
        /// that presents nothing for seconds therefore fed no voice, raised no OnBufferEnd and
        /// ticked no media clock, and the music stopped until the next frame (AC Pirates after
        /// PLAY, Halo's mission loads, every Mandreel load). On a phone XAudio2 runs passes on its
        /// own thread, whatever the game's threads are doing; this is that thread. It only acts
        /// when the frame loop has fallen behind (<see cref="EnginePassDue"/>), so in normal play
        /// every pass, and every callback, still happens exactly where it did before.
        /// </remarks>
        private void EnsureEngineThread()
        {
            if (!EngineThreadEnabled || _engineThread != 0)
            {
                return;
            }

            if (!_voices.Values.Any(v => v.IsSource) && OtherAudioActive?.Invoke() != true)
            {
                return;
            }

            long gate = _emulator.RegisterVtableMethod("XAudio2 engine thread", ParkEngine);
            _engineThread = _emulator.CreateGuestThread("XAudio2 engine", ArmEmulator.ThumbEntry(gate), 0);
            _emulator.MarkAudioEngineThread(_engineThread);
            _emulator.AudioPassDue = EnginePassDue;
            _emulator.AudioPassDueAt = IdlePassDueAt;
        }

        private bool EnginePassDue() =>
            !_enginePassRunning && _lastPass != 0
            && System.Diagnostics.Stopwatch.GetTimestamp() - _lastPass > EnginePassDueTicks;

        /// <summary>
        /// How long without a pass before the engine thread runs one when every guest thread is
        /// waiting (the presenting thread for the host's frame, the rest on their own waits). The
        /// CPU is idle then, so this is shorter than <see cref="EnginePassDueTicks"/>: a phone's
        /// XAudio2 runs a pass every 10 ms whatever the game does. Longer than one 60 fps frame, so
        /// a host that keeps up still sees every pass on the frame loop.
        /// <c>WPR_WP8_AUDIO_IDLE_PASS_MS</c> overrides it.
        /// </summary>
        private static readonly long IdlePassTicks = System.Diagnostics.Stopwatch.Frequency
            * (int.TryParse(Environment.GetEnvironmentVariable("WPR_WP8_AUDIO_IDLE_PASS_MS"), out int idle) && idle > 0 ? idle : 20) / 1000;

        private long? IdlePassDueAt() =>
            _enginePassRunning || _lastPass == 0 ? null : _lastPass + IdlePassTicks;

        /// <summary>One pass on the engine thread, then park again.</summary>
        private void EnginePass()
        {
            _enginePassRunning = true;
            EnginePasses++;
            var calls = CollectPass();
            if (calls.Length == 0)
            {
                ParkEngine();
                return;
            }

            RunCallbacks(calls, 0, ParkEngine);
        }

        /// <summary>
        /// Parks the engine thread until a pass is due. If no other thread can take the CPU (not
        /// expected: the thread it was switched in from is runnable), it ends instead, and passes go
        /// back to the frame loop alone.
        /// </summary>
        private void ParkEngine()
        {
            _enginePassRunning = false;
            if (_emulator.ParkAudioEngineThread(EnginePass, "XAudio2 engine, waiting for a stalled frame loop"))
            {
                return;
            }

            _engineThread = -1;
            _emulator.AudioPassDue = null;
            _emulator.AudioPassDueAt = null;
            if (!_emulator.EndCurrentGuestThread("the XAudio2 engine thread had no thread to hand the CPU back to"))
            {
                _emulator.Stop("the XAudio2 engine thread ended and no thread is runnable");
            }
        }

        private long _lastPass;
        private double _passInterval = 1.0 / 60;

        /// <summary>
        /// Seconds of sound a voice should have queued at a pass: three of our passes (one a
        /// frame, so a slow frame does not run it dry), 60 to 200 ms.
        /// </summary>
        private double PassLead()
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_lastPass != 0)
            {
                double seconds = (now - _lastPass) / (double)System.Diagnostics.Stopwatch.Frequency;
                _passInterval += (Math.Min(seconds, 0.25) - _passInterval) * 0.1;
            }

            _lastPass = now;
            return Math.Clamp(_passInterval * 3, 0.06, 0.2);
        }

        /// <summary>
        /// OnVoiceProcessingPassStart(BytesRequired) and OnVoiceProcessingPassEnd for a started
        /// voice, as XAudio2 raises them on every processing pass. A game with its own mixer
        /// (Mandreel's) submits from OnVoiceProcessingPassStart and nowhere else, so without it
        /// the voice is started and stays empty. BytesRequired is what tops the voice up to
        /// <paramref name="lead"/> seconds, in the voice's own format, and 0 while it has enough:
        /// XAudio2 asks for what avoids starving before the next pass, and ours come a frame apart.
        /// </summary>
        private void QueuePassCallbacks(Voice v, double lead)
        {
            if (v.Callback == 0)
            {
                return;
            }

            QueueCallback(v, 0, CallbackNames[0], v.Buffers.BytesRequired(lead));
            QueueCallback(v, 1, CallbackNames[1]);
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
