namespace WPR.Wp8Native
{
    /// <summary>
    /// MFPlat.dll: the Media Foundation platform as WP8 exposes it to native code.
    /// </summary>
    /// <remarks>
    /// The data containers - attributes, media types, samples, memory and 2D buffers,
    /// collections, async results - are pure bookkeeping and implemented whole, to their
    /// documented contracts. So are the platform and work-queue calls and the DXGI device
    /// manager, which only hands a device out under a lock. Where a function would need a
    /// decoder or a bridge this host does not have (a byte stream over a WinRT
    /// <c>IRandomAccessStream</c>, a video sample allocator, a surface-backed buffer), it fails
    /// with E_NOTIMPL, writes its out-pointer NULL and is recorded in
    /// <see cref="MediaCom.Unimplemented"/> - never success with nothing behind it.
    /// </remarks>
    internal sealed partial class MediaFoundation
    {
        public const long MfEShutdown = unchecked((int)0xC00D3E85);
        public const long MfEBadStartupVersion = unchecked((int)0xC00D36E3);
        public const long MfEBufferTooSmall = unchecked((int)0xC00D36B1);
        public const long MfEInvalidRequest = unchecked((int)0xC00D36B2);
        public const long MfEInvalidMediaType = unchecked((int)0xC00D36B4);
        public const long MfEUnsupportedRepresentation = unchecked((int)0xC00D36B7);
        public const long MfEUnsupportedService = unchecked((int)0xC00D36BA);
        public const long MfENoSampleTimestamp = unchecked((int)0xC00D36C8);
        public const long MfENoSampleDuration = unchecked((int)0xC00D36C9);
        public const long MfEInvalidWorkQueue = unchecked((int)0xC00D36FE);
        public const long MfEUnsupportedByteStreamType = unchecked((int)0xC00D36C4);
        public const long MfEDxgiDeviceNotInitialized = unchecked((int)0x80041000);
        public const long MfEDxgiNewVideoDevice = unchecked((int)0x80041001);
        public const long MfEDxgiVideoDeviceLocked = unchecked((int)0x80041002);
        public const long WrongState = unchecked((int)0x8007139F); // HRESULT_FROM_WIN32(ERROR_WRONG_STATE)

        public static readonly Guid IidAttributes = new("2cd2d921-c447-44a7-a13c-4adabfc247e3");
        public static readonly Guid IidMediaType = new("44ae0fa8-ea31-4109-8d2e-4cae4997c555");
        public static readonly Guid IidSample = new("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4");
        public static readonly Guid IidMediaBuffer = new("045fa593-8799-42b8-bc8d-8968c6453507");
        public static readonly Guid Iid2DBuffer = new("7dc9d5f9-9ed9-44ec-9bbf-0600bb589fbb");
        public static readonly Guid Iid2DBuffer2 = new("33ae5ea6-4316-436f-8ddd-d73d22f829ec");
        public static readonly Guid IidCollection = new("5bc8a76b-869a-46a3-9b03-fa218a66aebe");
        public static readonly Guid IidAsyncResult = new("ac6b7889-0740-4d51-8619-905994a55cc6");
        public static readonly Guid IidDxgiDeviceManager = new("eb533d5d-2db6-40f8-97a9-494692014f07");

        public static readonly Guid MtMajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        public static readonly Guid MtSubtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        public static readonly Guid MtAllSamplesIndependent = new("c9173739-5e56-461c-b713-46fb995cb95f");
        public static readonly Guid MtUserData = new("b6bc765f-4c3b-40a4-bd51-2535b66fe09d");
        public static readonly Guid MtAudioNumChannels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        public static readonly Guid MtAudioSamplesPerSecond = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        public static readonly Guid MtAudioAvgBytesPerSecond = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
        public static readonly Guid MtAudioBlockAlignment = new("322de230-9eeb-43bd-ab7a-ff412251541d");
        public static readonly Guid MtAudioBitsPerSample = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
        public static readonly Guid MtAudioValidBitsPerSample = new("d9bf8d6a-9530-4b7c-9ddf-ff6fd58bbd06");
        public static readonly Guid MtAudioChannelMask = new("55fb5765-644a-4caf-8479-938983bb1588");
        public static readonly Guid MtFrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
        public static readonly Guid MediaTypeAudio = new("73647561-0000-0010-8000-00aa00389b71");
        public static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00aa00389b71");

        /// <summary>The FOURCC/format-tag subtype family: {XXXXXXXX-0000-0010-8000-00AA00389B71}.</summary>
        public static Guid FormatGuid(uint tag) => new(tag, 0, 0x10, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71);

        public static bool IsFormatGuid(Guid g, out uint tag)
        {
            byte[] b = g.ToByteArray();
            tag = BitConverter.ToUInt32(b, 0);
            return b.AsSpan(4).SequenceEqual(FormatGuid(0).ToByteArray().AsSpan(4));
        }

