namespace WPR.Wp8Native
{
    /// <summary>
    /// The state a frame is assembled from, and a software rasteriser that turns it into
    /// pixels.
    /// </summary>
    /// <remarks>
    /// The Direct3D layer answers calls; this remembers what was in them. Every resource
    /// gets real backing store, every bind is recorded, and at Present the recorded draws
    /// are walked and rasterised into an 800x480 image.
    ///
    /// This is emphatically not a Direct3D implementation. It handles the one shape this
    /// title actually draws - indexed, textured, alpha-blended triangles whose vertices
    /// carry a position and a texture coordinate - and it runs no shaders at all: the
    /// position is taken from the vertex as the input layout describes it, and if that
    /// position is not already in clip space the first constant buffer is tried as a 4x4
    /// transform. A 2D sprite engine is almost entirely this shape, which is why the
    /// approach is worth anything; a 3D title would need the real thing.
    /// </remarks>
    public sealed class FrameCapture
    {
        /// <summary>
        /// The size of the surface everything is rasterised into, which is also the size the
        /// image is told its window and back buffer are.
        /// </summary>
        /// <remarks>
        /// One setting, three consumers - the window bounds this runtime reports, the swap
        /// chain description it answers with, and the raster target here - because a game
        /// lays out from the first and projects into the second, and any disagreement between
        /// them shows up as art that is the wrong shape rather than as an error.
        /// <para>
        /// <c>WPR_SCREEN=WxH</c> overrides it, which is how the right value was found: the
        /// image's own vertices are texel-exact at exactly one size.
        /// </para>
        /// </remarks>
        public static int Width { get; private set; } = 800;

        public static int Height { get; private set; } = 480;

        static FrameCapture()
        {
            string? size = Environment.GetEnvironmentVariable("WPR_SCREEN");
            if (size is null)
            {
                return;
            }

            string[] parts = size.Split('x', 'X');
            if (parts.Length == 2 &&
                int.TryParse(parts[0], out int width) && int.TryParse(parts[1], out int height) &&
                width > 0 && height > 0 && width <= 4096 && height <= 4096)
            {
                Width = width;
                Height = height;
            }
        }

        /// <summary>A Direct3D resource, with the bytes actually in it.</summary>
        public sealed class Resource
        {
            public required string Name { get; init; }

            /// <summary>Emulated memory backing this resource, so Map can hand it out.</summary>
            public long Storage { get; set; }

            public long StorageSize { get; set; }

            public int PixelWidth { get; set; }

            public int PixelHeight { get; set; }

            public uint Format { get; set; }

            /// <summary>Row stride in bytes, as the image uploaded it.</summary>
            /// <remarks>
            /// For a block-compressed texture this is a row of 4x4 *blocks*, not pixels, and
            /// <see cref="Rows"/> counts block rows. Treating one as four bytes per pixel
            /// reads a quarter of the data at four times the stride, which does not look like
            /// a format mistake - it looks like torn noise.
            /// </remarks>
            public int RowPitch { get; set; }

            /// <summary>How many rows the upload has - pixel rows, or block rows.</summary>
            public int Rows { get; set; }

            /// <summary>Bytes per 4x4 block, or zero when the texture is not compressed.</summary>
            public int BlockBytes { get; set; }

            /// <summary>
            /// Bytes per pixel for an uncompressed texture: four normally, two for the 16-bit
            /// formats.
            /// </summary>
            /// <remarks>
            /// Not every uncompressed texture is 32-bit, and assuming so is not a subtle
            /// error. A phone game stores its UI as B4G4R4A4 to halve the memory, and reading
            /// that at four bytes a pixel takes half the row at twice the stride: the result
            /// is a band of horizontal stripes, which reads as a missing sprite rather than as
            /// a format mistake. Angry Birds Rio's episode list is drawn this way.
            /// </remarks>
            public int PixelBytes { get; set; } = 4;

            /// <summary>True once anything has been written into it.</summary>
            public bool HasContent { get; set; }
        }

        /// <summary>One element of a vertex, as CreateInputLayout described it.</summary>
        public sealed record VertexElement(string Semantic, uint Index, uint Format, int Offset, int Slot);

