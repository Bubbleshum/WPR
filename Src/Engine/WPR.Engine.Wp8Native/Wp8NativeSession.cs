using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;

namespace WPR.Wp8Native
{
    /// <summary>What a <see cref="Wp8NativeSession"/> needs from whatever hosts it.</summary>
    public interface IWp8SessionHost
    {
        /// <summary>The device the frames are drawn with; read on the host's thread only.</summary>
        GraphicsDevice GraphicsDevice { get; }

        /// <summary>The guest is gone (or the game let Back through): end the game.</summary>
        void Exit();
    }

    /// <summary>
    /// A WP8 "Modern Native" title (an ARMv7 Thumb-2 PE against WinRT/D3D11/XAudio2) run by the
    /// host: as its own XNA <see cref="Game"/> (<see cref="Wp8NativeGame"/>, an exe or a
    /// Direct3D/XAML title whose page the host plays), or under the title's own managed page
    /// (<see cref="Wp8NativeEmbeddedLoop"/>, Docs/Wp8Native/ManagedPageBridge.md).
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
    public sealed class Wp8NativeSession : IDisposable
    {
        /// <summary>The guest's stack. Host stubs and the JIT's own frames share it.</summary>
        private const int GuestStackBytes = 64 * 1024 * 1024;

        private readonly string _executable;
        private readonly Action<string>? _log;
        private readonly IWp8SessionHost _host;
        private readonly bool _bridged;

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
        private int _silentReports;

        /// <summary>
        /// The component log for the run's end: its first 40 lines (activation and the page's
        /// set-up) and its last 60 (where it got to), with a count of what was left out between.
        /// </summary>
        private static IEnumerable<string> ComponentLogExcerpt(List<string> log)
        {
            string[] lines = log.ToArray();
            if (lines.Length <= 100)
            {
                return lines;
            }

            return [.. lines.Take(40), $"... {lines.Length - 100:N0} line(s) left out ...", .. lines.TakeLast(60)];
        }

        /// <summary>Stopwatch ticks the guest spent waiting for the host's tick after a frame, since the last stats line.</summary>
        private long _guestWaitTicks;

        /// <param name="executable">The title's executable (or Direct3D/XAML component) inside its install folder.</param>
        /// <param name="sandboxRoot">Where the title's local folder lives on the host.</param>
        /// <param name="log">Per-game trace sink; null to stay quiet.</param>
        /// <param name="bridged">The title's own managed page drives the component (and owns Back).</param>
        public Wp8NativeSession(IWp8SessionHost host, string executable, string sandboxRoot, Action<string>? log, IXboxLiveHost? xboxHost, bool bridged)
        {
            _host = host;
            _bridged = bridged;
            _executable = executable;
            _log = log;
            _xboxHost = xboxHost;
            HostStubs.SandboxRoot = sandboxRoot;

            // A live host has a real touch screen: the probe's scripted taps must be off. The
            // runtime reads this at type initialisation, so it has to be set before it is touched.
            Environment.SetEnvironmentVariable("WPR_TAP", "0");
        }

        /// <summary>
        /// Set when the guest stopped on its own - a fault, an uncaught throw, a diagnostic trap -
        /// rather than because the game was closed. ApplicationLaunch turns it into a WPR Hub crash
        /// report, which is how a WP8 title that fails in a player's hands reaches the to-do list.
        /// </summary>
        public Wp8GuestFailure? Failure { get; private set; }

        /// <summary>Why the guest stopped, once it has.</summary>
        public string? Outcome { get; private set; }

        /// <summary>Starts the guest. Graphics resources are made on the first <see cref="Draw"/>.</summary>
        public void Start()
        {
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
            _watchdog = new System.Threading.Timer(Watchdog, null, TimeSpan.FromSeconds(WatchdogSeconds), TimeSpan.FromSeconds(WatchdogSeconds));
        }

        /// <summary>The 2D draw list's effect and blank texture, made on the first frame (the device may not exist at <see cref="Start"/>).</summary>
        private void EnsureGraphics()
        {
            if (_effect is not null)
            {
                return;
            }

            _effect = new BasicEffect(_host.GraphicsDevice)
            {
                World = Matrix.Identity,
                View = Matrix.Identity,
                Projection = Matrix.Identity,
                TextureEnabled = true,
                VertexColorEnabled = true,
                LightingEnabled = false,
            };

            _white = new Texture2D(_host.GraphicsDevice, 1, 1, false, SurfaceFormat.Color);
            _white.SetData(new[] { Color.White });
        }

        /// <summary>The guest's WinRT runtime, once the guest thread has made it (see <see cref="RuntimeReady"/>).</summary>
        public WinRtRuntime? Runtime => _emulator?.WinRt;

        /// <summary>Bridged: the page's UI thread, whose calls run on the guest's main thread (others get lanes).</summary>
        public Thread? BridgeUiThread { get; set; }

        /// <summary>Set once <see cref="Runtime"/> exists and, bridged, the component is being served.</summary>
        public ManualResetEventSlim RuntimeReady { get; } = new(false);

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
                // A XAML title in the background draws nothing (its provider is disconnected), so
                // its loop waits for the tick here instead of after a frame.
                if (!HostBlockingFrameWait)
                {
                    _emulator.Direct3D.HostFrame = (() => _closing || _tick.CurrentCount > 0 || _emulator.WinRt.HasPendingBridgeCall, HostFrameTaken);
                }