        private readonly MediaCom _com;
        private readonly ArmEmulator _e;
        private readonly MfAttributeMethods _attributes;

        public MediaFoundation(MediaCom com, string packageRoot)
        {
            _com = com;
            _e = com.Emulator;
            _attributes = new MfAttributeMethods(com);
            com.LateInterface = LateInterface;
        }

        public MediaCom Com => _com;

        private long Arg(int i) => _com.Arg(i);

        private void Return(long v) => _com.Return(v);

        /// <summary>MFStartup calls not yet matched by MFShutdown.</summary>
        public int Startups { get; private set; }

        public int PlatformLocks { get; private set; }

        private bool PlatformRunning => Startups > 0 || PlatformLocks > 0;

        public void RegisterInto(Dictionary<string, Action> handlers)
        {
            // HRESULT MFStartup(ULONG Version, DWORD dwFlags). MF_VERSION is
            // (MF_SDK_VERSION << 16) | MF_API_VERSION: 0x00020070, or 0x00010070 from the Vista SDK.
            handlers["MFStartup"] = () =>
            {
                if (Arg(0) is not (0x00020070 or 0x00010070))
                {
                    Return(MfEBadStartupVersion);
                    return;
                }

                Startups++;
                Return(MediaCom.SOk);
            };
            handlers["MFShutdown"] = () =>
            {
                Startups = Math.Max(0, Startups - 1);
                Return(MediaCom.SOk);
            };
            handlers["MFLockPlatform"] = () => { PlatformLocks++; Return(MediaCom.SOk); };
            handlers["MFUnlockPlatform"] = () => { PlatformLocks = Math.Max(0, PlatformLocks - 1); Return(MediaCom.SOk); };

            // HRESULT MFCreateAttributes(IMFAttributes**, UINT32 cInitialSize).
            handlers["MFCreateAttributes"] = () => Out(Arg(0), () => CreateAttributes());
            handlers["MFCreateMediaType"] = () => Out(Arg(0), () => CreateMediaType());
            handlers["MFCreateSample"] = () => Out(Arg(0), () => _com.Expose(new MfSample(), SampleShape));
            handlers["MFCreateCollection"] = () => Out(Arg(0), () => _com.Expose(new MfCollection(), CollectionShape));

            // HRESULT MFCreateMemoryBuffer(DWORD cbMaxLength, IMFMediaBuffer**).
            handlers["MFCreateMemoryBuffer"] = () => Out(Arg(1), () => CreateMemoryBuffer(Arg(0), 0));

            // HRESULT MFCreateAlignedMemoryBuffer(DWORD cbMaxLength, DWORD fAlignmentFlags, IMFMediaBuffer**):
            // the flags are an alignment minus one (MF_16_BYTE_ALIGNMENT is 15).
            handlers["MFCreateAlignedMemoryBuffer"] = () =>
            {
                long mask = Arg(1);
                if ((mask & (mask + 1)) != 0)
                {
                    _com.W32(Arg(2), 0);
                    Return(MediaCom.EInvalidArg);
                    return;
                }

                Out(Arg(2), () => CreateMemoryBuffer(Arg(0), mask));
            };

            // HRESULT MFCreate2DMediaBuffer(DWORD dwWidth, DWORD dwHeight, DWORD dwFourCC, BOOL fBottomUp, IMFMediaBuffer**).
            handlers["MFCreate2DMediaBuffer"] = () =>
            {
                long buffer = Create2DBuffer((int)Arg(0), (int)Arg(1), (uint)Arg(2), Arg(3) != 0);
                _com.W32(Arg(4), buffer);
                Return(buffer == 0 ? MfEInvalidMediaType : MediaCom.SOk);
            };

            // HRESULT MFCreateMediaBufferWrapper(IMFMediaBuffer*, DWORD cbOffset, DWORD dwLength, IMFMediaBuffer**).
            handlers["MFCreateMediaBufferWrapper"] = CreateBufferWrapper;
            handlers["MFCreateMediaBufferFromMediaType"] = CreateBufferFromMediaType;

            // HRESULT MFCreateAsyncResult(IUnknown* punkObject, IMFAsyncCallback*, IUnknown* punkState, IMFAsyncResult**).
            handlers["MFCreateAsyncResult"] = () =>
            {
                if (Arg(3) == 0)
                {
                    Return(MediaCom.EPointer);
                    return;
                }

                List<GuestCall> calls = new();
                long result = CreateAsyncResult(Arg(0), Arg(1), Arg(2), calls);
                _com.W32(Arg(3), result);
                _com.Then(calls, _ => Return(MediaCom.SOk));
            };
            handlers["MFInvokeCallback"] = () => Return(InvokeLater(Arg(0)));

            RegisterWorkQueues(handlers);
            RegisterDxgiManager(handlers);
            RegisterBlobsAndFormats(handlers);
            RegisterByteStreams(handlers);
            RegisterProperties(handlers);
            RegisterUnsupported(handlers);
        }

