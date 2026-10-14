namespace WPR.Wp8Native
{
    /// <summary>
    /// The XAudio2_8.dll exports beyond XAudio2Create (ordinal 1). WP8's import library binds them
    /// by ordinal only, in the DLL's export order: 2 CreateAudioReverb, 3 CreateAudioVolumeMeter,
    /// 4 CreateFX, 5 X3DAudioCalculate, 6 X3DAudioInitialize.
    /// </summary>
    /// <remarks>
    /// X3DAudio is plain arithmetic and is implemented to its documented outputs: the
    /// distance-curve attenuation (inverse-square by default), listener and emitter cones,
    /// pairwise constant-power panning onto the speakers of the channel mask, inner-radius
    /// spread, interaural delay, LPF and reverb sends, Doppler factor and emitter angle.
    ///
    /// The effects are real XAPO COM objects - IXAPO and IXAPOParameters, with registration
    /// properties, format negotiation, Initialize and parameter round trips - which is all a
    /// game touches. Running them is the mixer's job (<see cref="XAudio2Runtime"/>), which does
    /// not apply effect chains, so <c>Process</c> is recorded if anything ever calls it.
    /// </remarks>
    internal sealed class X3DAudioLibrary
    {
        public static readonly Guid IidXapo = new("a90bc001-e897-e897-55e4-9e4700000000");
        public static readonly Guid IidXapoParameters = new("a90bc001-e897-e897-55e4-9e4700000001");
        public static readonly Guid ClsidAudioReverb = new("c2633b16-471b-4498-b8c5-4f0959e2ec09");
        public static readonly Guid ClsidVolumeMeter = new("4fc3b166-972a-40cf-bc37-7db03db2fba3");
        public static readonly Guid ClsidFxEq = new("f5e01117-d6c4-485a-a3f5-695196f3dbfa");
        public static readonly Guid ClsidFxMasteringLimiter = new("c4137916-2be1-46fd-8599-441536f49856");
        public static readonly Guid ClsidFxReverb = new("7d9aca56-cb68-4807-b632-b137352e8596");
        public static readonly Guid ClsidFxEcho = new("5039d740-f736-449a-84d3-a56202557b87");

        public const long XapoFormatUnsupported = unchecked((int)0x88970001);

        private const uint HandleMagic = 0x41443358; // "X3DA"

        private readonly MediaCom _com;
        private readonly ArmEmulator _e;

        public X3DAudioLibrary(MediaCom com)
        {
            _com = com;
            _e = com.Emulator;
        }

        private long Arg(int i) => _com.Arg(i);

        private void Return(long v) => _com.Return(v);

        /// <summary>Registers the ordinals under their qualified "dll!#n" names.</summary>
        public void RegisterInto(Dictionary<string, Action> handlers)
        {
            foreach (string dll in new[] { "XAudio2_8.dll", "xaudio2_8.dll", "XAUDIO2_8.DLL" })
            {
                handlers[$"{dll}!#2"] = () => CreateApo(ClsidAudioReverb, Arg(0), 0, 0);
                handlers[$"{dll}!#3"] = () => CreateApo(ClsidVolumeMeter, Arg(0), 0, 0);
                handlers[$"{dll}!#4"] = CreateFx;
                handlers[$"{dll}!#5"] = Calculate;
                handlers[$"{dll}!#6"] = Initialize;
            }

            handlers["CreateAudioReverb"] = handlers["XAudio2_8.dll!#2"];
            handlers["CreateAudioVolumeMeter"] = handlers["XAudio2_8.dll!#3"];
            handlers["CreateFX"] = CreateFx;
            handlers["X3DAudioCalculate"] = Calculate;
            handlers["X3DAudioInitialize"] = Initialize;
        }

        // ------------------------------------------------------------------------------
        // X3DAudio
        // ------------------------------------------------------------------------------

        /// <summary>
        /// HRESULT X3DAudioInitialize(UINT32 SpeakerChannelMask, FLOAT32 SpeedOfSound,
        /// X3DAUDIO_HANDLE Instance): hard-float, so the speed is s0 and the handle r1.
        /// </summary>
        private void Initialize()
        {
            uint mask = (uint)Arg(0);
            float speed = _com.FloatArg(0);
            long handle = Arg(1);
            if (handle == 0 || mask == 0 || !(speed >= float.Epsilon))
            {
                Return(MediaCom.EInvalidArg);
                return;
            }

            // X3DAUDIO_HANDLE is 20 opaque bytes; this layout is ours.
            _com.W32(handle, mask);
            _com.WFloat(handle + 4, speed);
            _com.W32(handle + 8, HandleMagic);
            _com.W32(handle + 12, System.Numerics.BitOperations.PopCount(mask));
            _com.W32(handle + 16, 0);
            Return(MediaCom.SOk);
        }

        private readonly record struct V3(float X, float Y, float Z)
        {
            public static V3 operator -(V3 a, V3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

            public static V3 operator +(V3 a, V3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

            public static V3 operator *(V3 a, float s) => new(a.X * s, a.Y * s, a.Z * s);

            public float Dot(V3 b) => (X * b.X) + (Y * b.Y) + (Z * b.Z);

            public V3 Cross(V3 b) => new((Y * b.Z) - (Z * b.Y), (Z * b.X) - (X * b.Z), (X * b.Y) - (Y * b.X));

            public float Length => MathF.Sqrt(Dot(this));

            public V3 Normal => Length > 1e-7f ? this * (1 / Length) : new V3(0, 0, 0);
        }

        private V3 Vec(long at) => new(_com.RFloat(at), _com.RFloat(at + 4), _com.RFloat(at + 8));

        /// <summary>The speakers of a channel mask, in output-channel order: azimuth (radians, clockwise), or null for LFE.</summary>
        private static List<float?> Speakers(uint mask)
        {
            float[] azimuths =
            [
                -MathF.PI / 4, MathF.PI / 4, 0, float.NaN, -3 * MathF.PI / 4, 3 * MathF.PI / 4,
                -MathF.PI / 8, MathF.PI / 8, MathF.PI, -MathF.PI / 2, MathF.PI / 2,
            ];
            List<float?> speakers = new();
            for (int bit = 0; bit < 18; bit++)
            {
                if ((mask & (1u << bit)) != 0)
                {
                    float az = bit < azimuths.Length ? azimuths[bit] : 0;
                    speakers.Add(float.IsNaN(az) ? null : az);
                }
            }

            return speakers;
        }

        /// <summary>A piecewise-linear X3DAUDIO_DISTANCE_CURVE at a normalised distance, or the default given.</summary>
        private float Curve(long curve, float distance, Func<float, float> fallback)
        {
            if (curve == 0)
            {
                return fallback(distance);
            }

            long points = _com.U32(curve);
            int count = (int)_com.U32(curve + 4);
            if (points == 0 || count <= 0)
            {
                return fallback(distance);
            }

            float lastD = _com.RFloat(points), lastV = _com.RFloat(points + 4);
            if (distance <= lastD)
            {
                return lastV;
            }

            for (int i = 1; i < count; i++)
            {
                float d = _com.RFloat(points + (i * 8)), v = _com.RFloat(points + (i * 8) + 4);
                if (distance <= d)
                {
                    return d <= lastD ? v : lastV + ((v - lastV) * (distance - lastD) / (d - lastD));
                }

                lastD = d;
                lastV = v;
            }

            return lastV;
        }

        private static float Linear(float d, float at0, float at1) => d >= 1 ? at1 : d <= 0 ? at0 : at0 + ((at1 - at0) * d);

        /// <summary>Cone scaling for an angle off the cone's axis: (volume, LPF, reverb).</summary>
        private (float Volume, float Lpf, float Reverb) Cone(long cone, float angle)
        {
            if (cone == 0)
            {
                return (1, 1, 1);
            }

            float inner = _com.RFloat(cone) / 2, outer = _com.RFloat(cone + 4) / 2;
            float t = angle <= inner ? 0 : angle >= outer ? 1 : (angle - inner) / Math.Max(outer - inner, 1e-6f);
            float Lerp(int offset) => _com.RFloat(cone + offset) + ((_com.RFloat(cone + offset + 4) - _com.RFloat(cone + offset)) * t);
            return (Lerp(8), Lerp(16), Lerp(24));
        }

        /// <summary>
        /// void X3DAudioCalculate(const X3DAUDIO_HANDLE, const X3DAUDIO_LISTENER*, const X3DAUDIO_EMITTER*,
        /// UINT32 Flags, X3DAUDIO_DSP_SETTINGS*).
        /// </summary>
        private void Calculate()
        {
            long handle = Arg(0), listener = Arg(1), emitter = Arg(2), dsp = Arg(4);
            uint flags = (uint)Arg(3);
            Return(0);
            if (handle == 0 || listener == 0 || emitter == 0 || dsp == 0 || _com.U32(handle + 8) != HandleMagic)
            {
                return;
            }

            uint mask = _com.U32(handle);
            float speedOfSound = _com.RFloat(handle + 4);

            // X3DAUDIO_LISTENER: OrientFront 0, OrientTop 12, Position 24, Velocity 36, pCone 48.
            V3 lFront = Vec(listener).Normal, lTop = Vec(listener + 12).Normal;
            V3 lPos = Vec(listener + 24), lVel = Vec(listener + 36);
            long lCone = _com.U32(listener + 48);
            V3 lRight = lTop.Cross(lFront).Normal;

            // X3DAUDIO_EMITTER: pCone 0, OrientFront 4, OrientTop 16, Position 28, Velocity 40,
            // InnerRadius 52, InnerRadiusAngle 56, ChannelCount 60, ChannelRadius 64,
            // pChannelAzimuths 68, pVolumeCurve 72, pLFECurve 76, pLPFDirectCurve 80,
            // pLPFReverbCurve 84, pReverbCurve 88, CurveDistanceScaler 92, DopplerScaler 96.
            long eCone = _com.U32(emitter);
            V3 eFront = Vec(emitter + 4).Normal, eTop = Vec(emitter + 16).Normal;
            V3 ePos = Vec(emitter + 28), eVel = Vec(emitter + 40);
            float innerRadius = _com.RFloat(emitter + 52);
            int channelCount = (int)_com.U32(emitter + 60);
            float channelRadius = _com.RFloat(emitter + 64);
            long azimuthsPointer = _com.U32(emitter + 68);
            float scaler = Math.Max(_com.RFloat(emitter + 92), float.Epsilon);
            float dopplerScaler = _com.RFloat(emitter + 96);
            V3 eRight = eTop.Cross(eFront).Normal;

            // X3DAUDIO_DSP_SETTINGS: pMatrixCoefficients 0, pDelayTimes 4, SrcChannelCount 8,
            // DstChannelCount 12, LPFDirectCoefficient 16, LPFReverbCoefficient 20, ReverbLevel 24,
            // DopplerFactor 28, EmitterToListenerAngle 32, EmitterToListenerDistance 36,
            // EmitterVelocityComponent 40, ListenerVelocityComponent 44.
            int srcCount = (int)_com.U32(dsp + 8), dstCount = (int)_com.U32(dsp + 12);

            V3 toEmitter = ePos - lPos;
            float distance = toEmitter.Length;
            V3 emitterToListener = (lPos - ePos).Normal;
            float normalised = distance / scaler;
            _com.WFloat(dsp + 36, distance);

            float listenerAngle = distance > 0 ? MathF.Acos(Math.Clamp(lFront.Dot(toEmitter.Normal), -1, 1)) : 0;
            float emitterAngle = distance > 0 ? MathF.Acos(Math.Clamp(eFront.Dot(emitterToListener), -1, 1)) : 0;
            var listenerCone = Cone(lCone, listenerAngle);
            var emitterCone = channelCount == 1 ? Cone(eCone, emitterAngle) : (1f, 1f, 1f);

            float volume = Curve(_com.U32(emitter + 72), normalised, d => d <= 1 ? 1 : 1 / d) * listenerCone.Volume * emitterCone.Item1;
            float lfe = Curve(_com.U32(emitter + 76), normalised, d => Linear(d, 1, 0));

            if ((flags & 0x1) != 0)
            {
                WriteMatrix(dsp, flags, mask, srcCount, dstCount, channelCount, channelRadius, azimuthsPointer, innerRadius,
                    ePos, eFront, eRight, lPos, lFront, lRight, lTop, volume, lfe);
            }

            if ((flags & 0x2) != 0 && _com.U32(dsp + 4) is var delays && delays != 0 && dstCount == 2)
            {
                // Interaural time difference: the far ear hears up to ~0.6 ms later.
                float side = distance > 0 ? toEmitter.Normal.Dot(lRight) : 0;
                const float MaxDelayMs = 0.6f;
                _com.WFloat(delays, side > 0 ? side * MaxDelayMs : 0);
                _com.WFloat(delays + 4, side < 0 ? -side * MaxDelayMs : 0);
            }

            if ((flags & 0x4) != 0)
            {
                float lpf = Curve(_com.U32(emitter + 80), normalised, d => Linear(d, 1, 0.75f));
                _com.WFloat(dsp + 16, lpf * listenerCone.Lpf * emitterCone.Item2);
            }

            if ((flags & 0x8) != 0)
            {
                _com.WFloat(dsp + 20, Curve(_com.U32(emitter + 84), normalised, _ => 0.75f) * listenerCone.Lpf);
            }

            if ((flags & 0x10) != 0)
            {
                _com.WFloat(dsp + 24, Curve(_com.U32(emitter + 88), normalised, d => Linear(d, 1, 0)) * listenerCone.Reverb * emitterCone.Item3);
            }

            if ((flags & 0x20) != 0)
            {
                // DopplerFactor = (c - s*vListener) / (c - s*vEmitter), components along emitter->listener.
                float limit = dopplerScaler > 0 ? speedOfSound / dopplerScaler * 0.5f : 0;
                float vEmitter = distance > 0 ? Math.Clamp(eVel.Dot(emitterToListener), -limit, limit) : 0;
                float vListener = distance > 0 ? Math.Clamp(lVel.Dot(emitterToListener), -limit, limit) : 0;
                float factor = dopplerScaler <= 0 || distance <= 0
                    ? 1
                    : (speedOfSound - (dopplerScaler * vListener)) / (speedOfSound - (dopplerScaler * vEmitter));
                _com.WFloat(dsp + 28, Math.Max(0, factor));
                _com.WFloat(dsp + 40, vEmitter);
                _com.WFloat(dsp + 44, vListener);
            }

            if ((flags & 0x40) != 0)
            {
                _com.WFloat(dsp + 32, emitterAngle);
            }
        }

        private void WriteMatrix(
            long dsp, uint flags, uint mask, int srcCount, int dstCount, int channelCount, float channelRadius,
            long azimuthsPointer, float innerRadius, V3 ePos, V3 eFront, V3 eRight, V3 lPos, V3 lFront, V3 lRight, V3 lTop,
            float volume, float lfe)
        {
            long matrix = _com.U32(dsp);
            if (matrix == 0 || srcCount <= 0 || dstCount <= 0 || srcCount > 64 || dstCount > 32)
            {
                return;
            }

            List<float?> speakers = Speakers(mask);
            bool zeroCentre = (flags & 0x10000) != 0;
            bool redirectToLfe = (flags & 0x20000) != 0;
            List<int> ring = Enumerable.Range(0, Math.Min(speakers.Count, dstCount))
                .Where(i => speakers[i] is { } az && !(zeroCentre && az == 0 && speakers.Count > 1))
                .OrderBy(i => speakers[i]!.Value)
                .ToList();
            int lfeIndex = speakers.FindIndex(s => s is null);

            float[] gains = new float[srcCount * dstCount];
            for (int s = 0; s < srcCount; s++)
            {
                float az = 0;
                V3 position = ePos;
                if (channelCount > 1 && azimuthsPointer != 0 && s < channelCount)
                {
                    az = _com.RFloat(azimuthsPointer + (s * 4));
                    if (az >= 2 * MathF.PI - 1e-4f)
                    {
                        // X3DAUDIO_2PI: an LFE channel, sent to the LFE speaker only.
                        if (lfeIndex >= 0 && lfeIndex < dstCount)
                        {
                            gains[(lfeIndex * srcCount) + s] = volume * lfe;
                        }

                        continue;
                    }

                    position = ePos + (((eRight * MathF.Sin(az)) + (eFront * MathF.Cos(az))) * channelRadius);
                }

                V3 rel = position - lPos;
                float d = rel.Length;
                float azimuth = d > 1e-6f ? MathF.Atan2(rel.Dot(lRight), rel.Dot(lFront)) : 0;

                // Inside the inner radius the sound spreads towards every speaker equally.
                float spread = innerRadius > 0 && d < innerRadius ? 1 - (d / innerRadius) : 0;
                float[] panned = Pan(speakers, ring, azimuth, dstCount);
                float omni = ring.Count == 0 ? 0 : 1 / MathF.Sqrt(ring.Count);
                for (int dst = 0; dst < dstCount; dst++)
                {
                    float g = panned[dst];
                    if (ring.Contains(dst))
                    {
                        g = (g * (1 - spread)) + (omni * spread);
                    }

                    gains[(dst * srcCount) + s] = g * volume;
                }

                if (redirectToLfe && lfeIndex >= 0 && lfeIndex < dstCount)
                {
                    gains[(lfeIndex * srcCount) + s] = volume * lfe;
                }
            }

            for (int i = 0; i < gains.Length; i++)
            {
                _com.WFloat(matrix + (i * 4), gains[i]);
            }
        }

        /// <summary>Constant-power panning between the two speakers of the ring either side of the azimuth.</summary>
        private static float[] Pan(List<float?> speakers, List<int> ring, float azimuth, int dstCount)
        {
            float[] g = new float[dstCount];
            if (ring.Count == 0)
            {
                return g;
            }

            if (ring.Count == 1)
            {
                g[ring[0]] = 1;
                return g;
            }

            static float Wrap(float a) => ((a % (2 * MathF.PI)) + (2 * MathF.PI)) % (2 * MathF.PI);

            // With no speaker behind the listener (stereo, 3.0) a source behind is heard where its
            // mirror image in front would be, and one beyond the outermost speaker comes from that
            // speaker alone; only a ring with rear speakers pans all the way round.
            bool surround = ring.Any(i => MathF.Abs(speakers[i]!.Value) > MathF.PI / 2);
            if (!surround)
            {
                float az = MathF.Abs(azimuth) > MathF.PI / 2 ? MathF.CopySign(MathF.PI - MathF.Abs(azimuth), azimuth) : azimuth;
                for (int i = 0; i < ring.Count; i++)
                {
                    float at = speakers[ring[i]]!.Value;
                    if (az <= at)
                    {
                        if (i == 0)
                        {
                            g[ring[0]] = 1;
                            return g;
                        }

                        float from = speakers[ring[i - 1]]!.Value;
                        float t = (az - from) / Math.Max(at - from, 1e-6f);
                        g[ring[i - 1]] = MathF.Cos(t * MathF.PI / 2);
                        g[ring[i]] = MathF.Sin(t * MathF.PI / 2);
                        return g;
                    }
                }

                g[ring[^1]] = 1;
                return g;
            }

            for (int i = 0; i < ring.Count; i++)
            {
                int a = ring[i], b = ring[(i + 1) % ring.Count];
                float from = speakers[a]!.Value, span = Wrap(speakers[b]!.Value - from);
                if (span <= 0)
                {
                    span = 2 * MathF.PI;
                }

                float offset = Wrap(azimuth - from);
                if (offset <= span)
                {
                    float t = offset / span;
                    g[a] += MathF.Cos(t * MathF.PI / 2);
                    g[b] += MathF.Sin(t * MathF.PI / 2);
                    return g;
                }
            }

            g[ring[0]] = 1;
            return g;
        }

        // ------------------------------------------------------------------------------
        // XAPO effects
        // ------------------------------------------------------------------------------

        private sealed class Apo : MediaObject
        {
            public override string Kind => "XAPO";

            public Guid Clsid;
            public byte[] Parameters = [];
            public byte[] InitData = [];
            public bool Locked;
            public long Registration;
        }

        private MediaInterface? _apoShape;
        private MediaInterface? _apoParametersShape;

        private void CreateFx()
        {
            // HRESULT CreateFX(REFCLSID clsid, IUnknown** pEffect, const void* pInitData, UINT32 InitDataByteSize).
            Guid clsid = _com.ReadGuid(Arg(0));
            if (clsid != ClsidFxEq && clsid != ClsidFxMasteringLimiter && clsid != ClsidFxReverb && clsid != ClsidFxEcho)
            {
                _com.W32(Arg(1), 0);
                Return(MediaCom.ClassNotAvailable);
                return;
            }

            CreateApo(clsid, Arg(1), Arg(2), Arg(3));
        }

        private void CreateApo(Guid clsid, long outPointer, long initData, long initSize)
        {
            if (outPointer == 0)
            {
                Return(MediaCom.EPointer);
                return;
            }

            Apo apo = new()
            {
                Clsid = clsid,
                InitData = initData == 0 || initSize <= 0 ? [] : _e.ReadMemory(initData, (int)Math.Min(initSize, 4096)),
            };
            apo.Parameters = apo.InitData;
            long pointer = _com.Expose(apo, ApoShape);
            _com.Expose(apo, ApoParametersShape);
            _com.W32(outPointer, pointer);
            Return(MediaCom.SOk);
        }

        // IXAPO: GetRegistrationProperties 3, IsInputFormatSupported 4, IsOutputFormatSupported 5,
        // Initialize 6, Reset 7, LockForProcess 8, UnlockForProcess 9, Process 10,
        // CalcInputFrames 11, CalcOutputFrames 12.
        private MediaInterface ApoShape => _apoShape ??= new("IXAPO", [IidXapo], 13, () => new()
        {
            [3] = ("GetRegistrationProperties", () =>
            {
                // XAPO_REGISTRATION_PROPERTIES: CLSID, FriendlyName[256], CopyrightInfo[256],
                // Major, Minor, Flags, Min/MaxInputBufferCount, Min/MaxOutputBufferCount: 1060 bytes,
                // XAPOAlloc'd (CoTaskMemAlloc) for the caller to free.
                Apo? apo = _com.Self<Apo>();
                if (apo is null || Arg(1) == 0)
                {
                    Return(MediaCom.EPointer);
                    return;
                }

                long p = _com.Alloc(1060);
                _com.WriteGuid(p, apo.Clsid);
                string name = apo.Clsid == ClsidVolumeMeter ? "AudioVolumeMeter" : apo.Clsid == ClsidAudioReverb ? "AudioReverb" : "FX";
                _e.WriteMemory(p + 16, System.Text.Encoding.Unicode.GetBytes(name));
                _com.W32(p + 528, 1);                     // MajorVersion
                _com.W32(p + 536, 0x1 | 0x2 | 0x4 | 0x8); // channels, rate, bits must match; in-place
                for (int i = 0; i < 4; i++)
                {
                    _com.W32(p + 540 + (i * 4), 1);
                }

                _com.W32(Arg(1), p);
                Return(MediaCom.SOk);
            }),
            [4] = ("IsInputFormatSupported", () => FormatSupported()),
            [5] = ("IsOutputFormatSupported", () => FormatSupported()),
            [6] = ("Initialize", () =>
            {
                if (_com.Self<Apo>() is { } apo && Arg(1) != 0 && Arg(2) > 0)
                {
                    apo.InitData = _e.ReadMemory(Arg(1), (int)Math.Min(Arg(2), 4096));
                    apo.Parameters = apo.InitData;
                }

                Return(MediaCom.SOk);
            }),
            [7] = ("Reset", () => Return(0)),
            [8] = ("LockForProcess", () =>
            {
                if (_com.Self<Apo>() is { } apo)
                {
                    apo.Locked = true;
                }

                Return(MediaCom.SOk);
            }),
            [9] = ("UnlockForProcess", () =>
            {
                if (_com.Self<Apo>() is { } apo)
                {
                    apo.Locked = false;
                }

                Return(0);
            }),
            [10] = ("Process", () =>
            {
                _com.Record("IXAPO::Process: effects are not run by this XAudio2 mixer");
                Return(0);
            }),
            [11] = ("CalcInputFrames", () => Return(Arg(1))),
            [12] = ("CalcOutputFrames", () => Return(Arg(1))),
        });

        /// <summary>
        /// IsInput/OutputFormatSupported(const WAVEFORMATEX* pOther, const WAVEFORMATEX* pRequested,
        /// WAVEFORMATEX** ppSupported): built-in effects take 32-bit float only, and propose that.
        /// </summary>
        private void FormatSupported()
        {
            long requested = Arg(2), supported = Arg(3);
            _com.W32(supported, 0);
            if (requested == 0)
            {
                Return(MediaCom.EPointer);
                return;
            }

            MediaFoundation.WaveFormat? wf = MediaFoundation.WaveFormat.Read(_e.ReadMemory(requested, 18));
            if (wf is not null && wf.EffectiveTag == MediaFoundation.WaveFormat.Float && wf.Bits == 32)
            {
                Return(MediaCom.SOk);
                return;
            }

            if (supported != 0 && wf is not null)
            {
                byte[] closest = MediaFoundation.WaveFormat.Write(true, Math.Max(1, wf.Channels), Math.Max(1000, wf.SampleRate), 32, false, 0);
                long p = _com.Alloc(closest.Length);
                _e.WriteMemory(p, closest);
                _com.W32(supported, p);
            }

            Return(XapoFormatUnsupported);
        }

        // IXAPOParameters: SetParameters 3, GetParameters 4.
        private MediaInterface ApoParametersShape => _apoParametersShape ??= new("IXAPOParameters", [IidXapoParameters], 5, () => new()
        {
            [3] = ("SetParameters", () =>
            {
                if (_com.Self<Apo>() is { } apo && Arg(1) != 0 && Arg(2) > 0)
                {
                    apo.Parameters = _e.ReadMemory(Arg(1), (int)Math.Min(Arg(2), 4096));
                }

                Return(0);
            }),
            [4] = ("GetParameters", () =>
            {
                Apo? apo = _com.Self<Apo>();
                long buffer = Arg(1), size = Arg(2);
                if (apo is not null && buffer != 0 && size > 0)
                {
                    if (apo.Clsid == ClsidVolumeMeter && size >= 12)
                    {
                        // XAUDIO2FX_VOLUMEMETER_LEVELS { float* pPeakLevels; float* pRMSLevels; UINT32 ChannelCount }:
                        // no audio has been metered, so every level is 0.
                        long peaks = _com.U32(buffer), rms = _com.U32(buffer + 4);
                        int channels = (int)Math.Min(_com.U32(buffer + 8), 64);
                        for (int i = 0; i < channels; i++)
                        {
                            _com.WFloat(peaks + (i * 4), 0);
                            _com.WFloat(rms + (i * 4), 0);
                        }
                    }
                    else
                    {
                        byte[] bytes = new byte[(int)Math.Min(size, 4096)];
                        apo.Parameters.AsSpan(0, Math.Min(apo.Parameters.Length, bytes.Length)).CopyTo(bytes);
                        _e.WriteMemory(buffer, bytes);
                    }
                }

                Return(0);
            }),
        });
    }
}
