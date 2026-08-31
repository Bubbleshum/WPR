namespace WPR.Wp8Native
{
    /// <summary>
    /// XAudio2, far enough for the image to build its mixer graph and play into silence.
    /// </summary>
    /// <remarks>
    /// Structurally unlike the Direct3D layer despite both being COM-shaped, and the
    /// difference is the reason this is not built on the same object machinery.
    /// <c>IXAudio2</c> derives from <c>IUnknown</c> as expected, but <c>IXAudio2Voice</c>
    /// does <b>not</b> - it has no QueryInterface, no AddRef and no Release at all, and its
    /// vtable starts directly at <c>GetVoiceDetails</c>. A builder that reserves the first
    /// three slots would put every voice method three places out.
    ///
    /// Nothing produces sound. Almost every voice method returns void, so doing nothing is
    /// indistinguishable from doing it - the exceptions are the two that hand data back,
    /// <c>GetVoiceDetails</c> and <c>GetState</c>, and both are answered honestly: a voice
    /// with nothing queued and nothing played.
    /// </remarks>
    public sealed class XAudio2Runtime
    {
        private const long HResultOk = 0;

        private readonly ArmEmulator _emulator;
        private readonly CallFrame _frame;

        private long Arg(int index) => _frame.Arg(index);

        private void Return(long value) => _frame.Return(value);

        public XAudio2Runtime(ArmEmulator emulator, CallFrame frame)
        {
            _emulator = emulator;
            _frame = frame;
        }

        private readonly List<string> _log = new();
        private readonly List<string> _unimplemented = new();

        /// <summary>What the image asked the audio engine to do.</summary>
        public IReadOnlyList<string> Log => _log;

        /// <summary>Voice methods reached with nothing behind them.</summary>
        public IReadOnlyList<string> Unimplemented => _unimplemented;

        /// <summary>True once the engine exists.</summary>
        public bool EngineCreated { get; private set; }

        /// <summary>Voices created, by kind.</summary>
        public Dictionary<string, int> VoicesCreated { get; } = new(StringComparer.Ordinal);

        /// <summary>Buffers the image submitted for playback.</summary>
        public int BuffersSubmitted { get; private set; }

        private long _engine;
        private long _masteringVoice;

        private readonly Dictionary<string, long> _vtables = new(StringComparer.Ordinal);

        /// <summary>
        /// Builds a vtable of traps, shared by every object of that interface.
        /// </summary>
        /// <param name="hasUnknown">
        /// False for the voice interfaces, which are not COM objects at all - they are
        /// plain virtual classes with a lifetime the engine owns and DestroyVoice ends.
        /// </param>
        private long VtableFor(
            string interfaceName,
            int slotCount,
            bool hasUnknown,
            IReadOnlyDictionary<int, (string Name, Action Handler)> known)
        {
            if (_vtables.TryGetValue(interfaceName, out long existing))
            {
                return existing;
            }

            // The same eight slots of headroom the Direct3D side carries, for the same
            // reason: a vtable one short does not report a missing method, it jumps into
            // the next object on the heap.
            const int padding = 8;
            long vtable = _emulator.AllocateHeap((slotCount + padding) * 4);

            for (int slot = 0; slot < slotCount + padding; slot++)
            {
                int captured = slot;
                (string name, Action handler) =
                    hasUnknown && slot < 3
                        ? (slot switch { 0 => "QueryInterface", 1 => "AddRef", _ => "Release" },
                           () => Return(slot == 0 ? HResultOk : 1))
                        : known.TryGetValue(slot, out (string Name, Action Handler) hit)
                            ? (hit.Name, hit.Handler)
                            : ($"slot{captured}", () => Unhandled(interfaceName, captured));

                long trap = _emulator.RegisterVtableMethod($"{interfaceName}::{name}", handler);
                _emulator.WriteUInt32(vtable + (slot * 4), (uint)ArmEmulator.ThumbEntry(trap));
            }

            _vtables[interfaceName] = vtable;
            return vtable;
        }

        private long CreateObject(
            string interfaceName,
            int slotCount,
            bool hasUnknown,
            IReadOnlyDictionary<int, (string Name, Action Handler)> known)
        {
            long instance = _emulator.AllocateHeap(8);
            _emulator.WriteUInt32(instance, (uint)VtableFor(interfaceName, slotCount, hasUnknown, known));
            _emulator.WriteUInt32(instance + 4, 1);
            return instance;
        }

        private void Unhandled(string interfaceName, int slot)
        {
            string entry = $"{interfaceName}::slot{slot}  r1=0x{Arg(1):X8} r2=0x{Arg(2):X8}";
            if (!_unimplemented.Contains(entry, StringComparer.Ordinal))
            {
                _unimplemented.Add(entry);
            }

            // Void for most of this surface, S_OK for the rest.
            Return(HResultOk);
        }

        private void Write(long address, uint value)
        {
            if (address != 0)
            {
                _emulator.WriteUInt32(address, value);
            }
        }

        /// <summary>
        /// HRESULT XAudio2Create(IXAudio2 **ppXAudio2, UINT32 Flags, XAUDIO2_PROCESSOR).
        /// </summary>
        /// <remarks>
        /// Exported from XAudio2_8.dll by ordinal 1 and by no name at all, which is why it
        /// shows up in the trace as <c>XAudio2_8.dll!#1</c>.
        /// </remarks>
        public void CreateEngine()
        {
            _engine = _engine != 0 ? _engine : BuildEngine();
            Write(Arg(0), (uint)_engine);
            EngineCreated = true;
            _log.Add($"XAudio2Create(flags=0x{Arg(1):X}) -> engine 0x{_engine:X8}");
            Return(HResultOk);
        }

        // IXAudio2: IUnknown 0-2, then RegisterForCallbacks 3, UnregisterForCallbacks 4,
        // CreateSourceVoice 5, CreateSubmixVoice 6, CreateMasteringVoice 7, StartEngine 8,
        // StopEngine 9, CommitChanges 10, GetPerformanceData 11, SetDebugConfiguration 12.
        private const int EngineSlots = 13;

        private long BuildEngine() => CreateObject(
            "IXAudio2",
            EngineSlots,
            hasUnknown: true,
            new Dictionary<int, (string, Action)>
            {
                [3] = ("RegisterForCallbacks", () => Return(HResultOk)),
                [4] = ("UnregisterForCallbacks", () => Return(HResultOk)),
                [5] = ("CreateSourceVoice", () =>
                {
                    Count("SourceVoice");
                    Write(Arg(1), (uint)BuildVoice(source: true));
                    Return(HResultOk);
                }),
                [6] = ("CreateSubmixVoice", () =>
                {
                    Count("SubmixVoice");
                    Write(Arg(1), (uint)BuildVoice(source: false));
                    Return(HResultOk);
                }),
                [7] = ("CreateMasteringVoice", () =>
                {
                    Count("MasteringVoice");
                    _masteringVoice = _masteringVoice != 0 ? _masteringVoice : BuildVoice(source: false);
                    Write(Arg(1), (uint)_masteringVoice);
                    Return(HResultOk);
                }),
                [8] = ("StartEngine", () => Return(HResultOk)),
                [9] = ("StopEngine", () => Return(HResultOk)),
                [10] = ("CommitChanges", () => Return(HResultOk)),
                [11] = ("GetPerformanceData", () =>
                {
                    // XAUDIO2_PERFORMANCE_DATA is all counters, and zero is truthful for an
                    // engine that has never mixed a sample.
                    if (Arg(1) != 0)
                    {
                        _emulator.WriteMemory(Arg(1), new byte[64]);
                    }

                    Return(HResultOk);
                }),
                [12] = ("SetDebugConfiguration", () => Return(HResultOk)),
            });

        private void Count(string kind)
            => VoicesCreated[kind] = VoicesCreated.GetValueOrDefault(kind) + 1;

        // IXAudio2Voice, with no IUnknown in front of it: GetVoiceDetails 0 through
        // DestroyVoice 18. IXAudio2SourceVoice continues at Start 19 and ends at
        // SetSourceSampleRate 28.
        private const int VoiceSlots = 19;

        private const int SourceVoiceSlots = 29;

        private long BuildVoice(bool source)
        {
            Dictionary<int, (string, Action)> methods = new()
            {
                [0] = ("GetVoiceDetails", () =>
                {
                    // XAUDIO2_VOICE_DETAILS: CreationFlags, ActiveFlags, InputChannels,
                    // InputSampleRate.
                    long details = Arg(1);
                    if (details == 0)
                    {
                        return;
                    }

                    _emulator.WriteUInt32(details + 0, 0);
                    _emulator.WriteUInt32(details + 4, 0);
                    _emulator.WriteUInt32(details + 8, 2);
                    _emulator.WriteUInt32(details + 12, 44100);
                }),
                [18] = ("DestroyVoice", () => Return(HResultOk)),
            };

            if (source)
            {
                methods[19] = ("Start", () => Return(HResultOk));
                methods[20] = ("Stop", () => Return(HResultOk));
                methods[21] = ("SubmitSourceBuffer", () =>
                {
                    BuffersSubmitted++;
                    Return(HResultOk);
                });
                methods[22] = ("FlushSourceBuffers", () => Return(HResultOk));
                methods[25] = ("GetState", () =>
                {
                    // XAUDIO2_VOICE_STATE: pCurrentBufferContext, BuffersQueued,
                    // SamplesPlayed (64-bit). A voice that reports buffers still queued
                    // will never be refilled; one that reports none is asked for more,
                    // which is the behaviour a streaming game is written around.
                    long state = Arg(1);
                    if (state == 0)
                    {
                        return;
                    }

                    _emulator.WriteMemory(state, new byte[16]);
                });
            }

            return CreateObject(
                source ? "IXAudio2SourceVoice" : "IXAudio2Voice",
                source ? SourceVoiceSlots : VoiceSlots,
                hasUnknown: false,
                methods);
        }

        /// <summary>A one-line summary of how far audio got.</summary>
        public string Summary()
        {
            string voices = VoicesCreated.Count == 0
                ? "no voices"
                : string.Join(", ", VoicesCreated.Select(v => $"{v.Value} {v.Key}"));

            return $"engine={(EngineCreated ? "yes" : "no")} {voices} buffers={BuffersSubmitted}";
        }
    }
}