        /// <summary>Writes a fresh object to an out-parameter: E_POINTER for a NULL one.</summary>
        private void Out(long outPointer, Func<long> create)
        {
            if (outPointer == 0)
            {
                Return(MediaCom.EPointer);
                return;
            }

            long created = create();
            _com.W32(outPointer, created);
            Return(created == 0 ? MediaCom.EOutOfMemory : MediaCom.SOk);
        }

        // ------------------------------------------------------------------------------
        // Attributes and media types
        // ------------------------------------------------------------------------------

        private sealed class MfAttributes : MfAttributedObject
        {
            public override string Kind => "IMFAttributes";
        }

        internal sealed class MfMediaType : MfAttributedObject
        {
            public override string Kind => "IMFMediaType";
        }

        private MediaInterface? _attributesShape;
        private MediaInterface? _mediaTypeShape;

        private MediaInterface AttributesShape => _attributesShape ??= new(
            "IMFAttributes", [IidAttributes], MfAttributeMethods.SlotCount, () => _attributes.Build());

        // IMFMediaType: IMFAttributes, then GetMajorType 33, IsCompressedFormat 34, IsEqual 35,
        // GetRepresentation 36, FreeRepresentation 37.
        private MediaInterface MediaTypeShape => _mediaTypeShape ??= new(
            "IMFMediaType", [IidMediaType, IidAttributes], 38, () => _attributes.Build(new()
            {
                [33] = ("GetMajorType", () =>
                {
                    Guid? major = _com.Self<MfMediaType>()?.Attributes.GuidOf(MtMajorType);
                    if (major is null)
                    {
                        Return(MfAttributeMethods.MfEAttributeNotFound);
                        return;
                    }

                    _com.WriteGuid(Arg(1), major.Value);
                    Return(MediaCom.SOk);
                }),

                // "FALSE if MF_MT_ALL_SAMPLES_INDEPENDENT is TRUE; if it is missing or FALSE, TRUE."
                [34] = ("IsCompressedFormat", () =>
                {
                    uint? independent = _com.Self<MfMediaType>()?.Attributes.UInt32(MtAllSamplesIndependent);
                    _com.W32(Arg(1), independent is > 0 ? 0 : 1);
                    Return(MediaCom.SOk);
                }),
                [35] = ("IsEqual", IsEqual),

                // AM_MEDIA_TYPE and friends are DirectShow structures; no representation is offered.
                [36] = ("GetRepresentation", () => { _com.W32(Arg(5), 0); Return(MfEUnsupportedRepresentation); }),
                [37] = ("FreeRepresentation", () => Return(MfEUnsupportedRepresentation)),
            }));

        public long CreateAttributes() => _com.Expose(new MfAttributes(), AttributesShape);

        public long CreateMediaType() => _com.Expose(new MfMediaType(), MediaTypeShape);

        /// <summary>HRESULT IsEqual(IMFMediaType* pIMediaType, DWORD* pdwFlags): S_OK only when all four flags hold.</summary>
        private void IsEqual()
        {
            MfAttributeStore? mine = _com.Self<MfMediaType>()?.Attributes;
            MfAttributeStore? theirs = (_com.Lookup(Arg(1)) as MfMediaType)?.Attributes;
            if (mine is null || theirs is null || Arg(2) == 0)
            {
                _com.W32(Arg(2), 0);
                Return(Arg(1) == 0 || Arg(2) == 0 ? MediaCom.EPointer : MediaCom.EInvalidArg);
                return;
            }

            static bool Same(MfValue? a, MfValue? b) => (a is null && b is null) || (a is not null && b is not null && a.SameAs(b));

            uint flags = 0;
            if (mine.Get(MtMajorType) is not null && Same(mine.Get(MtMajorType), theirs.Get(MtMajorType)))
            {
                flags |= 1; // MF_MEDIATYPE_EQUAL_MAJOR_TYPES
            }

            if (mine.Get(MtSubtype) is not null && Same(mine.Get(MtSubtype), theirs.Get(MtSubtype)))
            {
                flags |= 2; // MF_MEDIATYPE_EQUAL_FORMAT_TYPES
            }

            bool DataMatch(MfAttributeStore a, MfAttributeStore b) =>
                a.Items.Where(p => p.Key != MtUserData).All(p => b.Get(p.Key) is { } v && v.SameAs(p.Value));
            if (DataMatch(mine, theirs) && DataMatch(theirs, mine))
            {
                flags |= 4; // MF_MEDIATYPE_EQUAL_FORMAT_DATA
            }

            if (Same(mine.Get(MtUserData), theirs.Get(MtUserData)))
            {
                flags |= 8; // MF_MEDIATYPE_EQUAL_FORMAT_USER_DATA
            }

            _com.W32(Arg(2), flags);
            Return(flags == 0xF ? MediaCom.SOk : MediaCom.SFalse);
        }

        // ------------------------------------------------------------------------------
        // Buffers
        // ------------------------------------------------------------------------------

