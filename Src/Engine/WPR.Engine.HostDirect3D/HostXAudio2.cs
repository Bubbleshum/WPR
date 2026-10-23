using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WPR.Wp8Native;

namespace WPR.HostDirect3D
{
    /// <summary>
    /// XAudio2 2.8 for a managed title's SharpDX (<c>XAudio2Create</c> through SharpDX.WP8's
    /// <c>Interop</c>): the IXAudio2 engine and its voices as host-native objects. Source voices
    /// play through the WP8 native runtime's audio output (<see cref="IHostAudio"/>, one
    /// <c>DynamicSoundEffectInstance</c> per voice) and queue their buffers in its
    /// <see cref="XAudio2BufferQueue"/>, so formats, loops and callbacks behave as they do for a
    /// native title: PCM, MS ADPCM and float, mono or stereo.
    /// </summary>
    /// <remarks>
    /// <para>Voice callbacks (OnBufferStart/End, OnStreamEnd) and the engine's processing-pass
    /// callbacks are made from <see cref="PumpAll"/>, once per frame on the XNA thread, never from
    /// inside the game's own call into XAudio2: real XAudio2 makes them on its audio thread, and a
    /// game re-entered from its own SubmitSourceBuffer would see a state it never could.</para>
    /// <para>Not provided: the output matrix (pan) and filters, which are accepted and ignored;
    /// effect chains; xWMA (SubmitSourceBuffer's WMA packet table) - the buffer plays as its raw
    /// bytes would, i.e. not at all, and is logged.</para>
    /// </remarks>
    internal sealed unsafe class HostXAudio2 : HostObject
    {
        private static readonly Guid IidIXAudio2 = new("60d8dac8-5aa1-4e8e-b597-2f5e2883d484");
        private static readonly List<HostXAudio2> Live = [];

