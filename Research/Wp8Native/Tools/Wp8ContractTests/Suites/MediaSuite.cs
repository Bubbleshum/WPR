using System.Text;
using WPR.Wp8Native;

namespace Wp8ContractTests.Suites;

/// <summary>
/// MFPlat (attributes, media types, buffers, samples, collections, async results, work queues,
/// the DXGI device manager, blobs and wave formats), the media engine, PhoneAudioSes (WASAPI),
/// the XAudio2_8 ordinals 2-6 (XAPOs and X3DAudio) and CreateDXGIFactory1.
/// </summary>
public sealed class MediaSuite : IContractSuite
{
    public string Name => "media";

    private const long SOk = 0, SFalse = 1;
    private const long EPointer = 0x80004003, EInvalidArg = 0x80070057, ENoInterface = 0x80004002, EHandle = 0x80070006;
    private const long AttributeNotFound = 0xC00D36E6, InvalidType = 0xC00D36BD, NotSufficientBuffer = 0x8007007A;
    private const long BadStartupVersion = 0xC00D36E3, Shutdown = 0xC00D3E85, BufferTooSmall = 0xC00D36B1;
    private const long NoTimestamp = 0xC00D36C8, InvalidMediaType = 0xC00D36B4, InvalidWorkQueue = 0xC00D36FE;
    private const long DxgiNotInitialized = 0x80041000, DxgiNewDevice = 0x80041001, DxgiLocked = 0x80041002;

    private static readonly Guid IidAttributes = new("2cd2d921-c447-44a7-a13c-4adabfc247e3");
    private static readonly Guid Iid2DBuffer = new("7dc9d5f9-9ed9-44ec-9bbf-0600bb589fbb");
    private static readonly Guid MtMajor = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MtIndependent = new("c9173739-5e56-461c-b713-46fb995cb95f");
    private static readonly Guid MtRate = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid Audio = new("73647561-0000-0010-8000-00aa00389b71");