        internal sealed class MfBuffer : MediaObject
        {
            public override string Kind => "IMFMediaBuffer";

            public long Data;
            public int Max;
            public int Current;
            public long Allocation;
            public long Parent;
            public int Locks;

            public bool Is2D;
            public int Width, Height, Pitch, ContiguousLength;
            public bool BottomUp;
            public int Locks2D;

            public override IEnumerable<long> Destroy() => Parent == 0 ? [] : [Parent];
        }

        private MediaInterface? _bufferShape;
        private MediaInterface? _buffer2DShape;
        private MediaInterface? _buffer2D2Shape;

        // IMFMediaBuffer: Lock 3, Unlock 4, GetCurrentLength 5, SetCurrentLength 6, GetMaxLength 7.
        private Dictionary<int, (string, Action)> BufferMethods() => new()
        {
            [3] = ("Lock", () =>
            {
                MfBuffer? b = _com.Self<MfBuffer>();
                if (b is null || Arg(1) == 0)
                {
                    Return(MediaCom.EPointer);
                    return;
                }

                b.Locks++;
                _com.W32(Arg(1), b.Data);
                _com.W32(Arg(2), b.Max);
                _com.W32(Arg(3), b.Current);
                Return(MediaCom.SOk);
            }),
            [4] = ("Unlock", () =>
            {
                MfBuffer? b = _com.Self<MfBuffer>();
                if (b is null || b.Locks == 0)
                {
                    Return(b is null ? MediaCom.EPointer : WrongState);
                    return;
                }

                b.Locks--;
                Return(MediaCom.SOk);
            }),
            [5] = ("GetCurrentLength", () =>
            {
                _com.W32(Arg(1), _com.Self<MfBuffer>()?.Current ?? 0);
                Return(Arg(1) == 0 ? MediaCom.EPointer : MediaCom.SOk);
            }),
            [6] = ("SetCurrentLength", () =>
            {
                MfBuffer? b = _com.Self<MfBuffer>();
                if (b is null || Arg(1) > b.Max)
                {
                    Return(MediaCom.EInvalidArg);
                    return;
                }

                b.Current = (int)Arg(1);
                Return(MediaCom.SOk);
            }),
            [7] = ("GetMaxLength", () =>
            {
                _com.W32(Arg(1), _com.Self<MfBuffer>()?.Max ?? 0);
                Return(Arg(1) == 0 ? MediaCom.EPointer : MediaCom.SOk);
            }),
        };

        private MediaInterface BufferShape => _bufferShape ??= new("IMFMediaBuffer", [IidMediaBuffer], 8, BufferMethods);

        // IMF2DBuffer: Lock2D 3, Unlock2D 4, GetScanline0AndPitch 5, IsContiguousFormat 6,
        // GetContiguousLength 7, ContiguousCopyTo 8, ContiguousCopyFrom 9. IMF2DBuffer2: Lock2DSize 10, Copy2DTo 11.
        private Dictionary<int, (string, Action)> Buffer2DMethods() => new()
        {
            [3] = ("Lock2D", () =>
            {
                MfBuffer? b = _com.Self<MfBuffer>();
                if (b is null || Arg(1) == 0 || Arg(2) == 0)
                {
                    Return(MediaCom.EPointer);
                    return;
                }

                b.Locks2D++;
                WriteScanline0(b, Arg(1), Arg(2));
                Return(MediaCom.SOk);
            }),
            [4] = ("Unlock2D", () =>
            {
                MfBuffer? b = _com.Self<MfBuffer>();
                if (b is null || b.Locks2D == 0)
                {
                    Return(b is null ? MediaCom.EPointer : WrongState);
                    return;
                }

                b.Locks2D--;
                Return(MediaCom.SOk);
            }),
            [5] = ("GetScanline0AndPitch", () =>
            {
                MfBuffer? b = _com.Self<MfBuffer>();
                if (b is null || b.Locks2D == 0)
                {
                    Return(b is null ? MediaCom.EPointer : WrongState);
                    return;
                }

                WriteScanline0(b, Arg(1), Arg(2));
                Return(MediaCom.SOk);
            }),

            // The pitch here is exactly the row size, so the buffer is always contiguous.
            [6] = ("IsContiguousFormat", () => { _com.W32(Arg(1), 1); Return(MediaCom.SOk); }),
            [7] = ("GetContiguousLength", () => { _com.W32(Arg(1), _com.Self<MfBuffer>()?.ContiguousLength ?? 0); Return(MediaCom.SOk); }),
            [8] = ("ContiguousCopyTo", () =>
            {
                MfBuffer? b = _com.Self<MfBuffer>();
                if (b is null || Arg(2) < b.ContiguousLength)
                {
                    Return(MediaCom.EInvalidArg);
                    return;
                }

                _e.WriteMemory(Arg(1), _e.ReadMemory(b.Data, b.ContiguousLength));
                Return(MediaCom.SOk);
            }),
            [9] = ("ContiguousCopyFrom", () =>
            {
                MfBuffer? b = _com.Self<MfBuffer>();
                if (b is null || Arg(2) < b.ContiguousLength)
                {
                    Return(MediaCom.EInvalidArg);
                    return;
                }

                _e.WriteMemory(b.Data, _e.ReadMemory(Arg(1), b.ContiguousLength));
                b.Current = b.ContiguousLength;
                Return(MediaCom.SOk);
            }),

            // Lock2DSize(MF2DBuffer_LockFlags, BYTE** ppbScanline0, LONG* plPitch, BYTE** ppbBufferStart, DWORD* pcbBufferLength).
            [10] = ("Lock2DSize", () =>
            {
                MfBuffer? b = _com.Self<MfBuffer>();
                if (b is null || Arg(2) == 0 || Arg(3) == 0)
                {
                    Return(MediaCom.EPointer);
                    return;
                }

                b.Locks2D++;
                WriteScanline0(b, Arg(2), Arg(3));
                _com.W32(Arg(4), b.Data);
                _com.W32(Arg(5), b.ContiguousLength);
                Return(MediaCom.SOk);
            }),
            [11] = ("Copy2DTo", () =>
            {
                MfBuffer? b = _com.Self<MfBuffer>();
                MfBuffer? dest = _com.Lookup(Arg(1)) as MfBuffer;
                if (b is null || dest is null || !dest.Is2D || dest.ContiguousLength < b.ContiguousLength)
                {
                    Return(MediaCom.EInvalidArg);
                    return;
                }

                _e.WriteMemory(dest.Data, _e.ReadMemory(b.Data, b.ContiguousLength));
                Return(MediaCom.SOk);
            }),
        };

