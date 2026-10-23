using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using WPR.Wp8Native;
using WPR.Xna.Rhi;
using XnaGraphicsDevice = Microsoft.Xna.Framework.Graphics.GraphicsDevice;

namespace WPR.HostDirect3D
{
    /// <summary>
    /// What a WP8 <c>DrawingSurfaceBackgroundGrid</c> did for a MANAGED content provider (the
    /// SharpDX Toolkit's GameWindowWP8, and anything else that implements
    /// IDrawingSurfaceBackgroundContentProviderNative in C#): <c>Connect(host, device)</c> once, then
    /// each frame <c>PrepareResources</c> and <c>Draw(device, context, back buffer)</c>. The provider
    /// is called through its own native vtable, exactly as the phone's compositor called it, so the
    /// title's code is untouched; what it draws is recorded by <see cref="HostContext"/> and replayed
    /// onto the host's back buffer.
    /// </summary>
    public sealed unsafe class DrawingSurfaceHost : IEmbeddedGameLoop
    {
        /// <summary>IID_IDrawingSurfaceBackgroundContentProviderNative.</summary>
        private static readonly Guid ProviderIid = new("262c1892-975a-45aa-8fe7-f25c2c4088e3");

        private readonly IntPtr _provider;
        private HostDevice? _device;
        private HostRuntimeHost? _host;
        private HostView? _backBufferView;
        private Wp8GpuReplay? _replay;
        private long _frames;
        private bool _failed;

        private DrawingSurfaceHost(IntPtr provider) => _provider = provider;

        /// <summary>
        /// Called by the Silverlight grid when a title sets its content provider. Takes the
        /// provider's native face (a managed provider answers through ICustomQueryInterface, as the
        /// CLR's COM interop asked it on the phone) and attaches the host's loop to it.
        /// </summary>
        public static bool Attach(object? provider)
        {
            if (provider is null)
            {
                if (EmbeddedGameLoop.Current is DrawingSurfaceHost)
                {
                    HostLog.Write("DrawingSurfaceBackgroundGrid: content provider cleared");
                    EmbeddedGameLoop.Clear();
                }

                return false;
            }

            IntPtr native = IntPtr.Zero;
            if (provider is ICustomQueryInterface query)
            {
                Guid iid = ProviderIid;
                if (query.GetInterface(ref iid, out IntPtr face) == CustomQueryInterfaceResult.Handled)
                {
                    native = face;
                }
            }

            if (native == IntPtr.Zero)
            {
                HostLog.Write($"DrawingSurfaceBackgroundGrid: content provider {provider.GetType().FullName} exposes no native face; nothing will be drawn");
                return false;
            }

            if (EmbeddedGameLoop.Current is DrawingSurfaceHost current && current._provider == native)
            {
                // The same provider handed to another page's grid: it keeps its device and frames.
                return true;
            }

            HostLog.Write($"DrawingSurfaceBackgroundGrid: content provider {provider.GetType().FullName} attached");
            var host = new DrawingSurfaceHost(native);
            EmbeddedGameLoop.Set(host);

            // Connect now, inside SetBackgroundContentProvider, as the phone's grid did: Arkadium's
            // renderer (Microsoft Mahjong, Minesweeper, Solitaire) reads the provider's device on
            // the very next line and builds its back-buffer target from it.
            host.Start();
            return true;
        }

        public bool ExitRequested => false;

        public void SetActive(bool active)
        {
        }

        // XAudio2's callbacks are made here, once a frame on the XNA thread.
        public void Update(GameTime gameTime) => HostXAudio2.PumpAll();

        public void Draw(GameTime gameTime, XnaGraphicsDevice device)
        {
            if (_failed)
            {
                return;
            }

            if (_device is null)
            {
                Start();
            }

            HostTexture2D backBuffer = _backBufferView!.Texture;
            long time = DateTime.UtcNow.Ticks;
            float* size = stackalloc float[2] { backBuffer.Width, backBuffer.Height };
            Call(5, "PrepareResources", (IntPtr)(&time), (IntPtr)size);
            Call(6, "Draw", _device!.Pointer, _device.Context.Pointer, _backBufferView.Pointer);

            if (_replay is null)
            {
                _replay = new Wp8GpuReplay(device, line => System.Diagnostics.Trace.WriteLine(line));
                HostBuffer.Recycling = _replay.ForgetStream;
            }

            if (_device.Context.TakeFrame() is { } frame)
            {
                _replay.Replay(frame);
            }

            // No recorded draw now refers to an array a buffer replaced before this point.
            HostBuffer.FrameReplayed();

            // The provider drew the phone's portrait frame, already turned for the side the device
            // is held: undo that turn. LandscapeLeft (Microsoft Minesweeper's game page) is the
            // other way round from LandscapeRight and plain Landscape (Gravity Guy 2, Mahjong).
            int width = device.PresentationParameters.BackBufferWidth, height = device.PresentationParameters.BackBufferHeight;
            PresentTurn turn = width <= height ? PresentTurn.None
                : EmbeddedGameLoop.PageLandscapeLeft ? PresentTurn.Anticlockwise : PresentTurn.Clockwise;
            _replay.Present(width, height, turn);
            if (++_frames % 600 == 1)
            {
                HostLog.Write($"frame {_frames}: {_replay.Statistics()} drawn={_replay.DrawsReplayed} skipped={_replay.DrawsSkipped}");
            }
        }