        private static readonly VTable Table = new("IXAudio2", 13, new Slots
        {
            [3] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&RegisterForCallbacks,
            [4] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)&UnregisterForCallbacks,
            [5] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, byte*, uint, float, IntPtr, byte*, IntPtr, int>)&CreateSourceVoice,
            [6] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, uint, uint, uint, uint, byte*, IntPtr, int>)&CreateSubmixVoice,
            [7] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, uint, uint, uint, IntPtr, IntPtr, int, int>)&CreateMasteringVoice,
            [8] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int>)&StartEngine,
            [9] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, void>)&StopEngine,
            [10] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, int>)&CommitChanges,
            [11] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, void>)&GetPerformanceData,
            [12] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)&SetDebugConfiguration,
        }, ["", "", "", "RegisterForCallbacks", "UnregisterForCallbacks", "CreateSourceVoice", "CreateSubmixVoice",
            "CreateMasteringVoice", "StartEngine", "StopEngine", "CommitChanges", "GetPerformanceData", "SetDebugConfiguration"]);

        private readonly IHostAudio _output;
        private readonly List<IntPtr> _engineCallbacks = [];
        private readonly List<HostXAudio2Voice> _voices = [];
        private readonly List<(IntPtr Function, IntPtr Self, IntPtr Argument, int Kind)> _pending = [];

        private HostXAudio2() : base(Table, IidIXAudio2)
        {
            _output = IHostAudio.Create(line => System.Diagnostics.Trace.WriteLine(line));
            lock (Live)
            {
                Live.Add(this);
            }
        }

        internal HostXAudio2Voice? Mastering { get; private set; }

        internal bool Running { get; private set; } = true;

        /// <summary>XAudio2Create: a new engine, running, as XAudio2 2.8 starts one.</summary>
        public static int Create(IntPtr* result)
        {
            if (result == null)
            {
                return unchecked((int)0x80004003); // E_POINTER
            }

            *result = new HostXAudio2().Pointer;
            HostLog.WriteOnce("xaudio2", "XAudio2Create: host XAudio2 2.8 created");
            return 0;
        }

        /// <summary>Once per frame, XNA thread: feeds and retires every voice, then makes the callbacks due.</summary>
        public static void PumpAll()
        {
            HostXAudio2[] engines;
            lock (Live)
            {
                engines = Live.ToArray();
            }

            foreach (HostXAudio2 engine in engines)
            {
                engine.Pump();
            }
        }

        /// <summary>At the end of a game run: every engine still alive stops making sound.</summary>
        public static void DisposeAll()
        {
            HostXAudio2[] engines;
            lock (Live)
            {
                engines = Live.ToArray();
                Live.Clear();
            }

            foreach (HostXAudio2 engine in engines)
            {
                engine.Shutdown();
            }
        }

        internal void Forget(HostXAudio2Voice voice)
        {
            _voices.Remove(voice);
            if (ReferenceEquals(Mastering, voice))
            {
                Mastering = null;
            }

            _pending.RemoveAll(p => p.Self == voice.Callback && !_voices.Any(v => v.Callback == voice.Callback));
        }

        private void Pump()
        {
            _output.Pump();
            foreach (IntPtr callback in _engineCallbacks)
            {
                QueueCall(callback, 0, IntPtr.Zero, 0);   // OnProcessingPassStart
            }

            foreach (HostXAudio2Voice voice in _voices.ToArray())
            {
                voice.Pump(Running);
            }

            foreach (IntPtr callback in _engineCallbacks)
            {
                QueueCall(callback, 1, IntPtr.Zero, 0);   // OnProcessingPassEnd
            }

            if (_pending.Count == 0)
            {
                return;
            }

            var calls = _pending.ToArray();
            _pending.Clear();
            foreach ((IntPtr function, IntPtr self, IntPtr argument, int kind) in calls)
            {
                try
                {
                    if (kind == 0)
                    {
                        ((delegate* unmanaged[Stdcall]<IntPtr, void>)function)(self);
                    }
                    else
                    {
                        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void>)function)(self, argument);
                    }
                }
                catch (Exception ex)
                {
                    HostLog.WriteOnce("xaudio2 callback", $"XAudio2 callback threw {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Queues a call to slot <paramref name="slot"/> of a callback object (no IUnknown).
        /// <paramref name="kind"/> 0 calls it with no argument, 1 with <paramref name="argument"/>.
        /// </summary>
        internal void QueueCall(IntPtr callbackObject, int slot, IntPtr argument, int kind)
        {
            if (callbackObject == IntPtr.Zero)
            {
                return;
            }

            IntPtr function = (*(IntPtr**)callbackObject)[slot];
            if (function != IntPtr.Zero)
            {
                _pending.Add((function, callbackObject, argument, kind));
            }
        }

        private void Shutdown()
        {
            foreach (HostXAudio2Voice voice in _voices.ToArray())
            {
                voice.Close();
            }

            _voices.Clear();
            _pending.Clear();
            _output.Dispose();
        }

        protected override void Destroyed()
        {
            lock (Live)
            {
                Live.Remove(this);
            }

            Shutdown();
        }

        // ------------------------------------------------------------------------------
        // IXAudio2
        // ------------------------------------------------------------------------------

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int RegisterForCallbacks(IntPtr self, IntPtr callback)
        {
            HostXAudio2 engine = From<HostXAudio2>(self);
            if (callback != IntPtr.Zero && !engine._engineCallbacks.Contains(callback))
            {
                engine._engineCallbacks.Add(callback);
            }

            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void UnregisterForCallbacks(IntPtr self, IntPtr callback)
        {
            HostXAudio2 engine = From<HostXAudio2>(self);
            engine._engineCallbacks.Remove(callback);
            engine._pending.RemoveAll(p => p.Self == callback);
        }

        // CreateSourceVoice(ppSourceVoice, pSourceFormat, Flags, MaxFrequencyRatio, pCallback, pSendList, pEffectChain)
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int CreateSourceVoice(IntPtr self, IntPtr* voice, byte* format, uint flags, float maxRatio, IntPtr callback, byte* sends, IntPtr effects)
        {
            HostXAudio2 engine = From<HostXAudio2>(self);
            if (voice == null || format == null)
            {
                return unchecked((int)0x80070057); // E_INVALIDARG
            }

            XAudio2Format f = XAudio2Format.Read((offset, count) => new ReadOnlySpan<byte>(format + offset, count).ToArray());
            IAudioVoiceOutput? output = null;
            if (f.IsPlayable)
            {
                output = engine._output.CreateVoice(f.SampleRate, f.OutChannels);
            }
            else
            {
                HostLog.WriteOnce("xaudio2 format " + f, $"XAudio2 source voice: {f} cannot be played; the voice is silent");
            }

            var created = new HostXAudio2Voice(engine, isSource: true, f, output, callback, FirstSend(sends));
            engine._voices.Add(created);
            *voice = created.Pointer;
            return 0;
        }

        // CreateSubmixVoice(ppSubmixVoice, InputChannels, InputSampleRate, Flags, ProcessingStage, pSendList, pEffectChain)
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int CreateSubmixVoice(IntPtr self, IntPtr* voice, uint channels, uint rate, uint flags, uint stage, byte* sends, IntPtr effects)
        {
            HostXAudio2 engine = From<HostXAudio2>(self);
            if (voice == null)
            {
                return unchecked((int)0x80004003);
            }

            var created = new HostXAudio2Voice(engine, isSource: false, new XAudio2Format(1, (int)channels, (int)rate, 32, 0, 0, []), null, IntPtr.Zero, FirstSend(sends));
            engine._voices.Add(created);
            *voice = created.Pointer;
            return 0;
        }

        // CreateMasteringVoice(ppMasteringVoice, InputChannels, InputSampleRate, Flags, szDeviceId, pEffectChain, StreamCategory)
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int CreateMasteringVoice(IntPtr self, IntPtr* voice, uint channels, uint rate, uint flags, IntPtr deviceId, IntPtr effects, int category)
        {
            HostXAudio2 engine = From<HostXAudio2>(self);
            if (voice == null)
            {
                return unchecked((int)0x80004003);
            }

            // 0 means "the device's": a stereo device at 48 kHz.
            var details = new XAudio2Format(1, channels == 0 ? 2 : (int)channels, rate == 0 ? 48000 : (int)rate, 32, 0, 0, []);
            var created = new HostXAudio2Voice(engine, isSource: false, details, null, IntPtr.Zero, null) { IsMastering = true };
            engine._voices.Add(created);
            engine.Mastering = created;
            *voice = created.Pointer;
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int StartEngine(IntPtr self)
        {
            From<HostXAudio2>(self).SetRunning(true);
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void StopEngine(IntPtr self) => From<HostXAudio2>(self).SetRunning(false);

        private void SetRunning(bool running)
        {
            if (Running == running)
            {
                return;
            }

            Running = running;
            foreach (HostXAudio2Voice voice in _voices)
            {
                voice.EngineRunning(running);
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int CommitChanges(IntPtr self, uint operationSet) => 0;

        // XAUDIO2_PERFORMANCE_DATA (2.8): two UINT64 then twelve UINT32, 64 bytes. All zero: no glitches.
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void GetPerformanceData(IntPtr self, byte* data)
        {
            if (data != null)
            {
                new Span<byte>(data, 64).Clear();
                HostXAudio2 engine = From<HostXAudio2>(self);
                int sources = engine._voices.Count(v => v.IsSource);
                *(uint*)(data + 16 + (7 * 4)) = (uint)sources;   // ActiveSourceVoiceCount
                *(uint*)(data + 16 + (8 * 4)) = (uint)sources;   // TotalSourceVoiceCount
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void SetDebugConfiguration(IntPtr self, IntPtr configuration, IntPtr reserved)
        {
        }

        /// <summary>XAUDIO2_VOICE_SENDS { UINT32 SendCount; XAUDIO2_SEND_DESCRIPTOR* { UINT32 Flags; IXAudio2Voice* } }: the first destination.</summary>
        private static HostXAudio2Voice? FirstSend(byte* sends)
        {
            if (sends == null || *(uint*)sends == 0)
            {
                return null;
            }

            byte* descriptor = *(byte**)(sends + IntPtr.Size);
            IntPtr output = descriptor == null ? IntPtr.Zero : *(IntPtr*)(descriptor + IntPtr.Size);
            return FromOrNull<HostXAudio2Voice>(output);
        }
    }

    /// <summary>
    /// An XAudio2 voice: IXAudio2Voice (slots 0-18, no IUnknown), extended by IXAudio2SourceVoice
    /// (19-28) or IXAudio2MasteringVoice (19).
    /// </summary>
    internal sealed unsafe class HostXAudio2Voice : HostObject
    {
        private static readonly string[] Names =
        [
            "GetVoiceDetails", "SetOutputVoices", "SetEffectChain", "EnableEffect", "DisableEffect", "GetEffectState",
            "SetEffectParameters", "GetEffectParameters", "SetFilterParameters", "GetFilterParameters",
            "SetOutputFilterParameters", "GetOutputFilterParameters", "SetVolume", "GetVolume", "SetChannelVolumes",
            "GetChannelVolumes", "SetOutputMatrix", "GetOutputMatrix", "DestroyVoice",
            "Start", "Stop", "SubmitSourceBuffer", "FlushSourceBuffers", "Discontinuity", "ExitLoop", "GetState",
            "SetFrequencyRatio", "GetFrequencyRatio", "SetSourceSampleRate",
        ];

        private static Slots VoiceSlots() => new()
        {
            [0] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint*, void>)&GetVoiceDetails,
            [1] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)&SetOutputVoices,
            [2] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&Accept1,
            [3] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int>)&Accept2,
            [4] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int>)&Accept2,
            [8] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int>)&Accept2P,
            [10] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, uint, int>)&Accept3P,
            [12] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, float, uint, int>)&SetVolume,
            [13] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, float*, void>)&GetVolume,
            [14] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)&Accept3,
            [16] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, IntPtr, uint, int>)&SetOutputMatrix,
            [18] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, void>)&DestroyVoice,
        };

        private static readonly VTable SourceTable = BuildSourceTable();
        private static readonly VTable MixTable = BuildMixTable();

        private static VTable BuildSourceTable()
        {
            Slots slots = VoiceSlots();
            slots[19] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int>)&Start;
            slots[20] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int>)&Stop;
            slots[21] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, IntPtr, int>)&SubmitSourceBuffer;
            slots[22] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int>)&FlushSourceBuffers;
            slots[23] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int>)&Discontinuity;
            slots[24] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, int>)&ExitLoop;
            slots[25] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, uint, void>)&GetState;
            slots[26] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, float, uint, int>)&SetFrequencyRatio;
            slots[27] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, float*, void>)&GetFrequencyRatio;
            slots[28] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, int>)&SetSourceSampleRate;
            return new VTable("IXAudio2SourceVoice", 29, slots, Names, unknown: false);
        }

        private static VTable BuildMixTable()
        {
            Slots slots = VoiceSlots();
            slots[19] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)&GetChannelMask;
            return new VTable("IXAudio2MasteringVoice", 20, slots, [.. Names[..19], "GetChannelMask"], unknown: false);
        }

        private readonly HostXAudio2 _engine;
        private readonly XAudio2BufferQueue _buffers;
        private readonly IAudioVoiceOutput? _out;
        private HostXAudio2Voice? _destination;
        private bool _started;
        private bool _playing;
        private float _volume = 1f;
        private float _ratio = 1f;
        private float _appliedVolume = -1f;
        private float _appliedPitch = float.NaN;
        private bool _closed;

        public HostXAudio2Voice(HostXAudio2 engine, bool isSource, XAudio2Format format, IAudioVoiceOutput? output, IntPtr callback, HostXAudio2Voice? destination)
            : base(isSource ? SourceTable : MixTable)
        {
            _engine = engine;
            IsSource = isSource;
            Format = format;
            _out = output;
            Callback = callback;
            _destination = destination;
            _buffers = new XAudio2BufferQueue(format, output);
        }

        public bool IsSource { get; }

        public bool IsMastering { get; init; }

        public XAudio2Format Format { get; }

        /// <summary>The game's IXAudio2VoiceCallback, or zero.</summary>
        public IntPtr Callback { get; }

        /// <summary>This voice's volume times every voice it is routed through, the mastering voice last.</summary>
        private float EffectiveVolume()
        {
            float volume = _volume;
            HostXAudio2Voice? next = _destination ?? (IsMastering ? null : _engine.Mastering);
            for (int depth = 0; depth < 8 && next != null; depth++)
            {
                volume *= next._volume;
                next = next.IsMastering ? null : next._destination ?? _engine.Mastering;
            }

            return volume;
        }

        public void Pump(bool engineRunning)
        {
            if (!IsSource || _closed)
            {
                return;
            }

            _buffers.Retire(Callback_);
            _buffers.Feed(Callback_);
            if (_out is null)
            {
                return;
            }

            float volume = Math.Clamp(EffectiveVolume(), 0f, 1f);
            if (Math.Abs(volume - _appliedVolume) > 0.001f)
            {
                _appliedVolume = volume;
                _out.SetVolume(volume);
            }

            float pitch = Math.Clamp(MathF.Log2(Math.Max(_ratio, 0.0005f)), -1f, 1f);
            if (!(Math.Abs(pitch - _appliedPitch) < 0.001f))
            {
                _appliedPitch = pitch;
                _out.SetPitch(pitch);
            }

            UpdatePlaying(engineRunning);
        }

        public void EngineRunning(bool running) => UpdatePlaying(running);

        private void UpdatePlaying(bool engineRunning)
        {
            bool play = _started && engineRunning;
            if (play == _playing || _out is null)
            {
                return;
            }

            _playing = play;
            if (play)
            {
                _out.Play();
            }
            else
            {
                _out.Pause();
            }
        }

        // OnStreamEnd 2 takes no argument; OnBufferStart 3 and OnBufferEnd 4 take the buffer context.
        private void Callback_(int slot, long context) =>
            _engine.QueueCall(Callback, slot, (IntPtr)context, slot == XAudio2BufferQueue.OnStreamEnd ? 0 : 1);

        public void Close()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            _out?.Dispose();
        }

        // ---- IXAudio2Voice ----

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void GetVoiceDetails(IntPtr self, uint* details)
        {
            if (details != null)
            {
                XAudio2Format f = From<HostXAudio2Voice>(self).Format;
                details[0] = 0;
                details[1] = 0;
                details[2] = (uint)f.Channels;
                details[3] = (uint)f.SampleRate;
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int SetOutputVoices(IntPtr self, byte* sends)
        {
            HostXAudio2Voice voice = From<HostXAudio2Voice>(self);
            if (sends == null || *(uint*)sends == 0)
            {
                voice._destination = null;
                return 0;
            }

            byte* descriptor = *(byte**)(sends + IntPtr.Size);
            voice._destination = descriptor == null ? null : FromOrNull<HostXAudio2Voice>(*(IntPtr*)(descriptor + IntPtr.Size));
            return 0;
        }

        // Effects and filters: accepted, not applied.
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Accept1(IntPtr self, IntPtr a) => 0;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Accept2(IntPtr self, uint a, uint b) => 0;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Accept2P(IntPtr self, IntPtr a, uint b) => 0;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Accept3(IntPtr self, uint a, IntPtr b, uint c) => 0;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Accept3P(IntPtr self, IntPtr a, IntPtr b, uint c) => 0;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int SetVolume(IntPtr self, float volume, uint operationSet)
        {
            From<HostXAudio2Voice>(self)._volume = volume;
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void GetVolume(IntPtr self, float* volume)
        {
            if (volume != null)
            {
                *volume = From<HostXAudio2Voice>(self)._volume;
            }
        }

        // SetOutputMatrix(pDestinationVoice, SourceChannels, DestinationChannels, pLevelMatrix, OperationSet): pan, not applied.
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int SetOutputMatrix(IntPtr self, IntPtr destination, uint sourceChannels, uint destinationChannels, IntPtr matrix, uint operationSet)
        {
            HostLog.WriteOnce("xaudio2 matrix", "XAudio2 SetOutputMatrix: accepted, not applied (no pan)");
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void DestroyVoice(IntPtr self)
        {
            HostXAudio2Voice voice = From<HostXAudio2Voice>(self);
            voice.Close();
            voice._engine.Forget(voice);
            voice.ReleaseReference();
        }

        // ---- IXAudio2SourceVoice ----

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Start(IntPtr self, uint flags, uint operationSet)
        {
            HostXAudio2Voice voice = From<HostXAudio2Voice>(self);
            voice._started = true;
            voice._buffers.Feed(voice.Callback_);
            voice.UpdatePlaying(voice._engine.Running);
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Stop(IntPtr self, uint flags, uint operationSet)
        {
            HostXAudio2Voice voice = From<HostXAudio2Voice>(self);
            voice._started = false;
            voice.UpdatePlaying(voice._engine.Running);
            return 0;
        }

        // XAUDIO2_BUFFER { Flags, AudioBytes, pAudioData, PlayBegin, PlayLength, LoopBegin, LoopLength, LoopCount, pContext },
        // natural alignment: the two pointers at 8 and 40 on a 64-bit host, 8 and 32 on a 32-bit one.
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int SubmitSourceBuffer(IntPtr self, byte* buffer, IntPtr wma)
        {
            HostXAudio2Voice voice = From<HostXAudio2Voice>(self);
            if (buffer == null)
            {
                return unchecked((int)0x80070057);
            }

            if (wma != IntPtr.Zero)
            {
                HostLog.WriteOnce("xaudio2 wma", "XAudio2 SubmitSourceBuffer: xWMA buffers are not decoded; that sound is silent");
            }

            int p = IntPtr.Size;
            uint flags = *(uint*)buffer;
            int bytes = (int)*(uint*)(buffer + 4);
            byte* data = *(byte**)(buffer + 8);
            byte* rest = buffer + 8 + p;
            uint playBegin = *(uint*)rest, playLength = *(uint*)(rest + 4);
            uint loopBegin = *(uint*)(rest + 8), loopLength = *(uint*)(rest + 12), loopCount = *(uint*)(rest + 16);
            IntPtr context = *(IntPtr*)(buffer + (p == 8 ? 40 : 32));

            // Copied now: the game may reuse the memory once the buffer ends.
            byte[]? audio = data != null && bytes > 0 && wma == IntPtr.Zero ? new ReadOnlySpan<byte>(data, bytes).ToArray() : null;
            voice._buffers.Enqueue(flags, audio, playBegin, playLength, loopBegin, loopLength, (int)loopCount, (long)context);
            if (voice._started)
            {
                voice._buffers.Feed(voice.Callback_);
            }

            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int FlushSourceBuffers(IntPtr self)
        {
            From<HostXAudio2Voice>(self)._buffers.Flush();
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Discontinuity(IntPtr self) => 0;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int ExitLoop(IntPtr self, uint operationSet)
        {
            From<HostXAudio2Voice>(self)._buffers.ExitLoop();
            return 0;
        }

        // XAUDIO2_VOICE_STATE { void* pCurrentBufferContext; UINT32 BuffersQueued; UINT64 SamplesPlayed }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void GetState(IntPtr self, byte* state, uint flags)
        {
            if (state == null)
            {
                return;
            }

            HostXAudio2Voice voice = From<HostXAudio2Voice>(self);
            int p = IntPtr.Size;
            *(IntPtr*)state = (IntPtr)voice._buffers.CurrentContext;
            *(uint*)(state + p) = (uint)voice._buffers.Count;
            *(ulong*)(state + (p == 8 ? 16 : 8)) = (ulong)voice._buffers.SamplesPlayed;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int SetFrequencyRatio(IntPtr self, float ratio, uint operationSet)
        {
            From<HostXAudio2Voice>(self)._ratio = ratio;
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static void GetFrequencyRatio(IntPtr self, float* ratio)
        {
            if (ratio != null)
            {
                *ratio = From<HostXAudio2Voice>(self)._ratio;
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int SetSourceSampleRate(IntPtr self, uint rate) => 0;

        // ---- IXAudio2MasteringVoice ----

        // SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT, or SPEAKER_FRONT_CENTER for one channel.
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetChannelMask(IntPtr self, uint* mask)
        {
            if (mask != null)
            {
                *mask = From<HostXAudio2Voice>(self).Format.Channels == 1 ? 4u : 3u;
            }

            return 0;
        }
    }
}