        /// <summary>One vertex buffer bound to one input slot.</summary>
        /// <remarks>
        /// There is more than one, and that is the whole point of recording them separately.
        /// This title puts POSITION in slot 0 and TEXCOORD in slot 1, both at element offset
        /// zero - so a rasteriser that keeps only the first buffer reads the texture
        /// coordinate out of the position, and every quad ends up sampling itself.
        /// </remarks>
        public sealed record VertexStream(Resource? Buffer, int Stride, int Offset)
        {
            /// <summary>The bytes this stream held when the draw was issued.</summary>
            public byte[]? Data { get; init; }

            /// <summary>Which vertex <see cref="Data"/> starts at.</summary>
            public int FirstVertex { get; init; }
        }

        /// <summary>Everything bound at the moment a draw was issued.</summary>
        /// <summary>
        /// A draw, with the geometry it referred to copied out at the moment it was issued.
        /// </summary>
        /// <remarks>
        /// The copy is the whole point. This engine draws a frame by mapping one dynamic
        /// vertex buffer, writing a quad, drawing it, and rewriting the same buffer for the
        /// next quad - so reading the buffer later, at Present, gives every draw in the frame
        /// the *last* quad's geometry. The symptom was three different textures landing in
        /// exactly the same place with exactly the same texture coordinates, and most of the
        /// screen left empty.
        /// </remarks>
        public sealed record DrawCall(
            int IndexCount,
            int StartIndex,
            int BaseVertex,
            IReadOnlyList<VertexStream> Streams,
            Resource? IndexBuffer,
            uint IndexFormat,
            int IndexOffset,
            Resource? Texture,
            IReadOnlyList<VertexElement> Layout,
            float[]? Transform,
            uint Topology)
        {
            /// <summary>The indices this draw used, resolved when it was issued.</summary>
            public int[] Indices { get; init; } = [];

            public VertexStream? StreamFor(VertexElement element)
                => element.Slot >= 0 && element.Slot < Streams.Count ? Streams[element.Slot] : null;
        }

        /// <summary>
        /// Copies the geometry a draw refers to out of emulated memory, so that rewriting the
        /// buffer afterwards cannot change what this draw meant.
        /// </summary>
        public static DrawCall Snapshot(ArmEmulator emulator, DrawCall call)
        {
            int[] indices = ResolveIndices(emulator, call);
            if (indices.Length == 0)
            {
                return call with { Indices = indices };
            }

            int lowest = int.MaxValue;
            int highest = int.MinValue;
            foreach (int index in indices)
            {
                lowest = Math.Min(lowest, index);
                highest = Math.Max(highest, index);
            }

            var streams = new List<VertexStream>(call.Streams.Count);
            foreach (VertexStream stream in call.Streams)
            {
                streams.Add(SnapshotStream(emulator, call, stream, lowest, highest));
            }

            return call with { Indices = indices, Streams = streams };
        }

        private static VertexStream SnapshotStream(
            ArmEmulator emulator, DrawCall call, VertexStream stream, int lowest, int highest)
        {
            if (stream.Buffer is null || stream.Buffer.Storage == 0 || stream.Stride <= 0)
            {
                return stream;
            }

            int first = lowest + call.BaseVertex;
            int count = highest - lowest + 1;
            long length = (long)count * stream.Stride;

            if (count <= 0 || length > 8 * 1024 * 1024)
            {
                return stream;
            }

            try
            {
                byte[] data = emulator.ReadMemory(
                    stream.Buffer.Storage + stream.Offset + ((long)first * stream.Stride),
                    (int)length);

                return stream with { Data = data, FirstVertex = first };
            }
            catch (Exception)
            {
                return stream;
            }
        }

        private readonly List<DrawCall> _draws = new();

        /// <summary>Draw calls recorded for the frame being assembled.</summary>
        public IReadOnlyList<DrawCall> PendingDraws => _draws;

        public void Record(DrawCall call) => _draws.Add(call);

        public void BeginFrame(float[] clear)
        {
            _draws.Clear();
            ClearColour = clear;
        }

        public float[] ClearColour { get; private set; } = [0f, 0f, 0f, 1f];

