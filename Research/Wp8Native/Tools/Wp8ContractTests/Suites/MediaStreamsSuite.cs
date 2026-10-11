using System.Reflection;
using System.Text;
using WPR.Wp8Native;

namespace Wp8ContractTests.Suites;

/// <summary>
/// The Media Foundation bridges added on top of MFPlat: IMFByteStream over the host's streams
/// (HGLOBAL IStreams and WinRT random-access streams) and back, IPropertyStore with
/// MFCreatePropertiesFromMediaType / MFCreateMediaTypeFromProperties, PropVariantClear/Copy, and the
/// media engine decoding compressed audio through a platform <see cref="IMediaDecoder"/>.
/// </summary>
public sealed class MediaStreamsSuite : IContractSuite
{
    public string Name => "media streams";

    private const long SOk = 0, EPointer = 0x80004003, EInvalidArg = 0x80070057, ENoInterface = 0x80004002, ENotImpl = 0x80004001;
    private const long Shutdown = 0xC00D3E85, InvalidMediaType = 0xC00D36B4, BadVarType = 0x80020008;

    private static readonly Guid IidStream = new("0000000c-0000-0000-c000-000000000046");
    private static readonly Guid IidRandomAccessStream = new("905a0fe1-bc53-11df-8c49-001e4fc686da");
    private static readonly Guid IidActivationFactory = new("00000035-0000-0000-c000-000000000046");
    private static readonly Guid IidPropertyStore = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");
    private static readonly Guid IidAttributes = new("2cd2d921-c447-44a7-a13c-4adabfc247e3");
    private static readonly Guid MtMajor = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MtSubtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MtRate = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid MtUserData = new("b6bc765f-4c3b-40a4-bd51-2535b66fe09d");
    private static readonly Guid Audio = new("73647561-0000-0010-8000-00aa00389b71");
    private static readonly Guid Mp3 = new("00000055-0000-0010-8000-00aa00389b71");

    private Harness _h = null!;

    private void Check(string what, bool ok, string detail = "") => _h.Check(what, ok, detail);

    private long M(long self, int slot, params long[] a) => _h.CallMethod(self, slot, a);

    private long Out() => _h.Buffer(16);

    private long Ptr(long p) => _h.U32(p);

    public void Run(Harness h)
    {
        _h = h;
        Check("MFStartup", h.Call("MFStartup", 0x00020070, 0) == SOk);
        HGlobalByteStream();
        WinRtByteStream();
        PropertyStores();
        Decoding();
        Check("MediaUnimplemented no longer lists the byte-stream and property-store functions",
            !h.S.MediaUnimplemented.Any(x => x.StartsWith("MFCreateMFByteStreamOnStream:", StringComparison.Ordinal) ||
                                             x.StartsWith("MFCreatePropertiesFromMediaType:", StringComparison.Ordinal)),
            string.Join("; ", h.S.MediaUnimplemented));
    }

    // ---------------------------------------------------------------------------------------
    // IMFByteStream over an HGLOBAL IStream
    // ---------------------------------------------------------------------------------------

