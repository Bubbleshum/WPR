namespace WPR.Wp8Native
{
    /// <summary>
    /// IMFMediaEngine, created through CoCreateInstanceFromApp(CLSID_MFMediaEngineClassFactory):
    /// the HTML5-media-element-shaped player WP8 games use for cutscenes and music.
    /// </summary>
    /// <remarks>
    /// <para>It plays audio through the host's audio output, with the documented event sequence,
    /// clock, pause, seek, loop and volume: uncompressed RIFF/WAVE decoded here, and compressed
    /// audio (MP3 above all - what the library's media-engine users ship) through the platform's
    /// <see cref="IMediaDecoder"/> when the host has one. Sources come from a URL or from an
    /// IMFByteStream made by <c>MFCreateMFByteStreamOnStream(Ex)</c>.</para>
    /// <para>Movies (ASF or MP4 containers) play when the host registers a video decoder
    /// (<see cref="VideoDecoder"/>): frames through OnVideoStreamTick/TransferVideoFrame, the
    /// soundtrack as the source's audio. Without one, a source with a video stream, and any
    /// audio no decoder takes, fails as the element does when it cannot play a source:
    /// MF_MEDIA_ENGINE_EVENT_ERROR with MF_MEDIA_ENGINE_ERR_SRC_NOT_SUPPORTED and the HRESULT,
    /// delivered asynchronously through the game's IMFMediaEngineNotify. A game written for a
    /// phone that cannot play its intro movie skips it; one told the movie played - or handed its
    /// soundtrack with no picture - waits for frames that never come.</para>
    /// </remarks>
    internal sealed class MediaEngine
    {
        public static readonly Guid ClsidClassFactory = new("b44392da-499b-446b-a4cb-005fead0e6d5");
        public static readonly Guid IidClassFactory = new("4d645ace-26aa-4688-9be1-df3516990b93");
        public static readonly Guid IidEngine = new("98a1b0bb-03eb-4935-ae7c-93c1fa0e1c93");
        public static readonly Guid IidEngineEx = new("83015ead-b1e6-40d0-a98a-37145ffe1ad1");
        public static readonly Guid IidError = new("fc0e10d2-ab2a-4501-a951-06bb1075184c");
        public static readonly Guid IidTimeRange = new("db71a2fc-078a-414e-9df9-8c2531b0aa6c");
        public static readonly Guid AttrCallback = new("c60381b8-83a4-41f8-a3d0-de05076849a9");

        public const int EventLoadStart = 1, EventAbort = 4, EventError = 5, EventEmptied = 6, EventPlay = 8,
            EventPause = 9, EventLoadedMetadata = 10, EventLoadedData = 11, EventPlaying = 13, EventCanPlay = 14,
            EventCanPlayThrough = 15, EventSeeking = 16, EventSeeked = 17, EventTimeUpdate = 18, EventEnded = 19,
            EventRateChange = 20, EventDurationChange = 21, EventVolumeChange = 22, EventBalanceChange = 1003;

        public const int ErrSrcNotSupported = 4;
        private const long FileNotFound = unchecked((int)0x80070002);

        private readonly MediaCom _com;
        private readonly ArmEmulator _e;
        private readonly string _packageRoot;
        private readonly Func<IAudioOutput?> _output;
        private readonly Func<IMediaDecoder?> _decoder;
        private readonly MediaFoundation? _mf;
        private readonly List<Engine> _engines = new();

        public MediaEngine(MediaCom com, string packageRoot, Func<IAudioOutput?> output, Func<IMediaDecoder?>? decoder = null, MediaFoundation? mediaFoundation = null)
        {
            _com = com;
            _e = com.Emulator;
            _packageRoot = packageRoot;
            _output = output;
            _decoder = decoder ?? (() => null);
            _mf = mediaFoundation;
        }

        /// <summary>Sources decoded by the platform decoder - for the report and the tests.</summary>
        public List<string> Decoded { get; } = new();

        /// <summary>
        /// The platform's movie decoder, read when a source loads; null (the default) plays no video,
        /// and a source with video then fails as one the platform cannot play.
        /// </summary>
        public Func<IMediaVideoDecoder?> VideoDecoder { get; init; } = () => null;

        /// <summary>How long a load may wait for a movie's soundtrack to finish decoding.</summary>
        public TimeSpan SoundtrackTimeout { get; init; } = TimeSpan.FromSeconds(30);

        private long Arg(int i) => _com.Arg(i);

        private void Return(long v) => _com.Return(v);

        /// <summary>Events queued to games' IMFMediaEngineNotify, in order - for the report and the tests.</summary>
        public List<string> EventsFired { get; } = new();

        // ------------------------------------------------------------------------------
        // Activation
        // ------------------------------------------------------------------------------

        private sealed class Factory : MediaObject
        {
            public override string Kind => "IMFMediaEngineClassFactory";
        }

        private MediaInterface? _factoryShape;

        // IMFMediaEngineClassFactory: CreateInstance 3, CreateTimeRange 4, CreateError 5.
        private MediaInterface FactoryShape => _factoryShape ??= new("IMFMediaEngineClassFactory", [IidClassFactory], 6, () => new()
        {
            [3] = ("CreateInstance", CreateInstance),
            [4] = ("CreateTimeRange", () => { _com.W32(Arg(1), NewTimeRange([])); Return(Arg(1) == 0 ? MediaCom.EPointer : MediaCom.SOk); }),
            [5] = ("CreateError", () => { _com.W32(Arg(1), NewError(0, 0)); Return(Arg(1) == 0 ? MediaCom.EPointer : MediaCom.SOk); }),
        });

        public long CreateFactory() => _com.Expose(new Factory(), FactoryShape);

        /// <summary>
        /// CoCreateInstanceFromApp(rclsid, punkOuter, dwClsCtx, reserved, dwCount, MULTI_QI*), for the
        /// class factory only; false (nothing touched) for any other class.
        /// </summary>
        public bool TryCoCreateInstanceFromApp()
        {
            if (_com.ReadGuid(Arg(0)) != ClsidClassFactory)
            {
                return false;
            }

            long count = Arg(4), results = Arg(5);
            if (Arg(1) != 0)
            {
                Return(MediaCom.NoAggregation);
                return true;
            }

            if (count == 0 || results == 0)
            {
                Return(MediaCom.EInvalidArg);
                return true;
            }

            MediaObject factory = _com.Lookup(CreateFactory())!;
            int answered = 0;
            for (int i = 0; i < count; i++)
            {
                long entry = results + (i * 12);
                long found = _com.QueryHost(factory, _com.ReadGuid(_com.U32(entry)));
                _com.W32(entry + 4, found);
                _com.W32(entry + 8, found == 0 ? MediaCom.ENoInterface : MediaCom.SOk);
                if (found != 0)
                {
                    factory.Refs++;
                    answered++;
                }
            }

            factory.Refs--; // the creation reference; every MULTI_QI entry holds its own
            Return(answered == count ? MediaCom.SOk : answered > 0 ? 0x00080012 /* CO_S_NOTALLINTERFACES */ : MediaCom.ENoInterface);
            return true;
        }

        // ------------------------------------------------------------------------------
        // The engine
        // ------------------------------------------------------------------------------

        private sealed class Engine : MediaObject
        {
            public override string Kind => "IMFMediaEngine";

            public long Notify;
            public bool Shut;
            public string? Source;
            public int NetworkState;          // NETWORK_EMPTY
            public int ReadyState;            // HAVE_NOTHING
            public int Preload;
            public bool Paused = true;
            public bool Ended;
            public bool AutoPlay;
            public bool Loop;
            public bool Muted;
            public double Volume = 1;
            public double Balance;
            public double DefaultRate = 1;
            public int ErrorCode;
            public long ErrorHResult;
            public uint StreamCategory;
            public uint EndpointRole;
            public bool RealTime;

            public PcmAudio.Wave? Wave;
            public IAudioVoiceOutput? Voice;
            public int StartFrame;
            public int CompletedAtStart;
            public readonly List<int> Chunks = new();
            public double SimulatedTime;
            public double LastTimeUpdate;

            // A movie: its frames come from here, its soundtrack is Wave like any other source.
            public IMediaVideoStream? Video;
            public byte[]? Frame;
            public bool HaveFrame;
            public string? TemporaryFile;

            // The voice's clock only moves a chunk at a time (~90 ms); a movie's frames need finer.
            public int ClockChunks = -1;
            public long ClockStamp;
            public long ClockLogged;
            public int TickCalls;
            public int FramesHanded;

            public override IEnumerable<long> Destroy()
            {
                Voice?.Dispose();
                Voice = null;
                CloseVideo(this);
                return Notify == 0 ? [] : [Notify];
            }
        }

        private MediaInterface? _engineShape;

        private void CreateInstance()
        {
            // CreateInstance(DWORD dwFlags, IMFAttributes* pAttr, IMFMediaEngine** ppPlayer).
            long outPointer = Arg(3);
            if (outPointer == 0)
            {
                Return(MediaCom.EPointer);
                return;
            }

            _com.W32(outPointer, 0);
            MfAttributeStore? attributes = (_com.Lookup(Arg(2)) as MfAttributedObject)?.Attributes;
            if (attributes is null)
            {
                if (Arg(2) != 0)
                {
                    _com.Record("IMFMediaEngineClassFactory::CreateInstance with an IMFAttributes not created here -> E_NOTIMPL");
                }

                Return(Arg(2) == 0 ? MediaCom.EPointer : MediaCom.ENotImpl);
                return;
            }

            long notify = attributes.UnknownOf(AttrCallback);
            if (notify == 0)
            {
                // MF_MEDIA_ENGINE_CALLBACK is required.
                Return(MediaCom.EInvalidArg);
                return;
            }

            Engine engine = new() { Notify = notify };
            List<GuestCall> calls = new();
            _com.AddRefAny(notify, calls);
            _engines.Add(engine);
            long pointer = _com.Expose(engine, EngineShape);
            _com.W32(outPointer, pointer);
            _com.Then(calls, _ => Return(MediaCom.SOk));
        }

        private void Fire(Engine engine, int evt, long param1 = 0, long param2 = 0)
        {
            if (engine.Shut || engine.Dead || _com.GuestMethod(engine.Notify, 3) is not { } fn)
            {
                return;
            }

            EventsFired.Add(evt == EventError ? $"ERROR({param1}, 0x{param2 & 0xFFFFFFFF:X8})" : EventName(evt));
            _e.QueueDeferredCall("IMFMediaEngineNotify::EventNotify", fn, engine.Notify, evt, param1 & 0xFFFFFFFF, param2 & 0xFFFFFFFF);
        }

        private static string EventName(int evt) => evt switch
        {
            EventLoadStart => "LOADSTART",
            EventAbort => "ABORT",
            EventEmptied => "EMPTIED",
            EventPlay => "PLAY",
            EventPause => "PAUSE",
            EventLoadedMetadata => "LOADEDMETADATA",
            EventLoadedData => "LOADEDDATA",
            EventPlaying => "PLAYING",
            EventCanPlay => "CANPLAY",
            EventCanPlayThrough => "CANPLAYTHROUGH",
            EventSeeking => "SEEKING",
            EventSeeked => "SEEKED",
            EventTimeUpdate => "TIMEUPDATE",
            EventEnded => "ENDED",
            EventRateChange => "RATECHANGE",
            EventDurationChange => "DURATIONCHANGE",
            EventVolumeChange => "VOLUMECHANGE",
            EventBalanceChange => "BALANCECHANGE",
            _ => evt.ToString(),
        };

        private void With(Action<Engine> act)
        {
            Engine? engine = _com.Self<Engine>();
            if (engine is null)
            {
                Return(MediaCom.EPointer);
            }
            else if (engine.Shut)
            {
                Return(MediaFoundation.MfEShutdown);
            }
            else
            {
                act(engine);
            }
        }

        private void Value(Func<Engine, long> read) => With(e => Return(read(e)));

        private void Real(Func<Engine, double> read)
        {
            Engine? engine = _com.Self<Engine>();
            _com.SetDouble(engine is null ? double.NaN : read(engine));
        }

        // IMFMediaEngine slots 3-44, then IMFMediaEngineEx 45-81.
        private MediaInterface EngineShape => _engineShape ??= new("IMFMediaEngineEx", [IidEngineEx, IidEngine], 82, () => new()
        {
            [3] = ("GetError", () => With(e =>
            {
                _com.W32(Arg(1), e.ErrorCode == 0 ? 0 : NewError(e.ErrorCode, e.ErrorHResult));
                Return(Arg(1) == 0 ? MediaCom.EPointer : MediaCom.SOk);
            })),
            [4] = ("SetErrorCode", () => With(e =>
            {
                if (Arg(1) > 5)
                {
                    Return(MediaCom.EInvalidArg);
                    return;
                }

                e.ErrorCode = (int)Arg(1);
                Return(MediaCom.SOk);
            })),
            [5] = ("SetSourceElements", () => With(e =>
            {
                _com.Record("IMFMediaEngine::SetSourceElements (source element lists) -> E_NOTIMPL");
                Return(MediaCom.ENotImpl);
            })),
            [6] = ("SetSource", () => With(e =>
            {
                string? url = Arg(1) == 0 ? null : _com.ReadWide(Arg(1));
                SetSource(e, url, data: null);
                Return(MediaCom.SOk);
            })),
            [7] = ("GetCurrentSource", () => With(e =>
            {
                _com.W32(Arg(1), e.Source is null ? 0 : _com.AllocBstr(e.Source));
                Return(Arg(1) == 0 ? MediaCom.EPointer : MediaCom.SOk);
            })),
            [8] = ("GetNetworkState", () => Value(e => e.NetworkState)),
            [9] = ("GetPreload", () => Value(e => e.Preload)),
            [10] = ("SetPreload", () => With(e => { e.Preload = (int)Arg(1); Return(MediaCom.SOk); })),
            [11] = ("GetBuffered", () => With(e => OutRange(e.Wave is null ? [] : [(0, e.Wave.Duration)]))),
            [12] = ("Load", () => With(e => { SetSource(e, e.Source, data: null); Return(MediaCom.SOk); })),

            // CanPlayType(BSTR type, MF_MEDIA_ENGINE_CANPLAY*): WAVE, audio the platform decoder
            // takes, and the movie containers WP8 titles ship when the platform decodes video.
            [13] = ("CanPlayType", () => With(e =>
            {
                string type = MediaContainer.MimeBase(_com.ReadWide(Arg(1)));
                bool playable = MediaContainer.IsWaveMime(type) ||
                                (type.StartsWith("audio/", StringComparison.Ordinal) && _decoder()?.CanDecode(type) == true) ||
                                (type is "video/mp4" or "video/x-ms-wmv" or "video/x-ms-asf" && VideoDecoder() is not null);
                _com.W32(Arg(2), playable ? 1 /* MAYBE */ : 0 /* NOT_SUPPORTED */);
                Return(Arg(2) == 0 ? MediaCom.EPointer : MediaCom.SOk);
            })),
            [14] = ("GetReadyState", () => Value(e => e.ReadyState)),
            [15] = ("IsSeeking", () => Value(_ => 0)),
            [16] = ("GetCurrentTime", () => Real(Position)),
            [17] = ("SetCurrentTime", () => With(e => Return(Seek(e, _com.DoubleArg(0))))),
            [18] = ("GetStartTime", () => Real(_ => 0)),
            [19] = ("GetDuration", () => Real(e => e.Wave?.Duration ?? double.NaN)),
            [20] = ("IsPaused", () => Value(e => e.Paused ? 1 : 0)),
            [21] = ("GetDefaultPlaybackRate", () => Real(e => e.DefaultRate)),
            [22] = ("SetDefaultPlaybackRate", () => With(e => Return(SetRate(e, _com.DoubleArg(0), isDefault: true)))),
            [23] = ("GetPlaybackRate", () => Real(_ => 1)),
            [24] = ("SetPlaybackRate", () => With(e => Return(SetRate(e, _com.DoubleArg(0), isDefault: false)))),
            [25] = ("GetPlayed", () => With(e => OutRange(Position(e) > 0 ? [(0, Position(e))] : []))),
            [26] = ("GetSeekable", () => With(e => OutRange(e.Wave is null ? [] : [(0, e.Wave.Duration)]))),
            [27] = ("IsEnded", () => Value(e => e.Ended ? 1 : 0)),
            [28] = ("GetAutoPlay", () => Value(e => e.AutoPlay ? 1 : 0)),
            [29] = ("SetAutoPlay", () => With(e => { e.AutoPlay = Arg(1) != 0; Return(MediaCom.SOk); })),
            [30] = ("GetLoop", () => Value(e => e.Loop ? 1 : 0)),
            [31] = ("SetLoop", () => With(e => { e.Loop = Arg(1) != 0; Return(MediaCom.SOk); })),
            [32] = ("Play", () => With(e => { Play(e); Return(MediaCom.SOk); })),
            [33] = ("Pause", () => With(e => { Pause(e); Return(MediaCom.SOk); })),
            [34] = ("GetMuted", () => Value(e => e.Muted ? 1 : 0)),
            [35] = ("SetMuted", () => With(e =>
            {
                e.Muted = Arg(1) != 0;
                ApplyVolume(e);
                Fire(e, EventVolumeChange);
                Return(MediaCom.SOk);
            })),
            [36] = ("GetVolume", () => Real(e => e.Volume)),
            [37] = ("SetVolume", () => With(e =>
            {
                double volume = _com.DoubleArg(0);
                if (!(volume >= 0 && volume <= 1))
                {
                    Return(MediaCom.EInvalidArg);
                    return;
                }

                e.Volume = volume;
                ApplyVolume(e);
                Fire(e, EventVolumeChange);
                Return(MediaCom.SOk);
            })),
            [38] = ("HasVideo", () => Value(e => e.Video is null ? 0 : 1)),
            [39] = ("HasAudio", () => Value(e => e.Wave is null || (e.Video is not null && e.Video.AudioChannels == 0) ? 0 : 1)),

            // GetNativeVideoSize(DWORD* cx, DWORD* cy) / GetVideoAspectRatio(DWORD* cx, DWORD* cy):
            // the movie's frame size, and its picture aspect reduced; without video there is none.
            [40] = ("GetNativeVideoSize", () => With(e => VideoSize(e, reduce: false))),
            [41] = ("GetVideoAspectRatio", () => With(e => VideoSize(e, reduce: true))),
            [42] = ("Shutdown", () => With(e =>
            {
                StopVoice(e);
                CloseVideo(e);
                e.Shut = true;
                Return(MediaCom.SOk);
            })),

            // TransferVideoFrame(IUnknown* pDstSurf, const MFVideoNormalizedRect* pSrc,
            // const RECT* pDst, const MFARGB* pBorderClr): the current frame, scaled into the game's
            // texture.
            [43] = ("TransferVideoFrame", () => With(e =>
            {
                if (e.Video is null || e.Frame is null)
                {
                    Return(MediaFoundation.MfEInvalidRequest);
                    return;
                }

                if (Arg(1) == 0)
                {
                    Return(MediaCom.EPointer);
                    return;
                }

                bool written = _e.Direct3D.WriteVideoFrame(Arg(1), e.Frame, e.Video.Width, e.Video.Height, Arg(2), Arg(3), Arg(4));
                Return(written ? MediaCom.SOk : MediaCom.EInvalidArg);
            })),

            // OnVideoStreamTick(LONGLONG* pPts): S_OK and the frame's time (100 ns) when a new frame
            // is ready for TransferVideoFrame, S_FALSE when there is none yet.
            [44] = ("OnVideoStreamTick", () => With(e =>
            {
                if (e.Video is null || e.Frame is null)
                {
                    Return(MediaCom.SFalse);
                    return;
                }

                double at = Position(e);
                if (++e.TickCalls % 5000 == 0)
                {
                    System.Diagnostics.Trace.WriteLine($"[wpr-wp8] movie: OnVideoStreamTick #{e.TickCalls} at {at:0.00} s, {e.FramesHanded} frame(s) handed");
                }

                if (!e.Video.TryGetFrame(at, e.Frame))
                {
                    Return(MediaCom.SFalse);
                    return;
                }

                e.FramesHanded++;
                e.HaveFrame = true;
                if (Arg(1) != 0)
                {
                    _com.W64(Arg(1), (ulong)(long)(at * 10_000_000));
                }

                Return(MediaCom.SOk);
            })),

            // IMFMediaEngineEx.
            // SetSourceFromByteStream(IMFByteStream*, BSTR pURL): the stream is read whole, then
            // loaded as a file would be; the URL only names it. A game's own IMFByteStream cannot
            // be read here, and fails the load as any unplayable source does.
            [45] = ("SetSourceFromByteStream", () => With(e =>
            {
                string url = Arg(2) == 0 ? string.Empty : _com.ReadWide(Arg(2));
                if (Arg(1) == 0)
                {
                    Return(MediaCom.EPointer);
                    return;
                }

                if (_mf?.ByteStream(Arg(1)) is not { } stream)
                {
                    _com.Record($"IMFMediaEngineEx::SetSourceFromByteStream({url}): an IMFByteStream the game implemented itself cannot be read here -> load fails");
                    SetSourceFailed(e, url, MediaCom.ENotImpl);
                    Return(MediaCom.SOk);
                    return;
                }

                GuestOp op = new();
                byte[]? data = null;
                _com.Run(op, _mf.ReadAll(stream, op, bytes => data = bytes), () =>
                {
                    if (data is null)
                    {
                        SetSourceFailed(e, url, op.Hr);
                    }
                    else
                    {
                        SetSource(e, url, data);
                    }

                    Return(MediaCom.SOk);
                });
            })),
            [46] = ("GetStatistics", () => With(e => { _com.Record("IMFMediaEngineEx::GetStatistics -> E_NOTIMPL"); Return(MediaCom.ENotImpl); })),
            [47] = ("UpdateVideoStream", () => With(_ => Return(MediaFoundation.MfEInvalidRequest))),
            [48] = ("GetBalance", () => Real(e => e.Balance)),
            [49] = ("SetBalance", () => With(e =>
            {
                double balance = _com.DoubleArg(0);
                if (!(balance >= -1 && balance <= 1))
                {
                    Return(MediaCom.EInvalidArg);
                    return;
                }

                e.Balance = balance;
                Fire(e, EventBalanceChange);
                Return(MediaCom.SOk);
            })),
            [50] = ("IsPlaybackRateSupported", () => Value(_ => _com.DoubleArg(0) == 1 ? 1 : 0)),
            [51] = ("FrameStep", () => With(_ => Return(MediaFoundation.MfEInvalidRequest))),
            [52] = ("GetResourceCharacteristics", () => With(e =>
            {
                // MFMEDIASOURCE_CAN_SEEK | MFMEDIASOURCE_CAN_PAUSE for a loaded WAVE.
                if (e.Wave is null)
                {
                    Return(MediaFoundation.MfEInvalidRequest);
                    return;
                }

                _com.W32(Arg(1), 0x2 | 0x4);
                Return(MediaCom.SOk);
            })),
            [53] = ("GetPresentationAttribute", () => With(_ => Return(MfAttributeMethods.MfEAttributeNotFound))),
            [54] = ("GetNumberOfStreams", () => With(e =>
            {
                if (e.Wave is null)
                {
                    Return(MediaFoundation.MfEInvalidRequest);
                    return;
                }

                _com.W32(Arg(1), StreamCount(e));
                Return(MediaCom.SOk);
            })),
            [55] = ("GetStreamAttribute", () => With(_ => Return(MfAttributeMethods.MfEAttributeNotFound))),
            [56] = ("GetStreamSelection", () => With(e =>
            {
                if (e.Wave is null || Arg(1) >= StreamCount(e))
                {
                    Return(MediaCom.EInvalidArg);
                    return;
                }

                _com.W32(Arg(2), 1);
                Return(MediaCom.SOk);
            })),
            [57] = ("SetStreamSelection", () => With(e => Return(e.Wave is null || Arg(1) >= StreamCount(e) ? MediaCom.EInvalidArg : MediaCom.SOk))),
            [58] = ("ApplyStreamSelections", () => With(_ => Return(MediaCom.SOk))),
            [59] = ("IsProtected", () => With(_ => { _com.W32(Arg(1), 0); Return(MediaCom.SOk); })),
            [60] = ("InsertVideoEffect", () => With(_ => { _com.Record("IMFMediaEngineEx::InsertVideoEffect -> E_NOTIMPL"); Return(MediaCom.ENotImpl); })),
            [61] = ("InsertAudioEffect", () => With(_ => { _com.Record("IMFMediaEngineEx::InsertAudioEffect -> E_NOTIMPL"); Return(MediaCom.ENotImpl); })),
            [62] = ("RemoveAllEffects", () => With(_ => Return(MediaCom.SOk))),
            [63] = ("SetTimelineMarkerTimer", () => With(_ => { _com.Record("IMFMediaEngineEx::SetTimelineMarkerTimer -> E_NOTIMPL"); Return(MediaCom.ENotImpl); })),
            [64] = ("GetTimelineMarkerTimer", () => With(_ => { _com.W64(Arg(1), BitConverter.DoubleToUInt64Bits(double.NaN)); Return(MediaCom.SOk); })),
            [65] = ("CancelTimelineMarkerTimer", () => With(_ => Return(MediaCom.SOk))),
            [66] = ("IsStereo3D", () => Value(_ => 0)),
            [67] = ("GetStereo3DFramePackingMode", () => With(_ => Return(MediaFoundation.MfEInvalidRequest))),
            [68] = ("SetStereo3DFramePackingMode", () => With(_ => Return(MediaFoundation.MfEInvalidRequest))),
            [69] = ("GetStereo3DRenderMode", () => With(_ => Return(MediaFoundation.MfEInvalidRequest))),
            [70] = ("SetStereo3DRenderMode", () => With(_ => Return(MediaFoundation.MfEInvalidRequest))),
            [71] = ("EnableWindowlessSwapchainMode", () => With(_ => { _com.Record("IMFMediaEngineEx::EnableWindowlessSwapchainMode -> E_NOTIMPL"); Return(MediaCom.ENotImpl); })),
            [72] = ("GetVideoSwapchainHandle", () => With(_ => { _com.W32(Arg(1), 0); Return(MediaFoundation.MfEInvalidRequest); })),
            [73] = ("EnableHorizontalMirrorMode", () => With(_ => Return(MediaCom.SOk))),
            [74] = ("GetAudioStreamCategory", () => With(e => { _com.W32(Arg(1), e.StreamCategory); Return(MediaCom.SOk); })),
            [75] = ("SetAudioStreamCategory", () => With(e => { e.StreamCategory = (uint)Arg(1); Return(MediaCom.SOk); })),
            [76] = ("GetAudioEndpointRole", () => With(e => { _com.W32(Arg(1), e.EndpointRole); Return(MediaCom.SOk); })),
            [77] = ("SetAudioEndpointRole", () => With(e => { e.EndpointRole = (uint)Arg(1); Return(MediaCom.SOk); })),
            [78] = ("GetRealTimeMode", () => With(e => { _com.W32(Arg(1), e.RealTime ? 1 : 0); Return(MediaCom.SOk); })),
            [79] = ("SetRealTimeMode", () => With(e => { e.RealTime = Arg(1) != 0; Return(MediaCom.SOk); })),

            // SetCurrentTimeEx(double seekTime, MF_MEDIA_ENGINE_SEEK_MODE): the double is d0, the mode r1.
            [80] = ("SetCurrentTimeEx", () => With(e => Return(Seek(e, _com.DoubleArg(0))))),
            [81] = ("EnableTimeUpdateTimer", () => With(_ => Return(MediaCom.SOk))),
        });

        // ---- loading ----

        /// <summary>
        /// The file a media URL names, or null: ms-appx:/// (and relative paths, and the package's
        /// Install folder) resolve into the package; ms-appdata:///local/ and the app's Local folder
        /// into the local-data sandbox.
        /// </summary>
        public string? ResolveUrl(string url)
        {
            string path = url.Trim();
            string root = _packageRoot;
            if (path.StartsWith("ms-appdata:///local/", StringComparison.OrdinalIgnoreCase))
            {
                root = HostStubs.SandboxRoot;
                path = Uri.UnescapeDataString(path["ms-appdata:///local/".Length..]);
            }
            else
            {
                foreach (string prefix in new[] { "ms-appx:///", "ms-appx://", "file:///" })
                {
                    if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        path = Uri.UnescapeDataString(path[prefix.Length..]);
                        break;
                    }
                }
            }

            path = path.Replace('/', '\\');
            int install = path.IndexOf(@"\Install\", StringComparison.OrdinalIgnoreCase);
            int local = path.IndexOf(@"\Local\", StringComparison.OrdinalIgnoreCase);
            if (install >= 0)
            {
                path = path[(install + @"\Install\".Length)..];
            }
            else if (local >= 0 && path.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase))
            {
                root = HostStubs.SandboxRoot;
                path = path[(local + @"\Local\".Length)..];
            }

            path = path.TrimStart('\\');
            if (path.Length == 0 || path.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(path))
            {
                return null;
            }

            string full = Path.Combine(root, path.Replace('\\', Path.DirectorySeparatorChar));
            return File.Exists(full) ? full : null;
        }

        private void SetSource(Engine e, string? url, byte[]? data)
        {
            // The media element's load algorithm: anything playing is abandoned first.
            if (e.NetworkState is 2 or 1)
            {
                Fire(e, EventAbort);
            }

            if (e.NetworkState != 0)
            {
                Fire(e, EventEmptied);
            }

            StopVoice(e);
            CloseVideo(e);
            e.Wave = null;
            e.ReadyState = 0;
            e.Paused = true;
            e.Ended = false;
            e.ErrorCode = 0;
            e.ErrorHResult = 0;
            e.SimulatedTime = 0;
            e.StartFrame = 0;
            e.Source = url;
            if (url is null)
            {
                e.NetworkState = 0;
                return;
            }

            e.NetworkState = 2; // NETWORK_LOADING
            Fire(e, EventLoadStart);

            string? file = data is null ? ResolveUrl(url) : null;
            if (data is null && file is null)
            {
                FailLoad(e, url, FileNotFound);
                return;
            }

            byte[] bytes = data ?? File.ReadAllBytes(file!);
            string name = Path.GetFileName(file ?? url);
            PcmAudio.Wave? wave = PcmAudio.ReadWave(bytes) ?? OpenMovie(e, file, url, bytes, name) ?? Decode(bytes, name);
            if (wave is null)
            {
                FailLoad(e, url, MediaFoundation.MfEUnsupportedByteStreamType);
                return;
            }

            e.Wave = wave;
            e.NetworkState = 1; // NETWORK_IDLE: the whole file is in memory
            Fire(e, EventDurationChange);
            Fire(e, EventLoadedMetadata);
            e.ReadyState = 4;   // HAVE_ENOUGH_DATA
            Fire(e, EventLoadedData);
            Fire(e, EventCanPlay);
            Fire(e, EventCanPlayThrough);
            if (e.AutoPlay)
            {
                Play(e);
            }
        }

        /// <summary>
        /// A movie (an ASF or MP4 container the platform's video decoder opens): its frames are
        /// served through OnVideoStreamTick/TransferVideoFrame, and its soundtrack - decoded whole,
        /// padded with silence to the movie's length so the one clock covers both - is returned as
        /// the source's Wave. Null when there is no video decoder or it cannot open this, and the
        /// audio path then decides.
        /// </summary>
        /// <remarks>
        /// Refusing every movie, as this engine did, is correct for a game that skips an intro it
        /// cannot play - and wrong for one that cannot: Halo: Spartan Assault starts its logo movie,
        /// logs the error event and waits for ENDED or PAUSE, for ever.
        /// </remarks>
        private PcmAudio.Wave? OpenMovie(Engine e, string? file, string url, byte[] bytes, string name)
        {
            string container = MediaContainer.Sniff(bytes);
            if (container is not ("ASF (WMA/WMV)" or "MP4") || VideoDecoder() is not { } decoder)
            {
                return null;
            }

            // A byte-stream source is usually a package file the game opened itself. Handing the
            // decoder that file, not a temp copy, matters on a platform that plays only what was
            // prepared at install (Android: the sidecars beside the movie, since MediaCodec has
            // no WMV). A copy in the temp folder has no sidecar beside it.
            if (file is null && FindPackageMovie(url, bytes) is { } original)
            {
                System.Diagnostics.Trace.WriteLine($"[wpr-wp8] media: byte-stream source {name} is the package file {Path.GetRelativePath(_packageRoot, original)}");
                file = original;
            }

            string? temporary = null;
            if (file is null)
            {
                // A byte-stream source: the decoder reads paths, so it gets one.
                temporary = Path.Combine(Path.GetTempPath(), "wpr-wp8-media", $"{Guid.NewGuid():N}{(container == "MP4" ? ".mp4" : ".wmv")}");
                Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
                File.WriteAllBytes(temporary, bytes);
                file = temporary;
            }

            IMediaVideoStream? video = decoder.Open(file);
            if (video is null || video.Width <= 0 || video.Height <= 0)
            {
                video?.Dispose();
                DeleteQuietly(temporary);
                return null;
            }

            int channels = video.AudioChannels is 1 or 2 && video.AudioSampleRate > 0 ? video.AudioChannels : 0;
            int rate = channels > 0 ? video.AudioSampleRate : 44100;
            byte[] soundtrack = channels > 0 ? DrainSoundtrack(video) : [];
            int frameBytes = Math.Max(channels, 1) * 2;
            double audioSeconds = (double)(soundtrack.Length / frameBytes) / rate;

            // The clock runs to whichever is longer. A container that gives no length and a movie
            // with no sound still needs an end: the decoder's own end-of-video stops it (Tick).
            double seconds = Math.Max(video.DurationSeconds, audioSeconds);
            bool unknownLength = seconds <= 0;
            if (unknownLength)
            {
                seconds = 600;
            }

            long frames = (long)Math.Ceiling(seconds * rate);
            byte[] pcm = new byte[frames * frameBytes];
            Array.Copy(soundtrack, pcm, Math.Min(soundtrack.Length, pcm.Length));

            e.Video = video;
            e.TemporaryFile = temporary;
            e.Frame = new byte[video.Width * video.Height * 4];
            for (int i = 3; i < e.Frame.Length; i += 4)
            {
                e.Frame[i] = 0xFF;   // opaque black until the first frame arrives
            }

            e.HaveFrame = false;
            Decoded.Add($"{name} ({container}, video {video.Width}x{video.Height}, {seconds:0.0} s{(unknownLength ? " (length unknown)" : string.Empty)}, " +
                        $"{(channels == 0 ? "no sound" : $"{rate} Hz, {channels} ch")}, {decoder.Name})");
            return new PcmAudio.Wave(rate, Math.Max(channels, 1), pcm);
        }

        /// <summary>
        /// The package file a byte-stream source was read from, or null: first what its URL names
        /// (a game usually passes the file's own name), then any file of that name in the package,
        /// then any movie-sized file in the package with those exact bytes. Each candidate must
        /// match byte for byte, so a game's own generated stream is never mistaken for a file.
        /// </summary>
        internal string? FindPackageMovie(string url, byte[] bytes)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(url) && ResolveUrl(url) is { } named && SameBytes(named, bytes))
                {
                    return named;
                }

                string leaf = string.IsNullOrWhiteSpace(url) ? string.Empty : Path.GetFileName(url.Replace('\\', '/').TrimEnd('/'));
                string? byName = null;
                string? byContent = null;
                IEnumerable<string> candidates = new[] { _packageRoot, HostStubs.SandboxRoot }
                    .Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .SelectMany(r => Directory.EnumerateFiles(r, "*", SearchOption.AllDirectories));
                foreach (string candidate in candidates)
                {
                    string ext = Path.GetExtension(candidate);
                    bool movieExtension = ext.Equals(".wmv", StringComparison.OrdinalIgnoreCase)
                                          || ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
                                          || ext.Equals(".m4v", StringComparison.OrdinalIgnoreCase)
                                          || ext.Equals(".asf", StringComparison.OrdinalIgnoreCase)
                                          || ext.Equals(".3gp", StringComparison.OrdinalIgnoreCase);
                    bool sameName = leaf.Length > 0 && Path.GetFileName(candidate).Equals(leaf, StringComparison.OrdinalIgnoreCase);
                    if ((!movieExtension && !sameName) || new FileInfo(candidate).Length != bytes.Length)
                    {
                        continue;
                    }

                    if (sameName && byName is null && SameBytes(candidate, bytes))
                    {
                        byName = candidate;
                        break;
                    }

                    if (movieExtension && byContent is null && SameBytes(candidate, bytes))
                    {
                        byContent = candidate;
                    }
                }

                return byName ?? byContent;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static bool SameBytes(string path, byte[] bytes)
        {
            using FileStream s = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (s.Length != bytes.Length)
            {
                return false;
            }

            byte[] buffer = new byte[81920];
            int offset = 0;
            int read;
            while ((read = s.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (!buffer.AsSpan(0, read).SequenceEqual(bytes.AsSpan(offset, read)))
                {
                    return false;
                }

                offset += read;
            }

            return offset == bytes.Length;
        }

        /// <summary>The whole soundtrack, waiting for the decoder (bounded) as it fills.</summary>
        private byte[] DrainSoundtrack(IMediaVideoStream video)
        {
            using MemoryStream all = new();
            byte[] buffer = new byte[64 * 1024];
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed < SoundtrackTimeout)
            {
                int read = video.ReadAudio(buffer, 0, buffer.Length);
                if (read < 0)
                {
                    break;
                }

                if (read == 0)
                {
                    Thread.Sleep(1);
                    continue;
                }

                all.Write(buffer, 0, read);
            }

            return all.ToArray();
        }

        private static void CloseVideo(Engine e)
        {
            e.Video?.Dispose();
            e.Video = null;
            e.Frame = null;
            e.HaveFrame = false;
            DeleteQuietly(e.TemporaryFile);
            e.TemporaryFile = null;
        }

        private static void DeleteQuietly(string? path)
        {
            if (path is null)
            {
                return;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception)
            {
                // A temporary copy; the OS temp folder gets it eventually.
            }
        }

        private static long StreamCount(Engine e) => e.Video is null ? 1 : e.Video.AudioChannels > 0 ? 2 : 1;

        private void VideoSize(Engine e, bool reduce)
        {
            if (e.Video is null)
            {
                _com.W32(Arg(1), 0);
                _com.W32(Arg(2), 0);
                Return(MediaFoundation.MfEInvalidRequest);
                return;
            }

            long cx = e.Video.Width, cy = e.Video.Height;
            if (reduce)
            {
                long a = cx, b = cy;
                while (b != 0)
                {
                    (a, b) = (b, a % b);
                }

                cx /= a;
                cy /= a;
            }

            _com.W32(Arg(1), cx);
            _com.W32(Arg(2), cy);
            Return(MediaCom.SOk);
        }

        /// <summary>
        /// Compressed audio through the platform decoder; null (recorded, with the reason) when
        /// there is no decoder, it cannot decode this, or the source carries video.
        /// </summary>
        private PcmAudio.Wave? Decode(byte[] bytes, string name)
        {
            string container = MediaContainer.Sniff(bytes);
            IMediaDecoder? decoder = _decoder();
            if (decoder is null)
            {
                _com.Record($"IMFMediaEngine source {name} ({container}): no decoder on this platform (only uncompressed WAVE) -> MF_MEDIA_ENGINE_ERR_SRC_NOT_SUPPORTED");
                return null;
            }

            DecodedAudio? decoded;
            try
            {
                decoded = decoder.Decode(bytes, name);
            }
            catch (Exception ex)
            {
                // The contract says a decoder does not throw; one that does costs the source, never the game.
                _com.Record($"IMFMediaEngine source {name} ({container}): {decoder.Name} threw {ex.GetType().Name} -> MF_MEDIA_ENGINE_ERR_SRC_NOT_SUPPORTED");
                return null;
            }

            if (decoded is { HasVideo: true })
            {
                _com.Record($"IMFMediaEngine source {name} ({container}): has a video stream, and video is not decoded here -> MF_MEDIA_ENGINE_ERR_SRC_NOT_SUPPORTED");
                return null;
            }

            if (decoded is null || decoded.SampleRate <= 0 || decoded.Channels is not (1 or 2) ||
                decoded.Pcm16.Length == 0 || decoded.Pcm16.Length % (decoded.Channels * 2) != 0)
            {
                _com.Record($"IMFMediaEngine source {name} ({container}): {decoder.Name} found no audio it could decode -> MF_MEDIA_ENGINE_ERR_SRC_NOT_SUPPORTED");
                return null;
            }

            Decoded.Add($"{name} ({container}, {decoded.SampleRate} Hz, {decoded.Channels} ch)");
            return new PcmAudio.Wave(decoded.SampleRate, decoded.Channels, decoded.Pcm16);
        }

        /// <summary>The load algorithm for a source that cannot even be read: LOADSTART, then the error.</summary>
        private void SetSourceFailed(Engine e, string url, long hr)
        {
            SetSource(e, null, data: null);
            e.NetworkState = 2;
            Fire(e, EventLoadStart);
            FailLoad(e, url, hr);
        }

        private void FailLoad(Engine e, string url, long hr)
        {
            System.Diagnostics.Trace.WriteLine($"[wpr-wp8] media engine: source {url} failed 0x{hr & 0xFFFFFFFF:X8} -> MF_MEDIA_ENGINE_ERR_SRC_NOT_SUPPORTED");
            StopVoice(e);
            e.Source = url;
            e.Wave = null;
            e.ReadyState = 0;
            e.NetworkState = 3; // NETWORK_NO_SOURCE
            e.ErrorCode = ErrSrcNotSupported;
            e.ErrorHResult = hr;
            Fire(e, EventError, ErrSrcNotSupported, hr);
        }

        // ---- playback ----

        private const int ChunkFrames = 4096;

        private double Position(Engine e)
        {
            if (e.Wave is null)
            {
                return 0;
            }

            int frames = e.StartFrame;
            if (e.Voice is { } voice)
            {
                int done = Math.Clamp(voice.Completed - e.CompletedAtStart, 0, e.Chunks.Count);
                for (int i = 0; i < done; i++)
                {
                    frames += e.Chunks[i];
                }

                double seconds = (double)frames / e.Wave.SampleRate;

                // A movie's frames need a finer clock than a chunk (~90 ms): interpolate across the
                // chunk now playing by real time, never past its end.
                if (e.Video is not null && !e.Paused && done < e.Chunks.Count)
                {
                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (done != e.ClockChunks)
                    {
                        e.ClockChunks = done;
                        e.ClockStamp = now;
                    }

                    double into = (double)(now - e.ClockStamp) / System.Diagnostics.Stopwatch.Frequency;
                    seconds += Math.Min(into, (double)e.Chunks[done] / e.Wave.SampleRate);
                }

                return Math.Min(seconds, e.Wave.Duration);
            }

            return Math.Min(e.SimulatedTime, e.Wave.Duration);
        }

        private void Play(Engine e)
        {
            if (e.Source is null && e.NetworkState == 0)
            {
                return;
            }

            if (e.Ended && !e.Loop)
            {
                StartAt(e, 0);
                e.Ended = false;
            }

            if (!e.Paused)
            {
                return;
            }

            e.Paused = false;
            Fire(e, EventPlay);
            if (e.Wave is not null)
            {
                if (e.Voice is null)
                {
                    StartAt(e, (int)(Position(e) * e.Wave.SampleRate));
                }

                e.Voice?.Play();
                Fire(e, EventPlaying);
            }
        }

        private void Pause(Engine e)
        {
            if (e.Paused)
            {
                return;
            }

            e.Paused = true;
            e.Voice?.Pause();
            Fire(e, EventTimeUpdate);
            Fire(e, EventPause);
        }

        private long Seek(Engine e, double seconds)
        {
            if (e.Wave is null || e.ReadyState == 0)
            {
                return MediaFoundation.MfEInvalidRequest;
            }

            if (double.IsNaN(seconds))
            {
                return MediaCom.EInvalidArg;
            }

            double target = Math.Clamp(seconds, 0, e.Wave.Duration);
            Fire(e, EventSeeking);
            e.Ended = false;
            StartAt(e, (int)(target * e.Wave.SampleRate));
            if (!e.Paused)
            {
                e.Voice?.Play();
            }

            Fire(e, EventTimeUpdate);
            Fire(e, EventSeeked);
            return MediaCom.SOk;
        }

        private static long SetRate(Engine e, double rate, bool isDefault)
        {
            // Changing the rate needs time-stretching, which this host does not do.
            if (rate != 1)
            {
                return unchecked((int)0xC00D36D0); // MF_E_UNSUPPORTED_RATE
            }

            if (isDefault)
            {
                e.DefaultRate = rate;
            }

            return MediaCom.SOk;
        }

        /// <summary>(Re)queues the file from <paramref name="frame"/> on a fresh or flushed voice, paused.</summary>
        private void StartAt(Engine e, int frame)
        {
            PcmAudio.Wave wave = e.Wave!;
            frame = Math.Clamp(frame, 0, wave.Frames);
            e.StartFrame = frame;
            e.ClockChunks = -1;
            e.SimulatedTime = (double)frame / wave.SampleRate;
            e.LastTimeUpdate = e.SimulatedTime;
            e.Chunks.Clear();

            if (e.Voice is null && _output() is { } output)
            {
                e.Voice = output.CreateVoice(wave.SampleRate, wave.Channels);
                ApplyVolume(e);
            }

            if (e.Voice is not { } voice)
            {
                return;
            }

            voice.Pause();
            voice.Flush();
            e.CompletedAtStart = voice.Completed;
            int bytesPerFrame = wave.Channels * 2;
            for (int at = frame; at < wave.Frames; at += ChunkFrames)
            {
                int count = Math.Min(ChunkFrames, wave.Frames - at);
                voice.Submit(wave.Pcm16.AsSpan(at * bytesPerFrame, count * bytesPerFrame).ToArray());
                e.Chunks.Add(count);
            }
        }

        private static void ApplyVolume(Engine e) => e.Voice?.SetVolume(e.Muted ? 0f : (float)e.Volume);

        private static void StopVoice(Engine e)
        {
            e.Voice?.Flush();
            e.Voice?.Dispose();
            e.Voice = null;
            e.Chunks.Clear();
        }

        /// <summary>
        /// Once a frame: advances a voiceless clock, raises TIMEUPDATE about every 250 ms of
        /// playback, and ends (or loops) a source that has played out.
        /// </summary>
        public void Tick(double frameSeconds = 1.0 / 60)
        {
            foreach (Engine e in _engines.ToArray())
            {
                if (e.Dead)
                {
                    _engines.Remove(e);
                    continue;
                }

                if (e.Shut || e.Paused || e.Ended || e.Wave is null)
                {
                    continue;
                }

                if (e.Voice is null)
                {
                    e.SimulatedTime += frameSeconds;
                }

                double position = Position(e);
                if (e.Video is not null)
                {
                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (now - e.ClockLogged > 2 * System.Diagnostics.Stopwatch.Frequency)
                    {
                        // A movie that never ends is otherwise silent: say where its clock is.
                        e.ClockLogged = now;
                        System.Diagnostics.Trace.WriteLine(
                            $"[wpr-wp8] movie clock: {position:0.00}/{e.Wave.Duration:0.00} s, chunks {(e.Voice is { } v ? v.Completed - e.CompletedAtStart : -1)}/{e.Chunks.Count}, " +
                            $"frames handed {e.FramesHanded} of {e.TickCalls} ticks, video ended={e.Video.VideoEnded}");
                    }
                }

                if (position >= e.Wave.Duration - 1e-9 || e.Video is { DurationSeconds: <= 0, VideoEnded: true })
                {
                    if (e.Loop)
                    {
                        StartAt(e, 0);
                        e.Voice?.Play();
                        Fire(e, EventTimeUpdate);
                        continue;
                    }

                    e.Ended = true;
                    StopVoice(e);
                    e.SimulatedTime = e.Wave.Duration;
                    e.StartFrame = e.Wave.Frames;
                    Fire(e, EventTimeUpdate);
                    Fire(e, EventEnded);
                    continue;
                }

                if (position - e.LastTimeUpdate >= 0.25)
                {
                    e.LastTimeUpdate = position;
                    Fire(e, EventTimeUpdate);
                }
            }
        }

        // ------------------------------------------------------------------------------
        // IMFMediaError and IMFMediaTimeRange
        // ------------------------------------------------------------------------------

        private sealed class MediaError : MediaObject
        {
            public override string Kind => "IMFMediaError";

            public int Code;
            public long HResult;
        }

        private MediaInterface? _errorShape;

        // IMFMediaError: GetErrorCode 3, GetExtendedErrorCode 4, SetErrorCode 5, SetExtendedErrorCode 6.
        private MediaInterface ErrorShape => _errorShape ??= new("IMFMediaError", [IidError], 7, () => new()
        {
            [3] = ("GetErrorCode", () => Return(_com.Self<MediaError>()?.Code ?? 0)),
            [4] = ("GetExtendedErrorCode", () => Return(_com.Self<MediaError>()?.HResult ?? 0)),
            [5] = ("SetErrorCode", () =>
            {
                if (_com.Self<MediaError>() is not { } err || Arg(1) > 5)
                {
                    Return(MediaCom.EInvalidArg);
                    return;
                }

                err.Code = (int)Arg(1);
                Return(MediaCom.SOk);
            }),
            [6] = ("SetExtendedErrorCode", () =>
            {
                if (_com.Self<MediaError>() is { } err)
                {
                    err.HResult = (int)(uint)Arg(1);
                }

                Return(MediaCom.SOk);
            }),
        });

        private long NewError(int code, long hr) => _com.Expose(new MediaError { Code = code, HResult = hr }, ErrorShape);

        private sealed class TimeRange : MediaObject
        {
            public override string Kind => "IMFMediaTimeRange";

            public readonly List<(double Start, double End)> Ranges = new();
        }

        private MediaInterface? _rangeShape;

        // IMFMediaTimeRange: GetLength 3, GetStart 4, GetEnd 5, ContainsTime 6, AddRange 7, Clear 8.
        private MediaInterface RangeShape => _rangeShape ??= new("IMFMediaTimeRange", [IidTimeRange], 9, () => new()
        {
            [3] = ("GetLength", () => Return(_com.Self<TimeRange>()?.Ranges.Count ?? 0)),
            [4] = ("GetStart", () => RangeEdge(r => r.Start)),
            [5] = ("GetEnd", () => RangeEdge(r => r.End)),
            [6] = ("ContainsTime", () =>
            {
                double t = _com.DoubleArg(0);
                Return(_com.Self<TimeRange>()?.Ranges.Any(r => t >= r.Start && t <= r.End) == true ? 1 : 0);
            }),

            // AddRange(double start, double end): d0, d1. Overlapping ranges merge, as the
            // normalised TimeRanges of the media element do.
            [7] = ("AddRange", () =>
            {
                double start = _com.DoubleArg(0), end = _com.DoubleArg(1);
                if (_com.Self<TimeRange>() is not { } r || !(start <= end))
                {
                    Return(MediaCom.EInvalidArg);
                    return;
                }

                r.Ranges.Add((start, end));
                Normalise(r.Ranges);
                Return(MediaCom.SOk);
            }),
            [8] = ("Clear", () => { _com.Self<TimeRange>()?.Ranges.Clear(); Return(MediaCom.SOk); }),
        });

        private void RangeEdge(Func<(double Start, double End), double> edge)
        {
            TimeRange? r = _com.Self<TimeRange>();
            if (r is null || Arg(1) >= r.Ranges.Count)
            {
                Return(MediaCom.EInvalidArg);
                return;
            }

            _com.W64(Arg(2), BitConverter.DoubleToUInt64Bits(edge(r.Ranges[(int)Arg(1)])));
            Return(MediaCom.SOk);
        }

        private static void Normalise(List<(double Start, double End)> ranges)
        {
            ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
            for (int i = 0; i + 1 < ranges.Count;)
            {
                if (ranges[i + 1].Start <= ranges[i].End)
                {
                    ranges[i] = (ranges[i].Start, Math.Max(ranges[i].End, ranges[i + 1].End));
                    ranges.RemoveAt(i + 1);
                }
                else
                {
                    i++;
                }
            }
        }

        private long NewTimeRange(IEnumerable<(double, double)> ranges)
        {
            TimeRange r = new();
            r.Ranges.AddRange(ranges);
            return _com.Expose(r, RangeShape);
        }

        private void OutRange(List<(double, double)> ranges)
        {
            if (Arg(1) == 0)
            {
                Return(MediaCom.EPointer);
                return;
            }

            _com.W32(Arg(1), NewTimeRange(ranges));
            Return(MediaCom.SOk);
        }
    }
}