        /// <summary>What the rasteriser saw, for working out the coordinate conventions.</summary>
        public List<string> Notes { get; } = new();

        // -------------------------------------------------------------------
        // Rasterising
        // -------------------------------------------------------------------

        /// <summary>
        /// Draws the recorded calls into an RGBA image.
        /// </summary>
        public byte[] Rasterise(ArmEmulator emulator, out string summary)
        {
            byte[] pixels = new byte[Width * Height * 4];
            for (int i = 0; i < Width * Height; i++)
            {
                pixels[(i * 4) + 0] = (byte)Math.Clamp(ClearColour[0] * 255f, 0f, 255f);
                pixels[(i * 4) + 1] = (byte)Math.Clamp(ClearColour[1] * 255f, 0f, 255f);
                pixels[(i * 4) + 2] = (byte)Math.Clamp(ClearColour[2] * 255f, 0f, 255f);
                pixels[(i * 4) + 3] = 255;
            }

            int triangles = 0;
            int textured = 0;
            int skipped = 0;

            foreach (DrawCall call in _draws)
            {
                VertexElement? position = Find(call.Layout, "POSITION");
                VertexElement? uv = Find(call.Layout, "TEXCOORD");
                if (position is null || call.StreamFor(position) is not { Buffer: not null, Stride: > 0 })
                {
                    skipped++;
                    continue;
                }

                int[] indices = call.Indices;
                if (indices.Length < 3)
                {
                    skipped++;
                    continue;
                }

                if (call.Texture is not null && call.Texture.HasContent)
                {
                    textured++;
                }

                // The first few vertices of the first draw, so the coordinate space they are
                // actually in is a fact rather than an inference.
                if (Notes.Count < 60)
                {
                    Notes.Add($"draw: {indices.Length} indices, " +
                              $"streams {string.Join("/", call.Streams.Select(v => v.Buffer is null ? "-" : $"{v.Stride}b"))}, " +
                              $"texture {call.Texture?.Name ?? "none"} " +
                              $"{call.Texture?.PixelWidth}x{call.Texture?.PixelHeight} " +
                              $"fmt {call.Texture?.Format}, " +
                              $"transform {(call.Transform is null ? "none" : string.Join(",", call.Transform.Take(6).Select(f => f.ToString("0.###"))))}, " +
                              $"layout {string.Join("/", call.Layout.Select(e => $"{e.Semantic}@{e.Offset}:{e.Format}"))}");

                    for (int v = 0; v < Math.Min(4, indices.Length); v++)
                    {
                        Vertex sample = ReadVertex(call, indices[v], position, uv);
                        ToScreen(sample, out float sx, out float sy);
                        Notes.Add($"   v{indices[v]} pos=({sample.X:0.###}, {sample.Y:0.###}, " +
                                  $"{sample.Z:0.###}, {sample.W:0.###}) uv=({sample.U:0.###}, {sample.V:0.###}) " +
                                  $"screen=({sx:0}, {sy:0})");
                    }
                }

                for (int i = 0; i + 2 < indices.Length; i += 3)
                {
                    Vertex a = ReadVertex(call, indices[i + 0], position, uv);
                    Vertex b = ReadVertex(call, indices[i + 1], position, uv);
                    Vertex c = ReadVertex(call, indices[i + 2], position, uv);

                    if (FillTriangle(emulator, pixels, call, a, b, c))
                    {
                        triangles++;
                    }
                }
            }

            summary = $"{_draws.Count} draw(s), {triangles} triangle(s) rasterised, " +
                      $"{textured} textured, {skipped} skipped";
            return pixels;
        }

        private static VertexElement? Find(IReadOnlyList<VertexElement> layout, string semantic)
        {
            foreach (VertexElement element in layout)
            {
                if (element.Semantic.StartsWith(semantic, StringComparison.OrdinalIgnoreCase))
                {
                    return element;
                }
            }

            return null;
        }

        private readonly record struct Vertex(float X, float Y, float Z, float W, float U, float V);