    private void HGlobalByteStream()
    {
        ArmEmulator e = _h.E;
        long ps = Out();
        _h.Call("CreateStreamOnHGlobal", 0, 1, ps);
        long stream = Ptr(ps), done = Out();
        long text = _h.Narrow("hello world");
        M(stream, 4, text, 11, done);
        M(stream, 5, 0, 0, 0, 0, 0); // Seek(0, STREAM_SEEK_SET)

        long pbs = Out();
        Check("MFCreateMFByteStreamOnStream(NULL) is E_POINTER", _h.Call("MFCreateMFByteStreamOnStream", 0, pbs) == EPointer);
        Check("MFCreateMFByteStreamOnStream over an HGLOBAL IStream", _h.Call("MFCreateMFByteStreamOnStream", stream, pbs) == SOk && Ptr(pbs) != 0);
        long bs = Ptr(pbs), v = Out();
        Check("GetCapabilities: readable, writable, seekable, no network", M(bs, 3, v) == SOk && _h.U32(v) == (0x1 | 0x2 | 0x4 | 0x800));
        Check("GetLength is the stream's size", M(bs, 4, v) == SOk && _h.U64(v) == 11);
        long buffer = _h.Buffer(64), read = Out();
        Check("Read 5", M(bs, 9, buffer, 5, read) == SOk && _h.U32(read) == 5 && _h.ReadNarrow(buffer) == "hello");
        long pos = Out();
        Check("the read moved the IStream's own seek pointer (one position, shared)",
            M(stream, 5, 0, 0, 0, 1, pos) == SOk && _h.U64(pos) == 5); // Seek(0, STREAM_SEEK_CUR)
        Check("Seek(msoCurrent, +1) answers the new position", M(bs, 15, 1, 1, 0, 0, pos) == SOk && _h.U64(pos) == 6);
        e.WriteMemory(buffer, new byte[64]);
        Check("Read past the end is a short read", M(bs, 9, buffer, 64, read) == SOk && _h.U32(read) == 5 && _h.ReadNarrow(buffer) == "world");
        Check("IsEndOfStream", M(bs, 8, v) == SOk && _h.U32(v) == 1);
        Check("Write appends", M(bs, 12, _h.Narrow("!!"), 2, read) == SOk && _h.U32(read) == 2 && M(bs, 4, v) == SOk && _h.U64(v) == 13);
        Check("Seek with a bad origin is E_INVALIDARG", M(bs, 15, 7, 0, 0, 0, pos) == EInvalidArg);
        Check("SetLength truncates", M(bs, 5, 0, 4, 0) == SOk && M(bs, 4, v) == SOk && _h.U64(v) == 4);
        Check("SetCurrentPosition / GetCurrentPosition", M(bs, 7, 0, 1, 0) == SOk && M(bs, 6, v) == SOk && _h.U64(v) == 1);

        long got = Out();
        Check("an IMFByteStream answers IMFAttributes", M(bs, 0, _h.Guid(IidAttributes), got) == SOk && Ptr(got) != 0 && Ptr(got) != bs);
        M(Ptr(got), 2);

        // BeginRead: the transfer happens at once, the callback is queued, EndRead reports it.
        long pcb = Out();
        _h.Call("MFCreateAttributes", pcb, 0);
        long callback = Ptr(pcb); // any host object stands in: its slot 4 is code
        int pending = e.PendingDeferredCalls;
        e.WriteMemory(buffer, new byte[64]);
        Check("BeginRead queues the callback rather than calling it", M(bs, 10, buffer, 2, callback, 0) == SOk && e.PendingDeferredCalls == pending + 1);
        Check("and the bytes are already there", _h.ReadNarrow(buffer) == "el");
        long result = LastDeferredArgument(e, 1);
        Check("EndRead with the queued result: S_OK and the count", M(bs, 11, result, read) == SOk && _h.U32(read) == 2);
        Check("EndRead with a result from elsewhere is E_INVALIDARG", M(bs, 11, callback, read) == EInvalidArg);
        Check("BeginWrite / EndWrite", M(bs, 13, _h.Narrow("XY"), 2, callback, 0) == SOk && M(bs, 14, LastDeferredArgument(e, 1), read) == SOk && _h.U32(read) == 2);

        // And back: an HGLOBAL byte stream asked for as IStream is that IStream.
        long pout = Out();
        Check("MFCreateStreamOnMFByteStream gives back the IStream it wraps",
            _h.Call("MFCreateStreamOnMFByteStream", bs, pout) == SOk && Ptr(pout) == stream);
        M(stream, 2);
        M(bs, 4, v);
        ulong length = _h.U64(v);
        Check("MFCreateStreamOnMFByteStreamEx(IRandomAccessStream) over it: a WinRT stream on the same bytes",
            _h.Call("MFCreateStreamOnMFByteStreamEx", bs, _h.Guid(IidRandomAccessStream), pout) == SOk && Ptr(pout) != 0 &&
            M(Ptr(pout), 6, v) == SOk && _h.U64(v) == length && length == 5, $"{_h.U64(v)} vs {length}");

        Check("Close, then Read is MF_E_SHUTDOWN", M(bs, 17) == SOk && M(bs, 9, buffer, 1, read) == Shutdown);
        long refsBefore = M(stream, 1) - 1;
        M(stream, 2);
        Check("releasing the byte stream releases its reference on the IStream", M(bs, 2) == 0 && M(stream, 1) - 1 == refsBefore - 1, $"{refsBefore}");
        M(stream, 2);

        e.WriteUInt32(pbs, 0xDEAD);
        long foreign = _h.Buffer(16); // no vtable: a stream the game made, as far as anyone here can tell
        Check("MFCreateMFByteStreamOnStreamEx over a game's own stream is E_NOTIMPL with NULL",
            _h.Call("MFCreateMFByteStreamOnStreamEx", foreign, pbs) == ENotImpl && Ptr(pbs) == 0);
        Check("and is recorded", _h.S.MediaUnimplemented.Any(x => x.StartsWith("MFCreateMFByteStreamOnStreamEx over a stream the game", StringComparison.Ordinal)));
        long pa = Out();
        _h.Call("MFCreateAttributes", pa, 0);
        Check("over a media object that is not a stream it is E_NOINTERFACE", _h.Call("MFCreateMFByteStreamOnStreamEx", Ptr(pa), pbs) == ENoInterface);
    }

