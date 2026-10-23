namespace WPR.Wp8Native
{
    /// <summary>
    /// One presented frame as a GPU would want it: triangles in clip space with texture
    /// coordinates and colour, grouped by texture. Built on the emulator thread at Present and
    /// handed to a host that draws it with real hardware.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the alternative to <see cref="FrameCapture.Rasterise"/>. That software rasteriser
    /// costs 50-75% of wall time in a level on the Android emulator (measured: 33-42 fps with
    /// it), and because the image's clock advances a fixed 1/60 s per frame, every frame it
    /// costs is the game running slow - which is the jerkiness. Building this list is a few
    /// hundred vertex transforms; the pixels are the GPU's problem.
    /// </para>
    /// <para>
    /// The list is self-contained: vertex data was already copied when each draw was issued
    /// (<see cref="FrameCapture.Snapshot"/>), and texture pixels are decoded to RGBA8 once per
    /// (storage, version) and shared by reference. A consumer keyed on
    /// <see cref="TextureImage.Key"/> and <see cref="TextureImage.Version"/> uploads each
    /// texture exactly once.
    /// </para>
    /// </remarks>
    public sealed class FrameDrawList
    {
        /// <summary>Floats per vertex: x, y (clip space, already divided by w), u, v, r, g, b, a.</summary>
        public const int Stride = 8;

        public required float[] Clear { get; init; }

        /// <summary>All vertices of the frame, triangle list, <see cref="Stride"/> floats each.</summary>
        public required float[] Vertices { get; init; }

        public required IReadOnlyList<Batch> Batches { get; init; }

        /// <summary>A run of triangles sharing one texture (null = untextured, vertex colour only).</summary>
        public readonly record struct Batch(TextureImage? Texture, int FirstVertex, int VertexCount, bool Wrap = false);
    }

    /// <summary>
    /// A texture's pixels: tightly packed RGBA8, top row first - or, for a block-compressed
    /// texture headed for the GPU, its blocks as shipped (<see cref="Blocks"/>, tightly packed,
    /// <see cref="BlockFormat"/> 1/2/3 for BC1/BC2/BC3) with <see cref="Rgba"/> empty.
    /// </summary>
    public sealed record TextureImage(long Key, int Version, int Width, int Height, byte[] Rgba, byte[]? Blocks = null, int BlockFormat = 0);

    /// <summary>
    /// Decoded guest textures by storage address, each dropped once nothing has asked for it for
    /// <see cref="IdleLimit"/>. A texture's resource is never freed here (Release only counts), so a
    /// title that streams its art in and out - Jetpack Joyride makes a new set of level textures
    /// every few hundred metres - otherwise kept a CPU copy of every texture it ever drew: 39 MB of
    /// managed memory at the menu, 460 MB after two minutes of play on a Galaxy S24. An evicted
    /// texture is simply read from guest memory again if it is drawn later.
    /// </summary>
    internal sealed class TextureImageCache
    {
        public static readonly TimeSpan IdleLimit = TimeSpan.FromSeconds(10);

        private readonly Dictionary<long, (TextureImage Image, long LastUsed)> _entries = new();
        private long _lastSweep;

        public int Count => _entries.Count;

        public bool TryGet(long key, out TextureImage image)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                _entries[key] = (entry.Image, now);
                Sweep(now);
                image = entry.Image;
                return true;
            }

            image = null!;
            return false;
        }

        public void Set(long key, TextureImage image)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            _entries[key] = (image, now);
            Sweep(now);
        }

        /// <summary>Drops entries idle for longer than <see cref="IdleLimit"/>, at most once a second.</summary>
        public void Sweep(long now)
        {
            long second = System.Diagnostics.Stopwatch.Frequency;
            if (now - _lastSweep < second)
            {
                return;
            }

            _lastSweep = now;
            long limit = (long)(IdleLimit.TotalSeconds * second);
            List<long>? stale = null;
            foreach (var (key, entry) in _entries)
            {
                if (now - entry.LastUsed > limit)
                {
                    (stale ??= []).Add(key);
                }
            }

            foreach (long key in stale ?? [])
            {
                _entries.Remove(key);
            }
        }
    }

    public sealed partial class FrameCapture
    {
        internal static readonly TextureImageCache RgbaCache = new();

        /// <summary>
        /// The pending draws as a <see cref="FrameDrawList"/>. Same vertex interpretation as
        /// <see cref="Rasterise"/> - transform, vertex colour and its 0..255 scale, untextured
        /// layouts - so the two cannot disagree about what a frame contains.
        /// </summary>
        public FrameDrawList BuildDrawList(ArmEmulator emulator)
        {
            var vertices = new List<float>(4096);
            var batches = new List<FrameDrawList.Batch>(_draws.Count);

            foreach (DrawCall call in _draws)
            {
                VertexElement? position = Find(call.Layout, "POSITION");
                VertexElement? uv = Find(call.Layout, "TEXCOORD");
                if (position is null || call.StreamFor(position) is not { Buffer: not null, Stride: > 0 })
                {
                    continue;
                }

                int[] indices = call.Indices;
                if (indices.Length < 3)
                {
                    continue;
                }

                TextureImage? image = uv is null ? null : DecodeRgba(emulator, call.Texture);
                if (uv is not null && image is null)
                {
                    // Textured, but nothing decodable is bound: drawing it untextured would paint
                    // a solid shape over the scene, which is worse than leaving it out.
                    continue;
                }

                int first = vertices.Count / FrameDrawList.Stride;
                for (int i = 0; i + 2 < indices.Length; i += 3)
                {
                    for (int k = 0; k < 3; k++)
                    {
                        Vertex v = ReadVertex(call, indices[i + k], position, uv);
                        float w = Math.Abs(v.W) < 0.00001f ? 1f : v.W;
                        float x = v.X / w, y = v.Y / w;
                        if (!float.IsFinite(x) || !float.IsFinite(y))
                        {
                            x = y = 0f;
                        }

                        vertices.Add(x);
                        vertices.Add(y);
                        vertices.Add(v.U);
                        vertices.Add(v.V);
                        vertices.Add(v.R);
                        vertices.Add(v.G);
                        vertices.Add(v.B);
                        vertices.Add(v.A);
                    }
                }

                int count = (vertices.Count / FrameDrawList.Stride) - first;
                if (count == 0)
                {
                    continue;
                }

                // Consecutive draws on one texture merge into one batch: one bind, one draw call.
                bool wrap = call.WrapU || call.WrapV;
                if (batches.Count > 0 && ReferenceEquals(batches[^1].Texture, image) && batches[^1].Wrap == wrap &&
                    batches[^1].FirstVertex + batches[^1].VertexCount == first)
                {
                    FrameDrawList.Batch last = batches[^1];
                    batches[^1] = last with { VertexCount = last.VertexCount + count };
                }
                else
                {
                    batches.Add(new FrameDrawList.Batch(image, first, count, wrap));
                }
            }

            return new FrameDrawList
            {
                Clear = (float[])ClearColour.Clone(),
                Vertices = vertices.ToArray(),
                Batches = batches,
            };
        }

        /// <summary>
        /// The texture as RGBA8, whatever its storage format: block-compressed textures are
        /// already expanded by <see cref="LoadTexture"/>, and the 32- and 16-bit formats go
        /// through <see cref="Sample"/> texel by texel - once per version, so the cost does
        /// not matter.
        /// </summary>
        internal static TextureImage? DecodeRgba(ArmEmulator emulator, Resource? texture)
        {
            if (texture is null || texture.Storage == 0)
            {
                return null;
            }

            if (RgbaCache.TryGet(texture.Storage, out TextureImage cached) && cached.Version == texture.Version &&
                cached.Width == texture.PixelWidth && cached.Height == texture.PixelHeight)
            {
                return cached;
            }

            byte[]? raw = LoadTexture(emulator, texture);
            if (raw is null)
            {
                return null;
            }

            int width = texture.PixelWidth, height = texture.PixelHeight;
            byte[] rgba;
            if (texture.BlockBytes > 0)
            {
                rgba = raw;
            }
            else
            {
                rgba = new byte[width * height * 4];
                for (int y = 0; y < height; y++)
                {
                    float v = (y + 0.5f) / height;
                    for (int x = 0; x < width; x++)
                    {
                        Sample(raw, texture, (x + 0.5f) / width, v, out byte r, out byte g, out byte b, out byte a);
                        int at = ((y * width) + x) * 4;
                        rgba[at] = r;
                        rgba[at + 1] = g;
                        rgba[at + 2] = b;
                        rgba[at + 3] = a;
                    }
                }
            }

            var image = new TextureImage(texture.Storage, texture.Version, width, height, rgba);
            RgbaCache.Set(texture.Storage, image);
            return image;
        }

        internal static readonly TextureImageCache BlockCache = new();

        /// <summary>
        /// A texture as the GPU path wants it: a block-compressed one keeps its blocks, so the host
        /// stores it compressed when the device can sample BC1-BC3 and decompresses it through the
        /// XNA layer's one decoder (TextureFormatShim/DxtUtil) when it cannot; anything else is
        /// <see cref="DecodeRgba"/>.
        /// </summary>
        /// <remarks>
        /// Before this, every WP8 texture was expanded to RGBA on the CPU here, desktop included,
        /// though D3D11 samples BC natively: four times the upload and the memory, and a second
        /// BC decoder beside the XNA one. <see cref="DecodeBlocks"/> stays for the probe's software
        /// rasteriser and the WPR_WP8_GPU=0 draw list, which have no device to hand the blocks to.
        /// </remarks>
        internal static TextureImage? TextureForGpu(ArmEmulator emulator, Resource? texture)
        {
            if (texture is null || texture.BlockBytes == 0)
            {
                return DecodeRgba(emulator, texture);
            }

            if (!texture.HasContent || texture.Storage == 0 || texture.PixelWidth <= 0 || texture.PixelHeight <= 0)
            {
                return null;
            }

            if (BlockCache.TryGet(texture.Storage, out TextureImage cached) && cached.Version == texture.Version &&
                cached.Width == texture.PixelWidth && cached.Height == texture.PixelHeight)
            {
                return cached;
            }

            int across = Math.Max(1, (texture.PixelWidth + 3) / 4);
            int down = Math.Max(1, (texture.PixelHeight + 3) / 4);
            int tight = across * texture.BlockBytes;
            byte[] blocks = new byte[tight * down];
            try
            {
                // RowPitch is the game's own and may pad a row of blocks; the host wants them packed.
                for (int row = 0; row < down; row++)
                {
                    byte[] line = emulator.ReadMemory(texture.Storage + ((long)row * texture.RowPitch), tight);
                    Buffer.BlockCopy(line, 0, blocks, row * tight, tight);
                }
            }
            catch (Exception)
            {
                return null;
            }

            int format = texture.Format switch
            {
                70 or 71 or 72 => 1,    // BC1
                73 or 74 or 75 => 2,    // BC2
                _ => 3,                 // BC3 (76, 77, 78)
            };

            var image = new TextureImage(texture.Storage, texture.Version, texture.PixelWidth, texture.PixelHeight, [], blocks, format);
            BlockCache.Set(texture.Storage, image);
            return image;
        }
    }
}
