using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace WPR.Wp8Native
{
    /// <summary>
    /// Replays a <see cref="GpuFrame"/> on the host GPU with the game's own shaders: the 3D path.
    /// </summary>
    /// <remarks>
    /// <para>The game renders into what it believes is a 480x800 portrait back buffer, so that is
    /// exactly what it gets - a render target of that size, with depth - and its viewports and
    /// coordinates stay its own. The finished target is then turned a quarter onto the landscape
    /// window, the same way the 2D path and the pointer mapping turn (landscape (x, y) shows
    /// portrait (480 - y, x)).</para>
    /// <para>Shaders go through <see cref="Aon9Shader"/>: the Direct3D 9 program every 9_x shader
    /// carries, compiled by FNA3D's MojoShader, inside an effect whose only states are the two
    /// shaders. Constant buffers are copied into the effect's <c>vs_c</c>/<c>ps_c</c> arrays by
    /// the shader's own register map, and every piece of fixed-function state is set on the
    /// device directly.</para>
    /// </remarks>
    internal sealed class Wp8GpuReplay : IDisposable
    {
        private readonly GraphicsDevice _device;
        private readonly Action<string>? _log;
        private readonly SpriteBatch _blit;

        private readonly Dictionary<long, Aon9Shader?> _shaders = new();
        private readonly Dictionary<(long, long), Effect?> _effects = new();
        private readonly Dictionary<long, RenderTarget2D> _targets = new();
        private readonly Dictionary<long, (int Version, Texture2D Texture)> _textures = new();
        private readonly Dictionary<string, BlendState> _blends = new(StringComparer.Ordinal);
        private readonly Dictionary<string, DepthStencilState> _depths = new(StringComparer.Ordinal);
        private readonly Dictionary<string, RasterizerState> _rasters = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SamplerState> _samplers = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (VertexDeclaration Declaration, DynamicVertexBuffer? Buffer)> _vertexBuffers = new(StringComparer.Ordinal);
        private DynamicIndexBuffer? _indices16;
        private DynamicIndexBuffer? _indices32;
        private readonly HashSet<string> _warned = new(StringComparer.Ordinal);

        public Wp8GpuReplay(GraphicsDevice device, Action<string>? log)
        {
            _device = device;
            _log = log;
            _blit = new SpriteBatch(device);
        }

        public int DrawsReplayed { get; private set; }

        public int DrawsSkipped { get; private set; }

        /// <summary>Time spent replaying, commands replayed and cache sizes, for the stats line.</summary>
        public string Statistics()
        {
            string text = $"replay {_replayTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / Math.Max(1, _framesReplayed):0.0}ms/frame " +
                $"{_commands / Math.Max(1, _framesReplayed)} cmd/frame effects={_effects.Count} targets={_targets.Count} " +
                $"textures={_textures.Count} vbs={_vertexBuffers.Count} blends={_blends.Count} depths={_depths.Count} " +
                $"rasters={_rasters.Count} samplers={_samplers.Count} streams: kept={_uploaded.Count(u => u.Value.Buffer is not null)} reused={StreamsReused} uploaded={StreamsUploaded} " +
                $"managed={GC.GetTotalMemory(false) / (1024 * 1024)}MB gc={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}";
            _replayTicks = 0;
            _framesReplayed = 0;
            _commands = 0;
            return text;
        }

        /// <summary>WPR_WP8_GPU_TRACE=N: log every command of the Nth replayed frame.</summary>
        private static readonly int TraceFrame = int.TryParse(Environment.GetEnvironmentVariable("WPR_WP8_GPU_TRACE"), out int n) ? n : -1;

        private int _frameIndex;

        /// <summary>Depth clears waiting for the first draw that uses their depth view (by view key).</summary>
        private readonly Dictionary<long, (int Flags, float Depth, byte Stencil)> _pendingDepthClears = new();
        private string? _skip;

        private long _replayTicks;
        private int _framesReplayed;
        private int _commands;

        private void Warn(string key, string message)
        {
            if (_warned.Add(key) && _warned.Count < 200)
            {
                _log?.Invoke("[wpr-wp8-gpu] " + message);
            }
        }

        /// <summary>Replays the frame, then shows its back buffer on the window, rotated to landscape.</summary>
        public void Render(GpuFrame frame, int windowWidth, int windowHeight)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                RenderFrame(frame, windowWidth, windowHeight);
            }
            finally
            {
                _replayTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
                _framesReplayed++;
                _commands += frame.Commands.Count;
            }
        }

        private void RenderFrame(GpuFrame frame, int windowWidth, int windowHeight)
        {
            bool trace = _frameIndex++ == TraceFrame;
            EvictStreams();
            int index = 0;
            void Trace(string line)
            {
                if (trace)
                {
                    _log?.Invoke($"[wpr-wp8-gpu-trace] {index,4} {line}");
                }
            }

            RenderTarget2D? current = null;
            RenderTarget2D? backBuffer = null;
            foreach (GpuCommand command in frame.Commands)
            {
                index++;
                try
                {
                    switch (command)
                    {
                        case GpuClearColour clear:
                            if (trace)
                            {
                                Trace($"clear  {Describe(clear.Target)} ({string.Join(",", clear.Colour.Select(c => c.ToString("0.00")))})");
                            }

                            current = Bind(clear.Target, current);
                            backBuffer = clear.Target.IsBackBuffer ? current : backBuffer;
                            _device.Clear(ClearOptions.Target, new Vector4(clear.Colour[0], clear.Colour[1], clear.Colour[2], clear.Colour[3]), 1f, 0);
                            break;
                        case GpuClearDepth clear:
                            // A Direct3D depth view is its own texture; here depth belongs to the
                            // render target it is drawn with. So the clear waits for the next draw
                            // that uses this depth view, and clears that draw's target's depth. Bound
                            // as a colour target and cleared there, the back buffer's real depth
                            // was never cleared, and Modern Combat 4's whole world failed the test.
                            if (trace)
                            {
                                Trace($"cleard {Describe(clear.Target)} flags {clear.Flags} depth {clear.Depth}");
                            }

                            int flags = clear.Flags | (_pendingDepthClears.TryGetValue(clear.Target.Key, out var earlier) ? earlier.Flags : 0);
                            _pendingDepthClears[clear.Target.Key] = (flags, clear.Depth, clear.Stencil);
                            break;
                        case GpuDraw draw:
                            current = Bind(draw.Target, current);
                            backBuffer = draw.Target.IsBackBuffer ? current : backBuffer;
                            if (draw.DepthTarget is { } depthView && _pendingDepthClears.Remove(depthView.Key, out var pending))
                            {
                                ClearOptions options = ((pending.Flags & 1) != 0 ? ClearOptions.DepthBuffer : 0) | ((pending.Flags & 2) != 0 ? ClearOptions.Stencil : 0);
                                if (options != 0)
                                {
                                    _device.Clear(options, Vector4.Zero, pending.Depth, pending.Stencil);
                                }
                            }

                            bool drawn = Draw(draw);
                            if (drawn)
                            {
                                DrawsReplayed++;
                            }
                            else
                            {
                                DrawsSkipped++;
                            }

                            if (trace)
                            {
                                Trace($"draw   {Describe(draw.Target)} depth={(draw.DepthTarget is { } d ? Describe(d) : "none")} " +
                                      $"vs {draw.Vertex.Key:X8} ps {draw.Pixel.Key:X8} idx {draw.Geometry.Indices.Length} topo {draw.Geometry.Topology} " +
                                      $"tex [{string.Join(" ", draw.Textures.Select(t => $"{t.Key}:{(t.Value.RenderTarget is { } rt ? "rt" + Describe(rt) : t.Value.Image is { } im ? $"{im.Width}x{im.Height}" : "null")}"))}] " +
                                      $"blend={(draw.Blend is null ? "default" : System.Convert.ToHexString(draw.Blend, 8, 12))} vp={(draw.Viewport is { } vp ? string.Join(",", vp.Take(4)) : "none")} " +
                                      $"streams={string.Join(",", draw.Geometry.Streams.Select(st => st.Data?.Length ?? -1))} " +
                                      $"layout={string.Join(",", draw.Geometry.Layout.Select(e => $"{e.Semantic}{e.Index}@{e.Slot}:{e.Offset}/{e.Format}"))} " +
                                      $"vp6={(draw.Viewport is { } vq ? string.Join(",", vq) : "none")} depthdesc={(draw.DepthStencil is null ? "default" : System.Convert.ToHexString(draw.DepthStencil, 0, 12))} " +
                                      $"raster={(draw.Rasterizer is null ? "default" : System.Convert.ToHexString(draw.Rasterizer, 0, 12))} " +
                                      $"{(drawn ? "ok" : "SKIP " + _skip)}");
                            }

                            break;
                    }
                }
                catch (Exception ex)
                {
                    Trace($"EXCEPTION {ex.GetType().Name}: {ex.Message}");
                    if (trace && command is GpuDraw failed)
                    {
                        Trace($"   vs {failed.Vertex.Key:X8} streams={string.Join(",", failed.Geometry.Streams.Select(st => $"{st.Data?.Length ?? -1}/{st.Stride}/{(st.Buffer is null ? "null" : st.Buffer.Storage.ToString("X"))}"))} " +
                              $"layout={string.Join(",", failed.Geometry.Layout.Select(e => $"{e.Semantic}{e.Index}@{e.Slot}:{e.Offset}/{e.Format}"))} " +
                              $"inputs={(_shaders.GetValueOrDefault(failed.Vertex.Key) is { } fs ? string.Join(" ", fs.InputRegisters.Select(kv => $"{kv.Key.Semantic}{kv.Key.Index}=v{kv.Value}")) : "?")}");
                    }

                    DrawsSkipped++;
                    Warn("ex:" + ex.GetType().Name + ex.Message, $"replay failed: {ex.GetType().Name}: {ex.Message}");
                }
            }

            backBuffer ??= _targets.GetValueOrDefault(GpuTarget.BackBufferKey);
            _device.SetRenderTarget(null);
            _device.Clear(Color.Black);
            if (backBuffer is null)
            {
                return;
            }

            // Portrait to landscape: rotate by -90 degrees about the top-left, then drop it down
            // by the portrait width, so portrait (px, py) lands at (py, width - px).
            float scale = Math.Min(windowWidth / (float)backBuffer.Height, windowHeight / (float)backBuffer.Width);
            _blit.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone);
            _blit.Draw(backBuffer, new Vector2(0, backBuffer.Width * scale), null, Color.White, -MathHelper.PiOver2, Vector2.Zero, scale, SpriteEffects.None, 0f);
            _blit.End();
        }

        private static string Describe(GpuTarget target)
            => target.IsBackBuffer ? $"BB{target.Width}x{target.Height}" : $"{target.Key:X8}:{target.Width}x{target.Height}";

        private RenderTarget2D Bind(GpuTarget target, RenderTarget2D? current)
        {
            RenderTarget2D wanted = TargetFor(target);
            if (!ReferenceEquals(wanted, current))
            {
                _device.SetRenderTarget(wanted);
            }

            return wanted;
        }

        private RenderTarget2D TargetFor(GpuTarget target)
        {
            if (_targets.TryGetValue(target.Key, out RenderTarget2D? existing) &&
                existing.Width == target.Width && existing.Height == target.Height)
            {
                return existing;
            }

            existing?.Dispose();
            // Preserve contents: a frame switches targets and comes back, and a discarded target
            // would come back empty.
            RenderTarget2D created = new(_device, Math.Max(1, target.Width), Math.Max(1, target.Height), false,
                SurfaceFormat.Color, DepthFormat.Depth24Stencil8, 0, RenderTargetUsage.PreserveContents);
            _targets[target.Key] = created;
            return created;
        }

        // ------------------------------------------------------------------------------
        // Draws
        // ------------------------------------------------------------------------------

        private bool Draw(GpuDraw draw)
        {
            _skip = null;
            Aon9Shader? vertex = Shader(draw.Vertex);
            Aon9Shader? pixel = Shader(draw.Pixel);
            if (vertex is null || pixel is null || vertex.IsPixel || !pixel.IsPixel)
            {
                _skip = "shader unusable";
                return false;
            }

            Effect? effect = EffectFor(draw.Vertex, vertex, draw.Pixel, pixel);
            if (effect is null)
            {
                _skip = "no effect";
                return false;
            }

            FrameCapture.DrawCall geometry = draw.Geometry;
            int[] indices = geometry.Indices;
            if (indices.Length == 0)
            {
                _skip = "no indices";
                return false;
            }

            if (!PrimitiveFor(geometry.Topology, indices.Length, out PrimitiveType primitive, out int primitives))
            {
                Warn($"topology {geometry.Topology}", $"topology {geometry.Topology} is not replayed");
                _skip = "topology";
                return false;
            }

            IndexBuffer indexBuffer = IndicesFor(indices, out int lowest, out int vertexCount);
            if (!Streams(geometry, vertex, lowest + geometry.BaseVertex, vertexCount, out VertexBufferBinding[] bindings))
            {
                _skip = "no vertex streams";
                return false;
            }

            // Constants, by each shader's own register map.
            SetConstants(effect, vertex, draw.VertexConstants);
            SetConstants(effect, pixel, draw.PixelConstants);

            // Fixed-function state.
            _device.BlendState = BlendFor(draw.Blend, draw.BlendFactor);
            _device.DepthStencilState = draw.DepthTarget is null ? DepthStencilState.None : DepthFor(draw.DepthStencil, draw.StencilRef);
            _device.RasterizerState = RasterFor(draw.Rasterizer);
            if (draw.Viewport is { } v)
            {
                _device.Viewport = new Viewport((int)v[0], (int)v[1], Math.Max(1, (int)v[2]), Math.Max(1, (int)v[3]))
                {
                    MinDepth = v[4],
                    MaxDepth = v[5],
                };
            }

            foreach (Aon9SamplerMap map in pixel.Samplers)
            {
                _device.Textures[map.Register] = draw.Textures.TryGetValue(map.Texture, out GpuTexture? texture) ? TextureFor(texture) : null;
                _device.SamplerStates[map.Register] = SamplerFor(draw.Samplers.GetValueOrDefault(map.Sampler));
            }

            effect.CurrentTechnique.Passes[0].Apply();
            _device.SetVertexBuffers(bindings);
            _device.Indices = indexBuffer;
            _device.DrawIndexedPrimitives(primitive, 0, 0, vertexCount, 0, primitives);
            return true;
        }

        private static bool PrimitiveFor(uint topology, int indexCount, out PrimitiveType type, out int count)
        {
            (type, count) = topology switch
            {
                4 => (PrimitiveType.TriangleList, indexCount / 3),
                5 => (PrimitiveType.TriangleStrip, indexCount - 2),
                2 => (PrimitiveType.LineList, indexCount / 2),
                3 => (PrimitiveType.LineStrip, indexCount - 1),
                _ => (PrimitiveType.TriangleList, 0),
            };

            return count > 0;
        }

        private Aon9Shader? Shader(GpuShader shader)
        {
            if (!_shaders.TryGetValue(shader.Key, out Aon9Shader? parsed))
            {
                parsed = Aon9Shader.Parse(shader.Dxbc, out string? error);
                if (parsed is null)
                {
                    Warn($"shader {shader.Key}", $"shader 0x{shader.Key:X8} unusable: {error}");
                }

                _shaders[shader.Key] = parsed;
            }

            return parsed;
        }

        private Effect? EffectFor(GpuShader vertexKey, Aon9Shader vertex, GpuShader pixelKey, Aon9Shader pixel)
        {
            (long, long) key = (vertexKey.Key, pixelKey.Key);
            if (_effects.TryGetValue(key, out Effect? effect))
            {
                return effect;
            }

            try
            {
                effect = new Effect(_device, Aon9Effect.Build(vertex, pixel));
            }
            catch (Exception ex)
            {
                Warn($"effect {key}", $"effect for shaders 0x{key.Item1:X8}/0x{key.Item2:X8} failed: {ex.Message}");
                effect = null;
            }

            _effects[key] = effect;
            return effect;
        }

        private static void SetConstants(Effect effect, Aon9Shader shader, IReadOnlyDictionary<int, byte[]> buffers)
        {
            if (shader.ConstantRegisters == 0 || effect.Parameters[shader.ConstantName] is not { } parameter)
            {
                return;
            }

            // Registers nothing maps stay zero: c0 of a vertex shader is the runtime's position
            // fix-up, which the host's rasteriser does not need.
            Vector4[] registers = new Vector4[shader.ConstantRegisters];
            foreach (Aon9ConstantMap map in shader.ConstantMaps)
            {
                if (!buffers.TryGetValue(map.Buffer, out byte[]? data))
                {
                    continue;
                }

                for (int i = 0; i < map.Count; i++)
                {
                    int source = (map.Start + i) * 16;
                    int target = map.Target + i;
                    if (source + 16 <= data.Length && target < registers.Length)
                    {
                        registers[target] = new Vector4(
                            BitConverter.ToSingle(data, source),
                            BitConverter.ToSingle(data, source + 4),
                            BitConverter.ToSingle(data, source + 8),
                            BitConverter.ToSingle(data, source + 12));
                    }
                }
            }

            parameter.SetValue(registers);
        }

        // ------------------------------------------------------------------------------
        // Geometry
        // ------------------------------------------------------------------------------

        /// <summary>How one input slot is repacked for one vertex shader.</summary>
        private sealed record SlotPlan(
            int Slot,
            (int SourceOffset, uint SourceFormat, int Offset, int Size)[] Elements,
            VertexDeclaration Declaration,
            string Key,
            int[] Registers);

        /// <summary>How a layout feeds a vertex shader, and which of its inputs nothing feeds.</summary>
        private sealed record StreamPlan(SlotPlan[] Slots, int[] Unfed);

        private readonly Dictionary<(object Layout, Aon9Shader Shader), StreamPlan> _streamPlans = new();

        /// <summary>A vertex array already repacked and uploaded, reused while the game keeps drawing it.</summary>
        private sealed class UploadedStream
        {
            public VertexBuffer? Buffer;
            public int FirstSeen;
            public int LastUsed;
        }

        private readonly Dictionary<(byte[] Data, int Stride, string Key), UploadedStream> _uploaded = new(UploadedComparer.Instance);
        private byte[] _scratch = new byte[64 * 1024];

        private sealed class UploadedComparer : IEqualityComparer<(byte[] Data, int Stride, string Key)>
        {
            public static readonly UploadedComparer Instance = new();

            public bool Equals((byte[] Data, int Stride, string Key) a, (byte[] Data, int Stride, string Key) b)
                => ReferenceEquals(a.Data, b.Data) && a.Stride == b.Stride && ReferenceEquals(a.Key, b.Key);

            public int GetHashCode((byte[] Data, int Stride, string Key) k)
                => HashCode.Combine(RuntimeHelpers.GetHashCode(k.Data), k.Stride, RuntimeHelpers.GetHashCode(k.Key));
        }

        private StreamPlan PlanFor(FrameCapture.DrawCall geometry, Aon9Shader vertex)
        {
            if (_streamPlans.TryGetValue((geometry.Layout, vertex), out StreamPlan? plan))
            {
                return plan;
            }

            var slots = new List<SlotPlan>();
            var fed = new HashSet<int>();
            foreach (IGrouping<int, FrameCapture.VertexElement> slot in geometry.Layout.GroupBy(e => e.Slot))
            {
                var elements = new List<(int, uint, int, int)>();
                var declaration = new List<VertexElement>();
                var registers = new List<int>();
                int offset = 0;
                foreach (FrameCapture.VertexElement element in slot)
                {
                    if (!vertex.InputRegisters.TryGetValue((element.Semantic.ToUpperInvariant(), (int)element.Index), out int register))
                    {
                        continue;   // not read by this shader
                    }

                    if (VertexFormat(element.Format) is not { } format)
                    {
                        Warn($"vfmt {element.Format}", $"vertex format {element.Format} ({element.Semantic}) is not replayed");
                        continue;
                    }

                    elements.Add((element.Offset, element.Format, offset, format.Size));
                    declaration.Add(new VertexElement(offset, format.Format, VertexElementUsage.TextureCoordinate, register));
                    registers.Add(register);
                    fed.Add(register);
                    offset += format.Size;
                }

                if (elements.Count > 0)
                {
                    // Interned so the upload cache can compare keys by reference.
                    string key = string.Intern(string.Join(";", declaration.Select(d => $"{d.Offset}:{d.VertexElementFormat}:{d.UsageIndex}")));
                    slots.Add(new SlotPlan(slot.Key, [.. elements], new VertexDeclaration(offset, [.. declaration]), key, [.. registers]));
                }
            }

            int[] unfed = vertex.InputRegisters.Values.Where(r => !fed.Contains(r)).Distinct().Order().ToArray();
            plan = new StreamPlan([.. slots], unfed);
            _streamPlans[(geometry.Layout, vertex)] = plan;
            return plan;
        }

        /// <summary>
        /// One vertex buffer per input slot, repacked so every element is in a format FNA accepts
        /// and named TEXCOORDn after the shader input register its semantic feeds. Index 0 of the
        /// draw is vertex <paramref name="firstVertex"/>; <paramref name="vertexCount"/> vertices are read.
        /// </summary>
        /// <remarks>
        /// A vertex array the capture hands over again on a later frame is a buffer the game has
        /// not rewritten (the capture reuses its copy), so it is uploaded once into a buffer of
        /// its own and bound at an offset from then on. One seen for the first time goes through
        /// the shared dynamic buffer, which is all a buffer rewritten every frame ever needs.
        /// Repacking and uploading every static mesh on every draw was most of the replay's time.
        /// </remarks>
        private bool Streams(FrameCapture.DrawCall geometry, Aon9Shader vertex, int firstVertex, int vertexCount, out VertexBufferBinding[] bindings)
        {
            StreamPlan plan = PlanFor(geometry, vertex);
            var list = new List<VertexBufferBinding>(plan.Slots.Length + 1);
            List<int>? missing = null;
            foreach (SlotPlan slot in plan.Slots)
            {
                FrameCapture.VertexStream? stream = slot.Slot >= 0 && slot.Slot < geometry.Streams.Count ? geometry.Streams[slot.Slot] : null;
                int offsetInData = stream is null ? 0 : firstVertex - stream.FirstVertex;
                if (stream is not { Data: { } data, Stride: >= 0 } ||
                    (stream.Stride > 0 && (offsetInData < 0 || offsetInData + vertexCount > data.Length / stream.Stride)))
                {
                    Warn($"stream {slot.Slot}", $"vertex stream {slot.Slot} had no data");
                    (missing ??= []).AddRange(slot.Registers);
                    continue;
                }

                if (stream.Stride > 0)
                {
                    var key = (data, stream.Stride, slot.Key);
                    if (!_uploaded.TryGetValue(key, out UploadedStream? uploaded))
                    {
                        _uploaded[key] = uploaded = new UploadedStream { FirstSeen = _frameIndex };
                    }

                    uploaded.LastUsed = _frameIndex;
                    if (uploaded.Buffer is null && uploaded.FirstSeen != _frameIndex)
                    {
                        int all = data.Length / stream.Stride;
                        byte[] packed = Repack(slot, data, stream.Stride, 0, all);
                        uploaded.Buffer = new VertexBuffer(_device, slot.Declaration, all, BufferUsage.WriteOnly);
                        uploaded.Buffer.SetData(packed, 0, all * slot.Declaration.VertexStride);
                        StreamsCached++;
                    }

                    if (uploaded.Buffer is not null)
                    {
                        list.Add(new VertexBufferBinding(uploaded.Buffer, offsetInData));
                        StreamsReused++;
                        continue;
                    }
                }

                // Through the shared dynamic buffer: just this draw's vertices.
                byte[] scratch = Repack(slot, data, stream.Stride, stream.Stride == 0 ? 0 : offsetInData, vertexCount);
                if (!_vertexBuffers.TryGetValue(slot.Key, out var cached))
                {
                    cached = (slot.Declaration, null);
                }

                if (cached.Buffer is null || cached.Buffer.VertexCount < vertexCount)
                {
                    cached.Buffer?.Dispose();
                    cached.Buffer = new DynamicVertexBuffer(_device, cached.Declaration, Math.Max(vertexCount, 1024), BufferUsage.WriteOnly);
                }

                _vertexBuffers[slot.Key] = cached;
                cached.Buffer.SetData(scratch, 0, vertexCount * slot.Declaration.VertexStride, SetDataOptions.Discard);
                list.Add(new VertexBufferBinding(cached.Buffer));
                StreamsUploaded++;
            }

            // An input the shader reads with nothing feeding it makes Direct3D refuse the whole
            // input layout, losing the draw. Zeros are what an unbound slot reads on the phone.
            int[] unfed = missing is null ? plan.Unfed : [.. plan.Unfed.Concat(missing).Distinct().Order()];
            if (list.Count > 0 && unfed.Length > 0)
            {
                Warn("unfed " + string.Join(",", unfed), $"vertex shader inputs v{string.Join(",v", unfed)} have no stream; reading zeros");
                list.Add(new VertexBufferBinding(ZeroStream(unfed, vertexCount)));
            }

            bindings = [.. list];
            return list.Count > 0 && vertexCount > 0;
        }

        /// <summary>Vertex arrays uploaded once, binds that reused one, and per-draw uploads, since start.</summary>
        public int StreamsCached { get; private set; }

        public long StreamsReused { get; private set; }

        public long StreamsUploaded { get; private set; }

        /// <summary>
        /// Repacks <paramref name="count"/> vertices starting at <paramref name="first"/> into the
        /// scratch buffer (a stride of 0 repeats the one element).
        /// </summary>
        private byte[] Repack(SlotPlan slot, byte[] data, int sourceStride, int first, int count)
        {
            int stride = slot.Declaration.VertexStride;
            int length = count * stride;
            if (_scratch.Length < length)
            {
                _scratch = new byte[Math.Max(length, _scratch.Length * 2)];
            }

            Span<byte> target = _scratch.AsSpan(0, length);
            foreach (var e in slot.Elements)
            {
                bool copy = e.SourceFormat is not (87 or 31 or 11 or 12 or 24 or 35 or 36 or 49 or 51);
                for (int i = 0; i < count; i++)
                {
                    ReadOnlySpan<byte> source = data.AsSpan(((first + i) * sourceStride) + e.SourceOffset);
                    Span<byte> to = target.Slice((i * stride) + e.Offset, e.Size);
                    if (copy)
                    {
                        source[..e.Size].CopyTo(to);
                    }
                    else
                    {
                        Convert(source, e.SourceFormat, to);
                    }
                }
            }

            return _scratch;
        }

        /// <summary>Releases uploaded vertex arrays the game has stopped drawing.</summary>
        private void EvictStreams()
        {
            if (_frameIndex % 120 != 0)
            {
                return;
            }

            List<(byte[], int, string)>? stale = null;
            foreach (var (key, uploaded) in _uploaded)
            {
                if (uploaded.LastUsed < _frameIndex - 120)
                {
                    uploaded.Buffer?.Dispose();
                    (stale ??= []).Add(key);
                }
            }

            foreach (var key in stale ?? [])
            {
                _uploaded.Remove(key);
            }
        }

        private readonly Dictionary<string, VertexBuffer> _zeroStreams = new(StringComparer.Ordinal);

        /// <summary>A zero-filled float4 stream for each of <paramref name="registers"/>, at least <paramref name="count"/> long.</summary>
        private VertexBuffer ZeroStream(int[] registers, int count)
        {
            string key = string.Join(",", registers);
            if (_zeroStreams.TryGetValue(key, out VertexBuffer? buffer) && buffer.VertexCount >= count)
            {
                return buffer;
            }

            buffer?.Dispose();
            var declaration = new VertexDeclaration(registers.Select((r, i) => new VertexElement(i * 16, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, r)).ToArray());
            int size = Math.Max(count, 4096);
            buffer = new VertexBuffer(_device, declaration, size, BufferUsage.WriteOnly);
            buffer.SetData(new byte[size * declaration.VertexStride]);
            _zeroStreams[key] = buffer;
            return buffer;
        }

        /// <summary>DXGI input format to the FNA format it is stored as here, and that format's size.</summary>
        private static (VertexElementFormat Format, int Size)? VertexFormat(uint dxgi) => dxgi switch
        {
            2 => (VertexElementFormat.Vector4, 16),            // R32G32B32A32_FLOAT
            6 => (VertexElementFormat.Vector3, 12),            // R32G32B32_FLOAT
            16 => (VertexElementFormat.Vector2, 8),            // R32G32_FLOAT
            41 => (VertexElementFormat.Single, 4),             // R32_FLOAT
            28 or 29 or 87 => (VertexElementFormat.Color, 4),  // R8G8B8A8_UNORM(_SRGB), B8G8R8A8_UNORM
            30 => (VertexElementFormat.Byte4, 4),              // R8G8B8A8_UINT
            10 => (VertexElementFormat.HalfVector4, 8),        // R16G16B16A16_FLOAT
            34 => (VertexElementFormat.HalfVector2, 4),        // R16G16_FLOAT
            13 => (VertexElementFormat.NormalizedShort4, 8),   // R16G16B16A16_SNORM
            37 => (VertexElementFormat.NormalizedShort2, 4),   // R16G16_SNORM
            14 => (VertexElementFormat.Short4, 8),             // R16G16B16A16_SINT
            38 => (VertexElementFormat.Short2, 4),             // R16G16_SINT
            31 or 11 or 12 or 24 => (VertexElementFormat.Vector4, 16),   // RGBA8_SNORM, RGBA16_UNORM/UINT, R10G10B10A2_UNORM
            35 or 36 or 49 or 51 => (VertexElementFormat.Vector2, 8),    // RG16_UNORM/UINT, RG8_UNORM/SNORM
            _ => null,
        };

        private static void Floats(Span<byte> target, params float[] values)
        {
            for (int i = 0; i < values.Length; i++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(target[(i * 4)..], values[i]);
            }
        }

        private static void Convert(ReadOnlySpan<byte> source, uint dxgi, Span<byte> target)
        {
            switch (dxgi)
            {
                case 87: // BGRA8 -> RGBA8
                    target[0] = source[2];
                    target[1] = source[1];
                    target[2] = source[0];
                    target[3] = source[3];
                    break;
                case 31:
                    Floats(target, Math.Max(-1f, (sbyte)source[0] / 127f), Math.Max(-1f, (sbyte)source[1] / 127f),
                           Math.Max(-1f, (sbyte)source[2] / 127f), Math.Max(-1f, (sbyte)source[3] / 127f));
                    break;
                case 11:
                    Floats(target, U16(source, 0) / 65535f, U16(source, 2) / 65535f, U16(source, 4) / 65535f, U16(source, 6) / 65535f);
                    break;
                case 12:
                    Floats(target, U16(source, 0), U16(source, 2), U16(source, 4), U16(source, 6));
                    break;
                case 24:
                    uint packed = BinaryPrimitives.ReadUInt32LittleEndian(source);
                    Floats(target, (packed & 0x3FF) / 1023f, ((packed >> 10) & 0x3FF) / 1023f, ((packed >> 20) & 0x3FF) / 1023f, (packed >> 30) / 3f);
                    break;
                case 35:
                    Floats(target, U16(source, 0) / 65535f, U16(source, 2) / 65535f);
                    break;
                case 36:
                    Floats(target, U16(source, 0), U16(source, 2));
                    break;
                case 49:
                    Floats(target, source[0] / 255f, source[1] / 255f);
                    break;
                case 51:
                    Floats(target, Math.Max(-1f, (sbyte)source[0] / 127f), Math.Max(-1f, (sbyte)source[1] / 127f));
                    break;
                default:
                    source[..target.Length].CopyTo(target);
                    break;
            }
        }

        private static int U16(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt16LittleEndian(data[at..]);

        private short[] _indexScratch16 = new short[4096];
        private int[] _indexScratch32 = new int[4096];

        /// <summary>
        /// The draw's indices rebased to start at 0, and where they started
        /// (<paramref name="lowest"/>) and how many vertices they span.
        /// </summary>
        private IndexBuffer IndicesFor(int[] indices, out int lowest, out int vertexCount)
        {
            lowest = int.MaxValue;
            int highest = int.MinValue;
            foreach (int index in indices)
            {
                lowest = Math.Min(lowest, index);
                highest = Math.Max(highest, index);
            }

            vertexCount = highest - lowest + 1;
            if (vertexCount <= 65536)
            {
                if (_indexScratch16.Length < indices.Length)
                {
                    _indexScratch16 = new short[Math.Max(indices.Length, _indexScratch16.Length * 2)];
                }

                for (int i = 0; i < indices.Length; i++)
                {
                    _indexScratch16[i] = unchecked((short)(ushort)(indices[i] - lowest));
                }

                if (_indices16 is null || _indices16.IndexCount < indices.Length)
                {
                    _indices16?.Dispose();
                    _indices16 = new DynamicIndexBuffer(_device, IndexElementSize.SixteenBits, Math.Max(indices.Length, 4096), BufferUsage.WriteOnly);
                }

                _indices16.SetData(_indexScratch16, 0, indices.Length, SetDataOptions.Discard);
                return _indices16;
            }

            if (_indexScratch32.Length < indices.Length)
            {
                _indexScratch32 = new int[Math.Max(indices.Length, _indexScratch32.Length * 2)];
            }

            for (int i = 0; i < indices.Length; i++)
            {
                _indexScratch32[i] = indices[i] - lowest;
            }

            if (_indices32 is null || _indices32.IndexCount < indices.Length)
            {
                _indices32?.Dispose();
                _indices32 = new DynamicIndexBuffer(_device, IndexElementSize.ThirtyTwoBits, Math.Max(indices.Length, 4096), BufferUsage.WriteOnly);
            }

            _indices32.SetData(_indexScratch32, 0, indices.Length, SetDataOptions.Discard);
            return _indices32;
        }

        // ------------------------------------------------------------------------------
        // Textures and state
        // ------------------------------------------------------------------------------

        private Texture2D? TextureFor(GpuTexture texture)
        {
            if (texture.RenderTarget is { } target)
            {
                return _targets.GetValueOrDefault(target.Key);
            }

            if (texture.Image is not { } image)
            {
                return null;
            }

            if (_textures.TryGetValue(image.Key, out var known) && known.Version == image.Version &&
                known.Texture.Width == image.Width && known.Texture.Height == image.Height)
            {
                return known.Texture;
            }

            Texture2D created = known.Texture is { } reuse && reuse.Width == image.Width && reuse.Height == image.Height
                ? reuse
                : new Texture2D(_device, image.Width, image.Height, false, SurfaceFormat.Color);
            if (!ReferenceEquals(created, known.Texture))
            {
                known.Texture?.Dispose();
            }

            created.SetData(image.Rgba);
            _textures[image.Key] = (image.Version, created);
            return created;
        }

        private static readonly Blend[] Blends =
        [
            Blend.One, Blend.Zero, Blend.One, Blend.SourceColor, Blend.InverseSourceColor, Blend.SourceAlpha,
            Blend.InverseSourceAlpha, Blend.DestinationAlpha, Blend.InverseDestinationAlpha, Blend.DestinationColor,
            Blend.InverseDestinationColor, Blend.SourceAlphaSaturation, Blend.One, Blend.One, Blend.BlendFactor,
            Blend.InverseBlendFactor,
        ];

        private static Blend BlendOf(int d3d) => d3d >= 0 && d3d < Blends.Length ? Blends[d3d] : Blend.One;

        private static BlendFunction FunctionOf(int d3d) => d3d switch
        {
            2 => BlendFunction.Subtract,
            3 => BlendFunction.ReverseSubtract,
            4 => BlendFunction.Min,
            5 => BlendFunction.Max,
            _ => BlendFunction.Add,
        };

        /// <summary>D3D11_BLEND_DESC: AlphaToCoverage, IndependentBlend, then RenderTarget[0] at 8.</summary>
        private BlendState BlendFor(byte[]? desc, float[] factor)
        {
            string key = (desc is null ? "default" : System.Convert.ToHexString(desc)) + string.Join(",", factor);
            if (_blends.TryGetValue(key, out BlendState? state))
            {
                return state;
            }

            state = new BlendState
            {
                BlendFactor = new Color(factor[0], factor[1], factor[2], factor[3]),
            };

            if (desc is not null && I32(desc, 8) != 0)
            {
                state.ColorSourceBlend = BlendOf(I32(desc, 12));
                state.ColorDestinationBlend = BlendOf(I32(desc, 16));
                state.ColorBlendFunction = FunctionOf(I32(desc, 20));
                state.AlphaSourceBlend = BlendOf(I32(desc, 24));
                state.AlphaDestinationBlend = BlendOf(I32(desc, 28));
                state.AlphaBlendFunction = FunctionOf(I32(desc, 32));
            }
            else
            {
                state.ColorSourceBlend = state.AlphaSourceBlend = Blend.One;
                state.ColorDestinationBlend = state.AlphaDestinationBlend = Blend.Zero;
            }

            state.ColorWriteChannels = desc is null ? ColorWriteChannels.All : (ColorWriteChannels)(desc[36] & 0xF);
            _blends[key] = state;
            return state;
        }

        private static CompareFunction CompareOf(int d3d) => d3d switch
        {
            1 => CompareFunction.Never,
            2 => CompareFunction.Less,
            3 => CompareFunction.Equal,
            4 => CompareFunction.LessEqual,
            5 => CompareFunction.Greater,
            6 => CompareFunction.NotEqual,
            7 => CompareFunction.GreaterEqual,
            _ => CompareFunction.Always,
        };

        private static StencilOperation StencilOf(int d3d) => d3d switch
        {
            2 => StencilOperation.Zero,
            3 => StencilOperation.Replace,
            4 => StencilOperation.IncrementSaturation,
            5 => StencilOperation.DecrementSaturation,
            6 => StencilOperation.Invert,
            7 => StencilOperation.Increment,
            8 => StencilOperation.Decrement,
            _ => StencilOperation.Keep,
        };

        /// <summary>D3D11_DEPTH_STENCIL_DESC; null is Direct3D 11's default (depth on, write, LESS).</summary>
        private DepthStencilState DepthFor(byte[]? desc, int stencilRef)
        {
            string key = (desc is null ? "default" : System.Convert.ToHexString(desc)) + "/" + stencilRef;
            if (_depths.TryGetValue(key, out DepthStencilState? state))
            {
                return state;
            }

            state = new DepthStencilState { DepthBufferEnable = true, DepthBufferWriteEnable = true, DepthBufferFunction = CompareFunction.Less };
            if (desc is not null)
            {
                state.DepthBufferEnable = I32(desc, 0) != 0;
                state.DepthBufferWriteEnable = I32(desc, 4) != 0;
                state.DepthBufferFunction = CompareOf(I32(desc, 8));
                state.StencilEnable = I32(desc, 12) != 0;
                state.StencilMask = desc[16];
                state.StencilWriteMask = desc[17];
                state.StencilFail = StencilOf(I32(desc, 20));
                state.StencilDepthBufferFail = StencilOf(I32(desc, 24));
                state.StencilPass = StencilOf(I32(desc, 28));
                state.StencilFunction = CompareOf(I32(desc, 32));
                state.TwoSidedStencilMode = true;
                state.CounterClockwiseStencilFail = StencilOf(I32(desc, 36));
                state.CounterClockwiseStencilDepthBufferFail = StencilOf(I32(desc, 40));
                state.CounterClockwiseStencilPass = StencilOf(I32(desc, 44));
                state.CounterClockwiseStencilFunction = CompareOf(I32(desc, 48));
                state.ReferenceStencil = stencilRef;
            }

            _depths[key] = state;
            return state;
        }

        /// <summary>D3D11_RASTERIZER_DESC: FillMode, CullMode, FrontCounterClockwise, DepthBias, clamp, slope, ...</summary>
        private RasterizerState RasterFor(byte[]? desc)
        {
            string key = desc is null ? "default" : System.Convert.ToHexString(desc);
            if (_rasters.TryGetValue(key, out RasterizerState? state))
            {
                return state;
            }

            int cull = desc is null ? 3 : I32(desc, 4);
            bool frontCcw = desc is not null && I32(desc, 8) != 0;
            state = new RasterizerState
            {
                FillMode = desc is not null && I32(desc, 0) == 2 ? FillMode.WireFrame : FillMode.Solid,
                // XNA culls by winding with clockwise as the front face; cull BACK with a
                // clockwise front therefore culls counter-clockwise faces.
                CullMode = cull switch
                {
                    1 => CullMode.None,
                    2 => frontCcw ? CullMode.CullCounterClockwiseFace : CullMode.CullClockwiseFace,
                    _ => frontCcw ? CullMode.CullClockwiseFace : CullMode.CullCounterClockwiseFace,
                },
                ScissorTestEnable = false,
                DepthBias = desc is null ? 0 : I32(desc, 12) / 16777216f,
                SlopeScaleDepthBias = desc is null ? 0 : BitConverter.ToSingle(desc, 20),
            };

            _rasters[key] = state;
            return state;
        }

        /// <summary>D3D11_SAMPLER_DESC: Filter, AddressU, AddressV, AddressW, MipLODBias, MaxAnisotropy, ...</summary>
        private SamplerState SamplerFor(byte[]? desc)
        {
            string key = desc is null ? "default" : System.Convert.ToHexString(desc);
            if (_samplers.TryGetValue(key, out SamplerState? state))
            {
                return state;
            }

            TextureAddressMode Address(int d3d) => d3d switch
            {
                1 => TextureAddressMode.Wrap,
                2 or 5 => TextureAddressMode.Mirror,
                _ => TextureAddressMode.Clamp,
            };

            int filter = desc is null ? 0x15 : I32(desc, 0);
            bool min = (filter & 0x10) != 0, mag = (filter & 0x4) != 0, mip = (filter & 0x1) != 0;
            state = new SamplerState
            {
                Filter = (filter & 0x40) != 0 ? TextureFilter.Anisotropic : (min, mag, mip) switch
                {
                    (true, true, true) => TextureFilter.Linear,
                    (true, true, false) => TextureFilter.LinearMipPoint,
                    (false, false, true) => TextureFilter.PointMipLinear,
                    (false, false, false) => TextureFilter.Point,
                    (true, false, true) => TextureFilter.MinLinearMagPointMipLinear,
                    (true, false, false) => TextureFilter.MinLinearMagPointMipPoint,
                    (false, true, true) => TextureFilter.MinPointMagLinearMipLinear,
                    (false, true, false) => TextureFilter.MinPointMagLinearMipPoint,
                },
                AddressU = desc is null ? TextureAddressMode.Clamp : Address(I32(desc, 4)),
                AddressV = desc is null ? TextureAddressMode.Clamp : Address(I32(desc, 8)),
                AddressW = desc is null ? TextureAddressMode.Clamp : Address(I32(desc, 12)),
                MaxAnisotropy = desc is null ? 1 : Math.Clamp(I32(desc, 20), 1, 16),
            };

            _samplers[key] = state;
            return state;
        }

        private static int I32(byte[] data, int at) => at + 4 <= data.Length ? BitConverter.ToInt32(data, at) : 0;

        public void Dispose()
        {
            foreach (Effect? effect in _effects.Values)
            {
                effect?.Dispose();
            }

            foreach (RenderTarget2D target in _targets.Values)
            {
                target.Dispose();
            }

            foreach (var (_, texture) in _textures.Values)
            {
                texture.Dispose();
            }

            foreach (var (_, buffer) in _vertexBuffers.Values)
            {
                buffer?.Dispose();
            }

            foreach (UploadedStream uploaded in _uploaded.Values)
            {
                uploaded.Buffer?.Dispose();
            }

            foreach (VertexBuffer zero in _zeroStreams.Values)
            {
                zero.Dispose();
            }

            _indices16?.Dispose();
            _indices32?.Dispose();
            _blit.Dispose();
        }
    }
}