        private MediaInterface Buffer2DShape => _buffer2DShape ??= new("IMF2DBuffer", [Iid2DBuffer], 10, Buffer2DMethods);

        private MediaInterface Buffer2D2Shape => _buffer2D2Shape ??= new("IMF2DBuffer2", [Iid2DBuffer2, Iid2DBuffer], 12, Buffer2DMethods);

        private void WriteScanline0(MfBuffer b, long scanlineOut, long pitchOut)
        {
            bool flip = b.BottomUp;
            _com.W32(scanlineOut, flip ? b.Data + ((long)(b.Height - 1) * b.Pitch) : b.Data);
            _com.W32(pitchOut, flip ? -b.Pitch : b.Pitch);
        }

        private long LateInterface(MediaObject obj, Guid iid) =>
            obj is MfBuffer { Is2D: true } b && (iid == Iid2DBuffer || iid == Iid2DBuffer2)
                ? _com.Expose(b, Buffer2D2Shape)
                : 0;

        public long CreateMemoryBuffer(long max, long alignmentMask)
        {
            if (max < 0 || max > CallFrame.SaneLengthLimit)
            {
                return 0;
            }

            long allocation = _com.Alloc(max + alignmentMask + 1);
            long data = (allocation + alignmentMask) & ~alignmentMask;
            MfBuffer b = new() { Data = data, Max = (int)max, Allocation = allocation };
            b.Finalizer = () => _com.Free(allocation);
            return _com.Expose(b, BufferShape);
        }

        public MfBuffer? Buffer(long pointer) => _com.Lookup(pointer) as MfBuffer;

        /// <summary>Row size and contiguous size of a frame in a FOURCC or D3DFORMAT; null if unknown.</summary>
        public static (int Row, int Length, bool Rgb)? FrameLayout(uint format, int width, int height)
        {
            static uint Cc(string s) => (uint)(s[0] | (s[1] << 8) | (s[2] << 16) | (s[3] << 24));
            if (width <= 0 || height <= 0 || (long)width * height > 64L * 1024 * 1024)
            {
                return null;
            }

            int halfW = (width + 1) / 2, halfH = (height + 1) / 2;
            int? bytesPerPixel = format switch
            {
                21 or 22 or 32 or 31 => 4,                           // A8R8G8B8, X8R8G8B8, A2B10G10R10, A2R10G10B10
                20 => 3,                                             // R8G8B8
                23 or 24 or 25 or 26 => 2,                           // R5G6B5, X1R5G5B5, A1R5G5B5, A4R4G4B4
                28 or 41 or 50 => 1,                                 // A8, P8, L8
                _ => null,
            };
            if (bytesPerPixel is { } bpp)
            {
                return (width * bpp, width * bpp * height, true);
            }

            if (format == Cc("AYUV"))
            {
                return (width * 4, width * 4 * height, false);
            }

            if (format == Cc("YUY2") || format == Cc("UYVY") || format == Cc("YVYU"))
            {
                return (halfW * 4, halfW * 4 * height, false);
            }

            if (format == Cc("NV12"))
            {
                int w = halfW * 2;
                return (w, (w * height) + (w * halfH), false);
            }

            if (format == Cc("YV12") || format == Cc("IYUV") || format == Cc("I420"))
            {
                return (width, (width * height) + (2 * halfW * halfH), false);
            }

            return null;
        }