                _emulator.WinRt.IdleTurn += () =>
                {
                    if (!_closing)
                    {
                        _tick.Wait(250);
                    }
                };
                (_emulator.WinRt.KeyRepeatDelay, _emulator.WinRt.KeyRepeatInterval) = PlatformKeyRepeat();
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
                if (image.IsDll && _bridged)
                {
                    // The title's own managed page drives the component through the bridge
                    // (Runtime/ComponentBridge.cs); each call it queues wakes the guest's frame wait.
                    string directory = Path.GetDirectoryName(_executable)!;
                    WinmdReader metadata = WinmdReader.Load(Directory.GetFiles(directory, "*.winmd"));
                    _emulator.WinRt.BridgeCallQueued += WakeForBridgeCall;
                    _emulator.WinRt.BridgeUiThread = BridgeUiThread;
                    _emulator.WinRt.TerminationRequested = () => WPR.Xna.Rhi.GameUnhandledException.TerminationRequested;
                    long start = _emulator.WinRt.StartBridgedComponent(image, metadata, Path.GetFileName(_executable));
                    Log($"managed page drives {Path.GetFileName(_executable)}");
                    RuntimeReady.Set();
                    fault = _emulator.Run(start, long.MaxValue / 4);
                    foreach (string line in ComponentLogExcerpt(_emulator.WinRt.ComponentLog))
                    {
                        Log($"[xaml] {line}");
                    }
                }
                else if (image.IsDll)
                {
                    // A Direct3D/XAML title: the game is a WinRT component, and the host plays the
                    // managed page that would have activated it (Src/Engine/WPR.Engine.Wp8Native/Runtime/XamlInterop.cs).
                    string directory = Path.GetDirectoryName(_executable)!;
                    WinmdReader metadata = WinmdReader.Load(Directory.GetFiles(directory, "*.winmd"));
                    // The page description is read from the title's managed shell
                    // (XamlShellInference), for every title: WPR has no per-title descriptions.
                    XamlShell? shell = XamlShellInference.Infer(_executable, metadata, line => Log(line));
                    if (shell is { TouchFrame: null })
                    {
                        // A page that hands touch to the component from Touch.FrameReported (Asphalt 7)
                        // rather than a manipulation handler: used only if the class answers none.
                        shell = shell with { TouchFrame = XamlTouchFrames.Infer(directory, shell.ClassId, line => Log(line)) };
                    }
                    if (shell is null)
                    {
                        fault = $"{Path.GetFileName(_executable)} is a Direct3D/XAML component, but no class in its metadata declares CreateContentProvider";
                    }
                    else
                    {
                        Log(shell.Inferred
                            ? $"page description inferred from the managed shell: {XamlShellInference.Describe(shell)}"
                            : $"page description: {shell.ComponentDll} (hand-written)");
                        _presentUnturned = shell.PresentsUnturned;
                        long start = _emulator.WinRt.StartComponent(image, metadata, shell);
                        fault = _emulator.Run(start, long.MaxValue / 4);
                        foreach (string line in ComponentLogExcerpt(_emulator.WinRt.ComponentLog))
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
            _emulator?.WinRt.MarkGuestStopped();
            RuntimeReady.Set();

            // The guest ending on its own is a failure to report, unless the player closed the game
            // (OnExiting/Dispose set _closing first) or the game quit: returned from its entry point,
            // exit(0)/_exit(0), or CoreApplication.Exit(). Rovio titles quit with exit(0) when Back
            // is pressed on their main menu; that was reported as a crash and, once the error screen
            // existed, shown as one (Angry Birds Stella on the S24, 2026-10-16).
            // A stop after the page asked for Application.Terminate() is the app ending, too: on WP8
            // the process was gone by then, so whatever the guest met afterwards never happened.
            // The bridge normally stops the guest at that point; this covers a Terminate() made
            // from the page's own thread, which the guest can run past until the host's next tick.
            bool failed = !_closing && !IsCleanQuit(Outcome) && !WPR.Xna.Rhi.GameUnhandledException.TerminationRequested;
            if (failed && StoppedWhileShuttingDown(out string shutdown))
            {
                // Kept in the log (and the post-mortem below), but not a failure: no error screen
                // and no WPR Hub crash report for a quit the player asked for.
                Log($"guest stopped while shutting down ({shutdown}); treating it as the app closing, as a phone did");
                failed = false;
            }

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

                // Reported now, not when the game ends: the player may kill the app from the
                // error screen rather than press Back.
                bool reported = false;
                try
                {
                    GuestFailed?.Invoke(Failure);
                    reported = FailureReported;
                }
                catch (Exception ex)
                {
                    Log($"GuestFailed handler threw: {ex.Message}");
                }

                // Put the stop on the screen instead of closing the window on a black frame.
                if (!_closing && WPR.Xna.Rhi.ErrorScreen.Request(DescribeFailure(Failure, reported)))
                {
                    Log("stop shown on the error screen; Back leaves");
                    return;
                }
            }

            // The guest is gone; there is nothing left to show. Ending the Game takes the window
            // down the way any XNA title's exit does, and the launcher reports the outcome.
            if (!_closing)
            {
                _host.Exit();
            }
        }

        /// <summary>
        /// Raised on the guest thread when it stops on its own, with <see cref="Failure"/>.
        /// ApplicationLaunch queues the WPR Hub report from it and sets <see cref="FailureReported"/>.
        /// </summary>
        public event Action<Wp8GuestFailure>? GuestFailed;

        /// <summary>
        /// The ways a WP8 game ends itself on purpose. A non-zero exit code or std::terminate is
        /// still a failure.
        /// </summary>
        internal static bool IsCleanQuit(string outcome) => outcome is
            "the image returned" or
            "the image called exit(0)" or
            "the image called _exit(0)" or
            "the image called CoreApplication.Exit()" or
            WinRtRuntime.AppTerminatedOutcome;

        /// <summary>Set by the <see cref="GuestFailed"/> handler once the failure is queued for WPR Hub.</summary>
        public bool FailureReported { get; set; }

        private static WPR.Xna.Rhi.GameErrorReport DescribeFailure(Wp8GuestFailure failure, bool reported)
        {
            // The post-mortem, summary first, minus the line that repeats the headline.
            var lines = new List<string>();
            foreach (string raw in failure.Details.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("guest stopped after", StringComparison.Ordinal)) continue;
                lines.Add(line.Length > 200 ? line.Substring(0, 200) + "…" : line);
                if (lines.Count == 12) break;
            }

            return new WPR.Xna.Rhi.GameErrorReport
            {
                Headline = failure.Reason,
                Where = $"WP8 native code, after {failure.FramesPresented:N0} frame(s)",
                Detail = string.Join('\n', lines),
                Stopped = true,
                Reported = reported,
            };
        }

        /// <summary>
        /// One line per run, whatever ended it, for the library regression table
        /// (the runtime convergence plan, 1b): frames presented, the import that stopped the guest
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