        private static Vertex ReadVertex(
            DrawCall call, int index, VertexElement position, VertexElement? uv)
        {
            float[] p = ReadElement(call, index, position);
            float[] t = uv is null ? [0f, 0f] : ReadElement(call, index, uv);

            float x = p[0];
            float y = p[1];
            float z = p.Length > 2 ? p[2] : 0f;
            float w = p.Length > 3 ? p[3] : 1f;

            // A 2D engine usually hands the shader pixel or world coordinates and a matrix
            // in a constant buffer. Applying it here stands in for the vertex shader; if
            // there is no matrix the position is taken as already being in clip space.
            if (call.Transform is { Length: 16 })
            {
                // Row-major: HLSL packs a float4x4 constant column-major by default, and
                // the shader does mul(position, matrix) with a row vector - which comes to
                // taking the dot product with each *row* of what is in the buffer. Doing it
                // the other way round transposes the transform, which for a 2D orthographic
                // projection means the image comes out mirrored and in the wrong quadrant
                // rather than obviously broken.
                float[] m = call.Transform;
                float tx = (x * m[0]) + (y * m[1]) + (z * m[2]) + (w * m[3]);
                float ty = (x * m[4]) + (y * m[5]) + (z * m[6]) + (w * m[7]);
                float tz = (x * m[8]) + (y * m[9]) + (z * m[10]) + (w * m[11]);
                float tw = (x * m[12]) + (y * m[13]) + (z * m[14]) + (w * m[15]);
                x = tx;
                y = ty;
                z = tz;
                w = tw;
            }

            return new Vertex(x, y, z, w, t[0], t.Length > 1 ? t[1] : 0f);
        }

        /// <summary>Reads one attribute from whichever stream its input slot names.</summary>
        private static float[] ReadElement(DrawCall call, int index, VertexElement element)
        {
            VertexStream? stream = call.StreamFor(element);
            if (stream?.Data is null || stream.Stride <= 0)
            {
                return [0f, 0f];
            }

            int at = (((index + call.BaseVertex) - stream.FirstVertex) * stream.Stride) + element.Offset;
            return ReadFormat(stream.Data, at, element.Format);
        }

        /// <summary>Reads a vertex attribute in whichever DXGI format the layout named.</summary>
        private static float[] ReadFormat(byte[] data, int address, uint format)
        {
            try
            {
                if (address < 0 || address + 4 > data.Length)
                {
                    return [0f, 0f];
                }

                switch (format)
                {
                    case 2:  // R32G32B32A32_FLOAT
                        return [F(0), F(4), F(8), F(12)];
                    case 6:  // R32G32B32_FLOAT
                        return [F(0), F(4), F(8)];
                    case 16: // R32G32_FLOAT
                        return [F(0), F(4)];
                    case 41: // R32_FLOAT
                        return [F(0)];
                    case 28: // R8G8B8A8_UNORM
                    case 87: // B8G8R8A8_UNORM
                        return
                        [
                            data[address] / 255f, data[address + 1] / 255f,
                            data[address + 2] / 255f, data[address + 3] / 255f,
                        ];

                    case 34: // R16G16_FLOAT is rare here; treat as two shorts scaled
                        return [S(0) / 32767f, S(2) / 32767f];
                    default:
                        return [F(0), F(4)];
                }

                float F(int offset) => address + offset + 4 <= data.Length
                    ? BitConverter.ToSingle(data, address + offset)
                    : 0f;

                short S(int offset) => address + offset + 2 <= data.Length
                    ? BitConverter.ToInt16(data, address + offset)
                    : (short)0;
            }
            catch (Exception)
            {
                return [0f, 0f];
            }
        }

        private static int[] ResolveIndices(ArmEmulator emulator, DrawCall call)
        {
            int count = Math.Clamp(call.IndexCount, 0, 200_000);
            int[] indices = new int[count];

            if (call.IndexBuffer is null || call.IndexBuffer.Storage == 0)
            {
                // Not indexed: the draw walks the vertex buffer directly.
                for (int i = 0; i < count; i++)
                {
                    indices[i] = call.StartIndex + i;
                }

                return indices;
            }

            bool sixteenBit = call.IndexFormat != 42; // DXGI_FORMAT_R32_UINT
            int stride = sixteenBit ? 2 : 4;
            long start = call.IndexBuffer.Storage + call.IndexOffset + ((long)call.StartIndex * stride);

            try
            {
                byte[] raw = emulator.ReadMemory(start, count * stride);
                for (int i = 0; i < count; i++)
                {
                    indices[i] = sixteenBit
                        ? BitConverter.ToUInt16(raw, i * 2)
                        : (int)BitConverter.ToUInt32(raw, i * 4);
                }
            }
            catch (Exception)
            {
                return [];
            }

            return indices;
        }