    public void Run(Harness h)
    {
        ArmEmulator e = h.E;
        void Check(string what, bool ok, string detail = "") => h.Check(what, ok, detail);
        long Call(string fn, params long[] a) => h.Call(fn, a);
        long M(long self, int slot, params long[] a) => h.CallMethod(self, slot, a);
        long Out() => h.Buffer(16);
        long Ptr(long p) => h.U32(p);
        long Release(long p) => M(p, 2);
        long AddRef(long p) => M(p, 1);

        // ---- platform ----
        long q = Out();
        Check("MFAllocateWorkQueue before MFStartup is MF_E_SHUTDOWN", Call("MFAllocateWorkQueue", q) == Shutdown);
        Check("MFStartup with a foreign version is MF_E_BAD_STARTUP_VERSION", Call("MFStartup", 0x00030070, 0) == BadStartupVersion);
        Check("MFStartup(MF_VERSION) succeeds", Call("MFStartup", 0x00020070, 0) == SOk);
        Check("MFAllocateWorkQueue gives a private queue", Call("MFAllocateWorkQueue", q) == SOk && Ptr(q) >= 0x10000);
        Check("MFUnlockWorkQueue releases it", Call("MFUnlockWorkQueue", Ptr(q)) == SOk);
        Check("and then it is no longer a queue", Call("MFUnlockWorkQueue", Ptr(q)) == InvalidWorkQueue);
        Check("MFPutWorkItem on an unknown queue is MF_E_INVALID_WORKQUEUE", Call("MFPutWorkItem", 0x4242, 0x1000, 0) == InvalidWorkQueue);

        // ---- attributes ----
        long pa = Out();
        Check("MFCreateAttributes", Call("MFCreateAttributes", pa, 4) == SOk && Ptr(pa) != 0);
        long a = Ptr(pa);
        long k1 = h.Guid(Guid.NewGuid()), k2 = h.Guid(Guid.NewGuid()), k3 = h.Guid(Guid.NewGuid()), k4 = h.Guid(Guid.NewGuid()),
             k5 = h.Guid(Guid.NewGuid()), k6 = h.Guid(Guid.NewGuid()), missing = h.Guid(Guid.NewGuid());
        long v = Out();
        Check("SetUINT32 / GetUINT32 round trip", M(a, 21, k1, 7) == SOk && M(a, 7, k1, v) == SOk && h.U32(v) == 7);
        Check("GetUINT64 of a UINT32 is MF_E_INVALIDTYPE", M(a, 8, k1, v) == InvalidType);
        Check("a missing key is MF_E_ATTRIBUTENOTFOUND", M(a, 7, missing, v) == AttributeNotFound);
        Check("SetUINT64 takes the r2:r3 pair", M(a, 22, k2, 0x11223344, 0x55667788) == SOk && M(a, 8, k2, v) == SOk && h.U64(v) == 0x5566778811223344);
        h.SetD64(0, 2.5);
        Check("SetDouble takes d0", M(a, 23, k3) == SOk && M(a, 9, k3, v) == SOk && BitConverter.UInt64BitsToDouble(h.U64(v)) == 2.5);
        Guid g = Guid.NewGuid();
        Check("SetGUID / GetGUID", M(a, 24, k4, h.Guid(g)) == SOk && M(a, 10, k4, v) == SOk && new Guid(e.ReadMemory(v, 16)) == g);
        Check("SetString / GetStringLength", M(a, 25, k5, h.Wide("hello")) == SOk && M(a, 11, k5, v) == SOk && h.U32(v) == 5);
        long text = h.Buffer(64), cch = Out();
        Check("GetString into 3 chars is E_NOT_SUFFICIENT_BUFFER, reporting 5", M(a, 12, k5, text, 3, cch) == NotSufficientBuffer && h.U32(cch) == 5);
        Check("GetString with room", M(a, 12, k5, text, 32, cch) == SOk && h.ReadWide(text) == "hello");
        long blob = h.Buffer(8);
        e.WriteMemory(blob, [1, 2, 3]);
        Check("SetBlob / GetBlobSize", M(a, 26, k6, blob, 3) == SOk && M(a, 14, k6, v) == SOk && h.U32(v) == 3);
        long blobOut = h.Buffer(8);
        Check("GetBlob", M(a, 15, k6, blobOut, 8, v) == SOk && e.ReadMemory(blobOut, 3).SequenceEqual(new byte[] { 1, 2, 3 }));
        Check("GetItemType of the blob is VT_VECTOR|VT_UI1", M(a, 4, k6, v) == SOk && h.U32(v) == 0x1011);
        Check("GetCount counts six", M(a, 30, v) == SOk && h.U32(v) == 6);
        long pv = h.Buffer(16), key = Out();
        Check("GetItemByIndex(0) is the first set, as a VT_UI4 PROPVARIANT",
            M(a, 31, 0, key, pv) == SOk && h.U32(key) == h.U32(k1) && h.U16(pv) == 19 && h.U32(pv + 8) == 7);
        Check("GetItemByIndex past the end is E_INVALIDARG", M(a, 31, 6, key, pv) == EInvalidArg);
        long badPv = h.Buffer(16);
        e.WriteMemory(badPv, [2, 0]); // VT_I2 is not an attribute type
        Check("SetItem with a non-attribute VARTYPE is MF_E_INVALIDTYPE", M(a, 18, missing, badPv) == InvalidType);
        long cmp = Out();
        Check("CompareItem matches an equal PROPVARIANT", M(a, 5, k1, pv, cmp) == SOk && h.U32(cmp) == 1);

        long pb = Out();
        Call("MFCreateAttributes", pb, 0);
        long b = Ptr(pb);
        Check("CopyAllItems then Compare(ALL_ITEMS) is TRUE", M(a, 32, b) == SOk && M(a, 6, b, 2, cmp) == SOk && h.U32(cmp) == 1);
        Check("DeleteItem removes one", M(b, 19, k6) == SOk && M(b, 30, v) == SOk && h.U32(v) == 5);
        Check("Compare(ALL_ITEMS) is then FALSE, Compare(THEIR_ITEMS) TRUE",
            M(a, 6, b, 2, cmp) == SOk && h.U32(cmp) == 0 && M(a, 6, b, 1, cmp) == SOk && h.U32(cmp) == 1);

        long pu = Out();
        Call("MFCreateAttributes", pu, 0);
        long held = Ptr(pu);
        Check("SetUnknown holds a reference", M(a, 27, missing, held) == SOk && AddRef(held) == 3 && Release(held) == 2);
        long got = Out();
        Check("GetUnknown queries the held object", M(a, 17, missing, h.Guid(IidAttributes), got) == SOk && Ptr(got) == held);
        Release(held);
        Check("DeleteItem releases it", M(a, 19, missing) == SOk && Release(held) == 0);

        // Blob round trip.
        long size = Out();
        Check("MFGetAttributesAsBlobSize", Call("MFGetAttributesAsBlobSize", a, size) == SOk && h.U32(size) > 8);
        long bytes = h.Buffer((int)h.U32(size));
        Check("MFGetAttributesAsBlob into too small a buffer is MF_E_BUFFERTOOSMALL", Call("MFGetAttributesAsBlob", a, bytes, 4) == BufferTooSmall);
        Check("MFGetAttributesAsBlob", Call("MFGetAttributesAsBlob", a, bytes, h.U32(size)) == SOk);
        long pc = Out();
        Call("MFCreateAttributes", pc, 0);
        long c = Ptr(pc);
        Check("MFInitAttributesFromBlob rebuilds an equal store",
            Call("MFInitAttributesFromBlob", c, bytes, h.U32(size)) == SOk && M(a, 6, c, 2, cmp) == SOk && h.U32(cmp) == 1);
        Check("MFInitAttributesFromBlob of garbage is E_INVALIDARG", Call("MFInitAttributesFromBlob", c, h.Buffer(16), 16) == EInvalidArg);

        // ---- media types and wave formats ----
        long pt = Out();
        Check("MFCreateMediaType", Call("MFCreateMediaType", pt) == SOk);
        long t = Ptr(pt);
        Check("GetMajorType with none set is MF_E_ATTRIBUTENOTFOUND", M(t, 33, v) == AttributeNotFound);
        Check("SetGUID(MF_MT_MAJOR_TYPE) then GetMajorType", M(t, 24, h.Guid(MtMajor), h.Guid(Audio)) == SOk && M(t, 33, v) == SOk && new Guid(e.ReadMemory(v, 16)) == Audio);
        Check("IsCompressedFormat is TRUE without MF_MT_ALL_SAMPLES_INDEPENDENT", M(t, 34, v) == SOk && h.U32(v) == 1);
        M(t, 21, h.Guid(MtIndependent), 1);
        Check("and FALSE with it", M(t, 34, v) == SOk && h.U32(v) == 0);
        Check("an IMFMediaType answers for IMFAttributes with itself", M(t, 0, h.Guid(IidAttributes), got) == SOk && Ptr(got) == t);

        long wf = h.Buffer(18);
        e.WriteMemory(wf, WaveFormat(1, 2, 44100, 16));
        long pt2 = Out();
        Call("MFCreateMediaType", pt2);
        long t2 = Ptr(pt2);
        Check("MFInitMediaTypeFromWaveFormatEx", Call("MFInitMediaTypeFromWaveFormatEx", t2, wf, 18) == SOk && M(t2, 7, h.Guid(MtRate), v) == SOk && h.U32(v) == 44100);
        long ppwf = Out(), cb = Out();
        Check("MFCreateWaveFormatExFromMFMediaType gives back the WAVEFORMATEX",
            Call("MFCreateWaveFormatExFromMFMediaType", t2, ppwf, cb, 0) == SOk && h.U32(cb) == 18 &&
            e.ReadMemory(Ptr(ppwf), 16).SequenceEqual(WaveFormat(1, 2, 44100, 16).Take(16)));
        Check("with ForceExtensible it is 40 bytes, tag 0xFFFE",
            Call("MFCreateWaveFormatExFromMFMediaType", t2, ppwf, cb, 1) == SOk && h.U32(cb) == 40 && h.U16(Ptr(ppwf)) == 0xFFFE);
        long pt3 = Out();
        Call("MFCreateMediaType", pt3);
        long t3 = Ptr(pt3);
        Call("MFInitMediaTypeFromWaveFormatEx", t3, wf, 18);
        Check("IsEqual of identical types is S_OK with all four flags", M(t2, 35, t3, v) == SOk && h.U32(v) == 0xF);
        M(t3, 21, h.Guid(MtRate), 48000);
        Check("a different rate is S_FALSE without FORMAT_DATA", M(t2, 35, t3, v) == SFalse && h.U32(v) == 0xB);

        // ---- buffers ----
        long pbuf = Out();
        Check("MFCreateMemoryBuffer", Call("MFCreateMemoryBuffer", 100, pbuf) == SOk);
        long buf = Ptr(pbuf);
        long data = Out(), max = Out(), cur = Out();
        Check("Lock reports max 100, current 0", M(buf, 3, data, max, cur) == SOk && h.U32(max) == 100 && h.U32(cur) == 0 && Ptr(data) != 0);
        Check("SetCurrentLength past the max is E_INVALIDARG", M(buf, 6, 101) == EInvalidArg);
        Check("SetCurrentLength / GetCurrentLength", M(buf, 6, 50) == SOk && M(buf, 5, cur) == SOk && h.U32(cur) == 50);
        Check("Unlock, then Unlock again fails", M(buf, 4) == SOk && M(buf, 4) != SOk);
        Check("MFCreateAlignedMemoryBuffer aligns", Call("MFCreateAlignedMemoryBuffer", 100, 63, pbuf) == SOk &&
            M(Ptr(pbuf), 3, data, max, cur) == SOk && (Ptr(data) & 63) == 0);
        long pwrap = Out();
        Check("MFCreateMediaBufferWrapper points into its parent",
            Call("MFCreateMediaBufferWrapper", buf, 10, 20, pwrap) == SOk && M(Ptr(pwrap), 3, data, max, cur) == SOk &&
            h.U32(max) == 20 && Ptr(data) == BufferData(h, buf) + 10);
        Check("a wrapper past the parent's end is E_INVALIDARG", Call("MFCreateMediaBufferWrapper", buf, 90, 20, pwrap) == EInvalidArg);

        const long nv12 = 0x3231564E; // "NV12"
        Check("MFCreate2DMediaBuffer of an unknown format is MF_E_INVALIDMEDIATYPE", Call("MFCreate2DMediaBuffer", 64, 32, 0x12345678, 0, pbuf) == InvalidMediaType);
        Check("MFCreate2DMediaBuffer(NV12 64x32)", Call("MFCreate2DMediaBuffer", 64, 32, nv12, 0, pbuf) == SOk);
        long nv = Ptr(pbuf), p2d = Out(), scan = Out(), pitch = Out();
        Check("it answers IMF2DBuffer", M(nv, 0, h.Guid(Iid2DBuffer), p2d) == SOk && Ptr(p2d) != 0);
        Check("contiguous length is width*height*3/2", M(Ptr(p2d), 7, v) == SOk && h.U32(v) == 3072);
        Check("GetScanline0AndPitch unlocked fails", M(Ptr(p2d), 5, scan, pitch) != SOk);
        Check("Lock2D gives a 64-byte pitch", M(Ptr(p2d), 3, scan, pitch) == SOk && h.U32(pitch) == 64 && M(Ptr(p2d), 4) == SOk);
        Call("MFCreate2DMediaBuffer", 10, 4, 22, 1, pbuf); // X8R8G8B8, bottom-up
        long rgb = Ptr(pbuf);
        M(rgb, 0, h.Guid(Iid2DBuffer), p2d);
        Check("a bottom-up RGB buffer has scanline 0 last and a negative pitch",
            M(Ptr(p2d), 3, scan, pitch) == SOk && (int)h.U32(pitch) == -40 && Ptr(scan) == BufferData(h, rgb) + 120);

        // ---- samples ----
        long ps = Out();
        Check("MFCreateSample", Call("MFCreateSample", ps) == SOk);
        long s = Ptr(ps);
        Check("GetSampleTime with none set is MF_E_NO_SAMPLE_TIMESTAMP", M(s, 35, v) == NoTimestamp);
        Check("SetSampleTime takes the r2:r3 pair", M(s, 36, 0, 0x89ABCDEF, 0x1) == SOk && M(s, 35, v) == SOk && h.U64(v) == 0x189ABCDEF);
        long b1 = NewBuffer(h, [1, 2, 3]), b2 = NewBuffer(h, [4, 5, 6, 7, 8]);
        Check("AddBuffer twice, GetBufferCount 2", M(s, 42, b1) == SOk && M(s, 42, b2) == SOk && M(s, 39, v) == SOk && h.U32(v) == 2);
        Check("GetTotalLength sums current lengths", M(s, 45, v) == SOk && h.U32(v) == 8);
        long small = NewBuffer(h, new byte[4]);
        Check("CopyToBuffer into a smaller buffer is MF_E_BUFFERTOOSMALL", M(s, 46, small) == BufferTooSmall);
        long pcont = Out();
        Check("ConvertToContiguousBuffer merges into one buffer",
            M(s, 41, pcont) == SOk && M(s, 39, v) == SOk && h.U32(v) == 1 &&
            e.ReadMemory(BufferData(h, Ptr(pcont)), 8).SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
        Check("the merged buffers were released by the sample", Release(b1) == 0 && Release(b2) == 0);
        long cont = Ptr(pcont);
        Check("releasing the sample releases its buffer", Release(s) == 0 && Release(cont) == 0);

        // ---- collections ----
        long pcol = Out();
        Call("MFCreateCollection", pcol);
        long col = Ptr(pcol);
        Check("AddElement x2, GetElementCount 2", M(col, 5, a) == SOk && M(col, 5, b) == SOk && M(col, 3, v) == SOk && h.U32(v) == 2);
        Check("GetElement(1)", M(col, 4, 1, got) == SOk && Ptr(got) == b);
        Check("RemoveElement(0) hands it back", M(col, 6, 0, got) == SOk && Ptr(got) == a && M(col, 3, v) == SOk && h.U32(v) == 1);
        Check("GetElement past the end is E_INVALIDARG", M(col, 4, 5, got) == EInvalidArg);

        // ---- async results ----
        long pr = Out();
        Check("MFCreateAsyncResult", Call("MFCreateAsyncResult", b, c, 0, pr) == SOk);
        long r = Ptr(pr);
        Check("pCallback sits at +24, as in MFASYNCRESULT", h.U32(r + 24) == c);
        Check("GetState with no state is E_POINTER", M(r, 3, got) == EPointer);
        Check("GetObject hands out the object", M(r, 6, got) == SOk && Ptr(got) == b);
        Check("SetStatus / GetStatus", M(r, 5, 0x80004005) == SOk && M(r, 4) == 0x80004005);
        int pending = e.PendingDeferredCalls;
        Check("MFInvokeCallback queues the callback rather than calling it", Call("MFInvokeCallback", r) == SOk && e.PendingDeferredCalls == pending + 1);

        // ---- arithmetic ----
        long result = Call("MFllMulDiv", 1_000_000_000_000 & 0xFFFFFFFF, 1_000_000_000_000 >> 32, 3, 0, 7, 0, 0, 0);
        Check("MFllMulDiv(1e12, 3, 7, 0)", (result | (h.R1Value << 32)) == 428_571_428_571, $"{result | (h.R1Value << 32)}");

        // ---- DXGI device manager ----
        long ptok = Out(), pm = Out();
        Check("MFCreateDXGIDeviceManager", Call("MFCreateDXGIDeviceManager", ptok, pm) == SOk);
        long m = Ptr(pm), token = h.U32(ptok), hd = Out();
        Check("OpenDeviceHandle before ResetDevice is MF_E_DXGI_DEVICE_NOT_INITIALIZED", M(m, 6, hd) == DxgiNotInitialized);
        Check("ResetDevice with the wrong token is E_INVALIDARG", M(m, 7, a, token + 1) == EInvalidArg);
        Check("ResetDevice", M(m, 7, a, token) == SOk);
        Check("OpenDeviceHandle / TestDevice", M(m, 6, hd) == SOk && M(m, 8, Ptr(hd)) == SOk);
        long hd2 = Out();
        M(m, 6, hd2);
        Check("LockDevice queries the device", M(m, 5, Ptr(hd), h.Guid(IidAttributes), got, 0) == SOk && Ptr(got) == a);
        Check("a second handle cannot lock it meanwhile", M(m, 5, Ptr(hd2), h.Guid(IidAttributes), got, 0) == DxgiLocked);
        Check("UnlockDevice", M(m, 9, Ptr(hd), 0) == SOk);
        M(m, 7, b, token);
        Check("after a new device old handles are MF_E_DXGI_NEW_VIDEO_DEVICE", M(m, 8, Ptr(hd)) == DxgiNewDevice);
        Check("CloseDeviceHandle of a bogus handle is E_HANDLE", M(m, 3, 0x1234) == EHandle);
        long pm1 = Out(), pm2 = Out();
        Check("MFLockDXGIDeviceManager hands out one shared manager",
            Call("MFLockDXGIDeviceManager", ptok, pm1) == SOk && Call("MFLockDXGIDeviceManager", ptok, pm2) == SOk && Ptr(pm1) == Ptr(pm2));
        Check("MFUnlockDXGIDeviceManager", Call("MFUnlockDXGIDeviceManager") == SOk);

        // ---- documented failures ----
        long pbs = Out();
        e.WriteUInt32(pbs, 0xDEAD);
        Check("MFCreateMFByteStreamOnStreamEx over attributes (not a stream) is E_NOINTERFACE with NULL", Call("MFCreateMFByteStreamOnStreamEx", a, pbs) == ENoInterface && Ptr(pbs) == 0);
        Check("MFCreateVideoSampleAllocatorEx fails and writes NULL", Call("MFCreateVideoSampleAllocatorEx", h.Guid(Guid.NewGuid()), pbs) == 0x80004001 && Ptr(pbs) == 0);
        Check("and is recorded", s0(h).Any(x => x.Contains("MFCreateVideoSampleAllocatorEx")));

        // ---- the media engine ----
        MediaEngine(h, a, b);

        // ---- WASAPI ----
        Wasapi(h);

        // ---- XAudio2_8 ordinals ----
        XAudio(h);

        // ---- dxgi ----
        long pf = Out();
        Check("CreateDXGIFactory1(IID_IDXGIFactory1)", Call("CreateDXGIFactory1", h.Guid(new Guid("770aae78-f26f-4dba-a829-253c83d1b387")), pf) == SOk && Ptr(pf) != 0);
        e.WriteUInt32(pf, 0xDEAD);
        Check("CreateDXGIFactory1 of another IID is E_NOINTERFACE with NULL", Call("CreateDXGIFactory1", h.Guid(IidAttributes), pf) == ENoInterface && Ptr(pf) == 0);

        Check("MFShutdown", Call("MFShutdown") == SOk);
    }

    private static IReadOnlyList<string> s0(Harness h) => h.S.MediaUnimplemented;

    private static long BufferData(Harness h, long buffer)
    {
        long data = h.Buffer(4), max = h.Buffer(4), cur = h.Buffer(4);
        h.CallMethod(buffer, 3, data, max, cur);
        h.CallMethod(buffer, 4);
        return h.U32(data);
    }

    private static long NewBuffer(Harness h, byte[] content)
    {
        long pp = h.Buffer(4);
        h.Call("MFCreateMemoryBuffer", Math.Max(content.Length, 1), pp);
        long buffer = h.U32(pp);
        h.E.WriteMemory(BufferData(h, buffer), content);
        h.CallMethod(buffer, 6, content.Length);
        return buffer;
    }

    private static byte[] WaveFormat(ushort tag, int channels, int rate, int bits, bool extensibleTail = false)
    {
        int block = channels * bits / 8;
        using MemoryStream m = new();
        using BinaryWriter w = new(m);
        w.Write(tag);
        w.Write((ushort)channels);
        w.Write(rate);
        w.Write(rate * block);
        w.Write((ushort)block);
        w.Write((ushort)bits);
        w.Write((ushort)0);
        return m.ToArray();
    }

    private static void MediaEngine(Harness h, long attributesA, long notifyStandIn)
    {
        ArmEmulator e = h.E;
        void Check(string what, bool ok, string detail = "") => h.Check(what, ok, detail);
        long M(long self, int slot, params long[] a) => h.CallMethod(self, slot, a);
        long Out() => h.Buffer(16);
        IReadOnlyList<string> events = h.S.MediaEngineEvents;

        // MULTI_QI { const IID* pIID; IUnknown* pItf; HRESULT hr; }.
        long mqi = h.Buffer(12);
        e.WriteUInt32(mqi, (uint)h.Guid(new Guid("4d645ace-26aa-4688-9be1-df3516990b93")));
        long hr = h.Call("CoCreateInstanceFromApp", h.Guid(new Guid("b44392da-499b-446b-a4cb-005fead0e6d5")), 0, 1, 0, 1, mqi);
        long factory = h.U32(mqi + 4);
        Check("CoCreateInstanceFromApp(CLSID_MFMediaEngineClassFactory)", hr == SOk && factory != 0 && h.U32(mqi + 8) == 0);

        long pattr = Out();
        h.Call("MFCreateAttributes", pattr, 1);
        long attr = h.U32(pattr), pe = Out();
        Check("CreateInstance without MF_MEDIA_ENGINE_CALLBACK is E_INVALIDARG", M(factory, 3, 0, attr, pe) == EInvalidArg);

        // The notify callback stands in as any object with a method at slot 3; events are queued to it, not called.
        M(attr, 27, h.Guid(new Guid("c60381b8-83a4-41f8-a3d0-de05076849a9")), notifyStandIn);
        Check("CreateInstance", M(factory, 3, 0, attr, pe) == SOk && h.U32(pe) != 0);
        long engine = h.U32(pe);
        Check("a new engine is paused, NETWORK_EMPTY, HAVE_NOTHING", M(engine, 20) == 1 && M(engine, 8) == 0 && M(engine, 14) == 0);
        long answer = Out();
        Check("CanPlayType(video/mp4) is NOT_SUPPORTED", M(engine, 13, h.Bstr("video/mp4"), answer) == SOk && h.U32(answer) == 0);
        Check("CanPlayType(audio/wav) is MAYBE", M(engine, 13, h.Bstr("audio/wav"), answer) == SOk && h.U32(answer) == 1);

        int before = events.Count;
        Check("SetSource of a missing file returns S_OK (loading is asynchronous)", M(engine, 6, h.Bstr("ms-appx:///no/such/intro.mp4")) == SOk);
        Check("and queues LOADSTART then ERROR(SRC_NOT_SUPPORTED, file not found)",
            events.Skip(before).SequenceEqual(["LOADSTART", "ERROR(4, 0x80070002)"]), string.Join(",", events.Skip(before)));
        long perr = Out();
        Check("GetError reports MF_MEDIA_ENGINE_ERR_SRC_NOT_SUPPORTED", M(engine, 3, perr) == SOk && M(h.U32(perr), 3) == 4 && M(h.U32(perr), 4) == 0x80070002);
        Check("GetNetworkState is NETWORK_NO_SOURCE", M(engine, 8) == 3);

        string asset = Directory.EnumerateFiles(Path.GetDirectoryName(h.Image)!, "*", SearchOption.AllDirectories)
            .First(f => !f.EndsWith(".exe") && !f.EndsWith(".dll") && !f.Contains("original") && new FileInfo(f).Length > 64);
        string rel = Path.GetRelativePath(Path.GetDirectoryName(h.Image)!, asset).Replace('\\', '/');
        before = events.Count;
        M(engine, 6, h.Bstr("ms-appx:///" + rel));
        Check($"SetSource of a file that is not WAVE ({Path.GetFileName(asset)}) fails with MF_E_UNSUPPORTED_BYTESTREAM_TYPE",
            events.Skip(before).Last() == "ERROR(4, 0xC00D36C4)", string.Join(",", events.Skip(before)));
        Check("and the failure is recorded", h.S.MediaUnimplemented.Any(x => x.Contains(Path.GetFileName(asset))));

        // A real WAVE in the local folder: 0.5 s of 8 kHz mono 16-bit.
        Directory.CreateDirectory(h.Sandbox);
        File.WriteAllBytes(Path.Combine(h.Sandbox, "tone.wav"), Wave(8000, 1, 4000));
        before = events.Count;
        M(engine, 6, h.Bstr("ms-appdata:///local/tone.wav"));
        Check("a WAVE source loads: LOADSTART, DURATIONCHANGE, LOADEDMETADATA, LOADEDDATA, CANPLAY, CANPLAYTHROUGH",
            events.Skip(before).SequenceEqual(["EMPTIED", "LOADSTART", "DURATIONCHANGE", "LOADEDMETADATA", "LOADEDDATA", "CANPLAY", "CANPLAYTHROUGH"]),
            string.Join(",", events.Skip(before)));
        M(engine, 19);
        Check("GetDuration is 0.5 s (d0)", Math.Abs(h.D64(0) - 0.5) < 1e-9, $"{h.D64(0)}");
        Check("HasAudio, not HasVideo, HAVE_ENOUGH_DATA", M(engine, 39) == 1 && M(engine, 38) == 0 && M(engine, 14) == 4);
        before = events.Count;
        Check("Play", M(engine, 32) == SOk && M(engine, 20) == 0 && events.Skip(before).SequenceEqual(["PLAY", "PLAYING"]));
        h.S.TickMedia(0.25);
        M(engine, 16);
        Check("the clock advances while playing", Math.Abs(h.D64(0) - 0.25) < 1e-6, $"{h.D64(0)}");
        before = events.Count;
        h.S.TickMedia(0.3);
        Check("playing out raises ENDED and IsEnded", events.Skip(before).Contains("ENDED") && M(engine, 27) == 1, string.Join(",", events.Skip(before)));
        h.SetD64(0, 0.1);
        before = events.Count;
        Check("SetCurrentTime seeks: SEEKING, TIMEUPDATE, SEEKED, no longer ended",
            M(engine, 17) == SOk && events.Skip(before).SequenceEqual(["SEEKING", "TIMEUPDATE", "SEEKED"]) && M(engine, 27) == 0);
        h.SetD64(0, 2.0);
        Check("SetPlaybackRate(2) is MF_E_UNSUPPORTED_RATE", M(engine, 24) == 0xC00D36D0);
        h.SetD64(0, 1.5);
        Check("SetVolume(1.5) is E_INVALIDARG", M(engine, 37) == EInvalidArg);
        Check("TransferVideoFrame with no video fails", M(engine, 43, 0, 0, 0, 0) != SOk);
        Check("Shutdown, then every call is MF_E_SHUTDOWN", M(engine, 42) == SOk && M(engine, 32) == Shutdown);
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
        for (int i = 0; i < frames * channels; i++)
        {
            w.Write((short)(Math.Sin(i * 0.1) * 8000));
        }

        return m.ToArray();
    }

    private static void Wasapi(Harness h)
    {
        ArmEmulator e = h.E;
        void Check(string what, bool ok, string detail = "") => h.Check(what, ok, detail);
        long M(long self, int slot, params long[] a) => h.CallMethod(self, slot, a);
        long Out() => h.Buffer(16);

        long id = h.Call("GetDefaultAudioRenderId", 0);
        Check("GetDefaultAudioRenderId gives an id", id != 0 && h.ReadWide(id).Contains("MMDEVAPI"));
        Check("GetDefaultAudioCaptureId reports no capture device", h.Call("GetDefaultAudioCaptureId", 0) == 0);
        long pc = Out();
        long iidClient2 = h.Guid(new Guid("726778cd-f60a-4eda-82de-e47610cd78aa"));
        Check("ActivateAudioInterface on an unknown path is E_NOTFOUND", h.Call("ActivateAudioInterface", h.Wide("nope"), iidClient2, pc) == 0x80070490 && h.U32(pc) == 0);
        Check("ActivateAudioInterface(IAudioClient2)", h.Call("ActivateAudioInterface", id, iidClient2, pc) == SOk && h.U32(pc) != 0);
        long client = h.U32(pc), v = Out();
        Check("GetBufferSize before Initialize is AUDCLNT_E_NOT_INITIALIZED", M(client, 4, v) == 0x88890001);
        long pmix = Out();
        Check("GetMixFormat: 48 kHz stereo 32-bit, extensible",
            M(client, 8, pmix) == SOk && h.U16(h.U32(pmix)) == 0xFFFE && h.U16(h.U32(pmix) + 2) == 2 && h.U32(h.U32(pmix) + 4) == 48000 && h.U16(h.U32(pmix) + 14) == 32);
        long cd = h.Buffer(18), closest = Out();
        e.WriteMemory(cd, WaveFormat(1, 2, 44100, 16));
        Check("IsFormatSupported(44.1 kHz) is S_FALSE with the mix format as closest",
            M(client, 7, 0, cd, closest) == SFalse && h.U32(h.U32(closest) + 4) == 48000);
        Check("exclusive mode is AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED", M(client, 7, 1, cd, 0) == 0x8889000E);
        Check("Initialize at 44.1 kHz is AUDCLNT_E_UNSUPPORTED_FORMAT", M(client, 3, 0, 0x40000, 0, 200000, 0, 0, 0, cd, 0) == 0x88890008);
        long fmt = h.Buffer(18);
        e.WriteMemory(fmt, WaveFormat(3, 2, 48000, 32));
        Check("Initialize(event-driven, 20 ms, float stereo 48 kHz)", M(client, 3, 0, 0x40000, 0, 200000, 0, 0, 0, fmt, 0) == SOk);
        Check("Initialize again is AUDCLNT_E_ALREADY_INITIALIZED", M(client, 3, 0, 0x40000, 0, 200000, 0, 0, 0, fmt, 0) == 0x88890002);
        Check("GetBufferSize is 960 frames", M(client, 4, v) == SOk && h.U32(v) == 960);
        Check("Start without an event handle is AUDCLNT_E_EVENTHANDLE_NOT_SET", M(client, 10) == 0x88890014);

        long prc = Out();
        Check("GetService(IAudioRenderClient)", M(client, 14, h.Guid(new Guid("f294acfc-3146-4483-a7bf-addca7c260e2")), prc) == SOk);
        Check("GetService(IAudioCaptureClient) is AUDCLNT_E_WRONG_ENDPOINT_TYPE",
            M(client, 14, h.Guid(new Guid("c8adbd64-e71e-48a0-a4de-185c395cd317")), Out()) == 0x88890003);
        long render = h.U32(prc), pdata = Out();
        Check("GetBuffer beyond the free space is AUDCLNT_E_BUFFER_TOO_LARGE", M(render, 3, 961, pdata) == 0x88890006);
        Check("GetBuffer(480)", M(render, 3, 480, pdata) == SOk && h.U32(pdata) != 0);
        Check("a second GetBuffer before ReleaseBuffer is AUDCLNT_E_OUT_OF_ORDER", M(render, 3, 10, pdata) == 0x88890007);
        Check("ReleaseBuffer of more than was asked is AUDCLNT_E_INVALID_SIZE", M(render, 4, 481, 0) == 0x88890009);
        Check("ReleaseBuffer(480) and the padding is 480", M(render, 4, 480, 0) == SOk && M(client, 6, v) == SOk && h.U32(v) == 480);

        long evt = h.Call("CreateEventExW", 0, 0, 0, 0x1F0003);
        Check("SetEventHandle, Start", M(client, 13, evt) == SOk && M(client, 10) == SOk);
        Check("Start sets the event while there is room", h.Call("WaitForSingleObjectEx", evt, 0, 0) == 0);
        Check("Start again is AUDCLNT_E_NOT_STOPPED", M(client, 10) == 0x88890005);
        h.S.TickMedia(0.01);
        long pclock = Out(), pos = Out();
        M(client, 14, h.Guid(new Guid("cd63314f-3fba-4a1b-812c-ef96358728e7")), pclock);
        Check("10 ms later the padding is 0 and the clock moved 480 frames (in bytes)",
            M(client, 6, v) == SOk && h.U32(v) == 0 && M(h.U32(pclock), 4, pos, 0) == SOk && h.U64(pos) == 480 * 8);
        Check("the clock frequency is the byte rate", M(h.U32(pclock), 3, pos) == SOk && h.U64(pos) == 48000 * 8);
        Check("a frame later the event is set again", h.Call("WaitForSingleObjectEx", evt, 0, 0) == 0);
        Check("Stop, and Stop again is S_FALSE", M(client, 11) == SOk && M(client, 11) == SFalse);
        Check("Reset when stopped", M(client, 12) == SOk);

        long pvol = Out();
        M(client, 14, h.Guid(new Guid("87ce5498-68d6-44e5-9215-6da47ef883d8")), pvol);
        long vol = h.U32(pvol);
        h.SetS32(0, 0.5f);
        Check("ISimpleAudioVolume::SetMasterVolume (s0) / GetMasterVolume", M(vol, 3, 0) == SOk && M(vol, 4, v) == SOk && h.F32(v) == 0.5f);
        h.SetS32(0, 1.5f);
        Check("SetMasterVolume(1.5) is E_INVALIDARG", M(vol, 3, 0) == EInvalidArg);
    }

    private static void XAudio(Harness h)
    {
        ArmEmulator e = h.E;
        void Check(string what, bool ok, string detail = "") => h.Check(what, ok, detail);
        long M(long self, int slot, params long[] a) => h.CallMethod(self, slot, a);
        long Out() => h.Buffer(16);

        long handle = h.Buffer(20);
        h.SetS32(0, 343.5f);
        Check("X3DAudioInitialize (#6) with no speakers is E_INVALIDARG", h.CallImport("XAudio2_8.dll!#6", 0, handle) == EInvalidArg);
        h.SetS32(0, 343.5f);
        Check("X3DAudioInitialize (#6) for stereo", h.CallImport("XAudio2_8.dll!#6", 0x3, handle) == SOk);

        long listener = h.Buffer(52), emitter = h.Buffer(100), dsp = h.Buffer(48), matrix = h.Buffer(8), delays = h.Buffer(8);
        h.WriteF32(listener + 8, 1);   // front (0,0,1)
        h.WriteF32(listener + 16, 1);  // top (0,1,0)
        h.WriteF32(emitter + 4 + 8, 1);
        h.WriteF32(emitter + 16 + 4, 1);
        h.WriteF32(emitter + 28, 10);  // 10 m to the right
        e.WriteUInt32(emitter + 60, 1);
        h.WriteF32(emitter + 92, 1);   // CurveDistanceScaler
        h.WriteF32(emitter + 96, 1);   // DopplerScaler
        e.WriteUInt32(dsp, (uint)matrix);
        e.WriteUInt32(dsp + 4, (uint)delays);
        e.WriteUInt32(dsp + 8, 1);
        e.WriteUInt32(dsp + 12, 2);
        h.CallImport("XAudio2_8.dll!#5", handle, listener, emitter, 0x1 | 0x2 | 0x20 | 0x40, dsp);
        float left = h.F32(matrix), right = h.F32(matrix + 4);
        Check("X3DAudioCalculate (#5): a source to the right pans right", right > left * 4, $"L={left} R={right}");
        Check("inverse-square default curve: total level 1/10 at 10 m",
            Math.Abs(MathF.Sqrt((left * left) + (right * right)) - 0.1f) < 1e-3, $"{MathF.Sqrt((left * left) + (right * right))}");
        Check("EmitterToListenerDistance is 10", Math.Abs(h.F32(dsp + 36) - 10) < 1e-4);
        Check("the left ear is the delayed one", h.F32(delays) > 0 && h.F32(delays + 4) == 0);
        Check("a still emitter has a Doppler factor of 1", Math.Abs(h.F32(dsp + 28) - 1) < 1e-6);
        h.WriteF32(emitter + 40, -34.35f); // moving towards the listener at a tenth of the speed of sound
        h.CallImport("XAudio2_8.dll!#5", handle, listener, emitter, 0x20, dsp);
        Check("an approaching emitter raises the pitch (c / (c - v))", Math.Abs(h.F32(dsp + 28) - (343.5f / (343.5f - 34.35f))) < 1e-3, $"{h.F32(dsp + 28)}");
        h.WriteF32(emitter + 28, 0);
        h.WriteF32(emitter + 36, 0.5f); // half a metre straight ahead
        h.CallImport("XAudio2_8.dll!#5", handle, listener, emitter, 0x1, dsp);
        Check("a source dead ahead is centred at full level", Math.Abs(h.F32(matrix) - h.F32(matrix + 4)) < 1e-4 && Math.Abs(h.F32(matrix) - MathF.Sqrt(0.5f)) < 1e-3,
            $"L={h.F32(matrix)} R={h.F32(matrix + 4)}");

        long pfx = Out();
        Check("CreateFX (#4) of an unknown CLSID is CLASS_E_CLASSNOTAVAILABLE", h.CallImport("XAudio2_8.dll!#4", h.Guid(Guid.NewGuid()), pfx, 0, 0) == 0x80040111 && h.U32(pfx) == 0);
        Check("CreateFX(FXEcho)", h.CallImport("XAudio2_8.dll!#4", h.Guid(new Guid("5039d740-f736-449a-84d3-a56202557b87")), pfx, 0, 0) == SOk && h.U32(pfx) != 0);
        long fx = h.U32(pfx), pparams = Out();
        Check("it answers IXAPOParameters", M(fx, 0, h.Guid(new Guid("a90bc001-e897-e897-55e4-9e4700000001")), pparams) == SOk);
        long parameters = h.Buffer(12), readBack = h.Buffer(12);
        e.WriteMemory(parameters, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]);
        M(h.U32(pparams), 3, parameters, 12);
        M(h.U32(pparams), 4, readBack, 12);
        Check("SetParameters / GetParameters round trip", e.ReadMemory(readBack, 12).SequenceEqual(e.ReadMemory(parameters, 12)));
        long preverb = Out(), props = Out();
        Check("CreateAudioReverb (#2), registered as CLSID_AudioReverb",
            h.CallImport("XAudio2_8.dll!#2", preverb) == SOk && M(h.U32(preverb), 3, props) == SOk &&
            new Guid(e.ReadMemory(h.U32(props), 16)) == new Guid("c2633b16-471b-4498-b8c5-4f0959e2ec09"));
        long pmeter = Out();
        Check("CreateAudioVolumeMeter (#3)", h.CallImport("XAudio2_8.dll!#3", pmeter) == SOk && h.U32(pmeter) != 0);
        long fmt = h.Buffer(18);
        e.WriteMemory(fmt, WaveFormat(1, 2, 48000, 16));
        long proposed = Out();
        Check("an XAPO refuses 16-bit PCM and proposes float", M(h.U32(pmeter), 4, 0, fmt, proposed) == 0x88970001 && h.U16(h.U32(proposed)) == 3);
    }
}
