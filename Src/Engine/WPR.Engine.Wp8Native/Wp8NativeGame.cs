using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;

namespace WPR.Wp8Native
{
    /// <summary>
    /// A WP8 "Modern Native" title (an ARMv7 Thumb-2 PE against WinRT/D3D11/XAudio2) hosted as an
    /// XNA <see cref="Game"/>, so it gets the same window, graphics stack, input and lifecycle as
    /// any XNA title: GameActivity/SDL with FNA3D on Android, D3D11 on Windows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two threads.</b> The guest runs on its own thread on the dynarmic JIT - the image's
    /// entry point never returns, it owns a main loop - with a large stack, because host stubs
    /// and the JIT share it. This class is the XNA side: <see cref="Draw"/> renders the newest
    /// <see cref="FrameDrawList"/> the guest presented, and <see cref="Update"/> turns
    /// <see cref="TouchPanel"/> into the guest's pointer events.
    /// </para>
    /// <para>
    /// <b>Lockstep.</b> The image's clock advances one 1/60 s tick per presented frame (that is
    /// what keeps it deterministic), so game speed is frame rate. Each XNA tick releases the guest
    /// for one frame and the guest waits at Present until it is released again. That pins game
    /// time to XNA's own fixed 60 Hz step - the same pacing every XNA title gets - and a slow
    /// frame simply costs a frame instead of being followed by a burst of catch-up frames.
    /// </para>
    /// <para>
    /// <b>Drawing.</b> The runtime does not run the title's shaders; it reduces every draw to
    /// clip-space triangles with texture coordinates and a vertex colour (see
    /// <see cref="FrameCapture.BuildDrawList"/>), which is exactly a <see cref="BasicEffect"/>
    /// with identity matrices, texturing and vertex colour on. Textures are uploaded once per
    /// (storage, version) and kept for the run.
    /// </para>
    /// </remarks>
    public sealed class Wp8NativeGame : Game
    {
        /// <summary>The guest's stack. Host stubs and the JIT's own frames share it.</summary>
        private const int GuestStackBytes = 64 * 1024 * 1024;

        private readonly string _executable;
        private readonly Action<string>? _log;
        private readonly GraphicsDeviceManager _graphics;

        private ArmEmulator? _emulator;
        private Thread? _guest;
        private volatile bool _closing;
        private readonly SemaphoreSlim _tick = new(0, 2);

        private readonly object _gate = new();
        private FrameDrawList? _latest;

        private BasicEffect? _effect;
        private Texture2D? _white;
        private readonly Dictionary<long, (int Version, Texture2D Texture)> _textures = new();
        private VertexPositionColorTexture[] _vertices = new VertexPositionColorTexture[4096];

        private bool _pointerDown;
        private int _pointerId = -1;
        private long _presented;
        private long _drawn;
        private DateTime _statsAt;
        private long _statsPresented;

        /// <param name="executable">The title's executable inside its install folder.</param>
        /// <param name="sandboxRoot">Where the title's local folder lives on the host.</param>
        /// <param name="log">Per-game trace sink; null to stay quiet.</param>
        public Wp8NativeGame(string executable, string sandboxRoot, Action<string>? log = null, IXboxLiveHost? xboxHost = null)
        {
            _executable = executable;
            _log = log;
            _xboxHost = xboxHost;
            HostStubs.SandboxRoot = sandboxRoot;

            // A live host has a real touch screen: the probe's scripted taps must be off. The
            // runtime reads this at type initialisation, so it has to be set before it is touched.
            Environment.SetEnvironmentVariable("WPR_TAP", "0");

            _graphics = new GraphicsDeviceManager(this)
            {
                PreferredBackBufferWidth = FrameCapture.Width,
                PreferredBackBufferHeight = FrameCapture.Height,
                SupportedOrientations = DisplayOrientation.LandscapeLeft | DisplayOrientation.LandscapeRight,
                IsFullScreen = true,
            };

            IsFixedTimeStep = true;
            TargetElapsedTime = TimeSpan.FromTicks(166667);
        }

        /// <summary>Why the guest stopped, once it has.</summary>
        public string? Outcome { get; private set; }