        /// <summary>
        /// Fills one triangle, sampling the bound texture and blending over what is there.
        /// </summary>
        private static bool FillTriangle(
            ArmEmulator emulator, byte[] pixels, DrawCall call, Vertex a, Vertex b, Vertex c)
        {
            // Clip space to pixels. w of zero means the transform produced nothing usable.
            if (!ToScreen(a, out float ax, out float ay) ||
                !ToScreen(b, out float bx, out float by) ||
                !ToScreen(c, out float cx, out float cy))
            {
                return false;
            }

            float area = ((bx - ax) * (cy - ay)) - ((by - ay) * (cx - ax));
            if (Math.Abs(area) < 0.0001f)
            {
                return false;
            }

            int minX = Math.Max(0, (int)MathF.Floor(Math.Min(ax, Math.Min(bx, cx))));
            int maxX = Math.Min(Width - 1, (int)MathF.Ceiling(Math.Max(ax, Math.Max(bx, cx))));
            int minY = Math.Max(0, (int)MathF.Floor(Math.Min(ay, Math.Min(by, cy))));
            int maxY = Math.Min(Height - 1, (int)MathF.Ceiling(Math.Max(ay, Math.Max(by, cy))));

            if (minX > maxX || minY > maxY)
            {
                return false;
            }

            byte[]? texture = LoadTexture(emulator, call.Texture);

            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    float px = x + 0.5f;
                    float py = y + 0.5f;

                    float w0 = (((bx - px) * (cy - py)) - ((by - py) * (cx - px))) / area;
                    float w1 = (((cx - px) * (ay - py)) - ((cy - py) * (ax - px))) / area;
                    float w2 = 1f - w0 - w1;

                    if (w0 < 0f || w1 < 0f || w2 < 0f)
                    {
                        continue;
                    }

                    float u = (w0 * a.U) + (w1 * b.U) + (w2 * c.U);
                    float v = (w0 * a.V) + (w1 * b.V) + (w2 * c.V);

                    Sample(texture, call.Texture, u, v, out byte r, out byte g, out byte bl, out byte alpha);
                    if (alpha == 0)
                    {
                        continue;
                    }

                    int at = ((y * Width) + x) * 4;
                    float mix = alpha / 255f;
                    pixels[at + 0] = (byte)((r * mix) + (pixels[at + 0] * (1f - mix)));
                    pixels[at + 1] = (byte)((g * mix) + (pixels[at + 1] * (1f - mix)));
                    pixels[at + 2] = (byte)((bl * mix) + (pixels[at + 2] * (1f - mix)));
                    pixels[at + 3] = 255;
                }
            }