        public long Create2DBuffer(int width, int height, uint format, bool bottomUp)
        {
            if (FrameLayout(format, width, height) is not { } layout)
            {
                return 0;
            }

            long allocation = _com.Alloc(layout.Length);
            MfBuffer b = new()
            {
                Data = allocation,
                Allocation = allocation,
                Max = layout.Length,
                Current = layout.Length,
                Is2D = true,
                Width = width,
                Height = height,
                Pitch = layout.Row,
                ContiguousLength = layout.Length,
                BottomUp = bottomUp && layout.Rgb,
            };
            b.Finalizer = () => _com.Free(allocation);
            long pointer = _com.Expose(b, BufferShape);
            _com.Expose(b, Buffer2D2Shape);
            return pointer;
        }

        private void CreateBufferWrapper()
        {
            MfBuffer? parent = Buffer(Arg(0));
            long offset = Arg(1), length = Arg(2);
            if (Arg(3) == 0)
            {
                Return(MediaCom.EPointer);
                return;
            }

            if (parent is null)
            {
                _com.W32(Arg(3), 0);
                if (Arg(0) != 0)
                {
                    _com.Record("MFCreateMediaBufferWrapper over an IMFMediaBuffer not created here -> E_NOTIMPL");
                }

                Return(Arg(0) == 0 ? MediaCom.EPointer : MediaCom.ENotImpl);
                return;
            }

            if (offset + length > parent.Max)
            {
                _com.W32(Arg(3), 0);
                Return(MediaCom.EInvalidArg);
                return;
            }

            parent.Refs++;
            MfBuffer wrapper = new()
            {
                Data = parent.Data + offset,
                Max = (int)length,
                Current = (int)Math.Clamp(parent.Current - offset, 0, length),
                Parent = Arg(0),
            };
            _com.W32(Arg(3), _com.Expose(wrapper, BufferShape));
            Return(MediaCom.SOk);
        }

        /// <summary>
        /// HRESULT MFCreateMediaBufferFromMediaType(IMFMediaType*, LONGLONG llDuration, DWORD dwMinLength,
        /// DWORD dwMinAlignment, IMFMediaBuffer**): the duration is the r2:r3 pair, the rest on the stack.
        /// </summary>
        private void CreateBufferFromMediaType()
        {
            MfAttributeStore? type = (_com.Lookup(Arg(0)) as MfMediaType)?.Attributes;
            long duration = (long)((ulong)(uint)Arg(2) | ((ulong)(uint)Arg(3) << 32));
            long minLength = Arg(4), alignment = Arg(5), outPointer = Arg(6);
            if (outPointer == 0 || type is null)
            {
                _com.W32(outPointer, 0);
                Return(type is null && Arg(0) != 0 ? MediaCom.EInvalidArg : MediaCom.EPointer);
                return;
            }

            long mask = alignment <= 1 ? 0 : (long)(System.Numerics.BitOperations.RoundUpToPowerOf2((uint)alignment) - 1);
            Guid? major = type.GuidOf(MtMajorType);
            long buffer;
            if (major == MediaTypeAudio)
            {
                long block = Math.Max(1, type.UInt32(MtAudioBlockAlignment) ?? 1);
                long bytesPerSecond = type.UInt32(MtAudioAvgBytesPerSecond) ??
                                      (type.UInt32(MtAudioSamplesPerSecond) ?? 0) * block;
                long length = (long)Math.Ceiling(Math.Max(0, duration) * (double)bytesPerSecond / 10_000_000.0);
                length = (length + block - 1) / block * block;
                buffer = CreateMemoryBuffer(Math.Max(length, minLength), mask);
            }
            else if (major == MediaTypeVideo && type.UInt64(MtFrameSize) is { } size &&
                     type.GuidOf(MtSubtype) is { } subtype && IsFormatGuid(subtype, out uint fourcc))
            {
                buffer = Create2DBuffer((int)(size >> 32), (int)(uint)size, fourcc, bottomUp: false);
                if (buffer == 0)
                {
                    _com.W32(outPointer, 0);
                    Return(MfEInvalidMediaType);
                    return;
                }
            }
            else
            {
                buffer = CreateMemoryBuffer(minLength, mask);
            }

            _com.W32(outPointer, buffer);
            Return(buffer == 0 ? MediaCom.EOutOfMemory : MediaCom.SOk);
        }

        // ------------------------------------------------------------------------------
        // Samples
        // ------------------------------------------------------------------------------

        internal sealed class MfSample : MfAttributedObject
        {
            public override string Kind => "IMFSample";

            public uint Flags;
            public long? Time;
            public long? Duration;
            public readonly List<long> Buffers = new();

            public override IEnumerable<long> Destroy() => [.. base.Destroy(), .. Buffers];
        }

        private MediaInterface? _sampleShape;