        protected override void LoadContent()
        {
            base.LoadContent();

            _effect = new BasicEffect(GraphicsDevice)
            {
                World = Matrix.Identity,
                View = Matrix.Identity,
                Projection = Matrix.Identity,
                TextureEnabled = true,
                VertexColorEnabled = true,
                LightingEnabled = false,
            };

            _white = new Texture2D(GraphicsDevice, 1, 1, false, SurfaceFormat.Color);
            _white.SetData(new[] { Color.White });

            _audio = new Wp8NativeAudio(_log);
            _guest = new Thread(RunGuest, GuestStackBytes)
            {
                Name = "WP8 guest (dynarmic)",
                IsBackground = true,
            };
            _guest.Start();
        }

        // ------------------------------------------------------------------------------
        // The guest's side
        // ------------------------------------------------------------------------------

        private readonly IXboxLiveHost? _xboxHost;
        private Wp8NativeAudio? _audio;

        private void RunGuest()
        {
            string? fault;
            try
            {
                Log($"loading {_executable}");
                PeImage image = PeImage.Load(_executable);
                _emulator = new ArmEmulator(image, Path.GetDirectoryName(_executable)!, collectBlockStats: false);
                _emulator.Direct3D.FrameBuilt += OnFrameBuilt;
                _emulator.WinRt.XboxHost = _xboxHost;
                _emulator.XAudio2.Output = _audio;
                _emulator.WinRt.BackPressDelivered += handled =>
                {
                    Log($"back press {(handled ? "kept by the game" : "let through: closing")}");
                    if (!handled)
                    {
                        _closeRequested = true;
                    }
                };
                Log($"running on {_emulator.Cpu.Capabilities.Name}");
                if (image.IsDll)
                {
                    // A Direct3D/XAML title: the game is a WinRT component, and the host plays the
                    // managed page that would have activated it (Research/Wp8Native/XamlInterop.cs).
                    string directory = Path.GetDirectoryName(_executable)!;
                    XamlShell? shell = XamlShells.ForComponent(_executable);
                    if (shell is null)
                    {
                        fault = $"{Path.GetFileName(_executable)} is a Direct3D/XAML component WPR has no page description for yet";
                    }
                    else
                    {
                        WinmdReader metadata = WinmdReader.Load(Directory.GetFiles(directory, "*.winmd"));
                        long start = _emulator.WinRt.StartComponent(image, metadata, shell);
                        fault = _emulator.Run(start, long.MaxValue / 4);
                        foreach (string line in _emulator.WinRt.ComponentLog.Take(60))
                        {
                            Log($"[xaml] {line}");
                        }
                    }
                }
                else
                {
                    fault = _emulator.RunEntryPoint(long.MaxValue / 4);
                }
            }
            catch (Exception ex)
            {
                fault = $"host exception: {ex}";
            }

            Outcome = fault ?? _emulator?.StopReason ?? "the image returned";
            Log($"guest stopped after {_presented:N0} frame(s): {Outcome}");
            LogPostMortem();

            // The guest is gone; there is nothing left to show. Ending the Game takes the window
            // down the way any XNA title's exit does, and the launcher reports the outcome.
            if (!_closing)
            {
                Exit();
            }
        }