            if (_emulator.Direct3D.UnknownIids.Count > 0)
            {
                // A refused QueryInterface whose result the title never checked is a null call
                // later, possibly thousands of frames on (Halo's ID3DUserDefinedAnnotation).
                Log("d3d refused: " + string.Join("; ", _emulator.Direct3D.UnknownIids.Take(20)));
            }

            List<string> xbox = _emulator.WinRt.XboxCalls;
            if (xbox.Count > 0)
            {
                // Every Xbox Live call the title made (sign-in excluded): which of its achievement
                // and leaderboard calls reached the host, and with which ids.
                Log($"xbox: {xbox.Count} call(s): " + string.Join("; ", xbox.Distinct().Take(60)));
            }

            Log($"summary: frames={_presented} trap={trap} " +
                $"defaulted=[{string.Join(", ", stubs.DefaultedCalls.Select(p => $"{p.Key} x{p.Value}"))}] " +
                $"allowed=[{string.Join(", ", stubs.AllowedDefaults.Select(p => $"{p.Key} x{p.Value}"))}] " +
                $"raised=[{string.Join("; ", stubs.RaiseLog)}] " +
                $"slottrap={_emulator!.WinRt.FirstUnanswerableSlot ?? "none"} " +
                $"defaultedslots=[{string.Join(", ", _emulator.WinRt.DefaultedSlots.Select(p => $"{p.Key} x{p.Value}"))}] " +
                $"guessedslots=[{string.Join(", ", _emulator.WinRt.GuessedSlots.Select(p => $"{p.Key} x{p.Value}"))}]");

            // WPR_WP8_FILE_LOG=1: every file operation of the run, not only on a failure (a save
            // that does not survive a relaunch fails nothing).
            if (Environment.GetEnvironmentVariable("WPR_WP8_FILE_LOG") == "1")
            {
                foreach (string line in _emulator.Stubs.Files.Log)
                {
                    Log("file: " + line);
                }
            }

            // Every SignalNotifier attach and terminate (FMOD's threads on WP8), in order: a
            // handler fired after its owner let go of it shows up as an attach with no terminate.
            // FMOD makes a new notifier for every turn of its threads, so only the first few.
            List<string> notifierLog = _emulator.WinRt.SignalNotifierLog;
            foreach (string line in notifierLog.Take(6))
            {
                Log("signal notifier: " + line);
            }

            if (notifierLog.Count > 6)
            {
                Log($"signal notifier: ... {notifierLog.Count - 6} more line(s) (first {notifierLog.Count} kept)");
            }

            // Where each module was mapped, so a post-mortem's addresses can be put back into the
            // right DLL (a relocated module's code is nowhere near its file's ImageBase).
            foreach (string line in _emulator.ModuleLog.Where(l => l.StartsWith("mapped ", StringComparison.Ordinal)).Take(40))
            {
                Log("modules: " + line);
            }

