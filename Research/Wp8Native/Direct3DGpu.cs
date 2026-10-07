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

            return methods;
        }

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

            // OMSetBlendState(state, const FLOAT factor[4], UINT mask)
            methods[35] = ("OMSetBlendState", Chain(methods.GetValueOrDefault(35).Item2, () =>
            {
                _gpuBlend = Arg(1);
                _gpuBlendFactor = Arg(2) == 0
                    ? [1f, 1f, 1f, 1f]
                    : [.. Enumerable.Range(0, 4).Select(i => BitConverter.ToSingle(_emulator.ReadMemory(Arg(2) + (i * 4), 4)))];
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
                    byte[] raw = _emulator.ReadMemory(Arg(2), 24);
                    _gpuViewport = [.. Enumerable.Range(0, 6).Select(i => BitConverter.ToSingle(raw, i * 4))];
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

            Dictionary<int, byte[]> Constants(long[] slots)
            {
                Dictionary<int, byte[]> copies = [];
                for (int i = 0; i < slots.Length; i++)
                {
                    if (ResourceAt(slots[i]) is { Storage: not 0, StorageSize: > 0 } buffer)
                    {
                        copies[i] = _emulator.ReadMemory(buffer.Storage, (int)Math.Min(buffer.StorageSize, 64 * 1024));
                    }
                }

                return copies;
            }

            Dictionary<int, GpuTexture> textures = [];
            Dictionary<int, byte[]> samplers = [];
            for (int i = 0; i < _psViews.Length; i++)
            {
                if (_psViews[i] != 0 && _viewedResource.TryGetValue(_psViews[i], out FrameCapture.Resource? resource))
                {
                    textures[i] = _renderTargetKeys.Contains(resource.Storage)
                        ? new GpuTexture(null, new GpuTarget(resource.Storage, resource.PixelWidth, resource.PixelHeight))
                        : new GpuTexture(FrameCapture.DecodeRgba(_emulator, resource), null);
                }

                if (_psSamplers[i] != 0 && _gpuStateDescs.TryGetValue(_psSamplers[i], out byte[]? sampler))
                {
                    samplers[i] = sampler;
                }
            }

            _gpuCommands.Add(new GpuDraw(
                vertex,
                pixel,
                Constants(_vsConstantBuffers),
                Constants(_psConstantBuffers),
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
                geometry));
        }

        /// <summary>Hands the frame's commands to the host; true if a GPU host took it.</summary>
        private bool DeliverGpuFrame()
        {
            if (!GpuCapture || GpuFrameBuilt is not { } host)
            {
                return false;
            }

            GpuFrame frame = new() { Commands = _gpuCommands };
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