        // IMFSample: IMFAttributes, then GetSampleFlags 33, SetSampleFlags 34, GetSampleTime 35,
        // SetSampleTime 36, GetSampleDuration 37, SetSampleDuration 38, GetBufferCount 39,
        // GetBufferByIndex 40, ConvertToContiguousBuffer 41, AddBuffer 42, RemoveBufferByIndex 43,
        // RemoveAllBuffers 44, GetTotalLength 45, CopyToBuffer 46.
        private MediaInterface SampleShape => _sampleShape ??= new(
            "IMFSample", [IidSample, IidAttributes], 47, () => _attributes.Build(new()
            {
                [33] = ("GetSampleFlags", () => WithSample(s => { _com.W32(Arg(1), s.Flags); Return(MediaCom.SOk); })),
                [34] = ("SetSampleFlags", () => WithSample(s => { s.Flags = (uint)Arg(1); Return(MediaCom.SOk); })),
                [35] = ("GetSampleTime", () => WithSample(s =>
                {
                    if (s.Time is not { } t)
                    {
                        Return(MfENoSampleTimestamp);
                        return;
                    }

                    _com.W64(Arg(1), (ulong)t);
                    Return(MediaCom.SOk);
                })),
                [36] = ("SetSampleTime", () => WithSample(s => { s.Time = Pair(2); Return(MediaCom.SOk); })),
                [37] = ("GetSampleDuration", () => WithSample(s =>
                {
                    if (s.Duration is not { } d)
                    {
                        Return(MfENoSampleDuration);
                        return;
                    }

                    _com.W64(Arg(1), (ulong)d);
                    Return(MediaCom.SOk);
                })),
                [38] = ("SetSampleDuration", () => WithSample(s => { s.Duration = Pair(2); Return(MediaCom.SOk); })),
                [39] = ("GetBufferCount", () => WithSample(s => { _com.W32(Arg(1), s.Buffers.Count); Return(MediaCom.SOk); })),
                [40] = ("GetBufferByIndex", () => WithSample(s =>
                {
                    if (Arg(1) >= s.Buffers.Count)
                    {
                        _com.W32(Arg(2), 0);
                        Return(MediaCom.EInvalidArg);
                        return;
                    }

                    long b = s.Buffers[(int)Arg(1)];
                    Buffer(b)!.Refs++;
                    _com.W32(Arg(2), b);
                    Return(MediaCom.SOk);
                })),
                [41] = ("ConvertToContiguousBuffer", () => WithSample(ConvertToContiguous)),
                [42] = ("AddBuffer", () => WithSample(s =>
                {
                    MfBuffer? b = Buffer(Arg(1));
                    if (b is null)
                    {
                        if (Arg(1) != 0)
                        {
                            _com.Record("IMFSample::AddBuffer of an IMFMediaBuffer not created here -> E_NOTIMPL");
                        }

                        Return(Arg(1) == 0 ? MediaCom.EPointer : MediaCom.ENotImpl);
                        return;
                    }

                    b.Refs++;
                    s.Buffers.Add(Arg(1));
                    Return(MediaCom.SOk);
                })),
                [43] = ("RemoveBufferByIndex", () => WithSample(s =>
                {
                    if (Arg(1) >= s.Buffers.Count)
                    {
                        Return(MediaCom.EInvalidArg);
                        return;
                    }

                    long b = s.Buffers[(int)Arg(1)];
                    s.Buffers.RemoveAt((int)Arg(1));
                    List<GuestCall> calls = new();
                    _com.ReleaseAny(b, calls);
                    _com.Then(calls, _ => Return(MediaCom.SOk));
                })),
                [44] = ("RemoveAllBuffers", () => WithSample(s =>
                {
                    List<GuestCall> calls = new();
                    foreach (long b in s.Buffers)
                    {
                        _com.ReleaseAny(b, calls);
                    }

                    s.Buffers.Clear();
                    _com.Then(calls, _ => Return(MediaCom.SOk));
                })),
                [45] = ("GetTotalLength", () => WithSample(s =>
                {
                    _com.W32(Arg(1), s.Buffers.Sum(b => Buffer(b)?.Current ?? 0));
                    Return(MediaCom.SOk);
                })),
                [46] = ("CopyToBuffer", () => WithSample(s =>
                {
                    MfBuffer? dest = Buffer(Arg(1));
                    if (dest is null)
                    {
                        Return(MediaCom.EInvalidArg);
                        return;
                    }

                    int total = s.Buffers.Sum(b => Buffer(b)?.Current ?? 0);
                    if (total > dest.Max)
                    {
                        Return(MfEBufferTooSmall);
                        return;
                    }

                    long at = dest.Data;
                    foreach (MfBuffer b in s.Buffers.Select(Buffer).OfType<MfBuffer>())
                    {
                        _e.WriteMemory(at, _e.ReadMemory(b.Data, b.Current));
                        at += b.Current;
                    }

                    dest.Current = total;
                    Return(MediaCom.SOk);
                })),
            }));