        // One device, runtime host and back buffer for the whole launch, as the phone kept one
        // Direct3D device per app: a provider connected again (Arkadium's renderer moves between
        // pages' grids) must get the device it already has, or it reloads every resource onto a
        // "new" one (OnDeviceChange) and fails.
        private static HostDevice? _sharedDevice;
        private static HostRuntimeHost? _sharedHost;
        private static HostView? _sharedBackBufferView;

        /// <summary>At the end of a game run: the next launch gets a new device.</summary>
        public static void ResetDevice()
        {
            _sharedDevice = null;
            _sharedHost = null;
            _sharedBackBufferView = null;
            HostBuffer.Reset();
        }

        private void Start()
        {
            if (_device is not null)
            {
                return;
            }

            if (_sharedDevice is not null)
            {
                _device = _sharedDevice;
                _host = _sharedHost;
                _backBufferView = _sharedBackBufferView;
                HostLog.Write("DrawingSurfaceBackgroundGrid: Connect again, same device");
                Call(3, "Connect", _host!.Pointer, _device.Pointer);
                return;
            }

            _device = _sharedDevice = new HostDevice();
            _host = _sharedHost = new HostRuntimeHost();
            byte* desc = stackalloc byte[44];
            new Span<byte>(desc, 44).Clear();
            int* fields = (int*)desc;
            fields[0] = Wp8Display.PhysicalWidth;
            fields[1] = Wp8Display.PhysicalHeight;
            fields[2] = 1;          // MipLevels
            fields[3] = 1;          // ArraySize
            fields[4] = 87;         // B8G8R8A8_UNORM
            fields[5] = 1;          // SampleDesc.Count
            fields[8] = 0x28;       // RENDER_TARGET | SHADER_RESOURCE
            var backBuffer = new HostTexture2D(_device, desc, null, backBuffer: true);
            _backBufferView = _sharedBackBufferView = new HostView(_device, HostView.Kind.RenderTarget, backBuffer, null);
            HostLog.Write($"DrawingSurfaceBackgroundGrid: Connect with a {backBuffer.Width}x{backBuffer.Height} back buffer, feature level 9_3");
            Call(3, "Connect", _host.Pointer, _device.Pointer);
        }

        private void Call(int slot, string name, IntPtr a, IntPtr b, IntPtr c = default)
        {
            IntPtr fn = (*(IntPtr**)_provider)[slot];
            int hr = slot == 6
                ? ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr, int>)fn)(_provider, a, b, c)
                : ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)fn)(_provider, a, b);
            if (hr < 0)
            {
                HostLog.WriteOnce($"provider {name} {hr:X8}", $"content provider {name} failed: 0x{hr:X8}");
            }
        }
    }

    /// <summary>
    /// The DrawingSurfaceRuntimeHost a provider is connected to. RequestAdditionalFrame is always
    /// granted (the host draws every frame); synchronized textures (the DrawingSurface, not the
    /// background grid) are not provided.
    /// </summary>
    internal sealed unsafe class HostRuntimeHost : HostObject
    {
        private static readonly VTable Table = Build();

        public HostRuntimeHost() : base(Table, new Guid("2e61c499-57cd-48db-aff8-8d1fbc480db7"))
        {
        }

        private static VTable Build()
        {
            var s = new Slots
            {
                [3] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int>)&RequestAdditionalFrame,
            };
            return new VTable("IDrawingSurfaceRuntimeHostNative", 6, s,
                ["QueryInterface", "AddRef", "Release", "RequestAdditionalFrame", "CreateSynchronizedTexture", "GetSynchronizedTexture"]);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int RequestAdditionalFrame(IntPtr self) => 0;
    }
}
