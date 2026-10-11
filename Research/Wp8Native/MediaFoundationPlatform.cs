namespace WPR.Wp8Native
{
    /// <summary>
    /// The rest of MFPlat: async results and work queues, the DXGI device manager, attribute
    /// blobs and streams, wave-format conversion, and the functions answered with a documented
    /// failure.
    /// </summary>
    internal sealed partial class MediaFoundation
    {
        // ------------------------------------------------------------------------------
        // Async results, callbacks and work queues
        // ------------------------------------------------------------------------------

        /// <summary>
        /// An IMFAsyncResult laid out as MFASYNCRESULT: vtable, OVERLAPPED (20 bytes), then
        /// pCallback at +24 and hEvent at +28 - code built against the SDK reads pCallback straight
        /// out of the structure.
        /// </summary>
        internal sealed class MfAsyncResult : MediaObject
        {
            public override string Kind => "IMFAsyncResult";

            public long Object, Callback, State;
            public long Status;

            public override IEnumerable<long> Destroy() => new[] { Object, Callback, State }.Where(p => p != 0).ToList();
        }

        private MediaInterface? _asyncResultShape;

        // IMFAsyncResult: GetState 3, GetStatus 4, SetStatus 5, GetObject 6, GetStateNoAddRef 7.
        private MediaInterface AsyncResultShape => _asyncResultShape ??= new("IMFAsyncResult", [IidAsyncResult], 8, () => new()
        {
            [3] = ("GetState", () => HandOut(r => r.State)),
            [4] = ("GetStatus", () => Return(_com.Self<MfAsyncResult>()?.Status ?? MediaCom.EPointer)),
            [5] = ("SetStatus", () =>
            {
                if (_com.Self<MfAsyncResult>() is { } r)
                {
                    r.Status = (int)(uint)Arg(1);
                }

                Return(MediaCom.SOk);
            }),
            [6] = ("GetObject", () => HandOut(r => r.Object)),
            [7] = ("GetStateNoAddRef", () => Return(_com.Self<MfAsyncResult>()?.State ?? 0)),
        }, BlockSize: 32);

        /// <summary>Writes an AddRef'd held pointer to Arg(1); E_POINTER when there is none.</summary>
        private void HandOut(Func<MfAsyncResult, long> which)
        {
            MfAsyncResult? r = _com.Self<MfAsyncResult>();
            long held = r is null ? 0 : which(r);
            _com.W32(Arg(1), held);
            if (held == 0 || Arg(1) == 0)
            {
                Return(MediaCom.EPointer);
                return;
            }

            List<GuestCall> calls = new();
            _com.AddRefAny(held, calls);
            _com.Then(calls, _ => Return(MediaCom.SOk));
        }

        public long CreateAsyncResult(long obj, long callback, long state, List<GuestCall> calls)
        {
            MfAsyncResult r = new() { Object = obj, Callback = callback, State = state };
            long pointer = _com.Expose(r, AsyncResultShape);
            _com.W32(pointer + 24, callback);
            _com.AddRefAny(obj, calls);
            _com.AddRefAny(callback, calls);
            _com.AddRefAny(state, calls);
            return pointer;
        }

        private long _invokeTrampoline;

        /// <summary>
        /// Queues IMFAsyncCallback::Invoke(result) for later, as a work queue thread would run it:
        /// never on the caller's stack. The result is kept alive until the callback returns.
        /// </summary>
        public long InvokeLater(long result)
        {
            if (result == 0)
            {
                return MediaCom.EPointer;
            }

            long callback = _com.Lookup(result) is MfAsyncResult mine ? mine.Callback : _com.U32(result + 24);
            if (callback == 0 || _com.GuestMethod(callback, 4) is null)
            {
                return MediaCom.EInvalidArg;
            }

            if (_com.Lookup(result) is { } host)
            {
                host.Refs++;
            }

            _invokeTrampoline = _invokeTrampoline != 0 ? _invokeTrampoline : _com.HostFunction("MFInvokeCallback.trampoline", RunInvoke);
            _e.QueueDeferredCall("IMFAsyncCallback::Invoke", _invokeTrampoline, callback, result);
            InvokesQueued++;
            return MediaCom.SOk;
        }

        public int InvokesQueued { get; private set; }

        /// <summary>The queued call: r0 is the callback, r1 the result.</summary>
        private void RunInvoke()
        {
            long callback = Arg(0), result = Arg(1);
            int step = 0;
            Queue<GuestCall> releases = new();
            _com.Sequence(
                _ =>
                {
                    if (step++ == 0)
                    {
                        return new GuestCall("IMFAsyncCallback::Invoke", _com.GuestMethod(callback, 4)!.Value, [callback, result]);
                    }

                    if (step == 2 && _com.Lookup(result) is not null)
                    {
                        List<GuestCall> calls = new();
                        _com.ReleaseAny(result, calls);
                        calls.ForEach(releases.Enqueue);
                    }

                    return releases.Count > 0 ? releases.Dequeue() : null;
                },
                _ => Return(MediaCom.SOk));
        }

        private const uint FirstPrivateQueue = 0x10000;
        private readonly Dictionary<long, int> _workQueues = new();
        private uint _nextQueue = FirstPrivateQueue;

        /// <summary>The standard queues (1-7, not 6) and any allocated one still locked.</summary>
        private bool ValidQueue(long id) => id is >= 1 and <= 7 and not 6 || _workQueues.ContainsKey(id);

        private void RegisterWorkQueues(Dictionary<string, Action> handlers)
        {
            handlers["MFAllocateWorkQueue"] = () => AllocateQueue(Arg(0));
            handlers["MFAllocateWorkQueueEx"] = () => AllocateQueue(Arg(1));
            handlers["MFLockWorkQueue"] = () =>
            {
                if (!ValidQueue(Arg(0)))
                {
                    Return(MfEInvalidWorkQueue);
                    return;
                }

                if (_workQueues.ContainsKey(Arg(0)))
                {
                    _workQueues[Arg(0)]++;
                }

                Return(MediaCom.SOk);
            };
            handlers["MFUnlockWorkQueue"] = () =>
            {
                if (!ValidQueue(Arg(0)))
                {
                    Return(MfEInvalidWorkQueue);
                    return;
                }

                if (_workQueues.TryGetValue(Arg(0), out int locks) && --locks <= 0)
                {
                    _workQueues.Remove(Arg(0));
                }
                else if (_workQueues.ContainsKey(Arg(0)))
                {
                    _workQueues[Arg(0)] = locks;
                }

                Return(MediaCom.SOk);
            };

            // HRESULT MFPutWorkItem(DWORD dwQueue, IMFAsyncCallback*, IUnknown* pState).
            handlers["MFPutWorkItem"] = () =>
            {
                if (!PlatformRunning)
                {
                    Return(MfEShutdown);
                    return;
                }

                if (!ValidQueue(Arg(0)))
                {
                    Return(MfEInvalidWorkQueue);
                    return;
                }

                if (Arg(1) == 0)
                {
                    Return(MediaCom.EPointer);
                    return;
                }

                List<GuestCall> calls = new();
                long result = CreateAsyncResult(0, Arg(1), Arg(2), calls);
                long hr = InvokeLater(result);
                _com.ReleaseAny(result, calls); // the queue holds its own reference
                _com.Then(calls, _ => Return(hr));
            };

            // HRESULT MFPutWorkItemEx(DWORD dwQueue, IMFAsyncResult*).
            handlers["MFPutWorkItemEx"] = () =>
            {
                if (!PlatformRunning)
                {
                    Return(MfEShutdown);
                    return;
                }

                Return(!ValidQueue(Arg(0)) ? MfEInvalidWorkQueue : InvokeLater(Arg(1)));
            };
        }

        private void AllocateQueue(long outPointer)
        {
            if (outPointer == 0)
            {
                Return(MediaCom.EPointer);
                return;
            }

            if (!PlatformRunning)
            {
                Return(MfEShutdown);
                return;
            }

            uint id = _nextQueue++;
            _workQueues[id] = 1;
            _com.W32(outPointer, id);
            Return(MediaCom.SOk);
        }

        // ------------------------------------------------------------------------------
        // IMFDXGIDeviceManager
        // ------------------------------------------------------------------------------

        internal sealed class MfDxgiManager : MediaObject
        {
            public override string Kind => "IMFDXGIDeviceManager";

            public uint Token;
            public long Device;
            public int Generation;
            public readonly Dictionary<long, int> Handles = new();
            public long LockedBy;
            public long NextHandle = 0x4D460001;

            public override IEnumerable<long> Destroy() => Device == 0 ? [] : [Device];
        }

        private MediaInterface? _dxgiManagerShape;
        private uint _nextResetToken = 0x52455354;
        private long _sharedManager;

        // IMFDXGIDeviceManager: CloseDeviceHandle 3, GetVideoService 4, LockDevice 5,
        // OpenDeviceHandle 6, ResetDevice 7, TestDevice 8, UnlockDevice 9.
        private MediaInterface DxgiManagerShape => _dxgiManagerShape ??= new("IMFDXGIDeviceManager", [IidDxgiDeviceManager], 10, () => new()
        {
            [3] = ("CloseDeviceHandle", () => WithManager(m =>
            {
                if (!m.Handles.Remove(Arg(1)))
                {
                    Return(MediaCom.EHandle);
                    return;
                }

                if (m.LockedBy == Arg(1))
                {
                    m.LockedBy = 0;
                }

                Return(MediaCom.SOk);
            })),
            [4] = ("GetVideoService", () => WithManager(m =>
            {
                long hr = CheckHandle(m, Arg(1));
                if (hr != MediaCom.SOk)
                {
                    _com.W32(Arg(3), 0);
                    Return(hr);
                    return;
                }

                // The service (ID3D11VideoDevice) comes from the device; one without it has no
                // video service to give.
                _com.QueryAny(m.Device, _com.ReadGuid(Arg(2)), Arg(3), r => Return(r == MediaCom.ENoInterface ? MfEUnsupportedService : r));
            })),

            // LockDevice(HANDLE, REFIID, void**, BOOL fBlock).
            [5] = ("LockDevice", () => WithManager(m =>
            {
                long hr = CheckHandle(m, Arg(1));
                if (hr == MediaCom.SOk && m.LockedBy != 0 && m.LockedBy != Arg(1))
                {
                    // Blocking would wait for another thread to unlock; the lock is only ever held
                    // between two calls on one thread here, so waiting could never end.
                    hr = MfEDxgiVideoDeviceLocked;
                }

                if (hr != MediaCom.SOk)
                {
                    _com.W32(Arg(3), 0);
                    Return(hr);
                    return;
                }

                long handle = Arg(1);
                _com.QueryAny(m.Device, _com.ReadGuid(Arg(2)), Arg(3), r =>
                {
                    if (r == MediaCom.SOk)
                    {
                        m.LockedBy = handle;
                    }

                    Return(r);
                });
            })),
            [6] = ("OpenDeviceHandle", () => WithManager(m =>
            {
                if (m.Device == 0)
                {
                    _com.W32(Arg(1), 0);
                    Return(MfEDxgiDeviceNotInitialized);
                    return;
                }

                long handle = m.NextHandle++;
                m.Handles[handle] = m.Generation;
                _com.W32(Arg(1), handle);
                Return(MediaCom.SOk);
            })),

            // ResetDevice(IUnknown* pUnkDevice, UINT resetToken).
            [7] = ("ResetDevice", () => WithManager(m =>
            {
                if (Arg(1) == 0 || Arg(2) != m.Token)
                {
                    Return(MediaCom.EInvalidArg);
                    return;
                }

                List<GuestCall> calls = new();
                _com.AddRefAny(Arg(1), calls);
                _com.ReleaseAny(m.Device, calls);
                m.Device = Arg(1);
                m.Generation++;
                m.LockedBy = 0;
                _com.Then(calls, _ => Return(MediaCom.SOk));
            })),
            [8] = ("TestDevice", () => WithManager(m => Return(CheckHandle(m, Arg(1))))),
            [9] = ("UnlockDevice", () => WithManager(m =>
            {
                if (!m.Handles.ContainsKey(Arg(1)) || m.LockedBy != Arg(1))
                {
                    Return(MediaCom.EInvalidArg);
                    return;
                }

                m.LockedBy = 0;
                Return(MediaCom.SOk);
            })),
        });

        private static long CheckHandle(MfDxgiManager m, long handle) =>
            !m.Handles.TryGetValue(handle, out int generation) ? MediaCom.EHandle
            : generation != m.Generation ? MfEDxgiNewVideoDevice
            : MediaCom.SOk;

        private void WithManager(Action<MfDxgiManager> act)
        {
            if (_com.Self<MfDxgiManager>() is { } m)
            {
                act(m);
            }
            else
            {
                Return(MediaCom.EPointer);
            }
        }

        public long CreateDxgiManager(out uint token)
        {
            token = _nextResetToken++;
            return _com.Expose(new MfDxgiManager { Token = token }, DxgiManagerShape);
        }

        private void RegisterDxgiManager(Dictionary<string, Action> handlers)
        {
            // HRESULT MFCreateDXGIDeviceManager(UINT* resetToken, IMFDXGIDeviceManager**).
            handlers["MFCreateDXGIDeviceManager"] = () =>
            {
                if (Arg(0) == 0 || Arg(1) == 0)
                {
                    Return(MediaCom.EPointer);
                    return;
                }

                _com.W32(Arg(1), CreateDxgiManager(out uint token));
                _com.W32(Arg(0), token);
                Return(MediaCom.SOk);
            };

            // HRESULT MFLockDXGIDeviceManager(UINT* pResetToken, IMFDXGIDeviceManager**): the
            // process-wide manager, created on first use and AddRef'd per lock.
            handlers["MFLockDXGIDeviceManager"] = () =>
            {
                if (Arg(1) == 0)
                {
                    Return(MediaCom.EPointer);
                    return;
                }

                if (_com.Lookup(_sharedManager) is not MfDxgiManager shared)
                {
                    _sharedManager = CreateDxgiManager(out _);
                    shared = (MfDxgiManager)_com.Lookup(_sharedManager)!;
                }
                else
                {
                    shared.Refs++;
                }

                shared.Refs++; // the caller's reference; the extra one is the lock's
                _com.W32(Arg(0), shared.Token);
                _com.W32(Arg(1), _sharedManager);
                Return(MediaCom.SOk);
            };
            handlers["MFUnlockDXGIDeviceManager"] = () =>
            {
                List<GuestCall> calls = new();
                if (_com.Lookup(_sharedManager) is not null)
                {
                    _com.ReleaseAny(_sharedManager, calls);
                }

                _com.Then(calls, _ => Return(MediaCom.SOk));
            };
        }

        // ------------------------------------------------------------------------------
        // Blobs, streams, arithmetic and wave formats
        // ------------------------------------------------------------------------------

        private MfAttributeStore? StoreOf(long pointer) => (_com.Lookup(pointer) as MfAttributedObject)?.Attributes;

        private void ForeignAttributes(string function)
        {
            _com.Record($"{function} on an IMFAttributes not created here -> E_NOTIMPL");
            Return(MediaCom.ENotImpl);
        }

        private void RegisterBlobsAndFormats(Dictionary<string, Action> handlers)
        {
            // LONGLONG MFllMulDiv(LONGLONG a, LONGLONG b, LONGLONG c, LONGLONG d): (a*b + d) / c,
            // computed without overflow; the 64-bit arguments take r0:r1, r2:r3 and two stack pairs.
            handlers["MFllMulDiv"] = () =>
            {
                Int128 a = Pair(0), b = Pair(2), c = Pair(4), d = Pair(6);
                Int128 product = (a * b) + d;
                long result = c == 0
                    ? (product >= 0 ? long.MaxValue : long.MinValue)
                    : (product / c) is var q && q > long.MaxValue ? long.MaxValue
                    : q < long.MinValue ? long.MinValue
                    : (long)q;
                _com.Frame.Return64(result);
            };

            handlers["MFGetAttributesAsBlobSize"] = () =>
            {
                if (StoreOf(Arg(0)) is not { } store)
                {
                    ForeignAttributes("MFGetAttributesAsBlobSize");
                    return;
                }

                _com.W32(Arg(1), store.ToBlob().Length);
                Return(MediaCom.SOk);
            };

            // HRESULT MFGetAttributesAsBlob(IMFAttributes*, UINT8* pBuf, UINT cbBufSize).
            handlers["MFGetAttributesAsBlob"] = () =>
            {
                if (StoreOf(Arg(0)) is not { } store)
                {
                    ForeignAttributes("MFGetAttributesAsBlob");
                    return;
                }

                byte[] blob = store.ToBlob();
                if (Arg(2) < blob.Length)
                {
                    Return(MfEBufferTooSmall);
                    return;
                }

                _e.WriteMemory(Arg(1), blob);
                Return(MediaCom.SOk);
            };

            // HRESULT MFInitAttributesFromBlob(IMFAttributes*, const UINT8* pBuf, UINT cbBufSize).
            handlers["MFInitAttributesFromBlob"] = () =>
            {
                if (StoreOf(Arg(0)) is not { } store)
                {
                    ForeignAttributes("MFInitAttributesFromBlob");
                    return;
                }

                LoadBlob(store, Arg(2) <= 0 ? [] : _e.ReadMemory(Arg(1), (int)Math.Min(Arg(2), CallFrame.SaneLengthLimit)));
            };

            // HRESULT MFSerializeAttributesToStream(IMFAttributes*, DWORD dwOptions, IStream*). The
            // stream gets a 32-bit length, then the attribute blob (IUnknown values are not carried).
            handlers["MFSerializeAttributesToStream"] = () =>
            {
                if (StoreOf(Arg(0)) is not { } store)
                {
                    ForeignAttributes("MFSerializeAttributesToStream");
                    return;
                }

                long stream = Arg(2);
                if (_com.GuestMethod(stream, 4) is not { } write)
                {
                    Return(MediaCom.EPointer);
                    return;
                }

                byte[] blob = store.ToBlob();
                long scratch = _com.Alloc(blob.Length + 4);
                _e.WriteMemory(scratch, [.. BitConverter.GetBytes(blob.Length), .. blob]);
                _com.Then([new GuestCall("IStream::Write", write, [stream, scratch, blob.Length + 4, 0])], results =>
                {
                    _com.Free(scratch);
                    Return(results[0]);
                });
            };

            // HRESULT MFDeserializeAttributesFromStream(IMFAttributes*, DWORD dwOptions, IStream*).
            handlers["MFDeserializeAttributesFromStream"] = () =>
            {
                if (StoreOf(Arg(0)) is not { } store)
                {
                    ForeignAttributes("MFDeserializeAttributesFromStream");
                    return;
                }

                long stream = Arg(2);
                if (_com.GuestMethod(stream, 3) is not { } read)
                {
                    Return(MediaCom.EPointer);
                    return;
                }

                long scratch = _com.Alloc(8);
                long body = 0;
                int length = 0;
                _com.Sequence(
                    results => results.Count switch
                    {
                        0 => new GuestCall("IStream::Read", read, [stream, scratch, 4, scratch + 4]),
                        1 when results[0] == 0 && _com.U32(scratch + 4) == 4 &&
                               (length = (int)_com.U32(scratch)) is > 0 and < CallFrame.SaneLengthLimit
                            => new GuestCall("IStream::Read", read, [stream, body = _com.Alloc(length), length, scratch + 4]),
                        _ => null,
                    },
                    results =>
                    {
                        byte[]? blob = results.Count == 2 && results[1] == 0 && _com.U32(scratch + 4) == length
                            ? _e.ReadMemory(body, length)
                            : null;
                        _com.Free(scratch);
                        _com.Free(body);
                        if (blob is null)
                        {
                            Return(results.Count > 0 && results[^1] != 0 ? results[^1] : MediaCom.EInvalidArg);
                            return;
                        }

                        if (!store.FromBlob(blob))
                        {
                            Return(MediaCom.EInvalidArg);
                            return;
                        }

                        Return(MediaCom.SOk);
                    });
            };

            // HRESULT MFCreateWaveFormatExFromMFMediaType(IMFMediaType*, WAVEFORMATEX** ppWF,
            // UINT32* pcbSize, UINT32 Flags). Flags: 0 normal, 1 force WAVEFORMATEXTENSIBLE.
            handlers["MFCreateWaveFormatExFromMFMediaType"] = () =>
            {
                MfAttributeStore? type = StoreOf(Arg(0));
                if (Arg(1) == 0)
                {
                    Return(MediaCom.EPointer);
                    return;
                }

                byte[]? format = type is null ? null : WaveFormatFromMediaType(type, forceExtensible: Arg(3) == 1);
                if (format is null)
                {
                    _com.W32(Arg(1), 0);
                    Return(MediaCom.EInvalidArg);
                    return;
                }

                long p = _com.Alloc(format.Length);
                _e.WriteMemory(p, format);
                _com.W32(Arg(1), p);
                _com.W32(Arg(2), format.Length);
                Return(MediaCom.SOk);
            };

            // HRESULT MFInitMediaTypeFromWaveFormatEx(IMFMediaType*, const WAVEFORMATEX*, UINT32 cbBufSize).
            handlers["MFInitMediaTypeFromWaveFormatEx"] = () =>
            {
                MfAttributeStore? type = StoreOf(Arg(0));
                if (type is null || Arg(1) == 0 || Arg(2) < 16)
                {
                    Return(type is null && Arg(0) != 0 ? MediaCom.ENotImpl : MediaCom.EInvalidArg);
                    return;
                }

                WaveFormat? wf = WaveFormat.Read(_e.ReadMemory(Arg(1), (int)Math.Min(Arg(2), 4096)));
                if (wf is null)
                {
                    Return(MediaCom.EInvalidArg);
                    return;
                }

                List<GuestCall> calls = new();
                _attributes.CopyInto(new MfAttributeStore(), type, calls); // clears it, releasing anything held
                InitMediaTypeFromWaveFormat(type, wf);
                _com.Then(calls, _ => Return(MediaCom.SOk));
            };
        }

        private void LoadBlob(MfAttributeStore store, byte[] blob)
        {
            MfAttributeStore parsed = new();
            if (!parsed.FromBlob(blob))
            {
                Return(MediaCom.EInvalidArg);
                return;
            }

            List<GuestCall> calls = new();
            _attributes.CopyInto(parsed, store, calls);
            _com.Then(calls, _ => Return(MediaCom.SOk));
        }

        /// <summary>A WAVEFORMATEX (and its extensible tail) as read from memory.</summary>
        internal sealed record WaveFormat(
            ushort Tag, int Channels, int SampleRate, int AvgBytesPerSecond, int BlockAlign, int Bits,
            int ValidBits, uint ChannelMask, Guid? SubFormat, byte[] Extra)
        {
            public const ushort Pcm = 1, Float = 3, Extensible = 0xFFFE;

            public static readonly Guid SubtypePcm = FormatGuid(Pcm);
            public static readonly Guid SubtypeFloat = FormatGuid(Float);

            /// <summary>The effective format tag: an extensible format's sub-format, when it is a tag.</summary>
            public ushort EffectiveTag => Tag == Extensible && SubFormat is { } g && IsFormatGuid(g, out uint t) && t <= 0xFFFF ? (ushort)t : Tag;

            public static WaveFormat? Read(byte[] bytes)
            {
                if (bytes.Length < 16)
                {
                    return null;
                }

                ushort tag = BitConverter.ToUInt16(bytes, 0);
                int cbSize = bytes.Length >= 18 ? BitConverter.ToUInt16(bytes, 16) : 0;
                byte[] extra = bytes.Length >= 18 ? bytes.AsSpan(18, Math.Min(cbSize, bytes.Length - 18)).ToArray() : [];
                bool extensible = tag == Extensible && extra.Length >= 22;
                int bits = BitConverter.ToUInt16(bytes, 14);
                return new WaveFormat(
                    tag,
                    BitConverter.ToUInt16(bytes, 2),
                    BitConverter.ToInt32(bytes, 4),
                    BitConverter.ToInt32(bytes, 8),
                    BitConverter.ToUInt16(bytes, 12),
                    bits,
                    extensible ? BitConverter.ToUInt16(extra, 0) : bits,
                    extensible ? BitConverter.ToUInt32(extra, 2) : 0,
                    extensible ? new Guid(extra.AsSpan(6, 16)) : null,
                    extra);
            }

            /// <summary>A WAVEFORMATEX (18 bytes) or WAVEFORMATEXTENSIBLE (40 bytes) for PCM or float.</summary>
            public static byte[] Write(bool isFloat, int channels, int rate, int bits, bool extensible, uint mask)
            {
                int block = channels * (bits / 8);
                using MemoryStream m = new();
                using BinaryWriter w = new(m);
                w.Write(extensible ? Extensible : isFloat ? Float : Pcm);
                w.Write((ushort)channels);
                w.Write(rate);
                w.Write(rate * block);
                w.Write((ushort)block);
                w.Write((ushort)bits);
                w.Write((ushort)(extensible ? 22 : 0));
                if (extensible)
                {
                    w.Write((ushort)bits);
                    w.Write(mask);
                    w.Write((isFloat ? SubtypeFloat : SubtypePcm).ToByteArray());
                }

                return m.ToArray();
            }
        }

        /// <summary>The default speaker mask for a channel count (KSAUDIO_SPEAKER_*).</summary>
        public static uint DefaultChannelMask(int channels) => channels switch
        {
            1 => 0x4,
            2 => 0x3,
            3 => 0x7,
            4 => 0x33,
            5 => 0x37,
            6 => 0x3F,
            7 => 0x13F,
            8 => 0x63F,
            _ => 0,
        };

        private static byte[]? WaveFormatFromMediaType(MfAttributeStore type, bool forceExtensible)
        {
            if (type.GuidOf(MtMajorType) != MediaTypeAudio || type.GuidOf(MtSubtype) is not { } subtype || !IsFormatGuid(subtype, out uint tag))
            {
                return null;
            }

            int channels = (int)(type.UInt32(MtAudioNumChannels) ?? 0);
            int rate = (int)(type.UInt32(MtAudioSamplesPerSecond) ?? 0);
            int bits = (int)(type.UInt32(MtAudioBitsPerSample) ?? 0);
            if (tag is WaveFormat.Pcm or WaveFormat.Float)
            {
                if (channels == 0 || bits == 0)
                {
                    return null;
                }

                uint mask = type.UInt32(MtAudioChannelMask) ?? DefaultChannelMask(channels);
                bool extensible = forceExtensible || channels > 2 || bits > 16 && tag == WaveFormat.Pcm;
                return WaveFormat.Write(tag == WaveFormat.Float, channels, rate, bits, extensible, mask);
            }

            // A compressed format: the tag is the subtype's first field and its extra bytes are
            // the user data.
            byte[] user = type.Get(MtUserData) is { Type: MfValue.VtBlob } u ? u.Blob! : [];
            using MemoryStream m = new();
            using BinaryWriter w = new(m);
            w.Write((ushort)tag);
            w.Write((ushort)channels);
            w.Write(rate);
            w.Write((int)(type.UInt32(MtAudioAvgBytesPerSecond) ?? 0));
            w.Write((ushort)(type.UInt32(MtAudioBlockAlignment) ?? 0));
            w.Write((ushort)bits);
            w.Write((ushort)user.Length);
            w.Write(user);
            return m.ToArray();
        }

        private static void InitMediaTypeFromWaveFormat(MfAttributeStore type, WaveFormat wf)
        {
            ushort tag = wf.EffectiveTag;
            Guid subtype = wf.Tag == WaveFormat.Extensible && wf.SubFormat is { } s ? s : FormatGuid(tag);
            void U32(Guid key, long value) => type.Set(key, new MfValue { Type = MfValue.VtUi4, Integer = (uint)value });
            type.Set(MtMajorType, new MfValue { Type = MfValue.VtClsid, Guid = MediaTypeAudio });
            type.Set(MtSubtype, new MfValue { Type = MfValue.VtClsid, Guid = subtype });
            U32(MtAudioNumChannels, wf.Channels);
            U32(MtAudioSamplesPerSecond, wf.SampleRate);
            U32(MtAudioAvgBytesPerSecond, wf.AvgBytesPerSecond);
            U32(MtAudioBlockAlignment, wf.BlockAlign);
            if (wf.Bits != 0)
            {
                U32(MtAudioBitsPerSample, wf.Bits);
            }

            if (tag is WaveFormat.Pcm or WaveFormat.Float)
            {
                U32(MtAllSamplesIndependent, 1);
            }

            if (wf.Tag == WaveFormat.Extensible)
            {
                U32(MtAudioChannelMask, wf.ChannelMask);
                if (wf.ValidBits != 0)
                {
                    U32(MtAudioValidBitsPerSample, wf.ValidBits);
                }
            }
            else if (wf.Extra.Length > 0 && tag is not (WaveFormat.Pcm or WaveFormat.Float))
            {
                type.Set(MtUserData, new MfValue { Type = MfValue.VtBlob, Blob = wf.Extra });
            }
        }

        // ------------------------------------------------------------------------------
        // What is answered with a documented failure, and why
        // ------------------------------------------------------------------------------

        private void RegisterUnsupported(Dictionary<string, Action> handlers)
        {
            void Refuse(string function, int outIndex, string why)
            {
                handlers[function] = () =>
                {
                    _com.W32(Arg(outIndex), 0);
                    _com.Record($"{function}: {why} -> E_NOTIMPL");
                    Return(MediaCom.ENotImpl);
                };
            }

            const string noStreamBridge = "no bridge from a WinRT IRandomAccessStream / IStream to IMFByteStream exists here";
            Refuse("MFCreateMFByteStreamOnStreamEx", 1, noStreamBridge);
            Refuse("MFCreateMFByteStreamOnStream", 1, noStreamBridge);
            Refuse("MFCreateStreamOnMFByteStreamEx", 2, noStreamBridge);
            Refuse("MFCreateStreamOnMFByteStream", 1, noStreamBridge);
            Refuse("MFCreateMediaTypeFromProperties", 1, "media types from an IPropertyStore are not built here");
            Refuse("MFCreatePropertiesFromMediaType", 2, "no IPropertyStore implementation exists here");
            Refuse("MFCreateVideoSampleAllocatorEx", 1, "video frames are never decoded, so there is nothing to allocate surfaces for");

            // MFCreateDXGISurfaceBuffer(REFIID, IUnknown* punkSurface, UINT uSubresourceIndex, BOOL fBottomUp, IMFMediaBuffer**).
            Refuse("MFCreateDXGISurfaceBuffer", 4, "texture-backed media buffers cannot be locked into guest memory here");
        }
    }
}
