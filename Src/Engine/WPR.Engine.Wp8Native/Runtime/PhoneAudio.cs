namespace WPR.Wp8Native
{
    /// <summary>
    /// PhoneAudioSes.dll: WASAPI on Windows Phone 8 - the default render endpoint, and
    /// IAudioClient2 / IAudioRenderClient / IAudioClock / ISimpleAudioVolume /
    /// IAudioStreamVolume over the host's audio output.
    /// </summary>
    /// <remarks>
    /// The endpoint is shared-mode only, like a phone's: its mix format is 48 kHz stereo IEEE
    /// float, and a stream is accepted at that rate and channel count as float or 16-bit PCM.
    /// What the game writes is converted to 16-bit PCM and queued on an
    /// <see cref="IAudioVoiceOutput"/> one release at a time; padding is what the host has not
    /// yet played. An event-driven stream has its event set once a frame while there is room,
    /// which is how such a game is paced. There is no capture: the host has no microphone path,
    /// so <c>GetDefaultAudioCaptureId</c> reports no device.
    /// </remarks>
    internal sealed class PhoneAudio
    {
        public static readonly Guid IidAudioClient = new("1cb9ad4c-dbfa-4c32-b178-c2f568a703b2");
        public static readonly Guid IidAudioClient2 = new("726778cd-f60a-4eda-82de-e47610cd78aa");
        public static readonly Guid IidRenderClient = new("f294acfc-3146-4483-a7bf-addca7c260e2");
        public static readonly Guid IidCaptureClient = new("c8adbd64-e71e-48a0-a4de-185c395cd317");
        public static readonly Guid IidClock = new("cd63314f-3fba-4a1b-812c-ef96358728e7");
        public static readonly Guid IidSimpleVolume = new("87ce5498-68d6-44e5-9215-6da47ef883d8");
        public static readonly Guid IidStreamVolume = new("93014887-242d-4068-8a15-cf5e93b90fe3");

        public const long NotInitialized = unchecked((int)0x88890001);
        public const long AlreadyInitialized = unchecked((int)0x88890002);
        public const long WrongEndpointType = unchecked((int)0x88890003);
        public const long NotStopped = unchecked((int)0x88890005);
        public const long BufferTooLarge = unchecked((int)0x88890006);
        public const long OutOfOrder = unchecked((int)0x88890007);
        public const long UnsupportedFormat = unchecked((int)0x88890008);
        public const long InvalidSize = unchecked((int)0x88890009);
        public const long ExclusiveModeNotAllowed = unchecked((int)0x8889000E);
        public const long EventHandleNotExpected = unchecked((int)0x88890013);
        public const long EventHandleNotSet = unchecked((int)0x88890014);
        public const long OffloadNotCapable = unchecked((int)0x88890022);
        public const long OffloadModeOnly = unchecked((int)0x88890023);

        public const int MixRate = 48000, MixChannels = 2;

        /// <summary>The device period, 10 ms, in 100-ns units; the minimum is 3 ms.</summary>
        public const long DefaultPeriod = 100_000, MinimumPeriod = 30_000;

        /// <summary>
        /// The smallest endpoint buffer a stream gets, 50 ms, whatever it asked for. A phone woke an
        /// event-driven stream every 10 ms; this host wakes it once per guest frame, 16.7 ms at best
        /// and much longer while a game loads, so a stream sized to one 10 ms period cannot be kept
        /// fed. Monopoly Millionaire asks for exactly that (measured: 60% of real time even with the
        /// rest of this fixed). Shared mode may hand back a larger buffer than requested, and games
        /// size their writes from GetBufferSize, so this is within the contract. 20 ms already kept
        /// up at 60 fps; 50 ms also rides out slow frames.
        /// </summary>
        public const long MinimumBufferDuration = 500_000;

        public const string RenderId = @"\\?\SWD#MMDEVAPI#{0.0.0.00000000}.{6b4e04c0-6b2f-4fd3-9d32-2a26d1a2e4d1}#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
        public const string CommunicationsRenderId = @"\\?\SWD#MMDEVAPI#{0.0.0.00000000}.{6b4e04c0-6b2f-4fd3-9d32-2a26d1a2e4d2}#{e6327cad-dcec-4949-ae8a-991e976a79d2}";

        private readonly MediaCom _com;
        private readonly ArmEmulator _e;
        private readonly Func<IAudioOutput?> _output;
        private readonly Func<long, bool> _setEvent;
        private readonly List<Client> _clients = new();

        public PhoneAudio(MediaCom com, Func<IAudioOutput?> output, Func<long, bool> setEvent)
        {
            _com = com;
            _e = com.Emulator;
            _output = output;
            _setEvent = setEvent;
        }

        private long Arg(int i) => _com.Arg(i);

        private void Return(long v) => _com.Return(v);

        public void RegisterInto(Dictionary<string, Action> handlers)
        {
            // LPCWSTR GetDefaultAudioRenderId(AudioDeviceRole): a CoTaskMemAlloc'd id the caller frees.
            handlers["GetDefaultAudioRenderId"] = () =>
                Return(Arg(0) switch
                {
                    0 => _com.AllocWide(RenderId),
                    1 => _com.AllocWide(CommunicationsRenderId),
                    _ => 0,
                });

            // LPCWSTR GetDefaultAudioCaptureId(AudioDeviceRole): NULL - there is no capture device.
            handlers["GetDefaultAudioCaptureId"] = () => Return(0);

            // HRESULT ActivateAudioInterface(LPCWSTR deviceInterfacePath, REFIID riid, void** ppv).
            handlers["ActivateAudioInterface"] = () =>
            {
                long outPointer = Arg(2);
                if (outPointer == 0)
                {
                    Return(MediaCom.EPointer);
                    return;
                }

                _com.W32(outPointer, 0);
                string path = _com.ReadWide(Arg(0));
                if (path is not (RenderId or CommunicationsRenderId))
                {
                    Return(MediaCom.ENotFound);
                    return;
                }

                Guid iid = _com.ReadGuid(Arg(1));
                if (iid != IidAudioClient && iid != IidAudioClient2 && iid != MediaCom.IidUnknown)
                {
                    Return(MediaCom.ENoInterface);
                    return;
                }

                Client client = new();
                _clients.Add(client);
                _com.W32(outPointer, _com.Expose(client, ClientShape));
                Return(MediaCom.SOk);
            };
        }

        // ------------------------------------------------------------------------------
        // IAudioClient2
        // ------------------------------------------------------------------------------

        internal sealed class Client : MediaObject
        {
            public override string Kind => "IAudioClient";

            public bool Initialized;
            public bool Running;
            public bool EventDriven;
            public long EventHandle;
            public bool IsFloat;
            public int Channels, Rate, Bits, BlockAlign;
            public int BufferFrames;
            public long Scratch;
            public int Requested = -1;           // frames handed out by GetBuffer and not yet released
            public IAudioVoiceOutput? Voice;
            public readonly Queue<int> Queued = new();
            public int Retired;                  // voice.Completed already accounted for
            public long PlayedFrames;
            public long WrittenFrames;
            public float Volume = 1;
            public bool Mute;
            public float[] ChannelVolumes = [];
            public uint Category;

            /// <summary>
            /// What the game is told is still unplayed: the host's queue less one buffer of slack the
            /// game cannot see. A phone drained the buffer on its own clock and woke the stream as it
            /// went; here the wake arrives at frame granularity, up to a frame after the buffer
            /// empties, and a game that refills only an empty buffer (Monopoly Millionaire) left that
            /// gap every cycle - 85% of real time. With the slack it refills while the host still holds
            /// a buffer, for up to one buffer of extra latency.
            /// </summary>
            public int Padding => Math.Max(0, Queued.Sum() - BufferFrames);

            public override IEnumerable<long> Destroy()
            {
                Voice?.Dispose();
                Voice = null;
                return [];
            }
        }

        private MediaInterface? _clientShape;
        private MediaInterface? _renderShape;
        private MediaInterface? _clockShape;
        private MediaInterface? _simpleVolumeShape;
        private MediaInterface? _streamVolumeShape;

        private void With(Action<Client> act, bool needsInit = true)
        {
            Client? c = _com.Self<Client>();
            if (c is null)
            {
                Return(MediaCom.EPointer);
            }
            else if (needsInit && !c.Initialized)
            {
                Return(NotInitialized);
            }
            else
            {
                act(c);
            }
        }

        /// <summary>A WAVEFORMATEXTENSIBLE for the mix format.</summary>
        private static byte[] MixFormat() =>
            MediaFoundation.WaveFormat.Write(isFloat: true, MixChannels, MixRate, 32, extensible: true, mask: 0x3);

        /// <summary>The format, if the shared engine takes it as it is.</summary>
        private static MediaFoundation.WaveFormat? Acceptable(MediaFoundation.WaveFormat wf, out bool closeEnough)
        {
            ushort tag = wf.EffectiveTag;
            bool sampleType = (tag == MediaFoundation.WaveFormat.Float && wf.Bits == 32) ||
                              (tag == MediaFoundation.WaveFormat.Pcm && wf.Bits == 16);
            closeEnough = sampleType || tag is MediaFoundation.WaveFormat.Pcm or MediaFoundation.WaveFormat.Float;
            bool shape = wf.Channels == MixChannels && wf.SampleRate == MixRate && wf.BlockAlign == wf.Channels * (wf.Bits / 8);
            return sampleType && shape ? wf : null;
        }

        // IAudioClient: Initialize 3, GetBufferSize 4, GetStreamLatency 5, GetCurrentPadding 6,
        // IsFormatSupported 7, GetMixFormat 8, GetDevicePeriod 9, Start 10, Stop 11, Reset 12,
        // SetEventHandle 13, GetService 14. IAudioClient2: IsOffloadCapable 15,
        // SetClientProperties 16, GetBufferSizeLimits 17.
        private MediaInterface ClientShape => _clientShape ??= new("IAudioClient2", [IidAudioClient2, IidAudioClient], 18, () => new()
        {
            [3] = ("Initialize", () => With(Initialize, needsInit: false)),
            [4] = ("GetBufferSize", () => With(c => { _com.W32(Arg(1), c.BufferFrames); Return(Arg(1) == 0 ? MediaCom.EPointer : MediaCom.SOk); })),
            [5] = ("GetStreamLatency", () => With(c => { _com.W64(Arg(1), (ulong)DefaultPeriod); Return(Arg(1) == 0 ? MediaCom.EPointer : MediaCom.SOk); })),
            [6] = ("GetCurrentPadding", () => With(c =>
            {
                Retire(c);
                _com.W32(Arg(1), c.Padding);
                Return(Arg(1) == 0 ? MediaCom.EPointer : MediaCom.SOk);
            })),

            // IsFormatSupported(AUDCLNT_SHAREMODE, const WAVEFORMATEX*, WAVEFORMATEX** ppClosestMatch).
            [7] = ("IsFormatSupported", () => With(client =>
            {
                long closest = Arg(3);
                _com.W32(closest, 0);
                if (Arg(2) == 0)
                {
                    Return(MediaCom.EPointer);
                    return;
                }

                if (Arg(1) != 0)
                {
                    Return(ExclusiveModeNotAllowed);
                    return;
                }

                MediaFoundation.WaveFormat? wf = ReadFormat(Arg(2));
                if (wf is not null && Acceptable(wf, out _) is not null)
                {
                    Return(MediaCom.SOk);
                    return;
                }

                if (wf is not null && closest != 0 && Acceptable(wf, out bool close) is null && close)
                {
                    byte[] mix = MixFormat();
                    long p = _com.Alloc(mix.Length);
                    _e.WriteMemory(p, mix);
                    _com.W32(closest, p);
                    Return(MediaCom.SFalse);
                    return;
                }

                Return(UnsupportedFormat);
            }, needsInit: false)),
            [8] = ("GetMixFormat", () => With(_ =>
            {
                if (Arg(1) == 0)
                {
                    Return(MediaCom.EPointer);
                    return;
                }

                byte[] mix = MixFormat();
                long p = _com.Alloc(mix.Length);
                _e.WriteMemory(p, mix);
                _com.W32(Arg(1), p);
                Return(MediaCom.SOk);
            }, needsInit: false)),
            [9] = ("GetDevicePeriod", () => With(_ =>
            {
                if (Arg(1) == 0 && Arg(2) == 0)
                {
                    Return(MediaCom.EPointer);
                    return;
                }

                _com.W64(Arg(1), (ulong)DefaultPeriod);
                _com.W64(Arg(2), (ulong)MinimumPeriod);
                Return(MediaCom.SOk);
            }, needsInit: false)),
            [10] = ("Start", () => With(c =>
            {
                if (c.Running)
                {
                    Return(NotStopped);
                    return;
                }

                if (c.EventDriven && c.EventHandle == 0)
                {
                    Return(EventHandleNotSet);
                    return;
                }

                c.Running = true;
                c.Voice?.Play();
                Signal(c);
                Return(MediaCom.SOk);
            })),
            [11] = ("Stop", () => With(c =>
            {
                if (!c.Running)
                {
                    Return(MediaCom.SFalse);
                    return;
                }

                c.Running = false;
                c.Voice?.Pause();
                Return(MediaCom.SOk);
            })),
            [12] = ("Reset", () => With(c =>
            {
                if (c.Running)
                {
                    Return(NotStopped);
                    return;
                }

                if (c.Requested >= 0)
                {
                    Return(OutOfOrder);
                    return;
                }

                c.Voice?.Flush();
                Retire(c);
                c.Queued.Clear();
                c.PlayedFrames = 0;
                c.WrittenFrames = 0;
                Return(MediaCom.SOk);
            })),
            [13] = ("SetEventHandle", () => With(c =>
            {
                if (!c.EventDriven)
                {
                    Return(EventHandleNotExpected);
                    return;
                }

                if (Arg(1) == 0)
                {
                    Return(MediaCom.EInvalidArg);
                    return;
                }

                c.EventHandle = Arg(1);
                Return(MediaCom.SOk);
            })),

            // GetService(REFIID, void**).
            [14] = ("GetService", () => With(c =>
            {
                if (Arg(2) == 0)
                {
                    Return(MediaCom.EPointer);
                    return;
                }

                Guid iid = _com.ReadGuid(Arg(1));
                MediaInterface? shape =
                    iid == IidRenderClient ? RenderShape :
                    iid == IidClock ? ClockShape :
                    iid == IidSimpleVolume ? SimpleVolumeShape :
                    iid == IidStreamVolume ? StreamVolumeShape :
                    null;
                if (shape is null)
                {
                    _com.W32(Arg(2), 0);
                    Return(iid == IidCaptureClient ? WrongEndpointType : MediaCom.ENoInterface);
                    return;
                }

                c.Refs++;
                _com.W32(Arg(2), _com.Expose(c, shape));
                Return(MediaCom.SOk);
            })),
            [15] = ("IsOffloadCapable", () => With(_ => { _com.W32(Arg(2), 0); Return(Arg(2) == 0 ? MediaCom.EPointer : MediaCom.SOk); }, needsInit: false)),

            // SetClientProperties(const AudioClientProperties*: cbSize, bIsOffload, eCategory).
            [16] = ("SetClientProperties", () => With(c =>
            {
                if (c.Initialized)
                {
                    Return(AlreadyInitialized);
                    return;
                }

                if (Arg(1) == 0)
                {
                    Return(MediaCom.EPointer);
                    return;
                }

                if (_com.U32(Arg(1) + 4) != 0)
                {
                    Return(OffloadNotCapable);
                    return;
                }

                c.Category = _com.U32(Arg(1) + 8);
                Return(MediaCom.SOk);
            }, needsInit: false)),
            [17] = ("GetBufferSizeLimits", () => With(_ => Return(OffloadModeOnly), needsInit: false)),
        });

        private MediaFoundation.WaveFormat? ReadFormat(long pointer)
        {
            byte[] head = _e.ReadMemory(pointer, 18);
            int extra = BitConverter.ToUInt16(head, 16);
            return MediaFoundation.WaveFormat.Read(_e.ReadMemory(pointer, 18 + Math.Min(extra, 64)));
        }

        /// <summary>
        /// Initialize(ShareMode, StreamFlags, REFERENCE_TIME hnsBufferDuration, REFERENCE_TIME
        /// hnsPeriodicity, const WAVEFORMATEX*, LPCGUID): the two 64-bit durations skip r3 and land
        /// on the stack at words 4-5 and 6-7; the format and session GUID follow at 8 and 9.
        /// </summary>
        private void Initialize(Client c)
        {
            if (c.Initialized)
            {
                Return(AlreadyInitialized);
                return;
            }

            long shareMode = Arg(1), flags = Arg(2);
            long duration = (long)((ulong)(uint)Arg(4) | ((ulong)(uint)Arg(5) << 32));
            long format = Arg(8);
            if (format == 0)
            {
                Return(MediaCom.EPointer);
                return;
            }

            if (shareMode != 0)
            {
                Return(ExclusiveModeNotAllowed);
                return;
            }

            MediaFoundation.WaveFormat? wf = ReadFormat(format);
            if (wf is null || Acceptable(wf, out _) is null)
            {
                Return(UnsupportedFormat);
                return;
            }

            const long EventCallback = 0x00040000;
            c.EventDriven = (flags & EventCallback) != 0;
            c.IsFloat = wf.EffectiveTag == MediaFoundation.WaveFormat.Float;
            c.Channels = wf.Channels;
            c.Rate = wf.SampleRate;
            c.Bits = wf.Bits;
            c.BlockAlign = wf.BlockAlign;
            long hns = Math.Max(duration, MinimumBufferDuration);
            c.BufferFrames = (int)Math.Ceiling(hns * (double)c.Rate / 10_000_000);
            c.Scratch = _com.Alloc((long)c.BufferFrames * c.BlockAlign);
            c.ChannelVolumes = Enumerable.Repeat(1f, c.Channels).ToArray();
            c.Finalizer = () => _com.Free(c.Scratch);
            c.Voice = _output()?.CreateVoice(c.Rate, Math.Min(c.Channels, 2));
            c.Initialized = true;
            Return(MediaCom.SOk);
        }

        /// <summary>Takes the buffers the host has finished playing off the padding.</summary>
        private static void Retire(Client c)
        {
            if (c.Voice is not { } voice)
            {
                return;
            }

            while (c.Retired < voice.Completed && c.Queued.Count > 0)
            {
                c.PlayedFrames += c.Queued.Dequeue();
                c.Retired++;
            }

            c.Retired = Math.Max(c.Retired, voice.Completed);
        }

        private void Signal(Client c)
        {
            if (c.EventDriven && c.EventHandle != 0 && c.Running && c.Padding < c.BufferFrames)
            {
                _setEvent(c.EventHandle);
            }
        }

        /// <summary>
        /// Once a frame: retires what the host played (or, with no host output, what a frame of
        /// real time would have played), and wakes event-driven streams that have room.
        /// </summary>
        public void Tick(double frameSeconds = 1.0 / 60)
        {
            foreach (Client c in _clients.ToArray())
            {
                if (c.Dead)
                {
                    _clients.Remove(c);
                    continue;
                }

                if (!c.Initialized || !c.Running)
                {
                    continue;
                }

                if (c.Voice is null)
                {
                    int budget = (int)Math.Round(frameSeconds * c.Rate);
                    while (budget > 0 && c.Queued.Count > 0)
                    {
                        int head = c.Queued.Dequeue();
                        int take = Math.Min(head, budget);
                        budget -= take;
                        c.PlayedFrames += take;
                        if (take < head)
                        {
                            // Partly played: the rest stays queued at the front.
                            int[] rest = [head - take, .. c.Queued];
                            c.Queued.Clear();
                            foreach (int r in rest)
                            {
                                c.Queued.Enqueue(r);
                            }
                        }
                    }
                }
                else
                {
                    Retire(c);
                }

                Signal(c);
            }
        }

        // IAudioRenderClient: GetBuffer 3, ReleaseBuffer 4.
        private MediaInterface RenderShape => _renderShape ??= new("IAudioRenderClient", [IidRenderClient], 5, () => new()
        {
            // GetBuffer(UINT32 NumFramesRequested, BYTE** ppData).
            [3] = ("GetBuffer", () => With(c =>
            {
                if (Arg(2) == 0)
                {
                    Return(MediaCom.EPointer);
                    return;
                }

                if (c.Requested >= 0)
                {
                    _com.W32(Arg(2), 0);
                    Return(OutOfOrder);
                    return;
                }

                Retire(c);
                if (Arg(1) > c.BufferFrames - c.Padding)
                {
                    _com.W32(Arg(2), 0);
                    Return(BufferTooLarge);
                    return;
                }

                c.Requested = (int)Arg(1);
                _com.W32(Arg(2), Arg(1) == 0 ? 0 : c.Scratch);
                Return(MediaCom.SOk);
            })),

            // ReleaseBuffer(UINT32 NumFramesWritten, DWORD dwFlags): AUDCLNT_BUFFERFLAGS_SILENT is 2.
            [4] = ("ReleaseBuffer", () => With(c =>
            {
                int written = (int)Arg(1);
                if (c.Requested < 0)
                {
                    Return(written == 0 ? MediaCom.SOk : OutOfOrder);
                    return;
                }

                if (written > c.Requested)
                {
                    Return(InvalidSize);
                    return;
                }

                c.Requested = -1;
                if (written > 0)
                {
                    bool silent = (Arg(2) & 2) != 0;
                    byte[] raw = silent ? new byte[written * c.BlockAlign] : _e.ReadMemory(c.Scratch, written * c.BlockAlign);
                    if (silent && !c.IsFloat && c.Bits == 8)
                    {
                        Array.Fill(raw, (byte)0x80);
                    }

                    c.Voice?.Submit(PcmAudio.ToPcm16(raw, c.IsFloat, c.Bits, c.Channels, c.BlockAlign) ?? []);
                    c.Queued.Enqueue(written);
                    c.WrittenFrames += written;
                }

                Return(MediaCom.SOk);
            })),
        });

        // IAudioClock: GetFrequency 3, GetPosition 4, GetCharacteristics 5. In shared mode the
        // position is in bytes, so the frequency is the stream's byte rate.
        private MediaInterface ClockShape => _clockShape ??= new("IAudioClock", [IidClock], 6, () => new()
        {
            [3] = ("GetFrequency", () => With(c => { _com.W64(Arg(1), (ulong)((long)c.Rate * c.BlockAlign)); Return(Arg(1) == 0 ? MediaCom.EPointer : MediaCom.SOk); })),
            [4] = ("GetPosition", () => With(c =>
            {
                if (Arg(1) == 0)
                {
                    Return(MediaCom.EPointer);
                    return;
                }

                Retire(c);
                _com.W64(Arg(1), (ulong)(c.PlayedFrames * c.BlockAlign));
                if (Arg(2) != 0)
                {
                    _com.W64(Arg(2), (ulong)(System.Diagnostics.Stopwatch.GetTimestamp() * 10_000_000.0 / System.Diagnostics.Stopwatch.Frequency));
                }

                Return(MediaCom.SOk);
            })),
            [5] = ("GetCharacteristics", () => With(_ => { _com.W32(Arg(1), 1 /* AUDIOCLOCK_CHARACTERISTIC_FIXED_FREQ */); Return(MediaCom.SOk); })),
        });

        private void ApplyVolume(Client c)
        {
            float channels = c.ChannelVolumes.Length == 0 ? 1 : c.ChannelVolumes.Average();
            c.Voice?.SetVolume(c.Mute ? 0 : Math.Clamp(c.Volume * channels, 0, 1));
        }

        // ISimpleAudioVolume: SetMasterVolume 3, GetMasterVolume 4, SetMute 5, GetMute 6.
        private MediaInterface SimpleVolumeShape => _simpleVolumeShape ??= new("ISimpleAudioVolume", [IidSimpleVolume], 7, () => new()
        {
            // SetMasterVolume(float fLevel, LPCGUID): the float is s0, so the GUID is r1.
            [3] = ("SetMasterVolume", () => With(c =>
            {
                float level = _com.FloatArg(0);
                if (!(level >= 0 && level <= 1))
                {
                    Return(MediaCom.EInvalidArg);
                    return;
                }

                c.Volume = level;
                ApplyVolume(c);
                Return(MediaCom.SOk);
            })),
            [4] = ("GetMasterVolume", () => With(c => { _com.WFloat(Arg(1), c.Volume); Return(Arg(1) == 0 ? MediaCom.EPointer : MediaCom.SOk); })),
            [5] = ("SetMute", () => With(c => { c.Mute = Arg(1) != 0; ApplyVolume(c); Return(MediaCom.SOk); })),
            [6] = ("GetMute", () => With(c => { _com.W32(Arg(1), c.Mute ? 1 : 0); Return(Arg(1) == 0 ? MediaCom.EPointer : MediaCom.SOk); })),
        });

        // IAudioStreamVolume: GetChannelCount 3, SetChannelVolume 4, GetChannelVolume 5,
        // SetAllVolumes 6, GetAllVolumes 7.
        private MediaInterface StreamVolumeShape => _streamVolumeShape ??= new("IAudioStreamVolume", [IidStreamVolume], 8, () => new()
        {
            [3] = ("GetChannelCount", () => With(c => { _com.W32(Arg(1), c.Channels); Return(Arg(1) == 0 ? MediaCom.EPointer : MediaCom.SOk); })),

            // SetChannelVolume(UINT32 dwIndex, const float fLevel): the float is s0.
            [4] = ("SetChannelVolume", () => With(c =>
            {
                float level = _com.FloatArg(0);
                if (Arg(1) >= c.Channels || !(level >= 0 && level <= 1))
                {
                    Return(MediaCom.EInvalidArg);
                    return;
                }

                c.ChannelVolumes[(int)Arg(1)] = level;
                ApplyVolume(c);
                Return(MediaCom.SOk);
            })),
            [5] = ("GetChannelVolume", () => With(c =>
            {
                if (Arg(1) >= c.Channels)
                {
                    Return(MediaCom.EInvalidArg);
                    return;
                }

                _com.WFloat(Arg(2), c.ChannelVolumes[(int)Arg(1)]);
                Return(Arg(2) == 0 ? MediaCom.EPointer : MediaCom.SOk);
            })),
            [6] = ("SetAllVolumes", () => With(c =>
            {
                if (Arg(1) != c.Channels || Arg(2) == 0)
                {
                    Return(Arg(2) == 0 ? MediaCom.EPointer : MediaCom.EInvalidArg);
                    return;
                }

                float[] levels = Enumerable.Range(0, c.Channels).Select(i => _com.RFloat(Arg(2) + (i * 4))).ToArray();
                if (levels.Any(l => !(l >= 0 && l <= 1)))
                {
                    Return(MediaCom.EInvalidArg);
                    return;
                }

                c.ChannelVolumes = levels;
                ApplyVolume(c);
                Return(MediaCom.SOk);
            })),
            [7] = ("GetAllVolumes", () => With(c =>
            {
                if (Arg(1) != c.Channels || Arg(2) == 0)
                {
                    Return(Arg(2) == 0 ? MediaCom.EPointer : MediaCom.EInvalidArg);
                    return;
                }

                for (int i = 0; i < c.Channels; i++)
                {
                    _com.WFloat(Arg(2) + (i * 4), c.ChannelVolumes[i]);
                }

                Return(MediaCom.SOk);
            })),
        });
    }
}