            return true;
        }

        private static bool ToScreen(Vertex v, out float x, out float y)
        {
            float w = Math.Abs(v.W) < 0.00001f ? 1f : v.W;
            float clipX = v.X / w;
            float clipY = v.Y / w;

            x = (clipX + 1f) * 0.5f * Width;
            y = (1f - clipY) * 0.5f * Height;

            return float.IsFinite(x) && float.IsFinite(y) &&
                   Math.Abs(x) < Width * 64 && Math.Abs(y) < Height * 64;
        }

        private static readonly Dictionary<long, byte[]> TextureCache = new();

        private static byte[]? LoadTexture(ArmEmulator emulator, Resource? texture)
        {
            if (texture is null || !texture.HasContent || texture.Storage == 0 ||
                texture.PixelWidth <= 0 || texture.PixelHeight <= 0)
            {
                return null;
            }

            if (TextureCache.TryGetValue(texture.Storage, out byte[]? cached))
            {
                return cached;
            }

            try
            {
                int bytes = texture.RowPitch * Math.Max(texture.Rows, 1);
                byte[] raw = emulator.ReadMemory(texture.Storage, Math.Min(bytes, 32 * 1024 * 1024));

                if (texture.BlockBytes > 0)
                {
                    raw = DecodeBlocks(raw, texture);
                }

                TextureCache[texture.Storage] = raw;
                return raw;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Expands a BC1/BC2/BC3 texture to RGBA.
        /// </summary>
        /// <remarks>
        /// Every block is 4x4 pixels. The colour half is the same in all three: two RGB565
        /// endpoints and sixteen two-bit selectors. BC1 gets its alpha from the endpoint
        /// ordering - when the first endpoint is not greater than the second, selector 3 is
        /// transparent - while BC2 carries four bits of alpha per pixel and BC3 carries two
        /// endpoints and sixteen three-bit selectors of its own.
        /// </remarks>
        private static byte[] DecodeBlocks(byte[] source, Resource texture)
        {
            int width = texture.PixelWidth;
            int height = texture.PixelHeight;
            byte[] rgba = new byte[width * height * 4];

            int blocksAcross = Math.Max(1, (width + 3) / 4);
            int blocksDown = Math.Max(1, (height + 3) / 4);
            int blockBytes = texture.BlockBytes;
            bool hasAlphaBlock = blockBytes == 16;
            bool sharpAlpha = texture.Format is 76 or 77 or 78; // BC3

            for (int by = 0; by < blocksDown; by++)
            {
                for (int bx = 0; bx < blocksAcross; bx++)
                {
                    int at = (by * texture.RowPitch) + (bx * blockBytes);
                    if (at + blockBytes > source.Length)
                    {
                        continue;
                    }

                    int colourAt = hasAlphaBlock ? at + 8 : at;
                    ushort c0 = BitConverter.ToUInt16(source, colourAt);
                    ushort c1 = BitConverter.ToUInt16(source, colourAt + 2);
                    uint selectors = BitConverter.ToUInt32(source, colourAt + 4);

                    Span<int> red = stackalloc int[4];
                    Span<int> green = stackalloc int[4];
                    Span<int> blue = stackalloc int[4];
                    Span<int> alpha = stackalloc int[4];

                    Unpack565(c0, out red[0], out green[0], out blue[0]);
                    Unpack565(c1, out red[1], out green[1], out blue[1]);
                    alpha[0] = alpha[1] = alpha[2] = alpha[3] = 255;

                    if (c0 > c1 || hasAlphaBlock)
                    {
                        for (int i = 0; i < 3; i++)
                        {
                            red[2] = ((2 * red[0]) + red[1]) / 3;
                            green[2] = ((2 * green[0]) + green[1]) / 3;
                            blue[2] = ((2 * blue[0]) + blue[1]) / 3;
                            red[3] = (red[0] + (2 * red[1])) / 3;
                            green[3] = (green[0] + (2 * green[1])) / 3;
                            blue[3] = (blue[0] + (2 * blue[1])) / 3;
                        }
                    }
                    else
                    {
                        red[2] = (red[0] + red[1]) / 2;
                        green[2] = (green[0] + green[1]) / 2;
                        blue[2] = (blue[0] + blue[1]) / 2;
                        red[3] = green[3] = blue[3] = 0;
                        alpha[3] = 0;
                    }

                    // BC3's alpha: two endpoints and sixteen three-bit selectors.
                    Span<int> alphaRamp = stackalloc int[8];
                    ulong alphaBits = 0;
                    if (sharpAlpha)
                    {
                        int a0 = source[at];
                        int a1 = source[at + 1];
                        alphaRamp[0] = a0;
                        alphaRamp[1] = a1;
                        if (a0 > a1)
                        {
                            for (int i = 1; i < 7; i++)
                            {
                                alphaRamp[i + 1] = (((7 - i) * a0) + (i * a1)) / 7;
                            }
                        }
                        else
                        {
                            for (int i = 1; i < 5; i++)
                            {
                                alphaRamp[i + 1] = (((5 - i) * a0) + (i * a1)) / 5;
                            }

                            alphaRamp[6] = 0;
                            alphaRamp[7] = 255;
                        }

                        for (int i = 0; i < 6; i++)
                        {
                            alphaBits |= (ulong)source[at + 2 + i] << (8 * i);
                        }
                    }

                    for (int py = 0; py < 4; py++)
                    {
                        for (int px = 0; px < 4; px++)
                        {
                            int x = (bx * 4) + px;
                            int y = (by * 4) + py;
                            if (x >= width || y >= height)
                            {
                                continue;
                            }

                            int pixel = (py * 4) + px;
                            int selector = (int)((selectors >> (pixel * 2)) & 3);
                            int out_ = ((y * width) + x) * 4;

                            rgba[out_ + 0] = (byte)red[selector];
                            rgba[out_ + 1] = (byte)green[selector];
                            rgba[out_ + 2] = (byte)blue[selector];

                            rgba[out_ + 3] = sharpAlpha
                                ? (byte)alphaRamp[(int)((alphaBits >> (pixel * 3)) & 7)]
                                : hasAlphaBlock
                                    ? (byte)(((source[at + (pixel / 2)] >> ((pixel % 2) * 4)) & 0xF) * 17)
                                    : (byte)alpha[selector];
                        }
                    }
                }
            }

            // Decoded output is plain RGBA in that channel order, whatever the source said.
            texture.Format = 28;
            return rgba;
        }

        private static void Unpack565(ushort value, out int r, out int g, out int b)
        {
            r = ((value >> 11) & 0x1F) * 255 / 31;
            g = ((value >> 5) & 0x3F) * 255 / 63;
            b = (value & 0x1F) * 255 / 31;
        }

        private static void Sample(
            byte[]? texture, Resource? resource, float u, float v,
            out byte r, out byte g, out byte b, out byte a)
        {
            if (texture is null || resource is null)
            {
                // No texture bound, or nothing in it: draw the geometry as flat white so the
                // shape is visible rather than invisible. An untextured quad that vanishes
                // looks exactly like a draw that never happened.
                r = g = b = 220;
                a = 255;
                return;
            }

            int x = Math.Clamp((int)(u * resource.PixelWidth), 0, resource.PixelWidth - 1);
            int y = Math.Clamp((int)(v * resource.PixelHeight), 0, resource.PixelHeight - 1);

            // A decoded block-compressed texture is plain RGBA, so its stride is the pixel
            // width whatever the stored pitch was.
            bool decoded = resource.BlockBytes > 0;
            int each = decoded ? 4 : resource.PixelBytes;
            int pitch = decoded ? resource.PixelWidth * 4 : resource.RowPitch;
            int at = (y * pitch) + (x * each);

            if (at < 0 || at + each - 1 >= texture.Length)
            {
                r = g = b = 220;
                a = 255;
                return;
            }

            if (each == 2)
            {
                // Little-endian 16-bit. Each channel is expanded rather than shifted: 4 bits
                // of 0xF must come out 255, not 240, or every white in the image is grey.
                int packed = texture[at] | (texture[at + 1] << 8);

                switch (resource.Format)
                {
                    case 115:   // B4G4R4A4
                        b = (byte)((packed & 0xF) * 17);
                        g = (byte)(((packed >> 4) & 0xF) * 17);
                        r = (byte)(((packed >> 8) & 0xF) * 17);
                        a = (byte)(((packed >> 12) & 0xF) * 17);
                        return;

                    case 86:    // B5G5R5A1
                        b = (byte)(((packed & 0x1F) * 255) / 31);
                        g = (byte)((((packed >> 5) & 0x1F) * 255) / 31);
                        r = (byte)((((packed >> 10) & 0x1F) * 255) / 31);
                        a = (byte)((packed & 0x8000) != 0 ? 255 : 0);
                        return;

                    default:    // 85, B5G6R5 - no alpha channel at all
                        b = (byte)(((packed & 0x1F) * 255) / 31);
                        g = (byte)((((packed >> 5) & 0x3F) * 255) / 63);
                        r = (byte)((((packed >> 11) & 0x1F) * 255) / 31);
                        a = 255;
                        return;
                }
            }

            // Channel order follows the format rather than a guess. This title's art is
            // R8G8B8A8 (28) while the swap chain is B8G8R8A8 (87), and reading one as the
            // other swaps red and blue - which is subtle enough to look like a stylistic
            // choice until a red bird comes out blue.
            bool bgra = resource.Format is 87 or 88 or 91 or 93;
            r = texture[at + (bgra ? 2 : 0)];
            g = texture[at + 1];
            b = texture[at + (bgra ? 0 : 2)];
            a = texture[at + 3];
        }

        /// <summary>
        /// Writes an RGBA image as a PNG, with no compression beyond zlib's stored blocks.
        /// </summary>
        /// <remarks>
        /// Hand-rolled because the probe has no image library and pulling one in for a
        /// diagnostic would be the tail wagging the dog. Stored deflate blocks make the file
        /// large and the code short, which is the right trade for something written once and
        /// read by an image viewer.
        /// </remarks>
        public static void WritePng(string path, byte[] rgba, int width, int height)
        {
            using FileStream file = File.Create(path);

            file.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

            byte[] header = new byte[13];
            WriteBigEndian(header, 0, (uint)width);
            WriteBigEndian(header, 4, (uint)height);
            header[8] = 8;  // bit depth
            header[9] = 6;  // colour type: RGBA
            WriteChunk(file, "IHDR", header);

            // Each row is prefixed with a filter byte of zero.
            byte[] raw = new byte[(width * 4 + 1) * height];
            for (int y = 0; y < height; y++)
            {
                int from = y * width * 4;
                int to = y * ((width * 4) + 1);
                raw[to] = 0;
                Array.Copy(rgba, from, raw, to + 1, width * 4);
            }

            WriteChunk(file, "IDAT", Deflate(raw));
            WriteChunk(file, "IEND", []);
        }

        private static byte[] Deflate(byte[] data)
        {
            using MemoryStream output = new();
            output.WriteByte(0x78); // zlib header: deflate, 32K window
            output.WriteByte(0x01); // no compression

            int position = 0;
            while (position < data.Length)
            {
                int length = Math.Min(65535, data.Length - position);
                bool last = position + length >= data.Length;

                output.WriteByte((byte)(last ? 1 : 0));
                output.WriteByte((byte)(length & 0xFF));
                output.WriteByte((byte)(length >> 8));
                output.WriteByte((byte)(~length & 0xFF));
                output.WriteByte((byte)((~length >> 8) & 0xFF));
                output.Write(data, position, length);
                position += length;
            }

            WriteBigEndianTo(output, Adler32(data));
            return output.ToArray();
        }

        private static uint Adler32(byte[] data)
        {
            uint a = 1;
            uint b = 0;
            foreach (byte value in data)
            {
                a = (a + value) % 65521;
                b = (b + a) % 65521;
            }

            return (b << 16) | a;
        }

        private static void WriteChunk(Stream file, string type, byte[] data)
        {
            byte[] length = new byte[4];
            WriteBigEndian(length, 0, (uint)data.Length);
            file.Write(length);

            byte[] body = new byte[4 + data.Length];
            for (int i = 0; i < 4; i++)
            {
                body[i] = (byte)type[i];
            }

            Array.Copy(data, 0, body, 4, data.Length);
            file.Write(body);

            byte[] crc = new byte[4];
            WriteBigEndian(crc, 0, Crc32(body));
            file.Write(crc);
        }

        private static void WriteBigEndian(byte[] buffer, int at, uint value)
        {
            buffer[at + 0] = (byte)(value >> 24);
            buffer[at + 1] = (byte)(value >> 16);
            buffer[at + 2] = (byte)(value >> 8);
            buffer[at + 3] = (byte)value;
        }

        private static void WriteBigEndianTo(Stream stream, uint value)
        {
            stream.WriteByte((byte)(value >> 24));
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            uint[] table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                table[i] = c;
            }

            return table;
        }

        private static uint Crc32(byte[] data)
        {
            uint c = 0xFFFFFFFFu;
            foreach (byte value in data)
            {
                c = CrcTable[(c ^ value) & 0xFF] ^ (c >> 8);
            }

            return c ^ 0xFFFFFFFFu;
        }
    }
}