        /// <summary>
        /// What the guest was doing when it stopped, in the per-game log - the same facts the
        /// probe's report leads with, so a crash seen in play can be diagnosed without
        /// reproducing it. The guest thread has finished, so reading its state is safe.
        /// </summary>
        private void LogPostMortem()
        {
            ArmEmulator? emulator = _emulator;
            if (emulator is null || _closing)
            {
                return;
            }

            try
            {
                Log($"   pc=0x{emulator.ReadRegister(UnicornEngine.Const.Arm.UC_ARM_REG_PC):X8} " +
                    $"lr=0x{emulator.ReadRegister(UnicornEngine.Const.Arm.UC_ARM_REG_LR):X8} " +
                    $"sp=0x{emulator.ReadRegister(UnicornEngine.Const.Arm.UC_ARM_REG_SP):X8} " +
                    $"r0=0x{emulator.ReadRegister(UnicornEngine.Const.Arm.UC_ARM_REG_R0):X8} " +
                    $"r3=0x{emulator.ReadRegister(UnicornEngine.Const.Arm.UC_ARM_REG_R3):X8}");
                if (emulator.NullCall is { } nullCall)
                {
                    Log($"   null call to 0x{nullCall.Address:X8} from 0x{nullCall.CalledFrom:X8}");
                }

                if (emulator.UncontainedNullCall is { } wild)
                {
                    foreach (string line in wild.Split('\n').Take(16))
                    {
                        Log("   " + line.Trim());
                    }
                }

                foreach (string line in emulator.DescribeThreads())
                {
                    Log("   " + line);
                }

                foreach (string line in emulator.WinRt.UnimplementedCalls.TakeLast(12))
                {
                    Log("   unimplemented: " + line);
                }

                foreach (string line in emulator.Stubs.ThrowHistory.TakeLast(5))
                {
                    Log("   throw: " + line);
                }

                foreach (var (name, calls) in emulator.Stubs.DefaultedCalls.OrderByDescending(p => p.Value).Take(25))
                {
                    Log($"   defaulted: {calls,8:N0}  {name}");
                }

                IReadOnlyList<string> order = emulator.CallOrder;
                foreach (string call in order.Skip(Math.Max(0, order.Count - 15)))
                {
                    Log($"   last call: {call}");
                }
            }
            catch (Exception ex)
            {
                Log($"post-mortem failed: {ex.Message}");
            }
        }

        private void OnFrameBuilt(FrameDrawList frame)
        {
            _presented++;
            lock (_gate)
            {
                _latest = frame;
            }

            // Lockstep with the XNA loop: wait for the next tick before the guest builds another
            // frame. Bounded so a stalled host (a paused activity) cannot wedge the guest for ever
            // with nothing to show for it.
            if (!_closing)
            {
                _tick.Wait(250);
            }
        }

        // ------------------------------------------------------------------------------
        // The XNA side
        // ------------------------------------------------------------------------------

        protected override void Update(GameTime gameTime)
        {
            if (_closeRequested)
            {
                // The game let a Back press through, which on WP8 closes the app.
                _closeRequested = false;
                Exit();
                return;
            }

            PumpTouch();
            PumpBack();
            _audio?.Pump();

            // One guest frame per tick, never banked beyond two.
            if (_tick.CurrentCount < 2)
            {
                try { _tick.Release(); } catch (SemaphoreFullException) { }
            }

            ReportStatistics();
            base.Update(gameTime);
        }

        private void PumpTouch()
        {
            ArmEmulator? emulator = _emulator;
            if (emulator is null)
            {
                return;
            }

            // TouchPanel positions are in display space, which is the backbuffer - the 800x480
            // the guest composes in - so they go across unscaled. One finger: the runtime delivers
            // one pointer, and the first finger down is the one the game follows.
            TouchCollection touches = TouchPanel.GetState();
            TouchLocation? tracked = null;
            foreach (TouchLocation touch in touches)
            {
                // A finger first seen already Moved still starts a touch: a short tap can go down
                // and move inside one frame, and its Pressed sample is then never seen here.
                if (_pointerId == -1 && touch.State is TouchLocationState.Pressed or TouchLocationState.Moved)
                {
                    _pointerId = touch.Id;
                }

                if (touch.Id == _pointerId)
                {
                    tracked = touch;
                }
            }

            if (tracked is not { } t)
            {
                if (_pointerDown)
                {
                    // The finger vanished without a Released sample (a cancelled gesture).
                    _pointerDown = false;
                    _pointerId = -1;
                    emulator.WinRt.InjectPointer(WinRtRuntime.PointerKind.Released, _lastX, _lastY);
                }

                return;
            }

            float x = Math.Clamp(t.Position.X, 0, FrameCapture.Width - 1);
            float y = Math.Clamp(t.Position.Y, 0, FrameCapture.Height - 1);

            TouchLocationState state = !_pointerDown && t.State == TouchLocationState.Moved ? TouchLocationState.Pressed : t.State;
            switch (state)
            {
                case TouchLocationState.Pressed:
                    _pointerDown = true;
                    Log($"pointer pressed at ({x:0},{y:0}) [touch raw ({t.Position.X:0},{t.Position.Y:0}), display {TouchPanel.DisplayWidth}x{TouchPanel.DisplayHeight}]");
                    emulator.WinRt.InjectPointer(WinRtRuntime.PointerKind.Pressed, x, y);
                    break;
                case TouchLocationState.Moved when _pointerDown && (x != _lastX || y != _lastY):
                    emulator.WinRt.InjectPointer(WinRtRuntime.PointerKind.Moved, x, y);
                    break;
                case TouchLocationState.Released:
                    if (_pointerDown)
                    {
                        Log($"pointer released at ({x:0},{y:0})");
                        emulator.WinRt.InjectPointer(WinRtRuntime.PointerKind.Released, x, y);
                    }

                    _pointerDown = false;
                    _pointerId = -1;
                    break;
            }

            _lastX = x;
            _lastY = y;
        }

