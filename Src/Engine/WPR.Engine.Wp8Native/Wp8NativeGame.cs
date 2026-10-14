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

        private long _presented;
        private long _drawn;
        private DateTime _statsAt;
        private long _statsPresented;
        private int _sensorLogged;

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

        /// <summary>
        /// Set when the guest stopped on its own - a fault, an uncaught throw, a diagnostic trap -
        /// rather than because the game was closed. ApplicationLaunch turns it into a WPR Hub crash
        /// report, which is how a WP8 title that fails in a player's hands reaches the to-do list.
        /// </summary>
        public Wp8GuestFailure? Failure { get; private set; }

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

            _audio = IHostAudio.Create(_log);

            // A static event: removed again in Dispose, or the next title launched in this process
            // would still be feeding this one's (disposed) guest.
            TextInputEXT.TextInput += OnTextInput;

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
        private IHostAudio? _audio;
        private Wp8HostServices? _services;
        private volatile bool _hostActive = true;
        private int _lifecycleLogged;

        private void RunGuest()
        {
            string? fault;
            try
            {
                Log($"loading {_executable}");
                Log($"screen {Wp8Display.Describe()}");
                PeImage image = PeImage.Load(_executable);
                _emulator = new ArmEmulator(image, Path.GetDirectoryName(_executable)!, collectBlockStats: false);
                // The sampler's report is the probe's; WPR_WP8_CALLSITES=1 (diagnostic only) keeps it
                // and logs the hottest call sites when the guest stops - where a load or a loop is.
                _emulator.RecordCallSites = Environment.GetEnvironmentVariable("WPR_WP8_CALLSITES") == "1";
                _emulator.Direct3D.FrameBuilt += OnFrameBuilt;
                if (UseGpu)
                {
                    // The 3D path: the game's own shaders on the host GPU (Wp8GpuReplay).
                    _emulator.Direct3D.GpuCapture = true;
                    _emulator.Direct3D.GpuFrameBuilt += OnGpuFrameBuilt;
                    Log("rendering with the game's shaders on the GPU");
                }
                _emulator.WinRt.XboxHost = _xboxHost;
                _emulator.WinRt.Sensors = new Wp8NativeSensorHost();
                _services = Wp8HostServices.Attach(_emulator.WinRt, Log);
                if (!_hostActive)
                {
                    // Deactivated before the guest existed: it starts in the background.
                    _emulator.WinRt.InjectVisibility(false);
                }

                _emulator.XAudio2.Output = _audio;
                _emulator.Stubs.MediaDecoder = new Wp8NativeMediaDecoder(Log);
                _emulator.Stubs.VideoDecoder = new Wp8NativeVideoDecoder(Log);
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
                    // managed page that would have activated it (Src/Engine/WPR.Engine.Wp8Native/Runtime/XamlInterop.cs).
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

            // The guest ending on its own is a failure to report, unless the player closed the game
            // (OnExiting/Dispose set _closing first) or the image simply returned from its entry point.
            bool failed = !_closing && Outcome != "the image returned";
            if (failed)
            {
                _capture = new List<string>();
            }

            Log($"guest stopped after {_presented:N0} frame(s): {Outcome}");
            LogSummary();
            LogPostMortem();
            if (_emulator is { RecordCallSites: true } sampled)
            {
                try
                {
                    foreach (string line in sampled.Samples.Report(sampled.Unwinder, take: 20))
                    {
                        Log("   site: " + line);
                    }

                    foreach (LoadedModule module in sampled.Modules)
                    {
                        Log($"   site module: 0x{module.ImageBase:X8}-0x{module.ImageBase + module.MappedSize:X8} {module.GuestPath}");
                    }

                    foreach (string line in sampled.Direct3D.Log.TakeLast(25).Concat(sampled.Direct3D.Unimplemented).Concat(sampled.Direct3D.UnknownIids))
                    {
                        Log("   site d3d: " + line);
                    }

                    foreach (string line in sampled.DescribeThreads())
                    {
                        Log("   site thread: " + line);
                    }

                    foreach (string line in sampled.DeferredLog.TakeLast(30))
                    {
                        Log("   site deferred: " + line);
                    }
                }
                catch (Exception ex)
                {
                    Log($"   site report failed: {ex.Message}");
                }
            }

            if (failed)
            {
                Failure = new Wp8GuestFailure(Outcome, _presented, string.Join('\n', _capture!));
                _capture = null;
            }

            // The guest is gone; there is nothing left to show. Ending the Game takes the window
            // down the way any XNA title's exit does, and the launcher reports the outcome.
            if (!_closing)
            {
                Exit();
            }
        }

        /// <summary>
        /// One line per run, whatever ended it, for the library regression table
        /// (Plans/RUNTIME-CONVERGENCE.md, 1b): frames presented, the import that stopped the guest
        /// if one did, and every import still answered with 0. Logged even on a normal exit,
        /// which <see cref="LogPostMortem"/> is not.
        /// </summary>
        private void LogSummary()
        {
            HostStubs? stubs = _emulator?.Stubs;
            if (stubs is null)
            {
                return;
            }

            string trap = stubs.FirstUnimplementedImport is { } import
                ? $"{import} from 0x{stubs.FirstUnimplementedCaller:X8}"
                : "none";
            foreach (string drift in _emulator!.WinRt.SlotDrift)
            {
                Log("slot drift: " + drift);
            }

            if (stubs.Winsock.Log.Count > 0)
            {
                // Every host the game reached for: connect targets, UDP destinations, name lookups.
                Log("network: " + string.Join("; ", stubs.Winsock.Log.Take(40)));
            }

            Log($"summary: frames={_presented} trap={trap} " +
                $"defaulted=[{string.Join(", ", stubs.DefaultedCalls.Select(p => $"{p.Key} x{p.Value}"))}] " +
                $"allowed=[{string.Join(", ", stubs.AllowedDefaults.Select(p => $"{p.Key} x{p.Value}"))}] " +
                $"raised=[{string.Join("; ", stubs.RaiseLog)}] " +
                $"slottrap={_emulator!.WinRt.FirstUnanswerableSlot ?? "none"} " +
                $"defaultedslots=[{string.Join(", ", _emulator.WinRt.DefaultedSlots.Select(p => $"{p.Key} x{p.Value}"))}]");

            // Where every live guest thread was parked when the run ended: a title that stalls
            // without failing (waiting on a movie, a lock, a signal) leaves no post-mortem, and
            // this is the only record of what it was waiting for.
            try
            {
                foreach (string line in _emulator.DescribeThreads().Where(l => !l.Contains(": finished", StringComparison.Ordinal)))
                {
                    Log("threads: " + line);
                }
            }
            catch (Exception)
            {
                // Diagnostics only.
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
                // Every register that points into the heap: which block, who allocated it, what is in
                // it. A crash inside the game's own code (a divide by zero, an assert) says nothing
                // else about the data it choked on.
                if (emulator.UncontainedNullCall is null)
                {
                    int[] registers =
                    [
                        UnicornEngine.Const.Arm.UC_ARM_REG_R0, UnicornEngine.Const.Arm.UC_ARM_REG_R1, UnicornEngine.Const.Arm.UC_ARM_REG_R2,
                        UnicornEngine.Const.Arm.UC_ARM_REG_R3, UnicornEngine.Const.Arm.UC_ARM_REG_R4, UnicornEngine.Const.Arm.UC_ARM_REG_R5,
                        UnicornEngine.Const.Arm.UC_ARM_REG_R6, UnicornEngine.Const.Arm.UC_ARM_REG_R7, UnicornEngine.Const.Arm.UC_ARM_REG_R8,
                        UnicornEngine.Const.Arm.UC_ARM_REG_R9, UnicornEngine.Const.Arm.UC_ARM_REG_R10, UnicornEngine.Const.Arm.UC_ARM_REG_R11,
                        UnicornEngine.Const.Arm.UC_ARM_REG_R12,
                    ];
                    for (int i = 0; i < registers.Length; i++)
                    {
                        long value = emulator.ReadRegister(registers[i]);
                        string where = emulator.DescribeAllocation(value);
                        if (!where.StartsWith("not a block", StringComparison.Ordinal))
                        {
                            Log($"   r{i}=0x{value:X8} {where}");
                            Log("      " + string.Join(" ", Enumerable.Range(0, 16).Select(w => $"{emulator.ReadUInt32(value + (w * 4), 0):X8}")));

                            // One level down: heap pointers in those words, e.g. a vector's elements.
                            HashSet<long> followed = [];
                            for (int w = 0; w < 8; w++)
                            {
                                long inner = emulator.ReadUInt32(value + (w * 4), 0);
                                if (inner != value && followed.Add(inner) &&
                                    !emulator.DescribeAllocation(inner).StartsWith("not a block", StringComparison.Ordinal))
                                {
                                    Log($"      [+0x{w * 4:X}] -> 0x{inner:X8}{TypeOf(emulator, inner)}: " +
                                        string.Join(" ", Enumerable.Range(0, 32).Select(x => $"{emulator.ReadUInt32(inner + (x * 4), 0):X8}")));

                                    // And the objects that block points at (a vector of pointers, say).
                                    int shown = 0;
                                    for (int x = 0; x < 32 && shown < 4; x++)
                                    {
                                        long deeper = emulator.ReadUInt32(inner + (x * 4), 0);
                                        if (deeper != inner && followed.Add(deeper) &&
                                            !emulator.DescribeAllocation(deeper).StartsWith("not a block", StringComparison.Ordinal))
                                        {
                                            shown++;
                                            Log($"         [+0x{x * 4:X}] -> 0x{deeper:X8}{TypeOf(emulator, deeper)}: " +
                                                string.Join(" ", Enumerable.Range(0, 24).Select(y => $"{emulator.ReadUInt32(deeper + (y * 4), 0):X8}")));
                                        }
                                    }
                                }
                            }
                        }
                    }

                    foreach (string line in emulator.ScanStack(64).Split('\n').Take(16))
                    {
                        Log("   stack: " + line.Trim());
                    }
                }

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

                // The last file operations: a failure is often a file that did not open, and the
                // game's own message rarely names the path in a form that survives.
                foreach (string line in emulator.Stubs.Files.Log.TakeLast(12))
                {
                    Log("   file: " + line);
                }

                foreach (string line in emulator.Stubs.ThrowHistory.TakeLast(5))
                {
                    Log("   throw: " + line);
                }

                foreach (var (name, calls) in emulator.Stubs.DefaultedCalls.OrderByDescending(p => p.Value).Take(80))
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

        /// <summary>
        /// Every title replays its frames on the GPU with its own shaders. One path for 2D and 3D
        /// alike: a 2D game is just draws with a flat projection, so nothing per title decides it.
        /// WPR_WP8_GPU=0 falls back to the older 2D draw list, as a diagnostic only.
        /// </summary>
        /// <remarks>
        /// Measured 2026-10-12 over the ten installed titles, 30 s each from clean saves: the
        /// seven Angry Birds titles and Amazing Alex draw the same frames on both paths, and
        /// Modern Combat 4 and MONOPOLY Millionaire draw correctly only on this one (the draw
        /// list showed MC4 as flat silhouettes and Monopoly as black). Zero skipped draws on any
        /// title. It replaced a per-title allow-list.
        /// </remarks>
        private readonly bool UseGpu = Environment.GetEnvironmentVariable("WPR_WP8_GPU") != "0";

        /// <summary>
        /// Frames the guest has presented and the host has not replayed yet, oldest first.
        /// </summary>
        /// <remarks>
        /// Every frame is replayed exactly once, in order. A Direct3D render target keeps its
        /// contents from one frame to the next, and games rely on it: Assassin's Creed Pirates'
        /// loading screen draws its full-screen background once and then only what changes, so
        /// when the host kept just the latest frame (the guest may present two per host frame)
        /// the frames it dropped were the ones that painted the edges, and the menu showed
        /// through there. Replaying the latest frame again on a host frame with nothing new
        /// was wrong the other way: every blended draw landed twice on targets that persist.
        /// </remarks>
        private readonly List<GpuFrame> _pendingGpu = new();
        private bool _anyGpuFrame;
        private Wp8GpuReplay? _replay;

        /// <summary>Unreplayed frames kept at most; past this the oldest are dropped (and counted).</summary>
        private const int MaxPendingGpuFrames = 16;

        private long _gpuFramesDropped;

        private void OnGpuFrameBuilt(GpuFrame frame)
        {
            _presented++;
            LogScreenQueries();
            lock (_gate)
            {
                _pendingGpu.Add(frame);
                if (_pendingGpu.Count > MaxPendingGpuFrames)
                {
                    _gpuFramesDropped += _pendingGpu.Count - MaxPendingGpuFrames;
                    _pendingGpu.RemoveRange(0, _pendingGpu.Count - MaxPendingGpuFrames);
                }
            }

            if (!_closing)
            {
                _tick.Wait(250);
            }
        }

        /// <summary>
        /// Once, a few seconds in: what the title asked about the screen and what swap chain it made,
        /// on the guest thread (both logs are the emulator's).
        /// </summary>
        private void LogScreenQueries()
        {
            if (_presented != 240 || _emulator is not { } emulator)
            {
                return;
            }

            var (bounds, dpi, scale) = emulator.WinRt.ScreenQueries;
            string swapChain = emulator.Direct3D.Log.FirstOrDefault(l => l.StartsWith("CreateSwapChain", StringComparison.Ordinal)) ?? "no swap chain";
            Log($"screen queries: bounds={bounds} logicalDpi={dpi} resolutionScale={scale}; {swapChain}");
        }

        /// <summary>" (ClassName)" from an object's MSVC RTTI - vtable[-1] is the complete object locator - or "".</summary>
        private static string TypeOf(ArmEmulator emulator, long objectAddress)
        {
            try
            {
                long vtable = emulator.ReadUInt32(objectAddress, 0);
                long locator = vtable == 0 ? 0 : emulator.ReadUInt32(vtable - 4, 0);
                long descriptor = locator == 0 ? 0 : emulator.ReadUInt32(locator + 12, 0);
                if (descriptor == 0 || !emulator.IsExecutableCode(emulator.ReadUInt32(vtable, 0)))
                {
                    return "";
                }

                byte[] name = emulator.ReadMemory(descriptor + 8, 64);
                int end = Array.IndexOf(name, (byte)0);
                string text = System.Text.Encoding.ASCII.GetString(name, 0, end < 0 ? name.Length : end);
                return text.StartsWith(".?A", StringComparison.Ordinal) ? $" ({text})" : "";
            }
            catch (Exception)
            {
                return "";
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
            PumpKeyboard();
            _audio?.Pump();

            // One guest frame per tick, never banked beyond two.
            WakeGuest();

            ReportStatistics();
            base.Update(gameTime);
        }

        /// <summary>Fingers currently down, by TouchPanel id, with where they were last reported.</summary>
        private readonly Dictionary<int, (float X, float Y)> _fingers = new();

        private void PumpTouch()
        {
            ArmEmulator? emulator = _emulator;
            if (emulator is null)
            {
                return;
            }

            // TouchPanel positions are in display space, which is the backbuffer - the landscape
            // raster the guest composes in (1280x768 on WXGA) - so they go across unscaled, and
            // WinRtRuntime turns them into the portrait DIPs a PointerPoint carries. Every finger is its own pointer,
            // told apart by PointerId: Modern Combat 4 moves with one thumb and aims with the other.
            TouchCollection touches = TouchPanel.GetState();
            HashSet<int>? seen = null;
            foreach (TouchLocation touch in touches)
            {
                if (touch.State == TouchLocationState.Invalid)
                {
                    continue;
                }

                (seen ??= []).Add(touch.Id);
                float x = Math.Clamp(touch.Position.X, 0, FrameCapture.Width - 1);
                float y = Math.Clamp(touch.Position.Y, 0, FrameCapture.Height - 1);
                uint id = PointerIdFor(touch.Id);
                bool down = _fingers.TryGetValue(touch.Id, out var last);

                switch (touch.State)
                {
                    // A finger first seen already Moved still starts a touch: a short tap can go
                    // down and move inside one frame, and its Pressed sample is then never seen.
                    case TouchLocationState.Pressed:
                    case TouchLocationState.Moved when !down:
                        _fingers[touch.Id] = (x, y);
                        emulator.WinRt.InjectPointer(WinRtRuntime.PointerKind.Pressed, x, y, id);
                        break;
                    case TouchLocationState.Moved when x != last.X || y != last.Y:
                        _fingers[touch.Id] = (x, y);
                        emulator.WinRt.InjectPointer(WinRtRuntime.PointerKind.Moved, x, y, id);
                        break;
                    case TouchLocationState.Released when down:
                        _fingers.Remove(touch.Id);
                        emulator.WinRt.InjectPointer(WinRtRuntime.PointerKind.Released, x, y, id);
                        break;
                }
            }

            // A finger that vanished without a Released sample (a cancelled gesture) is lifted.
            foreach (int gone in _fingers.Keys.Where(k => seen is null || !seen.Contains(k)).ToArray())
            {
                (float x, float y) = _fingers[gone];
                _fingers.Remove(gone);
                emulator.WinRt.InjectPointer(WinRtRuntime.PointerKind.Released, x, y, PointerIdFor(gone));
            }
        }

        /// <summary>
        /// A WinRT pointer id for a TouchPanel finger. SDL numbers Android's fingers from 0, and a
        /// game may well take pointer 0 for "none", so every id moves up one.
        /// </summary>
        private static uint PointerIdFor(int touchId) => unchecked((uint)touchId + 1);

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

        /// <summary>Virtual keys down at the last <see cref="PumpKeyboard"/>, and whether each was an extended (right-hand) key.</summary>
        private Dictionary<uint, bool> _keysDown = new();

        /// <summary>
        /// The keyboard, as <c>CoreWindow.KeyDown</c>/<c>KeyUp</c>: the edges of
        /// <see cref="Keyboard.GetState()"/> between frames, mapped to <c>Windows.System.VirtualKey</c>
        /// (Src/Engine/WPR.Engine.Wp8Native/Runtime/Keyboard.cs raises them). On the desktop that is the PC keyboard; on
        /// Android only a physical one, since no keyboard-emulation host is registered there.
        /// </summary>
        /// <remarks>
        /// The key bound as the Back button is left out: it already reaches the game as
        /// <c>HardwareButtons.BackPressed</c> through <see cref="PumpBack"/>, and a phone never had a
        /// key that was both. Polled once a frame, so a tap shorter than a frame is lost - the same
        /// limit every XNA title has with <c>Keyboard.GetState</c>.
        /// </remarks>
        private void PumpKeyboard()
        {
            if (_emulator is not { } emulator)
            {
                return;
            }

            var now = new Dictionary<uint, bool>();
            foreach (Keys key in Keyboard.GetState().GetPressedKeys())
            {
                if (WPR.Xna.Rhi.XnaBackend.KeyboardEmulation?.IsBackKey(key) == true)
                {
                    continue;
                }

                uint virtualKey = ToVirtualKey(key, out bool extended);
                if (virtualKey != 0)
                {
                    now[virtualKey] = now.GetValueOrDefault(virtualKey) || extended;
                }
            }

            foreach ((uint virtualKey, bool extended) in _keysDown)
            {
                if (!now.ContainsKey(virtualKey))
                {
                    emulator.WinRt.InjectKey(virtualKey, down: false, extended);
                }
            }

            foreach ((uint virtualKey, bool extended) in now)
            {
                if (!_keysDown.ContainsKey(virtualKey))
                {
                    emulator.WinRt.InjectKey(virtualKey, down: true, extended);
                }
            }

            _keysDown = now;
        }

        /// <summary>
        /// The <c>Windows.System.VirtualKey</c> for an XNA key, or 0 for none. Both are the Win32 VK
        /// codes, so most keys map to themselves; the exceptions are the modifiers, which CoreWindow
        /// reports as the generic Shift/Control/Menu with the right-hand ones marked extended, and
        /// the Xbox chat-pad keys, which no WP8 device had.
        /// </summary>
        internal static uint ToVirtualKey(Keys key, out bool extended)
        {
            extended = key is Keys.RightControl or Keys.RightAlt;
            return key switch
            {
                Keys.None or Keys.ChatPadGreen or Keys.ChatPadOrange => 0,
                Keys.LeftShift or Keys.RightShift => 0x10,
                Keys.LeftControl or Keys.RightControl => 0x11,
                Keys.LeftAlt or Keys.RightAlt => 0x12,
                _ => (uint)key,
            };
        }

        /// <summary>
        /// Typed text, as <c>CoreWindow.CharacterReceived</c>. Taken from <see cref="TextInputEXT"/>
        /// rather than worked out from key codes, so it follows the keyboard layout and repeats while
        /// a key is held, as Windows' own character messages do.
        /// </summary>
        private void OnTextInput(char character)
        {
            // FNA reports Home, End and Delete as control characters 2, 3 and 127 for its own text
            // boxes; Windows sends no character for those keys.
            if (character is (char)2 or (char)3 or (char)127)
            {
                return;
            }

            _emulator?.WinRt.InjectCharacter(character);
        }

        protected override void Draw(GameTime gameTime)
        {
            if (UseGpu)
            {
                GpuFrame[] gpuFrames;
                lock (_gate)
                {
                    gpuFrames = [.. _pendingGpu];
                    _pendingGpu.Clear();
                }

                _anyGpuFrame |= gpuFrames.Length > 0;
                if (!_anyGpuFrame)
                {
                    GraphicsDevice.Clear(Color.Black);
                }
                else
                {
                    _replay ??= new Wp8GpuReplay(GraphicsDevice, _log);
                    foreach (GpuFrame gpuFrame in gpuFrames)
                    {
                        _replay.Replay(gpuFrame);
                        _drawn++;
                    }

                    _replay.Present(GraphicsDevice.PresentationParameters.BackBufferWidth, GraphicsDevice.PresentationParameters.BackBufferHeight);
                }

                base.Draw(gameTime);
                return;
            }

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
                $"gpu: replayed={_replay?.DrawsReplayed} skipped={_replay?.DrawsSkipped} frames-dropped={_gpuFramesDropped} {_replay?.Statistics()} unrecorded={string.Join(",", _emulator?.Direct3D.GpuSkipped.Select(p => $"{p.Key}:{p.Value}") ?? [])} " +
                $"audio: {_emulator?.XAudio2.Summary()} voices={_audio?.VoicesCreated} underruns={_audio?.Underruns} callbacks={_emulator?.XAudio2.CallbacksMade}");
            // What the game did with its sensors (a handful of lines per run; same racy read as below).
            if (_emulator?.WinRt.SensorLog is { } sensorLog)
            {
                try
                {
                    for (; _sensorLogged < sensorLog.Count; _sensorLogged++)
                    {
                        Log("sensors: " + sensorLog[_sensorLogged]);
                    }
                }
                catch (Exception)
                {
                }
            }

            LogLifecycle();

            if (HostStubs.CountCalls)
            {
                KeyValuePair<string, long>[] counts;
                lock (HostStubs.CallCounts)
                {
                    counts = HostStubs.CallCounts.ToArray();
                    HostStubs.CallCounts.Clear();
                }

                Log("threads: " + _emulator?.TakeThreadUsage());
                Log("calls/frame: " + string.Join(" ", counts.OrderByDescending(kv => kv.Value).Take(25).Select(kv => $"{kv.Key[(kv.Key.IndexOf('!') + 1)..]}={kv.Value / (double)Math.Max(1, _presented - _statsPresented):0.#}")));
            }

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

        private void Log(string message)
        {
            _capture?.Add(message);
            _log?.Invoke("[wpr-wp8] " + message);
        }

        /// <summary>While set, every logged line is also kept, to become <see cref="Failure"/>.</summary>
        private List<string>? _capture;

        // The sound itself is paused by the audio backend (Android's AudioTrack voices through
        // AudioBackendRegistry.SuspendForBackground from GameActivity.OnPause; FAudio by SDL), so
        // _audio.Suspend is a no-op kept for the IHostAudio contract. The guest is told, as WP8 told it:
        // CoreWindow.Activated/VisibilityChanged and CoreApplication.Suspending/Resuming, raised on
        // its own thread at its next turn round the main loop (Src/Engine/WPR.Engine.Wp8Native/Runtime/AppLifecycle.cs).
        // The motor is ours alone, so a buzz in flight is stopped here, as GameActivity.OnPause does.
        protected override void OnDeactivated(object sender, EventArgs args)
        {
            _hostActive = false;
            _audio?.Suspend(true);
            Wp8HostServices.StopVibration();
            _emulator?.WinRt.InjectVisibility(false);
            base.OnDeactivated(sender, args);
        }

        protected override void OnActivated(object sender, EventArgs args)
        {
            _hostActive = true;
            _audio?.Suspend(false);
            _emulator?.WinRt.InjectVisibility(true);
            base.OnActivated(sender, args);
        }

        /// <summary>The lifecycle events raised to the guest since the last call (racy read of a guest-side list; a diagnostic).</summary>
        private void LogLifecycle()
        {
            if (_emulator?.WinRt.AppLifecycleLog is not { } lifecycle)
            {
                return;
            }

            try
            {
                for (; _lifecycleLogged < lifecycle.Count; _lifecycleLogged++)
                {
                    Log("lifecycle: " + lifecycle[_lifecycleLogged]);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Releases one guest tick unless two are already banked. Shutdown reaches this from both
        /// <see cref="OnExiting"/> and <see cref="Dispose(bool)"/>, and an unguarded second release
        /// threw <see cref="SemaphoreFullException"/>: caught, but it put a first-chance stack trace
        /// in every crash report. The catch stays because the check and the release are not atomic.
        /// </summary>
        private void WakeGuest()
        {
            if (_tick.CurrentCount >= 2)
                return;
            try { _tick.Release(); } catch (SemaphoreFullException) { }
        }

        protected override void OnExiting(object sender, EventArgs args)
        {
            _closing = true;
            try { _emulator?.Stop("host exiting"); } catch { }
            WakeGuest();
            base.OnExiting(sender, args);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _closing = true;
                TextInputEXT.TextInput -= OnTextInput;
                try { _emulator?.Stop("host disposed"); } catch { }
                WakeGuest();

                // A title that exits mid-buzz never cancels it; the motor is ours to stop.
                Wp8HostServices.StopVibration();

                // The guest thread is inside the JIT; give it a moment to notice the stop before
                // its CPU and memory are torn down underneath it.
                // Only then is the CPU released: on the desktop several titles run in one
                // process, and each guest holds a gigabyte reservation plus the JIT's code cache.
                if (_guest is null || _guest.Join(TimeSpan.FromSeconds(2)))
                {
                    try
                    {
                        if (_emulator is { } stopping)
                        {
                            stopping.WinRt.StopSensors();
                            LogLifecycle();
                            foreach (string line in stopping.WinRt.SensorLog.Skip(_sensorLogged))
                            {
                                Log("sensors: " + line);
                            }

                            // Whether the title subscribed to the keyboard at all, and what reached it.
                            foreach (string line in stopping.WinRt.KeyboardLog)
                            {
                                Log("keyboard: " + line);
                            }

                            if (stopping.WinRt.KeyEventsDelivered > 0)
                            {
                                Log($"keyboard: {stopping.WinRt.KeyEventsDelivered} key event(s) delivered");
                            }
                        }
                    }
                    catch
                    {
                    }

                    _emulator?.Dispose();
                }

                _audio?.Dispose();
                _replay?.Dispose();
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

    /// <summary>A WP8 guest that stopped on its own: why, how far it got, and the post-mortem (summary first).</summary>
    public sealed record Wp8GuestFailure(string Reason, long FramesPresented, string Details);
}
