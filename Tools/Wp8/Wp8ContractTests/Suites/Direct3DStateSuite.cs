using WPR.Wp8Native;

namespace Wp8ContractTests.Suites;

/// <summary>
/// The Direct3D 11 state and resource behaviour Halo: Spartan Assault (Scaleform over D3D11) depends
/// on: resources describing themselves, CPU copies between resources, the output merger read back,
/// ID3D10Multithread, and the media engine playing a movie into a texture.
/// </summary>
public sealed class Direct3DStateSuite : IContractSuite
{
    public string Name => "direct3d state and movies";

    private const long SOk = 0, SFalse = 1;

    public void Run(Harness h)
    {
        ArmEmulator e = h.E;
        void Check(string what, bool ok, string detail = "") => h.Check(what, ok, detail);
        long M(long self, int slot, params long[] a) => h.CallMethod(self, slot, a);
        long Out() => h.Buffer(16);

        long pDevice = Out(), pLevel = Out(), pContext = Out();
        h.Call("D3D11CreateDevice", 0, 1, 0, 0, 0, 0, 7, pDevice, pLevel, pContext);
        long device = h.U32(pDevice), context = h.U32(pContext);
        Check("D3D11CreateDevice", device != 0 && context != 0);

        // D3D11_TEXTURE2D_DESC: Width, Height, MipLevels, ArraySize, Format, SampleDesc{1,0},
        // Usage, BindFlags, CPUAccessFlags, MiscFlags.
        long Desc(int w, int ht, uint format, uint usage, uint bind, uint cpu)
        {
            long d = h.Buffer(44);
            uint[] words = [(uint)w, (uint)ht, 1, 1, format, 1, 0, usage, bind, cpu, 0];
            for (int i = 0; i < words.Length; i++)
            {
                e.WriteUInt32(d + (i * 4), words[i]);
            }

            return d;
        }

        long Texture(long desc, long initial = 0)
        {
            long p = Out();
            M(device, 5, desc, initial, p);
            return h.U32(p);
        }

        long defaultDesc = Desc(8, 4, 87, 0, 0x8, 0);
        long texture = Texture(defaultDesc);
        long got = h.Buffer(44);
        M(texture, 10, got);
        Check("ID3D11Texture2D::GetDesc answers the description it was created with, not the back buffer's",
            e.ReadMemory(got, 44).SequenceEqual(e.ReadMemory(defaultDesc, 44)));
        long dim = Out();
        M(texture, 7, dim);
        Check("GetType writes D3D11_RESOURCE_DIMENSION_TEXTURE2D to its out-parameter", h.U32(dim) == 3);
        long bufferDesc = h.Buffer(24);
        e.WriteUInt32(bufferDesc, 64);
        e.WriteUInt32(bufferDesc + 8, 0x1);
        long pBuffer = Out();
        M(device, 3, bufferDesc, 0, pBuffer);
        M(h.U32(pBuffer), 7, dim);
        Check("a buffer's GetType is D3D11_RESOURCE_DIMENSION_BUFFER", h.U32(dim) == 1);

        // Staging upload, Scaleform's way: initial data in a staging twin, CopyResource into the texture.
        byte[] pixels = Enumerable.Range(0, 8 * 4 * 4).Select(i => (byte)i).ToArray();
        long sys = h.Buffer(pixels.Length);
        e.WriteMemory(sys, pixels);
        long init = h.Buffer(12);
        e.WriteUInt32(init, (uint)sys);
        e.WriteUInt32(init + 4, 8 * 4);
        long staging = Texture(Desc(8, 4, 87, 3, 0, 0x10000), init);
        M(context, 47, texture, staging);
        byte[] Contents(long resource)
        {
            long mapped = h.Buffer(12);
            M(context, 14, resource, 0, 1, 0, mapped);
            byte[] bytes = e.ReadMemory(h.U32(mapped), pixels.Length);
            M(context, 15, resource, 0);
            return bytes;
        }

        Check("CopyResource copies the staging texture's pixels into the texture", Contents(texture).SequenceEqual(pixels));

        // A texture created with no initial data reads as zeros even on a reused heap block: 2020: My
        // Country's sprite atlas showed an old loading screen between its sprites otherwise.
        long dirty = e.AllocateHeap(pixels.Length);
        e.WriteMemory(dirty, Enumerable.Repeat((byte)0xAB, pixels.Length).ToArray());
        e.FreeHeap(dirty);
        long empty = Texture(defaultDesc);
        Check("a texture created with no initial data is zeroed", Contents(empty).All(b => b == 0));

        long second = Texture(defaultDesc);
        long box = h.Buffer(24);
        foreach ((int at, uint v) in new[] { (0, 2u), (4, 1u), (8, 0u), (12, 4u), (16, 3u), (20, 1u) })
        {
            e.WriteUInt32(box + at, v);   // left 2, top 1, right 4, bottom 3
        }

        M(context, 46, second, 0, 5, 0, 0, staging, 0, box);
        byte[] region = Contents(second);
        bool regionOk = true;
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 2; x++)
            {
                int to = (y * 32) + ((5 + x) * 4), from = ((1 + y) * 32) + ((2 + x) * 4);
                regionOk &= region.AsSpan(to, 4).SequenceEqual(pixels.AsSpan(from, 4));
            }
        }

        Check("CopySubresourceRegion copies the box to (x, y)", regionOk && region.AsSpan(0, 4).SequenceEqual(new byte[4]));

        // ---- the output merger, read back ----
        long View(int slot, long resource)
        {
            long p = Out();
            M(device, slot, resource, 0, p);
            return h.U32(p);
        }

        long rtv0 = View(9, texture), rtv1 = View(9, second), dsv = View(10, texture);
        long views = h.Buffer(8);
        e.WriteUInt32(views, (uint)rtv0);
        e.WriteUInt32(views + 4, (uint)rtv1);
        M(context, 33, 2, views, dsv);
        long outViews = h.Buffer(12), outDsv = Out();
        e.WriteUInt32(outViews + 8, 0xDEAD);
        M(context, 89, 3, outViews, outDsv);
        Check("OMGetRenderTargets hands back what OMSetRenderTargets bound, null past it",
            h.U32(outViews) == rtv0 && h.U32(outViews + 4) == rtv1 && h.U32(outViews + 8) == 0 && h.U32(outDsv) == dsv);
        M(context, 33, 0, 0, 0);
        M(context, 89, 1, outViews, outDsv);
        Check("and nothing once it is unbound", h.U32(outViews) == 0 && h.U32(outDsv) == 0);

        // ---- scissor rectangles (Rovio's menus clip their scrolling lists with one) ----
        long rect = h.Buffer(16);
        foreach ((int at, uint v) in new[] { (0, 10u), (4, 200u), (8, 700u), (12, 600u) })
        {
            e.WriteUInt32(rect + at, v);
        }

        M(context, 45, 1, rect);
        Check("RSSetScissorRects records the rectangle each draw is replayed with",
            e.Direct3D.ScissorRect is [10, 200, 700, 600]);
        long count = Out(), outRect = h.Buffer(16);
        M(context, 96, count, 0);
        bool countOnly = h.U32(count) == 1;
        M(context, 96, count, outRect);
        Check("RSGetScissorRects answers the count, then the rectangle",
            countOnly && h.U32(count) == 1 && e.ReadMemory(outRect, 16).SequenceEqual(e.ReadMemory(rect, 16)));
        M(context, 45, 0, 0);
        M(context, 96, count, 0);
        Check("and none once zero rectangles are set", e.Direct3D.ScissorRect is null && h.U32(count) == 0);

        // ---- ID3D10Multithread ----
        long iid = h.Guid(new Guid("9b7e4e00-342c-4106-a19f-4f2704f689f0"));
        long pmt = Out();
        Check("the device answers ID3D10Multithread", M(device, 0, iid, pmt) == SOk && h.U32(pmt) != 0);
        long mt = h.U32(pmt);
        Check("SetMultithreadProtected returns the previous setting (a device starts protected)", M(mt, 5, 0) == 1 && M(mt, 6) == 0 && M(mt, 5, 1) == 0);
        M(mt, 3);
        M(mt, 3);
        Check("Enter nests for the holder", e.Direct3D.MultithreadLock.Depth == 2);
        M(mt, 4);
        M(mt, 4);
        Check("Leave releases it", e.Direct3D.MultithreadLock.Depth == 0);

        // ---- ID3DUserDefinedAnnotation (Halo's light pass brackets itself with BeginEvent) ----
        long aiid = h.Guid(new Guid("b2daad8b-03d4-4dbf-95eb-32ab4b63d0ab"));
        long pann = Out();
        Check("the immediate context answers ID3DUserDefinedAnnotation", M(context, 0, aiid, pann) == SOk && h.U32(pann) != 0);
        long ann = h.U32(pann);
        Check("BeginEvent/EndEvent answer -1 and GetStatus FALSE, as with no profiler attached",
            (uint)M(ann, 3, h.Bstr("BeginLightPass")) == 0xFFFFFFFF && (uint)M(ann, 4) == 0xFFFFFFFF && M(ann, 6) == 0);
        M(ann, 5, h.Bstr("marker"));
        long pnone = Out();
        e.WriteUInt32(pnone, 0xDEADBEEF);
        Check("the device does not answer it (it is a context interface)", M(device, 0, aiid, pnone) != SOk && h.U32(pnone) == 0);
        Check("and the refusal is named for the run summary", e.Direct3D.UnknownIids.Any(l => l.Contains("ID3DUserDefinedAnnotation")));

        // ---- WriteVideoFrame (TransferVideoFrame's target) ----
        byte[] frame = [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255];   // 2x2 RGBA: red green / blue white
        Check("a frame scales into a BGRA texture with its channels swapped",
            e.Direct3D.WriteVideoFrame(texture, frame, 2, 2, 0, 0, 0) &&
            Contents(texture).AsSpan(0, 4).SequenceEqual(new byte[] { 0, 0, 255, 255 }) &&
            Contents(texture).AsSpan((3 * 32) + (7 * 4), 4).SequenceEqual(new byte[] { 255, 255, 255, 255 }));
        Check("a block-compressed target is refused", !e.Direct3D.WriteVideoFrame(Texture(Desc(8, 4, 77, 0, 0x8, 0)), frame, 2, 2, 0, 0, 0));

        Movie(h, texture, () => Contents(texture));
    }

    /// <summary>A movie source through a stand-in platform decoder.</summary>
    private static void Movie(Harness h, long texture, Func<byte[]> contents)
    {
        ArmEmulator e = h.E;
        void Check(string what, bool ok, string detail = "") => h.Check(what, ok, detail);
        long M(long self, int slot, params long[] a) => h.CallMethod(self, slot, a);
        long Out() => h.Buffer(16);
        IReadOnlyList<string> events = h.S.MediaEngineEvents;

        FakeMovie movie = new();
        h.S.VideoDecoder = new FakeDecoder(movie);

        long mqi = h.Buffer(12);
        e.WriteUInt32(mqi, (uint)h.Guid(new Guid("4d645ace-26aa-4688-9be1-df3516990b93")));
        h.Call("CoCreateInstanceFromApp", h.Guid(new Guid("b44392da-499b-446b-a4cb-005fead0e6d5")), 0, 1, 0, 1, mqi);
        long factory = h.U32(mqi + 4);
        long pattr = Out();
        h.Call("MFCreateAttributes", pattr, 1);
        long attr = h.U32(pattr);
        M(attr, 27, h.Guid(new Guid("c60381b8-83a4-41f8-a3d0-de05076849a9")), factory);   // any object with a slot 3
        long pe = Out();
        M(factory, 3, 0, attr, pe);
        long engine = h.U32(pe);
        long answer = Out();
        Check("with a video decoder CanPlayType(video/x-ms-wmv) is MAYBE", M(engine, 13, h.Bstr("video/x-ms-wmv"), answer) == SOk && h.U32(answer) == 1);

        // An ASF header is all the engine sniffs; the decoder stands in for the rest.
        Directory.CreateDirectory(h.Sandbox);
        File.WriteAllBytes(Path.Combine(h.Sandbox, "intro.wmv"),
            [0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C, 0, 0, 0, 0]);
        int before = events.Count;
        M(engine, 6, h.Bstr("ms-appdata:///local/intro.wmv"));
        Check("a movie loads (no ERROR) and the decoder was given the file", !events.Skip(before).Any(x => x.StartsWith("ERROR", StringComparison.Ordinal)) &&
            events.Skip(before).Contains("CANPLAYTHROUGH") && movie.OpenedPath?.EndsWith("intro.wmv", StringComparison.Ordinal) == true,
            string.Join(",", events.Skip(before)));
        Check("HasVideo and HasAudio", M(engine, 38) == 1 && M(engine, 39) == 1);
        long cx = Out(), cy = Out();
        Check("GetNativeVideoSize is the movie's", M(engine, 40, cx, cy) == SOk && h.U32(cx) == 4 && h.U32(cy) == 2);
        Check("GetVideoAspectRatio is reduced (2:1)", M(engine, 41, cx, cy) == SOk && h.U32(cx) == 2 && h.U32(cy) == 1);
        M(engine, 19);
        Check("the duration is the movie's, though its soundtrack is shorter", Math.Abs(h.D64(0) - 1.0) < 0.01, $"{h.D64(0)}");

        M(engine, 32);
        long pts = Out();
        Check("OnVideoStreamTick: S_OK with a time when a frame is due", M(engine, 44, pts) == SOk);
        Check("and S_FALSE when nothing newer is", M(engine, 44, pts) == SFalse);
        Check("TransferVideoFrame paints the frame into the game's texture",
            M(engine, 43, texture, 0, 0, 0) == SOk && contents().AsSpan(0, 4).SequenceEqual(new byte[] { 30, 20, 10, 255 }));
        before = events.Count;
        h.S.TickMedia(0.6);
        h.S.TickMedia(0.6);
        Check("playing to the movie's length raises ENDED", events.Skip(before).Contains("ENDED"), string.Join(",", events.Skip(before)));

        // A byte-stream source holding a file's exact bytes is handed over as that file, not as a
        // temp copy, so a platform that plays only prepared sidecars (Android) finds them.
        byte[] asf = [0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C, 7, 7, 7, 7, 7];
        Directory.CreateDirectory(Path.Combine(h.Sandbox, "Movies"));
        File.WriteAllBytes(Path.Combine(h.Sandbox, "Movies", "logo.wmv"), asf);
        long ByteStream(byte[] content)
        {
            long ps = Out();
            h.Call("CreateStreamOnHGlobal", 0, 1, ps);
            long buffer = h.Buffer(content.Length);
            e.WriteMemory(buffer, content);
            long stream = h.U32(ps);
            M(stream, 4, buffer, content.Length, Out());   // the engine reads a byte stream whole, wherever it stands
            long pbs = Out();
            h.Call("MFCreateMFByteStreamOnStream", stream, pbs);
            return h.U32(pbs);
        }

        movie.OpenedPath = null;
        M(engine, 45, ByteStream(asf), h.Bstr("Movies/logo.wmv"));
        Check("a byte stream of a file's bytes is opened as that file",
            movie.OpenedPath?.EndsWith(Path.Combine("Movies", "logo.wmv"), StringComparison.OrdinalIgnoreCase) == true, movie.OpenedPath ?? "(not opened)");
        movie.OpenedPath = null;
        M(engine, 45, ByteStream(asf), h.Bstr("anything"));
        Check("found by content when its name says nothing",
            movie.OpenedPath?.EndsWith(Path.Combine("Movies", "logo.wmv"), StringComparison.OrdinalIgnoreCase) == true, movie.OpenedPath ?? "(not opened)");
        movie.OpenedPath = null;
        byte[] other = (byte[])asf.Clone();
        other[^1] = 9;
        M(engine, 45, ByteStream(other), h.Bstr("Movies/logo.wmv"));
        Check("a stream matching no file goes through a temp file",
            movie.OpenedPath?.Contains("wpr-wp8-media", StringComparison.Ordinal) == true, movie.OpenedPath ?? "(not opened)");
        M(engine, 42);
        Check("Shutdown closes the movie", movie.Disposed);
        h.S.VideoDecoder = null;
    }

    private sealed class FakeDecoder(FakeMovie movie) : IMediaVideoDecoder
    {
        public string Name => "test";

        public IMediaVideoStream? Open(string path)
        {
            movie.OpenedPath = path;
            return movie;
        }
    }

    /// <summary>4x2, 1 s, half a second of 8 kHz mono sound, every frame RGB(10, 20, 30).</summary>
    private sealed class FakeMovie : IMediaVideoStream
    {
        private bool _audioDone;
        private bool _frameGiven;

        public string? OpenedPath { get; set; }

        public bool Disposed { get; private set; }

        public int Width => 4;

        public int Height => 2;

        public double DurationSeconds => 1.0;

        public int AudioChannels => 1;

        public int AudioSampleRate => 8000;

        public bool VideoEnded => _frameGiven;

        public bool TryGetFrame(double seconds, byte[] rgba)
        {
            if (_frameGiven)
            {
                return false;
            }

            for (int i = 0; i < rgba.Length; i += 4)
            {
                (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = ((byte)10, (byte)20, (byte)30, (byte)255);
            }

            _frameGiven = true;
            return true;
        }

        public int ReadAudio(byte[] buffer, int offset, int count)
        {
            if (_audioDone)
            {
                return -1;
            }

            int bytes = Math.Min(count, 8000);   // 0.5 s of mono 16-bit
            Array.Clear(buffer, offset, bytes);
            _audioDone = true;
            return bytes;
        }

        public void Dispose() => Disposed = true;
    }
}