    private static long LastDeferredArgument(ArmEmulator e, int index)
    {
        object queue = typeof(ArmEmulator).GetField("_deferred", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(e)!;
        object last = ((System.Collections.IEnumerable)queue).Cast<object>().Last();
        long[] args = (long[])last.GetType().GetField("Item3")!.GetValue(last)!;
        return args[index];
    }

    // ---------------------------------------------------------------------------------------
    // IMFByteStream over a WinRT random-access stream
    // ---------------------------------------------------------------------------------------

    private void WinRtByteStream()
    {
        long factory = _h.E.WinRt.GetActivationFactory("Windows.Storage.Streams.InMemoryRandomAccessStream", IidActivationFactory)!.Value;
        long pras = Out();
        M(factory, 6, pras);
        long ras = Ptr(pras), v = Out();
        Check("InMemoryRandomAccessStream activates", ras != 0);

        long pbs = Out();
        Check("MFCreateMFByteStreamOnStreamEx over an IRandomAccessStream", _h.Call("MFCreateMFByteStreamOnStreamEx", ras, pbs) == SOk && Ptr(pbs) != 0);
        long bs = Ptr(pbs), n = Out();
        Check("Write through the byte stream", M(bs, 12, _h.Narrow("media bytes"), 11, n) == SOk && _h.U32(n) == 11);
        Check("the WinRT stream sees the size", M(ras, 6, v) == SOk && _h.U64(v) == 11);
        Check("and the position: they are one stream", M(ras, 10, v) == SOk && _h.U64(v) == 11);
        M(ras, 11, 0, 6, 0); // IRandomAccessStream::Seek(6)
        long buffer = _h.Buffer(32);
        Check("a Seek on the WinRT stream moves the byte stream", M(bs, 6, v) == SOk && _h.U64(v) == 6 &&
            M(bs, 9, buffer, 32, n) == SOk && _h.ReadNarrow(buffer) == "bytes");

        long pout = Out();
        Check("MFCreateStreamOnMFByteStreamEx(IRandomAccessStream) shares the data",
            _h.Call("MFCreateStreamOnMFByteStreamEx", bs, _h.Guid(IidRandomAccessStream), pout) == SOk && M(Ptr(pout), 6, v) == SOk && _h.U64(v) == 11);
        Check("an IStream over a WinRT stream is not made here: E_NOINTERFACE, recorded",
            _h.Call("MFCreateStreamOnMFByteStreamEx", bs, _h.Guid(IidStream), pout) == ENoInterface && Ptr(pout) == 0 &&
            _h.S.MediaUnimplemented.Any(x => x.Contains("MFCreateStreamOnMFByteStreamEx(0000000c")));
        Check("MFCreateStreamOnMFByteStreamEx over a game's IMFByteStream is E_NOTIMPL",
            _h.Call("MFCreateStreamOnMFByteStreamEx", _h.Buffer(16), _h.Guid(IidStream), pout) == ENotImpl);
        Check("the byte stream is released cleanly", M(bs, 2) == 0);
    }

    // ---------------------------------------------------------------------------------------
    // IPropertyStore and PROPVARIANT
    // ---------------------------------------------------------------------------------------

    private void PropertyStores()
    {
        ArmEmulator e = _h.E;
        long pt = Out();
        _h.Call("MFCreateMediaType", pt);
        long t = Ptr(pt), v = Out();
        M(t, 24, _h.Guid(MtMajor), _h.Guid(Audio));
        M(t, 24, _h.Guid(MtSubtype), _h.Guid(Mp3));
        M(t, 21, _h.Guid(MtRate), 44100);
        long blob = _h.Buffer(4);
        e.WriteMemory(blob, [9, 8, 7]);
        M(t, 26, _h.Guid(MtUserData), blob, 3);

        long pps = Out();
        Check("MFCreatePropertiesFromMediaType(IPropertyStore)", _h.Call("MFCreatePropertiesFromMediaType", t, _h.Guid(IidPropertyStore), pps) == SOk && Ptr(pps) != 0);
        long ps = Ptr(pps);
        Check("GetCount is the attribute count", M(ps, 3, v) == SOk && _h.U32(v) == 4);
        long key = _h.Buffer(20);
        Check("GetAt(0): fmtid is the attribute GUID, pid PID_FIRST_USABLE",
            M(ps, 4, 0, key) == SOk && new Guid(e.ReadMemory(key, 16)) == MtMajor && _h.U32(key + 16) == 2);
        Check("GetAt past the end is E_INVALIDARG", M(ps, 4, 9, key) == EInvalidArg);
        long pv = _h.Buffer(16);
        long rateKey = Key(MtRate);
        Check("GetValue(rate) is VT_UI4 44100", M(ps, 5, rateKey, pv) == SOk && _h.U16(pv) == 19 && _h.U32(pv + 8) == 44100);
        Check("GetValue of a missing key is S_OK with VT_EMPTY", M(ps, 5, Key(Guid.NewGuid()), pv) == SOk && _h.U16(pv) == 0);
        Check("GetValue(MF_MT_USER_DATA) is a VT_VECTOR|VT_UI1 copy", M(ps, 5, Key(MtUserData), pv) == SOk && _h.U16(pv) == 0x1011 &&
            _h.U32(pv + 8) == 3 && _h.U32(pv + 12) != blob && e.ReadMemory(_h.U32(pv + 12), 3).SequenceEqual(new byte[] { 9, 8, 7 }));
        long copy = _h.Buffer(16);
        Check("PropVariantCopy deep-copies a vector", _h.Call("PropVariantCopy", copy, pv) == SOk && _h.U32(copy + 12) != _h.U32(pv + 12) &&
            e.ReadMemory(_h.U32(copy + 12), 3).SequenceEqual(new byte[] { 9, 8, 7 }));
        Check("PropVariantClear frees it and leaves VT_EMPTY", _h.Call("PropVariantClear", pv) == SOk && _h.U16(pv) == 0 && _h.U32(pv + 12) == 0);
        _h.Call("PropVariantClear", copy);
        long odd = _h.Buffer(16);
        e.WriteMemory(odd, BitConverter.GetBytes((ushort)0x2003)); // VT_ARRAY|VT_I4: a SAFEARRAY, not handled here
        Check("PropVariantClear of a VARTYPE it cannot free is DISP_E_BADVARTYPE", _h.Call("PropVariantClear", odd) == BadVarType);

        long i4 = _h.Buffer(16);
        e.WriteMemory(i4, BitConverter.GetBytes((ushort)3)); // VT_I4
        e.WriteUInt32(i4 + 8, 48000);
        Check("SetValue(rate, VT_I4 48000)", M(ps, 6, rateKey, i4) == SOk && M(ps, 3, v) == SOk && _h.U32(v) == 4);
        Check("Commit", M(ps, 7) == SOk);
        long pt2 = Out();
        Check("MFCreateMediaTypeFromProperties", _h.Call("MFCreateMediaTypeFromProperties", ps, pt2) == SOk && Ptr(pt2) != 0);
        long t2 = Ptr(pt2);
        Check("the VT_I4 came back as a UINT32 attribute", M(t2, 7, _h.Guid(MtRate), v) == SOk && _h.U32(v) == 48000);
        Check("the major type and the user data came back", M(t2, 33, v) == SOk && new Guid(e.ReadMemory(v, 16)) == Audio && M(t2, 14, _h.Guid(MtUserData), v) == SOk && _h.U32(v) == 3);

        long empty = _h.Buffer(16);
        Check("SetValue(VT_EMPTY) deletes the property", M(ps, 6, Key(MtMajor), empty) == SOk && M(ps, 3, v) == SOk && _h.U32(v) == 3);
        Check("MFCreateMediaTypeFromProperties without a major type is MF_E_INVALIDMEDIATYPE",
            _h.Call("MFCreateMediaTypeFromProperties", ps, pt2) == InvalidMediaType && Ptr(pt2) == 0);
        Check("MFCreatePropertiesFromMediaType of another interface is E_NOINTERFACE, recorded",
            _h.Call("MFCreatePropertiesFromMediaType", t, _h.Guid(Guid.NewGuid()), pps) == ENoInterface && Ptr(pps) == 0 &&
            _h.S.MediaUnimplemented.Any(x => x.StartsWith("MFCreatePropertiesFromMediaType(", StringComparison.Ordinal)));
        Check("MFCreatePropertiesFromMediaType(NULL out) is E_POINTER", _h.Call("MFCreatePropertiesFromMediaType", t, _h.Guid(IidPropertyStore), 0) == EPointer);
        Check("the property store is released cleanly", M(ps, 2) == 0);
    }

    private long Key(Guid fmtid)
    {
        long key = _h.Buffer(20);
        _h.E.WriteMemory(key, fmtid.ToByteArray());
        _h.E.WriteUInt32(key + 16, 2);
        return key;
    }

    // ---------------------------------------------------------------------------------------
    // The media engine with a platform decoder
    // ---------------------------------------------------------------------------------------

    /// <summary>A stand-in platform decoder: "FAKEAUDIO" decodes to a quarter second of 8 kHz mono, "FAKEVIDEO" also has video.</summary>
    private sealed class FakeDecoder : IMediaDecoder
    {
        public int Calls;

        public string Name => "fake";

        public bool CanDecode(string mimeType) => mimeType == "audio/mpeg";

        public DecodedAudio? Decode(byte[] container, string? nameHint)
        {
            Calls++;
            string head = Encoding.ASCII.GetString(container, 0, Math.Min(9, container.Length));
            return head switch
            {
                "FAKEAUDIO" => new DecodedAudio(8000, 1, new byte[2000 * 2], HasVideo: false),
                "FAKEVIDEO" => new DecodedAudio(8000, 2, new byte[2000 * 4], HasVideo: true),
                _ => null,
            };
        }
    }

    private void Decoding()
    {
        ArmEmulator e = _h.E;
        IReadOnlyList<string> events = _h.S.MediaEngineEvents;
        FakeDecoder decoder = new();

        long mqi = _h.Buffer(12);
        e.WriteUInt32(mqi, (uint)_h.Guid(new Guid("4d645ace-26aa-4688-9be1-df3516990b93")));
        _h.Call("CoCreateInstanceFromApp", _h.Guid(new Guid("b44392da-499b-446b-a4cb-005fead0e6d5")), 0, 1, 0, 1, mqi);
        long factory = _h.U32(mqi + 4);
        long pattr = Out(), pnotify = Out();
        _h.Call("MFCreateAttributes", pattr, 1);
        _h.Call("MFCreateAttributes", pnotify, 0);
        M(Ptr(pattr), 27, _h.Guid(new Guid("c60381b8-83a4-41f8-a3d0-de05076849a9")), Ptr(pnotify));
        long pe = Out();
        M(factory, 3, 0, Ptr(pattr), pe);
        long engine = Ptr(pe), answer = Out();

        Directory.CreateDirectory(_h.Sandbox);
        File.WriteAllBytes(Path.Combine(_h.Sandbox, "song.mp3"), Encoding.ASCII.GetBytes("FAKEAUDIO and the rest of an MP3"));
        File.WriteAllBytes(Path.Combine(_h.Sandbox, "intro.mp4"), Encoding.ASCII.GetBytes("FAKEVIDEO and the rest of an MP4"));

        _h.S.MediaDecoder = null;
        Check("without a decoder CanPlayType(audio/mpeg) is NOT_SUPPORTED", M(engine, 13, _h.Bstr("audio/mpeg"), answer) == SOk && _h.U32(answer) == 0);
        int before = events.Count;
        M(engine, 6, _h.Bstr("ms-appdata:///local/song.mp3"));
        Check("without a decoder an MP3 fails with SRC_NOT_SUPPORTED", events.Skip(before).Last() == "ERROR(4, 0xC00D36C4)", string.Join(",", events.Skip(before)));
        Check("and says why", _h.S.MediaUnimplemented.Any(x => x.Contains("song.mp3") && x.Contains("no decoder")));

        _h.S.MediaDecoder = decoder;
        Check("with one, CanPlayType(audio/mpeg; codecs=mp3) is MAYBE", M(engine, 13, _h.Bstr("audio/mpeg; codecs=mp3"), answer) == SOk && _h.U32(answer) == 1);
        Check("video is still NOT_SUPPORTED", M(engine, 13, _h.Bstr("video/mp4"), answer) == SOk && _h.U32(answer) == 0);
        before = events.Count;
        M(engine, 6, _h.Bstr("ms-appdata:///local/song.mp3"));
        Check("an MP3 loads through the decoder: ... CANPLAY, CANPLAYTHROUGH", events.Skip(before).TakeLast(2).SequenceEqual(["CANPLAY", "CANPLAYTHROUGH"]) && decoder.Calls == 1,
            string.Join(",", events.Skip(before)));
        M(engine, 19);
        Check("its duration is the decoded audio's (0.25 s)", Math.Abs(_h.D64(0) - 0.25) < 1e-9, $"{_h.D64(0)}");
        Check("HasAudio", M(engine, 39) == 1);
        before = events.Count;
        Check("it plays", M(engine, 32) == SOk && events.Skip(before).SequenceEqual(["PLAY", "PLAYING"]));
        _h.S.TickMedia(0.3);
        Check("and ends", M(engine, 27) == 1);

        before = events.Count;
        M(engine, 6, _h.Bstr("ms-appdata:///local/intro.mp4"));
        Check("a source with video fails with SRC_NOT_SUPPORTED even though its audio decodes", events.Skip(before).Last() == "ERROR(4, 0xC00D36C4)", string.Join(",", events.Skip(before)));
        Check("recorded as video", _h.S.MediaUnimplemented.Any(x => x.Contains("intro.mp4") && x.Contains("video")));

        // SetSourceFromByteStream: an HGLOBAL stream holding a WAVE, read whole without moving it.
        long ps = Out();
        _h.Call("CreateStreamOnHGlobal", 0, 1, ps);
        byte[] wave = Wave(8000, 1, 800);
        long bytes = _h.Buffer(wave.Length);
        e.WriteMemory(bytes, wave);
        M(Ptr(ps), 4, bytes, wave.Length, Out());
        long pbs = Out();
        _h.Call("MFCreateMFByteStreamOnStream", Ptr(ps), pbs);
        long bs = Ptr(pbs), v = Out();
        M(bs, 7, 0, 3, 0);
        before = events.Count;
        Check("SetSourceFromByteStream returns S_OK", M(engine, 45, bs, _h.Bstr("memory.wav")) == SOk);
        Check("and loads the stream's WAVE", events.Skip(before).TakeLast(2).SequenceEqual(["CANPLAY", "CANPLAYTHROUGH"]), string.Join(",", events.Skip(before)));
        M(engine, 19);
        Check("duration 0.1 s", Math.Abs(_h.D64(0) - 0.1) < 1e-9, $"{_h.D64(0)}");
        Check("the byte stream's position is where it was", M(bs, 6, v) == SOk && _h.U64(v) == 3);
        before = events.Count;
        M(engine, 45, _h.Buffer(16), _h.Bstr("theirs.mp3"));
        Check("a game's own IMFByteStream fails the load, recorded", events.Skip(before).Last() == "ERROR(4, 0x80004001)" &&
            _h.S.MediaUnimplemented.Any(x => x.Contains("SetSourceFromByteStream(theirs.mp3)")), string.Join(",", events.Skip(before)));
        Check("SetSourceFromByteStream(NULL) is E_POINTER", M(engine, 45, 0, 0) == EPointer);
        M(engine, 42);
        _h.S.MediaDecoder = null;
    }

    private static byte[] Wave(int rate, int channels, int frames)
    {
        int data = frames * channels * 2;
        using MemoryStream m = new();
        using BinaryWriter w = new(m);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + data);
        w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        w.Write(16);
        w.Write((ushort)1);
        w.Write((ushort)channels);
        w.Write(rate);
        w.Write(rate * channels * 2);
        w.Write((ushort)(channels * 2));
        w.Write((ushort)16);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(data);
        w.Write(new byte[data]);
        return m.ToArray();
    }
}
