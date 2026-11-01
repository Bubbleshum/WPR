namespace WPR.Wp8Native
{
    /// <summary>
    /// The 3D path's recorder: everything a draw needs to be replayed on the host GPU with the
    /// game's own shaders, captured as <see cref="GpuFrame"/> commands.
    /// </summary>
    /// <remarks>
    /// It rides alongside the 2D path rather than replacing it: each hooked device and context
    /// method records what it needs and then runs the existing handler unchanged. With
    /// <see cref="GpuCapture"/> off nothing here is recorded and the 2D path behaves exactly as
    /// before.
    /// </remarks>
    public sealed partial class Direct3DRuntime
    {
        /// <summary>Record GPU commands and deliver them through <see cref="GpuFrameBuilt"/> instead of the 2D draw list.</summary>
        public bool GpuCapture { get; set; }

        /// <summary>Raised at each Present with the frame's commands, on the emulator's thread.</summary>
        public event Action<GpuFrame>? GpuFrameBuilt;

        private readonly Dictionary<long, GpuShader> _gpuShaders = new();
        private readonly Dictionary<long, byte[]> _gpuStateDescs = new();

        /// <summary>
        /// D3D11_BLEND_DESC1 (AlphaToCoverage, IndependentBlend, then RenderTarget[0]: BlendEnable,
        /// LogicOpEnable, SrcBlend, DestBlend, BlendOp, SrcBlendAlpha, DestBlendAlpha, BlendOpAlpha,
        /// LogicOp, WriteMask) as the D3D11_BLEND_DESC prefix the replay reads (the same fields
        /// without the two logic-op ones).
        /// </summary>
        internal static byte[] BlendDescFromDesc1(byte[] desc1)
        {
            byte[] desc = new byte[40];
            Array.Copy(desc1, 0, desc, 0, 12);    // AlphaToCoverage, IndependentBlend, BlendEnable
            Array.Copy(desc1, 16, desc, 12, 24);  // SrcBlend .. BlendOpAlpha
            desc[36] = desc1[44];                 // RenderTargetWriteMask
            return desc;
        }
        private readonly HashSet<long> _renderTargetKeys = new();
        private readonly long[] _vsConstantBuffers = new long[14];
        private readonly long[] _psConstantBuffers = new long[14];
        private readonly long[] _psViews = new long[16];
        private readonly long[] _psSamplers = new long[16];
        private long _gpuVertexShader, _gpuPixelShader, _gpuBlend, _gpuDepth, _gpuRaster, _gpuRenderTarget, _gpuDepthTarget;
        private float[] _gpuBlendFactor = [1f, 1f, 1f, 1f];
        private int _gpuStencilRef;
        private float[]? _gpuViewport;
        private List<GpuCommand> _gpuCommands = new();

        /// <summary>Draws the GPU path skipped, by reason - a shader it has not seen, no render target.</summary>
        public Dictionary<string, int> GpuSkipped { get; } = new(StringComparer.Ordinal);

        private void GpuSkip(string why) => GpuSkipped[why] = GpuSkipped.GetValueOrDefault(why) + 1;

        /// <summary>
        /// Runs <paramref name="wrap"/> around a method's existing handler - or around a plain S_OK
        /// when the 2D path never needed one (PSSetShader and PSSetConstantBuffers have none).
        /// </summary>
        private void Wrap(Dictionary<int, (string, Action)> methods, int slot, Func<Action, Action> wrap)
        {
            (string Name, Action Handler) entry = methods.TryGetValue(slot, out var existing)
                ? existing
                : ($"slot{slot}", () => Return(HResultOk));
            methods[slot] = (entry.Name, wrap(entry.Handler));
        }

        private Dictionary<int, (string, Action)> DeviceMethods()
        {
            Dictionary<int, (string, Action)> methods = DeviceCore();

            // Create*Shader(bytecode, length, linkage, out): keep the DXBC by the object made.
            foreach (int slot in new[] { 12, 15 })
            {
                Wrap(methods, slot, original => () =>
                {
                    long code = Arg(1), length = Arg(2), outPointer = Arg(4);
                    original();
                    long shader = outPointer == 0 ? 0 : _emulator.ReadUInt32(outPointer, 0);
                    if (shader != 0 && code != 0 && length is > 0 and < 1024 * 1024)
                    {
                        _gpuShaders[shader] = new GpuShader(shader, _emulator.ReadMemory(code, (int)length));
                    }
                });
            }

            // Create{Blend,DepthStencil,Rasterizer,Sampler}State(desc, out): keep the desc bytes.
            foreach ((int slot, int size) in new[] { (20, 40), (21, 52), (22, 40), (23, 52) })
            {
                Wrap(methods, slot, original => () =>
                {
                    long desc = Arg(1), outPointer = Arg(2);
                    original();
                    long state = outPointer == 0 ? 0 : _emulator.ReadUInt32(outPointer, 0);
                    if (state != 0 && desc != 0)
                    {
                        _gpuStateDescs[state] = _emulator.ReadMemory(desc, size);
                    }
                });
            }

            // ID3D11Device1's CreateBlendState1 (45) and CreateRasterizerState1 (46). Missing these
            // drew every state made through them as the default - opaque, no blending - so a
            // title that makes its blend states this way (Where's My Perry) drew each sprite's
            // transparent area black. RASTERIZER_DESC1 is RASTERIZER_DESC plus ForcedSampleCount;
            // BLEND_DESC1's render-target entries carry LogicOpEnable and LogicOp, so it is
            // rewritten into the BLEND_DESC layout the replay reads.
            Wrap(methods, 45, original => () =>
            {
                long desc = Arg(1), outPointer = Arg(2);
                original();
                long state = outPointer == 0 ? 0 : _emulator.ReadUInt32(outPointer, 0);
                if (state != 0 && desc != 0)
                {
                    _gpuStateDescs[state] = BlendDescFromDesc1(_emulator.ReadMemory(desc, 8 + 40));
                }
            });
            Wrap(methods, 46, original => () =>
            {
                long desc = Arg(1), outPointer = Arg(2);
                original();
                long state = outPointer == 0 ? 0 : _emulator.ReadUInt32(outPointer, 0);
                if (state != 0 && desc != 0)
                {
                    _gpuStateDescs[state] = _emulator.ReadMemory(desc, 40);
                }
            });

            return methods;
        }

        /// <summary>
        /// <paramref name="current"/> when it already holds <paramref name="values"/>, else a new
        /// array of them. Recorded draws keep the array they were given, so a changed value needs a
        /// new one; an unchanged one (nearly every call: a title sets the same viewport and blend
        /// factor before each draw) needs nothing.
        /// </summary>
        private static float[] SameOrNew(float[]? current, ReadOnlySpan<float> values) =>
            current is not null && values.SequenceEqual(current) ? current : values.ToArray();

        private void HookGpuContext(Dictionary<int, (string, Action)> methods)
        {
            void Slots(int slot, long[] into) => Wrap(methods, slot, original => () =>
            {
                // (StartSlot, NumX, X* const*)
                long start = Arg(1), count = Arg(2), array = Arg(3);
                for (long i = 0; i < count && start + i < into.Length; i++)
                {
                    into[start + i] = array == 0 ? 0 : _emulator.ReadUInt32(array + (i * 4), 0);
                }

                original();
            });

            Slots(7, _vsConstantBuffers);
            Slots(16, _psConstantBuffers);
            Slots(8, _psViews);
            Slots(10, _psSamplers);
            Wrap(methods, 9, original => () => { _gpuPixelShader = Arg(1); original(); });
            Wrap(methods, 11, original => () => { _gpuVertexShader = Arg(1); original(); });

            // OMSetRenderTargets(NumViews, RTV* const*, DSV*)
            methods[33] = (methods.GetValueOrDefault(33).Item1 ?? "OMSetRenderTargets", Chain(methods.GetValueOrDefault(33).Item2, () =>
            {
                _gpuRenderTarget = Arg(1) > 0 && Arg(2) != 0 ? _emulator.ReadUInt32(Arg(2), 0) : 0;
                _gpuDepthTarget = Arg(3);
            }));

            // OMSetRenderTargetsAndUnorderedAccessViews(NumRTVs, RTV* const*, DSV*, ...): the same
            // binding, unless NumRTVs is D3D11_KEEP_RENDER_TARGETS_AND_DEPTH_STENCIL.
            methods[34] = (methods.GetValueOrDefault(34).Item1 ?? "OMSetRenderTargetsAndUnorderedAccessViews", Chain(methods.GetValueOrDefault(34).Item2, () =>
            {
                if (Arg(1) != 0xFFFFFFFF)
                {
                    _gpuRenderTarget = Arg(1) > 0 && Arg(2) != 0 ? _emulator.ReadUInt32(Arg(2), 0) : 0;
                    _gpuDepthTarget = Arg(3);
                }
            }));

            // OMSetBlendState(state, const FLOAT factor[4], UINT mask)
            methods[35] = ("OMSetBlendState", Chain(methods.GetValueOrDefault(35).Item2, () =>
            {
                _gpuBlend = Arg(1);
                Span<float> factor = stackalloc float[4];
                if (Arg(2) == 0)
                {
                    factor.Fill(1f);
                }
                else
                {
                    _emulator.ReadMemory(Arg(2), System.Runtime.InteropServices.MemoryMarshal.AsBytes(factor));
                }

                _gpuBlendFactor = SameOrNew(_gpuBlendFactor, factor);
            }));

            // OMSetDepthStencilState(state, UINT ref)
            methods[36] = ("OMSetDepthStencilState", Chain(methods.GetValueOrDefault(36).Item2, () =>
            {
                _gpuDepth = Arg(1);
                _gpuStencilRef = (int)Arg(2);
            }));

            methods[43] = ("RSSetState", Chain(methods.GetValueOrDefault(43).Item2, () => _gpuRaster = Arg(1)));

            // RSSetViewports(Num, const D3D11_VIEWPORT*): TopLeftX, TopLeftY, Width, Height, MinDepth, MaxDepth.
            Wrap(methods, 44, original => () =>
            {
                if (Arg(1) > 0 && Arg(2) != 0)
                {
                    Span<float> viewport = stackalloc float[6];
                    _emulator.ReadMemory(Arg(2), System.Runtime.InteropServices.MemoryMarshal.AsBytes(viewport));
                    _gpuViewport = SameOrNew(_gpuViewport, viewport);
                }

                original();
            });

            // CopyResource(dst, src), and CopySubresourceRegion(dst, dstSub, x, y, z, src, srcSub, box)
            // when it copies a whole surface to the origin. Spark2 (Assassin's Creed Pirates) renders
            // on a device of its own and its Draw copies that shared texture into the host's target.
            // Only a source the GPU drew into is a GPU copy. One filled from the CPU (a staging
            // texture, Mapped and written - Scaleform's upload path) is copied in memory by the
            // context itself; turning both ends into render targets here made the destination an
            // empty target that every later draw sampled instead of its pixels.
            void Copy(long destination, long source, int x = 0, int y = 0, int[]? box = null)
            {
                bool drawnSource = source == _backBufferPointer || (ResourceAt(source) is { Storage: not 0 } s && _renderTargetKeys.Contains(s.Storage));
                if (GpuCapture && drawnSource && TargetOfResource(destination) is { } to && TargetOfResource(source) is { } from && to.Key != from.Key)
                {
                    _gpuCommands.Add(new GpuCopy(to, from, x, y, box));
                }
            }

            Wrap(methods, 47, original => () => { Copy(Arg(1), Arg(2)); original(); });

            // ResolveSubresource(dst, dstSub, src, srcSub, format): a multisampled target resolved into
            // a texture that is then drawn with (Asphalt 7's race). The host's targets are not
            // multisampled, so it is a copy of the whole surface.
            Wrap(methods, 57, original => () =>
            {
                if (Arg(2) == 0 && Arg(4) == 0)
                {
                    Copy(Arg(1), Arg(3));
                }

                original();
            });
            Wrap(methods, 46, original => () =>
            {
                // r0 this, r1 dst, r2 dstSub, r3 x; stack: y, z, src, srcSub, box. Only mip 0 of a
                // 2D target is a render target here; another subresource is not replayed.
                if (Arg(2) == 0 && Arg(5) == 0 && Arg(7) == 0)
                {
                    int[]? box = null;
                    if (Arg(8) != 0)
                    {
                        // D3D11_BOX: left, top, front, right, bottom, back.
                        byte[] raw = _emulator.ReadMemory(Arg(8), 24);
                        box = [BitConverter.ToInt32(raw, 0), BitConverter.ToInt32(raw, 4), BitConverter.ToInt32(raw, 12), BitConverter.ToInt32(raw, 16)];
                    }

                    Copy(Arg(1), Arg(6), (int)Arg(3), (int)Arg(4), box);
                }

                original();
            });

            // ClearRenderTargetView(RTV, const FLOAT colour[4])
            Wrap(methods, 50, original => () =>
            {
                if (GpuCapture && TargetOf(Arg(1)) is { } target && Arg(2) != 0)
                {
                    byte[] raw = _emulator.ReadMemory(Arg(2), 16);
                    _gpuCommands.Add(new GpuClearColour(target, [.. Enumerable.Range(0, 4).Select(i => BitConverter.ToSingle(raw, i * 4))]));
                }

                original();
            });

            // ClearDepthStencilView(DSV, UINT flags, FLOAT depth, UINT8 stencil): the float is in s0,
            // so the stencil value is r3.
            Wrap(methods, 53, original => () =>
            {
                if (GpuCapture && TargetOf(Arg(1)) is { } target)
                {
                    _gpuCommands.Add(new GpuClearDepth(target, (int)Arg(2), _frame.FloatArg(0), (byte)Arg(3)));
                }

                original();
            });
        }

        private Action Chain(Action? original, Action record) => () =>
        {
            record();
            if (original is not null)
            {
                original();
            }
            else
            {
                Return(HResultOk);
            }
        };

        /// <summary>The render target a view points at: the back buffer, or a texture by its storage.</summary>
        private GpuTarget? TargetOf(long view)
        {
            if (view == 0 || !_viewResources.TryGetValue(view, out long resource) || resource == 0)
            {
                return null;
            }

            return TargetOfResource(resource);
        }

        /// <summary>A texture as a render target: the back buffer, or by its storage.</summary>
        private GpuTarget? TargetOfResource(long resource)
        {
            if (resource == 0)
            {
                return null;
            }

            if (resource == _backBufferPointer)
            {
                return new GpuTarget(GpuTarget.BackBufferKey, (int)BackBufferWidth, (int)BackBufferHeight);
            }

            if (ResourceAt(resource) is not { Storage: not 0 } texture)
            {
                return null;
            }

            _renderTargetKeys.Add(texture.Storage);
            return new GpuTarget(texture.Storage, texture.PixelWidth, texture.PixelHeight);
        }

        /// <summary>Called by <see cref="RecordDraw"/> with the geometry snapshot it just took.</summary>
        private void GpuRecordDraw(FrameCapture.DrawCall geometry)
        {
            if (!GpuCapture)
            {
                return;
            }

            if (!_gpuShaders.TryGetValue(_gpuVertexShader, out GpuShader? vertex) ||
                !_gpuShaders.TryGetValue(_gpuPixelShader, out GpuShader? pixel))
            {
                GpuSkip(_gpuShaders.Count == 0 ? "no shaders captured" : $"shader not captured (vs 0x{_gpuVertexShader:X8} ps 0x{_gpuPixelShader:X8}, {_gpuShaders.Count} known)");
                return;
            }

            if (TargetOf(_gpuRenderTarget) is not { } target)
            {
                GpuSkip("no render target");
                return;
            }

            Dictionary<int, byte[]> Constants(long[] slots, ref Dictionary<int, byte[]>? last)
            {
                Dictionary<int, byte[]> copies = Collect(slots);

                // Most draws bind exactly the copies the previous one did; hand back that draw's map
                // (draws never write to it) rather than a new dictionary per draw.
                if (last is not null && last.Count == copies.Count)
                {
                    bool same = true;
                    foreach (var (slot, copy) in copies)
                    {
                        if (!last.TryGetValue(slot, out byte[]? earlier) || !ReferenceEquals(earlier, copy))
                        {
                            same = false;
                            break;
                        }
                    }

                    if (same)
                    {
                        _constantScratch = copies;
                        return last;
                    }
                }

                last = copies;
                return copies;
            }

            Dictionary<int, byte[]> Collect(long[] slots)
            {
                Dictionary<int, byte[]> copies = _constantScratch ?? [];
                _constantScratch = null;
                copies.Clear();
                for (int i = 0; i < slots.Length; i++)
                {
                    if (ResourceAt(slots[i]) is { Storage: not 0, StorageSize: > 0 } buffer)
                    {
                        // Copied once per content version (Map and UpdateSubresource bump it), not
                        // once per draw: a scene binds the same unchanged buffers hundreds of times.
                        if (buffer.ConstantCopy is null || buffer.ConstantCopyVersion != buffer.Version)
                        {
                            if (buffer.ConstantCopy is { } superseded)
                            {
                                _retiredConstants.Enqueue((_gpuFrameSerial, superseded));
                            }

                            byte[] copy = TakeConstantArray((int)Math.Min(buffer.StorageSize, 64 * 1024));
                            _emulator.ReadMemory(buffer.Storage, copy);
                            buffer.ConstantCopy = copy;
                            buffer.ConstantCopyVersion = buffer.Version;
                        }

                        copies[i] = buffer.ConstantCopy;
                    }
                }

                return copies;
            }

            // Built into scratch maps and swapped for the previous draw's when they match, as the
            // constants are: most draws bind what the one before did, and a dictionary and a
            // GpuTexture per slot per draw was a steady stream of garbage in a race.
            Dictionary<int, GpuTexture> textures = _textureScratch ?? [];
            Dictionary<int, byte[]> samplers = _samplerScratch ?? [];
            _textureScratch = null;
            _samplerScratch = null;
            textures.Clear();
            samplers.Clear();
            for (int i = 0; i < _psViews.Length; i++)
            {
                if (_psViews[i] != 0 && _viewedResource.TryGetValue(_psViews[i], out FrameCapture.Resource? resource))
                {
                    GpuTexture? earlier = _lastTextures?.GetValueOrDefault(i);
                    if (_renderTargetKeys.Contains(resource.Storage))
                    {
                        var viewed = new GpuTarget(resource.Storage, resource.PixelWidth, resource.PixelHeight);
                        textures[i] = earlier is { Image: null } && earlier.RenderTarget == viewed ? earlier : new GpuTexture(null, viewed);
                    }
                    else
                    {
                        TextureImage? image = FrameCapture.CubeOrTextureForGpu(_emulator, resource);
                        textures[i] = earlier is { RenderTarget: null } && ReferenceEquals(earlier.Image, image) ? earlier : new GpuTexture(image, null);
                    }
                }

                if (_psSamplers[i] != 0 && _gpuStateDescs.TryGetValue(_psSamplers[i], out byte[]? sampler))
                {
                    samplers[i] = sampler;
                }
            }

            if (SameBindings(_lastTextures, textures))
            {
                _textureScratch = textures;
                textures = _lastTextures!;
            }
            else
            {
                _lastTextures = textures;
            }

            if (SameBindings(_lastSamplers, samplers))
            {
                _samplerScratch = samplers;
                samplers = _lastSamplers!;
            }
            else
            {
                _lastSamplers = samplers;
            }

            _gpuCommands.Add(new GpuDraw(
                vertex,
                pixel,
                Constants(_vsConstantBuffers, ref _lastVsConstants),
                Constants(_psConstantBuffers, ref _lastPsConstants),
                textures,
                samplers,
                _gpuStateDescs.GetValueOrDefault(_gpuBlend),
                _gpuBlendFactor,
                _gpuStateDescs.GetValueOrDefault(_gpuDepth),
                _gpuStencilRef,
                _gpuStateDescs.GetValueOrDefault(_gpuRaster),
                target,
                TargetOf(_gpuDepthTarget),
                _gpuViewport,
                geometry,
                ScissorRect));
        }

        // Constant-buffer copies, recycled. A title that rewrites a buffer before every draw (Jetpack
        // Joyride's sprite palette, ~5,000 times a second) made a new array per draw, ~10 MB/s of
        // garbage that a phone paid for in frequent collections felt as jitter. A superseded copy
        // may still sit in a frame the host has not replayed, so it is retired with the serial of
        // the frame being recorded and reused only once the host has replayed that frame.
        private Dictionary<int, byte[]>? _lastVsConstants, _lastPsConstants, _constantScratch;
        private Dictionary<int, GpuTexture>? _lastTextures, _textureScratch;
        private Dictionary<int, byte[]>? _lastSamplers, _samplerScratch;

        /// <summary>Whether two slot maps hold the same objects in the same slots.</summary>
        private static bool SameBindings<T>(Dictionary<int, T>? last, Dictionary<int, T> current) where T : class
        {
            if (last is null || last.Count != current.Count)
            {
                return false;
            }

            foreach (var (slot, value) in current)
            {
                if (!last.TryGetValue(slot, out T? earlier) || !ReferenceEquals(earlier, value))
                {
                    return false;
                }
            }

            return true;
        }
        private long _gpuFrameSerial;
        private long _replayedGpuSerial = -1;
        private readonly System.Collections.Concurrent.ConcurrentQueue<(long Serial, byte[] Array)> _retiredConstants = new();
        private readonly Dictionary<int, Stack<byte[]>> _constantPool = new();

        /// <summary>
        /// The host has replayed GPU frame <paramref name="serial"/> (and every one before it): the
        /// constant copies those frames held may be reused. Any thread.
        /// </summary>
        public void GpuFrameReplayed(long serial)
        {
            long seen;
            while ((seen = Interlocked.Read(ref _replayedGpuSerial)) < serial &&
                   Interlocked.CompareExchange(ref _replayedGpuSerial, serial, seen) != seen)
            {
            }
        }

        private byte[] TakeConstantArray(int length) => TakeConstantArray(length, out _);

        private byte[] TakeConstantArray(int length, out bool reused)
        {
            long replayed = Interlocked.Read(ref _replayedGpuSerial);
            while (_retiredConstants.TryPeek(out var retired) && retired.Serial <= replayed && _retiredConstants.TryDequeue(out retired))
            {
                if (!_constantPool.TryGetValue(retired.Array.Length, out Stack<byte[]>? free))
                {
                    _constantPool[retired.Array.Length] = free = new Stack<byte[]>();
                }

                // About 2 MB of each size: a race rewrites a few hundred small constant buffers a
                // frame, and a vertex copy can be megabytes on its own.
                if (free.Count < Math.Clamp(2 * 1024 * 1024 / Math.Max(1, retired.Array.Length), 4, 4096))
                {
                    free.Push(retired.Array);
                }
            }

            if (_constantPool.TryGetValue(length, out Stack<byte[]>? pool) && pool.TryPop(out byte[]? array))
            {
                reused = true;
                return array;
            }

            reused = false;
            return new byte[length];
        }

        // Vertex copies (FrameCapture.SnapshotStream) go through the same pool. A dynamic vertex
        // buffer is rewritten every frame, so its copies cannot be shared between frames: Asphalt 7
        // took ~25 MB/s of them in a race, felt on a phone as GC pauses and jumps. A superseded copy
        // is retired with the frame being recorded and reused once the host has replayed it. Only
        // while every frame goes to the GPU host alone: the software rasteriser keeps draws longer.
        private bool RecyclesSnapshots => GpuCapture && GpuFrameBuilt is not null && FramePresented is null;

        private readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> _reusedSnapshots = new();

        /// <summary>A copy no longer referenced by its buffer; reused after the frame being recorded is replayed.</summary>
        internal void RetireSnapshotArray(byte[] array)
        {
            if (RecyclesSnapshots)
            {
                _retiredConstants.Enqueue((_gpuFrameSerial, array));
            }
        }

        /// <summary>An array for a vertex copy: a retired one of that length when there is one.</summary>
        internal byte[] TakeSnapshotArray(int length)
        {
            if (!RecyclesSnapshots)
            {
                return new byte[length];
            }

            byte[] array = TakeConstantArray(length, out bool reused);
            if (reused)
            {
                _reusedSnapshots.Enqueue(array);
            }

            return array;
        }

        // Index copies (FrameCapture.ResolveIndicesCached), the same way: a dynamic index buffer is
        // resolved afresh each frame. The replay copies them into its own buffer per draw and keeps
        // no reference, so a reused array needs no forgetting.
        private readonly System.Collections.Concurrent.ConcurrentQueue<(long Serial, int[] Array)> _retiredIndices = new();
        private readonly Dictionary<int, Stack<int[]>> _indexPool = new();

        internal void RetireIndexArray(int[] array)
        {
            if (RecyclesSnapshots && array.Length > 0)
            {
                _retiredIndices.Enqueue((_gpuFrameSerial, array));
            }
        }

        internal int[] TakeIndexArray(int length)
        {
            if (!RecyclesSnapshots)
            {
                return new int[length];
            }

            long replayed = Interlocked.Read(ref _replayedGpuSerial);
            while (_retiredIndices.TryPeek(out var retired) && retired.Serial <= replayed && _retiredIndices.TryDequeue(out retired))
            {
                if (!_indexPool.TryGetValue(retired.Array.Length, out Stack<int[]>? free))
                {
                    _indexPool[retired.Array.Length] = free = new Stack<int[]>();
                }

                if (free.Count < Math.Clamp(512 * 1024 / retired.Array.Length, 4, 4096))
                {
                    free.Push(retired.Array);
                }
            }

            return _indexPool.TryGetValue(length, out Stack<int[]>? pool) && pool.TryPop(out int[]? array) ? array : new int[length];
        }

        /// <summary>
        /// A vertex copy that now holds other bytes. The host replay caches its GPU buffers by the
        /// array, so it must drop that one before replaying a later frame (Wp8GpuReplay.ForgetStream).
        /// Any thread.
        /// </summary>
        public bool TryTakeReusedSnapshot(out byte[] array) => _reusedSnapshots.TryDequeue(out array!);

        /// <summary>
        /// The part of the back buffer a background grid's provider asked to be shown (its
        /// desiredRenderTargetSize), or null for all of it. Set by the XAML host every frame.
        /// </summary>
        public (int Width, int Height)? ShownSize { get; set; }

        /// <summary>Hands the frame's commands to the host; true if a GPU host took it.</summary>
        private bool DeliverGpuFrame()
        {
            if (!GpuCapture || GpuFrameBuilt is not { } host)
            {
                return false;
            }

            GpuFrame frame = new() { Commands = _gpuCommands, Serial = _gpuFrameSerial++, Shown = ShownSize };
            _gpuCommands = new List<GpuCommand>();
            try
            {
                host(frame);
            }
            catch (Exception ex)
            {
                Note($"GPU frame delivery failed: {ex.Message}");
            }

            return true;
        }
    }
}