        private float _lastX;
        private float _lastY;

        private bool _backWasDown;
        private volatile bool _closeRequested;

        /// <summary>
        /// The phone's Back button. It reaches every XNA title as <c>GamePad.Buttons.Back</c> -
        /// the hardware key on Android, the bound key (Escape) on the desktop - held for at least
        /// a frame, so a press is the frame it goes down.
        /// </summary>
        private void PumpBack()
        {
            bool down = GamePad.GetState(PlayerIndex.One).Buttons.Back == ButtonState.Pressed;
            if (down && !_backWasDown && _emulator is { } emulator)
            {
                Log("back pressed");
                emulator.WinRt.InjectBack();
            }

            _backWasDown = down;
        }

        protected override void Draw(GameTime gameTime)
        {
            FrameDrawList? frame;
            lock (_gate)
            {
                frame = _latest;
            }

            if (frame is null || _effect is null)
            {
                GraphicsDevice.Clear(Color.Black);
                base.Draw(gameTime);
                return;
            }

            GraphicsDevice.Clear(new Color(frame.Clear[0], frame.Clear[1], frame.Clear[2], 1f));
            GraphicsDevice.BlendState = BlendState.NonPremultiplied;
            GraphicsDevice.DepthStencilState = DepthStencilState.None;
            GraphicsDevice.RasterizerState = RasterizerState.CullNone;
            GraphicsDevice.SamplerStates[0] = SamplerState.LinearClamp;

            float[] v = frame.Vertices;
            int count = v.Length / FrameDrawList.Stride;
            if (_vertices.Length < count)
            {
                _vertices = new VertexPositionColorTexture[Math.Max(count, _vertices.Length * 2)];
            }

            for (int i = 0, at = 0; i < count; i++, at += FrameDrawList.Stride)
            {
                _vertices[i] = new VertexPositionColorTexture(
                    new Vector3(v[at], v[at + 1], 0f),
                    new Color(v[at + 4], v[at + 5], v[at + 6], v[at + 7]),
                    new Vector2(v[at + 2], v[at + 3]));
            }

            foreach (FrameDrawList.Batch batch in frame.Batches)
            {
                if (batch.VertexCount < 3)
                {
                    continue;
                }

                _effect.Texture = batch.Texture is null ? _white : TextureFor(batch.Texture);
                GraphicsDevice.SamplerStates[0] = batch.Wrap ? SamplerState.LinearWrap : SamplerState.LinearClamp;
                foreach (EffectPass pass in _effect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    GraphicsDevice.DrawUserPrimitives(
                        PrimitiveType.TriangleList, _vertices, batch.FirstVertex, batch.VertexCount / 3);
                }
            }

            _drawn++;
            base.Draw(gameTime);
        }

        private Texture2D TextureFor(TextureImage image)
        {
            if (_textures.TryGetValue(image.Key, out var known) && known.Version == image.Version &&
                known.Texture.Width == image.Width && known.Texture.Height == image.Height)
            {
                return known.Texture;
            }

            Texture2D texture = known.Texture is { } existing &&
                                existing.Width == image.Width && existing.Height == image.Height
                ? existing
                : new Texture2D(GraphicsDevice, image.Width, image.Height, false, SurfaceFormat.Color);

            if (!ReferenceEquals(texture, known.Texture))
            {
                known.Texture?.Dispose();
            }

            texture.SetData(image.Rgba);
            _textures[image.Key] = (image.Version, texture);
            return texture;
        }