            // Where every live guest thread was parked when the run ended: a title that stalls
            // without failing (waiting on a movie, a lock, a signal) leaves no post-mortem, and
            // this is the only record of what it was waiting for.
            try
            {
                foreach (string line in _emulator.DescribeThreads().Where(l => !l.Contains(": finished", StringComparison.Ordinal)))
                {
                    Log("threads: " + line);
                }

                // A thread switched out between two instructions because it entered no trap for a
                // whole slice (GuestThreads.PreemptAfterSlice): a title that polls memory to wait.
                Log($"threads: {_emulator.Preemptions} preemption(s), {_emulator.ThreadSwitches} switch(es), {_emulator.PreemptsDeferredInStub} slice end(s) inside the resume stub");
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
                foreach (string line in emulator.Stubs.Files.Log.TakeLast(Environment.GetEnvironmentVariable("WPR_WP8_FILE_LOG") == "1" ? int.MaxValue : 12))
                {
                    Log("   file: " + line);
                }

                // The last pointer events and which handlers took them: "it ignores my taps".
                foreach (string line in emulator.InputDelivered.TakeLast(8))
                {
                    Log("   input: " + line);
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

        /// <summary>Test hook: <c>WPR_TEST_WP8_STOP_AFTER_FRAMES=N</c> stops the guest at frame N, as a
        /// fault would, to see the error screen on demand. Off unless set.</summary>
        private static readonly long StopAfterFrames =
            long.TryParse(Environment.GetEnvironmentVariable("WPR_TEST_WP8_STOP_AFTER_FRAMES"), out long n) ? n : 0;

        private void TestStopHook()
        {
            if (StopAfterFrames > 0 && _presented == StopAfterFrames)
            {
                _emulator?.Stop($"test hook WPR_TEST_WP8_STOP_AFTER_FRAMES={StopAfterFrames}");
            }
        }

        /// <summary>
        /// The longest gap between two guest frames, and the gaps over 50 ms, since the last
        /// statistics line: a hitch every few seconds averages away in a 5 s fps figure.
        /// </summary>
        private long _lastFrameTicks, _longestGapTicks;
        private int _hitches;
        private TimeSpan _statsGcPause;
        /// <summary>Host Updates and Draws since start: fewer Draws than Updates is the fixed timestep catching up.</summary>
        private long _hostDraws, _hostUpdates;
        private long _statsIoTicks, _statsIoCalls;

        private void NoteFrameGap()
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            long gap = _lastFrameTicks == 0 ? 0 : now - _lastFrameTicks;
            _lastFrameTicks = now;
            _longestGapTicks = Math.Max(_longestGapTicks, gap);
            if (gap > System.Diagnostics.Stopwatch.Frequency / 20)
            {
                _hitches++;
                LogHitch(gap);
            }

            NoteFrameStart();
        }

        // What a long frame was spent on, so a stutter can be put down to the guest's own work, the
        // host it waited for, file I/O or a collection. The first 30 of a run, then every 50th.
        private long _frameWaitTicks, _frameCalls, _frameIoTicks, _frameIoCalls;
        private int _frameGen0, _frameGen1, _frameGen2, _hitchesLogged;

        private void NoteFrameStart()
        {
            _frameCalls = _emulator?.CallOrderTotal ?? 0;
            _frameIoTicks = _emulator?.Stubs.Files.IoTicks ?? 0;
            _frameIoCalls = _emulator?.Stubs.Files.IoCalls ?? 0;
            _frameGen0 = GC.CollectionCount(0);
            _frameGen1 = GC.CollectionCount(1);
            _frameGen2 = GC.CollectionCount(2);
        }

        private void LogHitch(long gap)
        {
            int n = ++_hitchesLogged;
            if (n > 30 && n % 50 != 0 || _emulator is not { } emulator)
            {
                return;
            }

            double ms(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Log($"hitch #{n} at frame {_presented}: {ms(gap):0} ms (guest {ms(gap - _frameWaitTicks):0}, waiting for the host {ms(_frameWaitTicks):0}), " +
                $"{emulator.CallOrderTotal - _frameCalls:N0} host call(s), file io {ms(emulator.Stubs.Files.IoTicks - _frameIoTicks):0} ms/{emulator.Stubs.Files.IoCalls - _frameIoCalls}, " +
                $"gc {GC.CollectionCount(0) - _frameGen0}/{GC.CollectionCount(1) - _frameGen1}/{GC.CollectionCount(2) - _frameGen2}");
        }

        private void OnGpuFrameBuilt(GpuFrame frame)
        {
            _presented++;
            NoteFrameGap();
            TestStopHook();
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

            WaitForHostFrame();
        }

        /// <summary>
        /// <c>WPR_WP8_FRAME_WAIT=host</c>: the old frame wait, a plain host wait inside the frame
        /// handler, during which no guest thread runs. For A/B runs only.
        /// </summary>
        private static readonly bool HostBlockingFrameWait =
            string.Equals(Environment.GetEnvironmentVariable("WPR_WP8_FRAME_WAIT"), "host", StringComparison.OrdinalIgnoreCase);

        private long _frameWaitFrom;

        /// <summary>
        /// The guest has finished a frame. Normally the presenting thread then waits for the host's
        /// next tick inside the runtime (<see cref="Direct3DRuntime.AwaitHostFrame"/>), other guest
        /// threads running meanwhile; this only starts the clock on that wait.
        /// </summary>
        private void WaitForHostFrame()
        {
            _frameWaitFrom = System.Diagnostics.Stopwatch.GetTimestamp();
            if (HostBlockingFrameWait && !_closing)
            {
                _tick.Wait(250);
                HostFrameTaken();
            }
        }

        /// <summary>On the guest thread, when the presenting thread carries on after a frame.</summary>
        private void HostFrameTaken()
        {
            bool ticked = HostBlockingFrameWait || _closing || _tick.Wait(0);

            // Woken only because the page queued a call: the guest serves it and waits on, and the
            // frame wait goes on being timed (a wait that ran out with a call pending is the same).
            bool granted = ticked || _emulator?.WinRt.HasPendingBridgeCall != true;
            if (_emulator is { } emulator)
            {
                emulator.Direct3D.HostFrameGranted = granted;
            }

            if (!granted)
            {
                return;
            }

            _frameWaitTicks = System.Diagnostics.Stopwatch.GetTimestamp() - _frameWaitFrom;
            Interlocked.Add(ref _guestWaitTicks, _frameWaitTicks);
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
            NoteFrameGap();
            TestStopHook();
            lock (_gate)
            {
                _latest = frame;
            }

            // Lockstep with the XNA loop: wait for the next tick before the guest builds another
            // frame. Bounded so a stalled host (a paused activity) cannot wedge the guest for ever
            // with nothing to show for it.
            WaitForHostFrame();
        }

        // ------------------------------------------------------------------------------
        // The XNA side
        // ------------------------------------------------------------------------------

        public void Update()
        {
            if (_closeRequested)
            {
                // The game let a Back press through, which on WP8 closes the app.
                _closeRequested = false;
                _host.Exit();
                return;
            }

            if (_bridged)
            {
                // This update runs on the page's UI thread, so any handler that handed over the
                // provider has returned: the component may draw.
                _emulator?.WinRt.PageTurned();
            }

            NoteBackForShutdown();
            PumpTouch();
            if (!_bridged)
            {
                // Bridged, Back belongs to the page (its BackKeyPress asks the component itself).
                PumpBack();
            }
            PumpKeyboard();
            _audio?.Pump();

            _hostUpdates++;
            _updatesSinceDraw++;
            Volatile.Write(ref _lastUpdateTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());
            ReportStatistics();
        }

        /// <summary>Fingers currently down, by TouchPanel id, with where they were last reported.</summary>
        private readonly Dictionary<int, (float X, float Y)> _fingers = new();

        /// <summary>The newest TouchPanel id seen, to tell a whole missed tap from a stale lift.</summary>
        private int _newestTouchId;

        /// <summary>Lifts of missed taps, delivered on the next update (see PumpTouch).</summary>
        private readonly List<(uint Id, float X, float Y)> _releaseNext = new();

        /// <summary>Presses written to the log so far (the first few of a run).</summary>
        private int _pressesLogged;

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
            // Under a title's own page the backbuffer is the page's (800x480), not the composed
            // raster, so positions are scaled to it; as its own Game the two are the same size.
            // A portrait host (a portrait page) is the composition turned upright: its x runs
            // along the composition's height, and WinRtRuntime (PointerRotation "portrait") takes
            // the positions as they are.
            float spanX = _hostPortrait ? FrameCapture.Height : FrameCapture.Width;
            float spanY = _hostPortrait ? FrameCapture.Width : FrameCapture.Height;
            float scaleX = TouchPanel.DisplayWidth > 0 ? spanX / TouchPanel.DisplayWidth : 1f;
            float scaleY = TouchPanel.DisplayHeight > 0 ? spanY / TouchPanel.DisplayHeight : 1f;
            foreach ((uint id, float x, float y) in _releaseNext)
            {
                emulator.WinRt.InjectPointer(WinRtRuntime.PointerKind.Released, x, y, id);
            }
            _releaseNext.Clear();

            HashSet<int>? seen = null;
            foreach (TouchLocation touch in touches)
            {
                if (touch.State == TouchLocationState.Invalid)
                {
                    continue;
                }

                (seen ??= []).Add(touch.Id);
                float x = Math.Clamp(touch.Position.X * scaleX, 0, spanX - 1);
                float y = Math.Clamp(touch.Position.Y * scaleY, 0, spanY - 1);
                uint id = PointerIdFor(touch.Id);
                bool down = _fingers.TryGetValue(touch.Id, out var last);
                bool unseen = touch.Id > _newestTouchId;
                _newestTouchId = Math.Max(_newestTouchId, touch.Id);

                switch (touch.State)
                {
                    // A finger first seen already Moved still starts a touch: a short tap can go
                    // down and move inside one frame, and its Pressed sample is then never seen.
                    case TouchLocationState.Pressed:
                    case TouchLocationState.Moved when !down:
                        _fingers[touch.Id] = (x, y);
                        emulator.WinRt.InjectPointer(WinRtRuntime.PointerKind.Pressed, x, y, id);
                        if (++_pressesLogged <= 6)
                        {
                            // "Taps land in the wrong place" needs these three spaces side by side.
                            Log($"pointer {id} pressed: touch ({touch.Position.X:0},{touch.Position.Y:0}) on a " +
                                $"{TouchPanel.DisplayWidth}x{TouchPanel.DisplayHeight} display -> ({x:0},{y:0}) in the " +
                                $"{FrameCapture.Width}x{FrameCapture.Height} composition{(_hostPortrait ? ", portrait host" : "")}");
                        }

                        break;
                    case TouchLocationState.Moved when x != last.X || y != last.Y:
                        _fingers[touch.Id] = (x, y);
                        emulator.WinRt.InjectPointer(WinRtRuntime.PointerKind.Moved, x, y, id);
                        break;
                    case TouchLocationState.Released when down:
                        _fingers.Remove(touch.Id);
                        emulator.WinRt.InjectPointer(WinRtRuntime.PointerKind.Released, x, y, id);
                        break;
                    // A touch newer than any seen, already lifted: a whole tap fell between two
                    // reads (TouchPanel advanced twice in one tick, as when a page pumps
                    // FrameworkDispatcher itself), and its Pressed sample was never seen here.
                    // The game still gets the tap, lifted on the next update: delivered together,
                    // both reach the guest in one ProcessEvents and a game that polls whether a
                    // pointer is down each frame (Jetpack Joyride's menus) never sees it. A lift
                    // of an older touch never seen down is a finger that was down before this
                    // session, and stays dropped.
                    case TouchLocationState.Released when unseen:
                        emulator.WinRt.InjectPointer(WinRtRuntime.PointerKind.Pressed, x, y, id);
                        _releaseNext.Add((id, x, y));
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
        /// Remembers when the player last pressed Back and how much guest heap was live then, for
        /// <see cref="StoppedWhileShuttingDown"/>. Reads the pad state only, so it takes nothing
        /// from the page or <see cref="PumpBack"/>.
        /// </summary>
        private void NoteBackForShutdown()
        {
            bool down = GamePad.GetState(PlayerIndex.One).Buttons.Back == ButtonState.Pressed;
            if (down && !_backSeenDown && _emulator is { } emulator)
            {
                _lastBackAt = System.Diagnostics.Stopwatch.GetTimestamp();
                _liveHeapAtBack = emulator.LiveHeapBytes;
                Log($"back noted (live guest heap {_liveHeapAtBack / (1024 * 1024)} MB)");
            }

            _backSeenDown = down;
        }

        private bool _backSeenDown;
        private long _lastBackAt;
        private long _liveHeapAtBack;

        /// <summary>How recent a Back press must be for a fault to count as part of a quit it started.</summary>
        private static readonly TimeSpan QuitAfterBackWindow = TimeSpan.FromSeconds(10);

        /// <summary>
        /// True when the guest faulted while tearing itself down after the player asked to leave:
        /// Back was pressed within <see cref="QuitAfterBackWindow"/>, and since then the live guest
        /// heap has shrunk by at least a sixteenth and 16 MB, which only a shutdown that frees the
        /// game's assets does. On a phone a native crash closed the app with no message, so a
        /// game that crashes in its own quit (Asphalt 7: its file system goes before a file it
        /// still holds) simply closed there. Here it is treated the same way: no error screen.
        /// A sixteenth, not an eighth: quitting after a race (S24, 0.2.10) freed 38 of 336 MB in
        /// 1.3 s, which an eighth called a crash and put on the error screen.
        /// </summary>
        private bool StoppedWhileShuttingDown(out string why)
        {
            why = string.Empty;
            if (_emulator is not { } emulator || _lastBackAt == 0)
            {
                return false;
            }

            TimeSpan since = System.Diagnostics.Stopwatch.GetElapsedTime(_lastBackAt);
            long freed = _liveHeapAtBack - emulator.LiveHeapBytes;
            if (since > QuitAfterBackWindow || freed < 16L * 1024 * 1024 || freed * 16 < _liveHeapAtBack)
            {
                Log($"not a shutdown: {since.TotalSeconds:F1} s after Back, live guest heap {_liveHeapAtBack / (1024 * 1024)} -> {emulator.LiveHeapBytes / (1024 * 1024)} MB");
                return false;
            }

            why = $"{since.TotalSeconds:F1} s after Back, with the live guest heap down from " +
                  $"{_liveHeapAtBack / (1024 * 1024)} MB to {emulator.LiveHeapBytes / (1024 * 1024)} MB";
            return true;
        }

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

            // A held key repeats KeyDown, as WinRT's did (Keyboard.cs times it).
            emulator.WinRt.PumpKeyRepeat();
        }

        /// <summary>
        /// The platform's keyboard repeat delay and interval: the Windows settings (Control Panel,
        /// Keyboard) on the desktop, read once at start; elsewhere Windows' defaults, 500 ms and
        /// about 30 a second, which are also Android's (ViewConfiguration's 500 ms timeout).
        /// </summary>
        internal static (TimeSpan Delay, TimeSpan Interval) PlatformKeyRepeat()
        {
            var fallback = (TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(33));
            if (!OperatingSystem.IsWindows())
            {
                return fallback;
            }

            try
            {
                // SPI_GETKEYBOARDDELAY: 0-3, i.e. 250 ms steps from 250 ms. SPI_GETKEYBOARDSPEED: 0-31,
                // roughly 2.5 to 30 repeats a second.
                uint delay = 0, speed = 0;
                if (!SystemParametersInfo(0x0016, 0, ref delay, 0) || !SystemParametersInfo(0x000A, 0, ref speed, 0))
                {
                    return fallback;
                }

                double perSecond = 2.5 + (Math.Min(speed, 31u) * 27.5 / 31);
                return (TimeSpan.FromMilliseconds(250 * (Math.Min(delay, 3u) + 1)), TimeSpan.FromSeconds(1 / perSecond));
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(uint action, uint param, ref uint value, uint winIni);

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

        /// <summary>
        /// The frame is a landscape page's DrawingSurface texture, already the right way up
        /// (<see cref="XamlShell.PresentsUnturned"/>); every other WP8 frame is the portrait screen.
        /// </summary>
        private volatile bool _presentUnturned;

        /// <summary>The host's backbuffer is portrait (a portrait page): touch is in portrait space.</summary>
        private volatile bool _hostPortrait;

        public void Draw()
        {
            long drawStart = System.Diagnostics.Stopwatch.GetTimestamp();
            NoteHostFrame(drawStart);
            try
            {
                DrawCore();
            }
            finally
            {
                _hostDrawTicks = System.Diagnostics.Stopwatch.GetTimestamp() - drawStart;
            }
        }

        // The host side of a stutter: a host frame that came late, split into this session's own
        // Draw (and the replay inside it) and everything outside it (the page, present, vsync,
        // whatever else the host loop did). The first 30 of a run, then every 50th.
        private long _lastHostDrawStart, _hostDrawTicks, _hostReplayTicks, _hostReplayedFrames;
        private int _hostGen0, _hostGen1, _hostGen2, _hostLateLogged, _hostCached;
        private long _hostUploaded;

        private void NoteHostFrame(long now)
        {
            long interval = _lastHostDrawStart == 0 ? 0 : now - _lastHostDrawStart;
            _lastHostDrawStart = now;
            if (interval > System.Diagnostics.Stopwatch.Frequency / 40 && _replay is { } replay)
            {
                int n = ++_hostLateLogged;
                if (n <= 30 || n % 50 == 0)
                {
                    double ms(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    Log($"late host frame #{n} at frame {_presented}: {ms(interval):0} ms since the last (our Draw {ms(_hostDrawTicks):0}, replay {ms(_hostReplayTicks):0} for {_hostReplayedFrames} frame(s), " +
                        $"outside {ms(interval - _hostDrawTicks):0} of which the last swap {ms(Microsoft.Xna.Framework.Graphics.GraphicsDevice.WprLastSwapTicks):0}), streams cached {replay.StreamsCached - _hostCached} uploaded {replay.StreamsUploaded - _hostUploaded}, " +
                        $"gc {GC.CollectionCount(0) - _hostGen0}/{GC.CollectionCount(1) - _hostGen1}/{GC.CollectionCount(2) - _hostGen2}");
                }
            }

            _hostGen0 = GC.CollectionCount(0);
            _hostGen1 = GC.CollectionCount(1);
            _hostGen2 = GC.CollectionCount(2);
            _hostReplayTicks = 0;
            _hostReplayedFrames = 0;
            if (_replay is { } current)
            {
                _hostCached = current.StreamsCached;
                _hostUploaded = current.StreamsUploaded;
            }
        }

        private void DrawCore()
        {
            EnsureGraphics();

            // The frame is portrait (the phone's screen, or a portrait page's DrawingSurface)
            // unless it is a landscape DrawingSurface; it is turned only when the host's screen
            // is the other way round. As its own Game the host is landscape; under a title's own
            // page it is the page's, so a portrait page's surface goes up as it is (Where's My
            // Perry, Tiny Death Star), and its pointers are portrait too.
            PresentationParameters host = _host.GraphicsDevice.PresentationParameters;
            bool hostPortrait = host.BackBufferHeight > host.BackBufferWidth;
            if (_bridged && _emulator is { } running)
            {
                // Under its own page the host's backbuffer is the page's orientation, which a
                // DrawingSurface follows (Fruit Ninja: landscape, applied after the provider).
                running.WinRt.NotePageLandscape(!hostPortrait);
                _presentUnturned = running.WinRt.PresentsUnturned;
            }

            _hostPortrait = hostPortrait;
            bool turned = _presentUnturned == hostPortrait;
            if (_emulator is { } current && hostPortrait && !_presentUnturned)
            {
                current.WinRt.PointerRotation = "portrait";
            }
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
                    _host.GraphicsDevice.Clear(Color.Black);
                }
                else
                {
                    _replay ??= new Wp8GpuReplay(_host.GraphicsDevice, _log);
                    // A vertex copy the recorder reused holds other bytes now: drop the GPU buffer
                    // cached for it before replaying a frame that may use it again.
                    while (_emulator?.Direct3D.TryTakeReusedSnapshot(out byte[] reused) == true)
                    {
                        _replay.ForgetStream(reused);
                    }

                    long replayStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    _hostReplayedFrames = gpuFrames.Length;
                    foreach (GpuFrame gpuFrame in gpuFrames)
                    {
                        _replay.Replay(gpuFrame);
                        _emulator?.Direct3D.GpuFrameReplayed(gpuFrame.Serial);
                        _drawn++;
                    }

                    _hostReplayTicks = System.Diagnostics.Stopwatch.GetTimestamp() - replayStart;

                    _replay.Present(_host.GraphicsDevice.PresentationParameters.BackBufferWidth, _host.GraphicsDevice.PresentationParameters.BackBufferHeight,
                        turned ? PresentTurn.Anticlockwise : PresentTurn.None);
                }

                return;
            }

            FrameDrawList? frame;
            lock (_gate)
            {
                frame = _latest;
            }

            if (frame is null || _effect is null)
            {
                _host.GraphicsDevice.Clear(Color.Black);
                return;
            }

            _host.GraphicsDevice.Clear(new Color(frame.Clear[0], frame.Clear[1], frame.Clear[2], 1f));
            _host.GraphicsDevice.BlendState = BlendState.NonPremultiplied;
            _host.GraphicsDevice.DepthStencilState = DepthStencilState.None;
            _host.GraphicsDevice.RasterizerState = RasterizerState.CullNone;
            _host.GraphicsDevice.SamplerStates[0] = SamplerState.LinearClamp;

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
                _host.GraphicsDevice.SamplerStates[0] = batch.Wrap ? SamplerState.LinearWrap : SamplerState.LinearClamp;
                foreach (EffectPass pass in _effect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    _host.GraphicsDevice.DrawUserPrimitives(
                        PrimitiveType.TriangleList, _vertices, batch.FirstVertex, batch.VertexCount / 3);
                }
            }

            _drawn++;
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
                : new Texture2D(_host.GraphicsDevice, image.Width, image.Height, false, SurfaceFormat.Color);

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
            TimeSpan gcPause = GC.GetTotalPauseDuration();
            Log($"guest fps={(_presented - _statsPresented) / seconds:0.0} frames={_presented} drawn={_drawn} " +
                $"longest-gap={_longestGapTicks * 1000 / System.Diagnostics.Stopwatch.Frequency}ms hitches>50ms={_hitches} gc-pause={(gcPause - _statsGcPause).TotalMilliseconds:0}ms " +
                $"host updates={_hostUpdates} draws={_hostDraws} " +
                $"guest-wait={Interlocked.Exchange(ref _guestWaitTicks, 0) * 1000 / System.Diagnostics.Stopwatch.Frequency / Math.Max(1, _presented - _statsPresented)}ms/frame " +
                $"file-io={((_emulator?.Stubs.Files.IoTicks ?? 0) - _statsIoTicks) * 1000 / System.Diagnostics.Stopwatch.Frequency}ms/{(_emulator?.Stubs.Files.IoCalls ?? 0) - _statsIoCalls} " +
                $"drawlist-build total={build * 1000 / System.Diagnostics.Stopwatch.Frequency}ms textures={_textures.Count} " +
                $"guest-heap: used={(_emulator?.HeapUsed ?? 0) / (1024 * 1024)}MB released={(_emulator?.BytesReleased ?? 0) / (1024 * 1024)}MB " +
                $"gpu: replayed={_replay?.DrawsReplayed} skipped={_replay?.DrawsSkipped} frames-dropped={_gpuFramesDropped} {_replay?.Statistics()} unrecorded={string.Join(",", _emulator?.Direct3D.GpuSkipped.Select(p => $"{p.Key}:{p.Value}") ?? [])} " +
                $"audio: {_emulator?.XAudio2.Summary()} voices={_audio?.VoicesCreated} underruns={_audio?.Underruns} callbacks={_emulator?.XAudio2.CallbacksMade} engine-passes={_emulator?.XAudio2.EnginePasses} audio-preemptions={_emulator?.AudioPreemptions} idle-passes={_emulator?.IdleAudioPasses} frame-waits-shared={_emulator?.HostFrameHandoffs} frame-preemptions={_emulator?.HostFramePreemptions}");
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
                    _silentReports = calls == _statsCalls ? _silentReports + 1 : 0;
                    if (calls == _statsCalls && emulator.RecentMethodCall is { } method)
                    {
                        Log($"   last host method: {method}");
                    }
                    if (_silentReports >= 3 && Environment.GetEnvironmentVariable("WPR_WP8_HALT_ON_STALL") == "1")
                    {
                        emulator.Stop("halted: no host call for a whole report (WPR_WP8_HALT_ON_STALL)");
                    }

                    LogHotImports(emulator);
                    _statsCalls = calls;

                    // Once per stall, where every guest thread is: a thread spinning on a lock says
                    // little until you know what the lock's owner is waiting for.
                    if (++_stallReports == 2)
                    {
                        foreach (string line in emulator.DescribeThreads().Where(l => !l.Contains(": finished", StringComparison.Ordinal)))
                        {
                            Log("   thread: " + line);
                        }

                        // And what the guest last asked of the file system: a loader that gave up on
                        // a missing file (Marmalade's s3e.icf) stops with an assert dialog, before
                        // the run's end-of-run file log would ever be written.
                        // Read racily, like the rest of this report; a torn read lands in the catch.
                        foreach (string line in emulator.Stubs.Files.Log.TakeLast(8).ToArray())
                        {
                            Log("   file: " + line);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"stall report failed: {ex.Message}");
                }
            }
            else
            {
                _stallReports = 0;
            }

            _statsAt = now;
            _statsPresented = _presented;
            _longestGapTicks = 0;
            _hitches = 0;
            _statsGcPause = GC.GetTotalPauseDuration();
            _statsIoTicks = _emulator?.Stubs.Files.IoTicks ?? 0;
            _statsIoCalls = _emulator?.Stubs.Files.IoCalls ?? 0;
        }

        /// <summary>
        /// The imports the guest has called most since the last such report: they name the loop
        /// it is in. A racy snapshot of counts the guest is writing as fast as it can, retried.
        /// </summary>
        private void LogHotImports(ArmEmulator emulator)
        {
            lock (_hotImportsLock)
            {
                KeyValuePair<string, int>[]? counts = null;
                for (int attempt = 0; attempt < 50 && counts is null; attempt++)
                {
                    // Read racily from the guest thread's trap table: a torn read can throw more than
                    // InvalidOperationException; a NullReferenceException used to end the whole stall report.
                    try { counts = emulator.CallCounts.ToArray(); } catch (Exception) { }
                }

                if (counts is null)
                {
                    return;
                }

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
        }

        // ------------------------------------------------------------------------------
        // Watchdog
        // ------------------------------------------------------------------------------

        /// <summary>Seconds without a host update before the watchdog reports.</summary>
        private const int WatchdogSeconds = 10;

        private System.Threading.Timer? _watchdog;
        private long _lastUpdateTimestamp;
        private long _watchdogCalls;
        private long _watchdogStall;
        private int _watchdogReports;

        /// <summary>
        /// Reports, from a thread of its own, a host frame loop that has stopped running. The
        /// statistics above are written by that loop, so a stall that holds the host thread
        /// itself (a call into the page's component that the guest never answers, say) would
        /// otherwise leave nothing in the log: no frames, no stall report, until the player
        /// gives up and closes the game.
        /// </summary>
        private void Watchdog(object? state)
        {
            long last = Volatile.Read(ref _lastUpdateTimestamp);
            // Once the guest has stopped the host loop is the error screen's or teardown's, not ours.
            if (_closing || last == 0 || _capture is not null || Outcome is not null || _emulator is not { } emulator)
            {
                return;
            }

            double stalled = (System.Diagnostics.Stopwatch.GetTimestamp() - last) / (double)System.Diagnostics.Stopwatch.Frequency;
            if (stalled < WatchdogSeconds)
            {
                return;
            }

            // A backgrounded app stops its loop too; a minute of reports says all there is to say.
            _watchdogReports = last == _watchdogStall ? _watchdogReports + 1 : 1;
            _watchdogStall = last;
            if (_watchdogReports > 6)
            {
                return;
            }

            try
            {
                long calls = emulator.CallOrderTotal;
                Log($"host frame loop has not run for {stalled:0}s; host waiting on a page call: {emulator.WinRt.BridgeCallWaiting ?? "no"}");
                Log($"   guest: {emulator.LastSlice}; {calls - _watchdogCalls:N0} host call(s) since the last check");
                if (emulator.RecentMethodCall is { } method)
                {
                    Log($"   last host method: {method}");
                }

                // The guest's last file requests, once per stall (the first check that saw calls).
                if (calls != _watchdogCalls)
                {
                    foreach (string line in emulator.Stubs.Files.Log.TakeLast(8).ToArray())
                    {
                        Log("   file: " + line);
                    }
                }

                LogHotImports(emulator);
                _watchdogCalls = calls;
            }
            catch (Exception ex)
            {
                Log($"watchdog report failed: {ex.Message}");
            }
        }

        private readonly object _hotImportsLock = new();
        private int _stallReports;
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
        /// <summary>The host lost the foreground.</summary>
        public void Deactivated()
        {
            _hostActive = false;
            _audio?.Suspend(true);
            Wp8HostServices.StopVibration();
            _emulator?.WinRt.InjectVisibility(false);
        }

        /// <summary>The host regained the foreground.</summary>
        public void Activated()
        {
            _hostActive = true;
            _audio?.Suspend(false);
            _emulator?.WinRt.InjectVisibility(true);
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
        /// <summary>
        /// A page call was queued. While the frame loop draws, the main thread's frame wait takes it
        /// (<see cref="WinRtRuntime.BridgeCallsServedBetweenFrames"/>), so the guest is only woken:
        /// releasing a tick gave every call a frame of its own (a finger down on Shadow Fight 2 ran
        /// it at twice the display's rate). Otherwise the guest waits on the tick, which it needs.
        /// </summary>
        private void WakeForBridgeCall()
        {
            if (_emulator is { } emulator && emulator.WinRt.BridgeCallsServedBetweenFrames)
            {
                emulator.WakeIdle();
                return;
            }

            WakeGuest();
        }

        private void WakeGuest()
        {
            if (_tick.CurrentCount >= 2)
                return;
            try { _tick.Release(); } catch (SemaphoreFullException) { }
            _emulator?.WakeIdle();
        }

        /// <summary>
        /// One guest frame per frame the host shows. The guest was woken once per Update, and FNA's
        /// fixed timestep beats against a display that is not exactly 60 Hz: on the Galaxy S24
        /// (60.000004 Hz) one tick in ~100 ran two Updates before one Draw, so the guest built two
        /// frames back to back, one never shown, and waited a whole tick for the next - Jetpack
        /// Joyride stuttered every couple of seconds. An isolated catch-up now costs the guest one
        /// frame of its own time (its clock is counted in frames; about 1% at worst, which nobody
        /// sees) instead of a dropped frame. Each catch-up is a frame of debt, one is forgiven every
        /// 60 draws, and a debt of two is paid by moving the guest's clock on a frame
        /// (<see cref="HostStubs.ClockFramesSkipped"/>): a host that keeps falling behind (a device
        /// that cannot draw 60 a second) still runs the game at its speed, while the occasional beat
        /// never costs a shown frame. While the host draws nothing (the background) the guest's
        /// bounded waits pace it.
        /// <para>The debt used to be paid with an extra guest frame. A host behind because its GPU
        /// is (Asphalt 7's races on the S24) then had two frames to replay in one draw, twice the
        /// GPU work, so it fell further behind: a stutter every few frames. A phone that cannot keep
        /// up renders fewer frames and its clock, which is real time, gives the next one a longer
        /// step; moving the clock instead of rendering is that.</para>
        /// </summary>
        /// <summary>After the host has shown a frame.</summary>
        public void AfterDraw()
        {
            _hostDraws++;
            _frameDebt = Math.Min(4, _frameDebt + Math.Max(0, _updatesSinceDraw - 1));
            _updatesSinceDraw = 0;
            if (++_drawsSinceForgiven >= 60)
            {
                _drawsSinceForgiven = 0;
                _frameDebt = Math.Max(0, _frameDebt - 1);
            }

            if (_frameDebt >= 2 && _emulator is { } emulator)
            {
                _frameDebt--;
                Interlocked.Increment(ref emulator.Stubs.ClockFramesSkipped);
            }

            WakeGuest();
        }

        private int _updatesSinceDraw, _frameDebt, _drawsSinceForgiven;

        /// <summary>The host is closing.</summary>
        public void Exiting()
        {
            _closing = true;
            try { _emulator?.Stop("host exiting"); } catch { }
            WakeGuest();
        }

        public void Dispose()
        {
            {
                _closing = true;
                _watchdog?.Dispose();
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
        }
    }

    /// <summary>A WP8 guest that stopped on its own: why, how far it got, and the post-mortem (summary first).</summary>
    public sealed record Wp8GuestFailure(string Reason, long FramesPresented, string Details);
}