        private long Pair(int first) => (long)((ulong)(uint)Arg(first) | ((ulong)(uint)Arg(first + 1) << 32));

        private void WithSample(Action<MfSample> act)
        {
            if (_com.Self<MfSample>() is { } s)
            {
                act(s);
            }
            else
            {
                Return(MediaCom.EPointer);
            }
        }

        private void ConvertToContiguous(MfSample s)
        {
            if (Arg(1) == 0)
            {
                Return(MediaCom.EPointer);
                return;
            }

            if (s.Buffers.Count == 0)
            {
                _com.W32(Arg(1), 0);
                Return(MediaCom.EUnexpected);
                return;
            }

            if (s.Buffers.Count == 1)
            {
                Buffer(s.Buffers[0])!.Refs++;
                _com.W32(Arg(1), s.Buffers[0]);
                Return(MediaCom.SOk);
                return;
            }

            int total = s.Buffers.Sum(b => Buffer(b)?.Current ?? 0);
            long merged = CreateMemoryBuffer(total, 0);
            MfBuffer m = Buffer(merged)!;
            long at = m.Data;
            List<GuestCall> calls = new();
            foreach (long p in s.Buffers)
            {
                MfBuffer b = Buffer(p)!;
                _e.WriteMemory(at, _e.ReadMemory(b.Data, b.Current));
                at += b.Current;
                _com.ReleaseAny(p, calls);
            }

            m.Current = total;
            s.Buffers.Clear();
            s.Buffers.Add(merged);
            m.Refs++; // one for the sample, one for the caller
            _com.W32(Arg(1), merged);
            _com.Then(calls, _ => Return(MediaCom.SOk));
        }

        // ------------------------------------------------------------------------------
        // Collections
        // ------------------------------------------------------------------------------

        private sealed class MfCollection : MediaObject
        {
            public override string Kind => "IMFCollection";

            public readonly List<long> Elements = new();

            public override IEnumerable<long> Destroy() => Elements.Where(e => e != 0).ToList();
        }

        private MediaInterface? _collectionShape;

        // IMFCollection: GetElementCount 3, GetElement 4, AddElement 5, RemoveElement 6,
        // InsertElementAt 7, RemoveAllElements 8. Elements may be NULL.
        private MediaInterface CollectionShape => _collectionShape ??= new("IMFCollection", [IidCollection], 9, () => new()
        {
            [3] = ("GetElementCount", () => WithCollection(c => { _com.W32(Arg(1), c.Elements.Count); Return(MediaCom.SOk); })),
            [4] = ("GetElement", () => WithCollection(c =>
            {
                if (Arg(1) >= c.Elements.Count)
                {
                    _com.W32(Arg(2), 0);
                    Return(MediaCom.EInvalidArg);
                    return;
                }

                long element = c.Elements[(int)Arg(1)];
                _com.W32(Arg(2), element);
                List<GuestCall> calls = new();
                _com.AddRefAny(element, calls);

                // A NULL element is legal, and reported as E_INVALIDARG with a NULL out-pointer.
                _com.Then(calls, _ => Return(element == 0 ? MediaCom.EInvalidArg : MediaCom.SOk));
            })),
            [5] = ("AddElement", () => WithCollection(c =>
            {
                List<GuestCall> calls = new();
                _com.AddRefAny(Arg(1), calls);
                c.Elements.Add(Arg(1));
                _com.Then(calls, _ => Return(MediaCom.SOk));
            })),
            [6] = ("RemoveElement", () => WithCollection(c =>
            {
                if (Arg(1) >= c.Elements.Count)
                {
                    Return(MediaCom.EInvalidArg);
                    return;
                }

                long element = c.Elements[(int)Arg(1)];
                c.Elements.RemoveAt((int)Arg(1));

                // The caller's out-pointer inherits the collection's reference.
                _com.W32(Arg(2), element);
                Return(MediaCom.SOk);
            })),
            [7] = ("InsertElementAt", () => WithCollection(c =>
            {
                if (Arg(1) > c.Elements.Count)
                {
                    Return(MediaCom.EInvalidArg);
                    return;
                }

                List<GuestCall> calls = new();
                _com.AddRefAny(Arg(2), calls);
                c.Elements.Insert((int)Arg(1), Arg(2));
                _com.Then(calls, _ => Return(MediaCom.SOk));
            })),
            [8] = ("RemoveAllElements", () => WithCollection(c =>
            {
                List<GuestCall> calls = new();
                foreach (long element in c.Elements)
                {
                    _com.ReleaseAny(element, calls);
                }

                c.Elements.Clear();
                _com.Then(calls, _ => Return(MediaCom.SOk));
            })),
        });

        private void WithCollection(Action<MfCollection> act)
        {
            if (_com.Self<MfCollection>() is { } c)
            {
                act(c);
            }
            else
            {
                Return(MediaCom.EPointer);
            }
        }
    }
}