        private void ReportStatistics()
        {
            DateTime now = DateTime.UtcNow;
            if (_statsAt == default)
            {
                _statsAt = now;
                return;
            }

            double seconds = (now - _statsAt).TotalSeconds;
            if (seconds < 5)
            {
                return;
            }

            long build = _emulator?.Direct3D.RasteriseTicks ?? 0;
            Log($"guest fps={(_presented - _statsPresented) / seconds:0.0} frames={_presented} drawn={_drawn} " +
                $"drawlist-build total={build * 1000 / System.Diagnostics.Stopwatch.Frequency}ms textures={_textures.Count} " +
                $"audio: {_emulator?.XAudio2.Summary()} voices={_audio?.VoicesCreated} underruns={_audio?.Underruns} callbacks={_emulator?.XAudio2.CallbacksMade}");
            // What the audio engine was asked for since the last report (racy read of a list the
            // guest appends to; a diagnostic, so a missed line is fine).
            if (_emulator is { } audioOwner)
            {
                try
                {
                    IReadOnlyList<string> audioLog = audioOwner.XAudio2.Log;
                    for (; _audioLogged < audioLog.Count && _audioLogged < 64; _audioLogged++)
                    {
                        Log($"[audio] {audioLog[_audioLogged]}");
                    }

                    foreach (string format in audioOwner.XAudio2.UnsupportedFormats.ToArray().Skip(_unsupportedLogged))
                    {
                        Log($"[audio] cannot play {format}");
                        _unsupportedLogged++;
                    }
                }
                catch (Exception)
                {
                }
            }

            // A guest that has stopped presenting is either computing (a load) or stuck in a
            // loop. The tail of its host calls says which, and where - read racily from the
            // guest thread's own bookkeeping, which is fine for a diagnostic.
            if (_presented == _statsPresented && _emulator is { } emulator)
            {
                try
                {
                    long calls = emulator.CallOrderTotal;
                    Log($"guest has not presented for {seconds:0}s: {calls - _statsCalls:N0} host call(s) since the last report");
                    // Racy snapshot of the per-import counts; retried, because the guest is writing
                    // them as fast as it can. The hottest deltas name the loop.
                    KeyValuePair<string, int>[]? counts = null;
                    for (int attempt = 0; attempt < 50 && counts is null; attempt++)
                    {
                        try { counts = emulator.CallCounts.ToArray(); } catch (InvalidOperationException) { }
                    }

                    if (counts is not null)
                    {
                        var deltas = counts
                            .Select(kv => (kv.Key, Delta: kv.Value - (_lastCounts.TryGetValue(kv.Key, out int was) ? was : 0)))
                            .Where(d => d.Delta > 0)
                            .OrderByDescending(d => d.Delta)
                            .Take(8);
                        foreach (var (name, delta) in deltas)
                        {
                            Log($"   hot: {delta,12:N0}  {name}");
                        }

                        _lastCounts = counts.ToDictionary(kv => kv.Key, kv => kv.Value);
                    }

                    _statsCalls = calls;
                }
                catch (Exception ex)
                {
                    Log($"stall report failed: {ex.Message}");
                }
            }

            _statsAt = now;
            _statsPresented = _presented;
        }

        private int _audioLogged;
        private int _unsupportedLogged;
        private long _statsCalls;
        private Dictionary<string, int> _lastCounts = new();

        private void Log(string message) => _log?.Invoke("[wpr-wp8] " + message);

        protected override void OnExiting(object sender, EventArgs args)
        {
            _closing = true;
            try { _emulator?.Stop("host exiting"); } catch { }
            try { _tick.Release(); } catch { }
            base.OnExiting(sender, args);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _closing = true;
                try { _emulator?.Stop("host disposed"); } catch { }
                try { _tick.Release(); } catch { }

                // The guest thread is inside the JIT; give it a moment to notice the stop before
                // its CPU and memory are torn down underneath it.
                // Only then is the CPU released: on the desktop several titles run in one
                // process, and each guest holds a gigabyte reservation plus the JIT's code cache.
                if (_guest is null || _guest.Join(TimeSpan.FromSeconds(2)))
                {
                    _emulator?.Dispose();
                }

                _audio?.Dispose();
                foreach (var (_, texture) in _textures.Values)
                {
                    texture.Dispose();
                }

                _textures.Clear();
                _white?.Dispose();
                _effect?.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
