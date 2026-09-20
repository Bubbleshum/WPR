using WPR.Engine.Audio;
using System;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using WPR.Engine.GameLoop;
using WPR.Models;
using WPR.Xna.Rhi;

namespace WPR.Backend.FNA
{
    /// <summary>
    /// FNA implementation of <see cref="IGameHost"/>: composes the FNA adapters into the RHI
    /// seams for one game run, then hands the run to the engine's launch sequence
    /// (<see cref="WPR.ApplicationLaunch"/>), supplying the four backend-specific steps that
    /// sequence needs through <see cref="IGameLaunchHooks"/>.
    ///
    /// <para><b>What is and is not here.</b> Everything in <see cref="RunAsync"/> before the
    /// call into <c>ApplicationLaunch.Start</c> names an FNA type — that is the test for whether a
    /// line belongs in this file. The launch sequence itself (install folder, the collectible ALC,
    /// the WP7 lifecycle priming, the ordered teardown) is engine code and lived here only from
    /// Stage 4 until 2026-09-20, when the last FNA references inside it had dwindled to the hooks
    /// below. The framework-only hooks it used to register (game-thread post, focus guard, the
    /// presentation hook) went with it.</para>
    ///
    /// <para><b>Still deliberately thin on the <see cref="IGameHost"/> side.</b>
    /// <see cref="Shutdown"/> == <see cref="RequestExit"/>: the ordered teardown runs inside
    /// <c>ApplicationLaunch.Start</c>'s finally as the loop unwinds, and <see cref="Activated"/> /
    /// <see cref="Deactivated"/> are declared but not yet raised.</para>
    /// </summary>
    public sealed class FnaGameHost : IGameHost
    {
        private readonly Application _app;
        private readonly Action<DisplayOrientation>? _requestOrientation;
        private readonly GameWindowIcon? _windowIcon;
        private GameHostState _state = GameHostState.NotStarted;

        /// <param name="windowIcon">
        /// Optional decoded icon for the game's window. Takes DATA, deliberately, not the
        /// <c>Action&lt;Game&gt;</c> hook this replaced (2026-09-01, Stage 5): that parameter put
        /// FNA's <c>Game</c> in the public signature, which is what kept BOTH platform heads in
        /// <c>KnownBackendLeaks</c> — the Windows head genuinely used it, and the Android head
        /// leaked purely because its call site named the full ctor signature while passing null.
        /// The icon is applied by <see cref="GameWindowIcon.ApplyTo"/> from
        /// <see cref="LaunchHooks.OnGameCreated"/>.
        /// </param>
        public FnaGameHost(
            Application app,
            Action<DisplayOrientation>? requestOrientation = null,
            GameWindowIcon? windowIcon = null)
        {
            _app = app ?? throw new ArgumentNullException(nameof(app));
            _requestOrientation = requestOrientation;
            _windowIcon = windowIcon;
        }

        public GameHostState State => _state;

        // Declared by the contract; not yet raised. Wired when ApplicationLaunch's
        // Game.Activated / PhoneApplicationService hooks move onto this instance.
        public event Action? Activated;
        public event Action? Deactivated;

        /// <summary>
        /// Runs the game loop and completes when the game exits. This is the launchers' entry point;
        /// the returned Task is the launch sequence's own, so nothing about the run/teardown
        /// behaviour differs from calling <c>ApplicationLaunch.Start</c> directly.
        /// </summary>
        public async Task RunAsync()
        {
            _state = GameHostState.Running;

            // Apply the platform's declared graphics driver BEFORE anything creates a device:
            // FNA3D reads the force hint exactly once, inside FNA3D_PrepareWindowAttributes.
            // Nothing happens when no platform declared one — GraphicsDriver.Unspecified means
            // "leave the lever alone", which is not the same instruction as "clear it" and is what
            // keeps the desktop on the D3D11 path it never had to ask for.
            string? requestedDriver = null;
            if (WPR.Engine.Graphics.GraphicsDriverPreference.HasPreference)
            {
                requestedDriver = WPR.Engine.Graphics.GraphicsDriverPreference.ResolveDriverName();
                GraphicsDriverSelection.Apply(requestedDriver);
            }

            // Drop the crash breadcrumb here and nowhere earlier: this is the last instruction
            // before anything can reach the driver, and the first call into it (PrepareWindowAttributes)
            // already builds a throwaway device to test the waters — so a hostile driver can take
            // the process down well before FNA3D_CreateDevice, with no exception and no log. It is
            // retired at the first presented frame in FnaGraphicsBackend.SwapBuffers, so the
            // pending window is device creation plus one frame rather than a play session.
            // Inert on a platform whose head never configured the probe (Windows declares no
            // driver at all), which is the usual absent-means-unavailable rule.
            WPR.Engine.Graphics.GraphicsDriverProbe.MarkAttempting(requestedDriver);

            // 5c-0 (Plans/STAGE5C-SCOPE.md): publish the FNA graphics RHI so the WPR-owned XNA
            // runtime (WPR.Framework.Xna) can reach the GPU through IGraphicsBackend without
            // referencing FNA. Registered here — before the game constructs its GraphicsDevice —
            // and the per-launch hooks are cleared in finally (a backend registry must not outlive
            // the run; ADR Risk #1).
            XnaBackend.SetGraphics(new FnaGraphicsBackend());
            // Spine relocation step 1: Game and GraphicsDeviceManager reach the window and the
            // event pump through this instead of naming FNAPlatform. Registered FIRST — Game's
            // ctor calls CreateWindow, so an unset slot here is a launch failure, not a late one.
            XnaBackend.SetPlatform(new FnaPlatformBackend());
            // Synthetic touch is layered over the real input backend rather than beside it: the
            // injector has to be the last writer inside UpdateTouchPanelState, which only a
            // decorator can guarantee. The engine decides whether to wrap (a head registered a
            // keyboard-emulation host) — with none (Android) or no bindings, the plain backend is
            // registered and the touch pipeline behaves exactly as it always has.
            XnaBackend.SetInput(WPR.Engine.Input.KeyboardEmulation.WrapInput(new FnaInputBackend()));
            XnaBackend.SetStorage(new FnaStorageBackend());
            XnaBackend.SetTitleLocation(() => Microsoft.Xna.Framework.TitleLocation.Path);
            XnaBackend.SetLogInfo(msg => Microsoft.Xna.Framework.FNALoggerEXT.LogInfo?.Invoke(msg));
            XnaBackend.SetLogWarn(msg => Microsoft.Xna.Framework.FNALoggerEXT.LogWarn?.Invoke(msg));

            // Audio is composed, not hardcoded (2026-09-01). The three audio seams
            // (IAudioBackend / IXactBackend / IMediaBackend) are filled by whatever modules are in
            // AudioBackendRegistry: this host supplies the FAudio module as the BASE so any code
            // path that runs a game has audio — including the bare-FnaGameHost console harness,
            // which never reaches a platform head's ServicesSetup — and a head layers its own on
            // top at launcher startup. Android registers AndroidMediaPlayerModule there, which
            // claims the song half and hands video back to FAudio's Theorafile.
            //
            // Composed HERE rather than at head startup because these are per-launch slots, cleared
            // on teardown (ADR Risk #1); the modules themselves are process-lifetime. That split is
            // what the old MediaBackendOverride existed to paper over, for the media seam only.
            // Logging hooks are set above so a module failure lands in the per-game log.
            //
            // Compose() is called on its own line, NOT as the argument of a
            // `FNALoggerEXT.LogInfo?.Invoke(...)`. `?.` short-circuits the WHOLE invocation
            // expression, arguments included — and LogInfo is still null here, because it is
            // FNAPlatform's static ctor that fills it in and nothing has touched FNAPlatform yet
            // this launch. Composing inside that argument therefore did not compose at all: every
            // audio seam stayed empty on both heads, and the only symptom was games throwing
            // NoAudioHardwareException out of Update — the same "External component has thrown an
            // exception" a machine with no sound card gives. Never put a call with side effects in
            // a `?.` argument.
            AudioBackendRegistry.SetBase(new WPR.Audio.FAudio.FAudioModule());
            string audioComposition = AudioBackendRegistry.Compose();
            Microsoft.Xna.Framework.FNALoggerEXT.LogInfo?.Invoke("[wpr-audio] " + audioComposition);

            // The framework-side hooks (game-thread post, focus-activation guard, the presentation
            // hook that feeds Mouse/TouchPanel) are registered by ApplicationLaunch itself now —
            // they name only WPR.Framework.Xna types. XnaBackend.Clear() below still drops them.
            try
            {
                await WPR.ApplicationLaunch.Start(_app, new LaunchHooks(this));
            }
            finally
            {
                XnaBackend.Clear();
                _state = GameHostState.Stopped;
            }
        }

        /// <summary><see cref="IGameHost.Run"/> — synchronous blocking conformance. Launchers use
        /// <see cref="RunAsync"/>; this exists for callers holding the interface synchronously.</summary>
        public void Run() => RunAsync().GetAwaiter().GetResult();

        /// <summary>
        /// Delivers one WP7 hardware-Back press to the running game — the same edge the desktop
        /// head produces from Esc / SDLK_AC_BACK, i.e. <c>GamePad.Buttons.Back</c> for exactly one
        /// frame. For hosts whose platform Back never reaches SDL as a key event (Android, where
        /// the activity gets it); the desktop head needs no such call. Thread-safe.
        /// </summary>
        public void PressBackButton() => WprPhoneBackButton.Press();

        /// <summary>Asks the running game to exit at the next safe point. Thread-safe.</summary>
        public void RequestExit()
        {
            _state = GameHostState.ShuttingDown;
            WPR.ApplicationLaunch.RequestExit();
        }

        /// <summary>
        /// Idempotent. Signals exit; the ordered teardown (StopAudio → ClearContentCaches →
        /// DisposeGame, plus ALC unload) executes in <c>ApplicationLaunch</c>'s finally as the
        /// loop unwinds — preserved verbatim.
        /// </summary>
        public void Shutdown() => RequestExit();

        // Suppress "event never used" until the lifecycle events are wired; keeps the public surface honest.
        private void TouchEvents() { Activated?.Invoke(); Deactivated?.Invoke(); }

        /// <summary>
        /// The FNA-specific steps of the launch sequence. Each member is called at a fixed point
        /// inside <c>ApplicationLaunch.Start</c>; see <see cref="IGameLaunchHooks"/> for where.
        /// Every line in here names an FNA or SDL type, which is exactly why these four steps are
        /// the ones that stayed in the backend.
        /// </summary>
        private sealed class LaunchHooks : IGameLaunchHooks
        {
            private readonly FnaGameHost _owner;

            public LaunchHooks(FnaGameHost owner) => _owner = owner;

            /// <summary>FNA's content root: TitleContainer opens every content stream under it.</summary>
            public void SetTitleLocation(string installFolder) => FNAPlatform.TitleLocation = installFolder;

            public void RecordLaunchBaseline() => TeardownDiagnostics.RecordLaunchBaseline();

            /// <summary>
            /// The window exists: put the per-game icon on it (SDL_SetWindowIcon needs the handle),
            /// and hand the head's orientation callback to the WP7 <c>GraphicsDeviceManager</c>
            /// override games bind. The static assignment used to sit a few statements later in
            /// the launch sequence; nothing reads it before <c>ApplyChanges</c>, so the move is
            /// behaviour-neutral.
            /// </summary>
            public void OnGameCreated(Game game)
            {
                _owner._windowIcon?.ApplyTo(game);
                Compat.GraphicsDeviceManager.RequestOrientation = _owner._requestOrientation;
            }

            /// <summary>
            /// Route the backbuffer the game is about to create into the orientation request —
            /// the same width-vs-height rule <c>ApplyChanges</c> applies, for games that size the
            /// device through <c>PreparingDeviceSettings</c> instead.
            /// </summary>
            public void OnBeforeRun(Game game)
            {
                GraphicsDeviceManager? manager =
                    game.Services.GetService(typeof(IGraphicsDeviceManager)) as GraphicsDeviceManager;
                if (manager != null)
                {
                    manager.PreparingDeviceSettings += (_, args) =>
                    {
                        Compat.GraphicsDeviceManager.RequestOrientationChange(
                            args.GraphicsDeviceInformation.PresentationParameters.BackBufferWidth,
                            args.GraphicsDeviceInformation.PresentationParameters.BackBufferHeight
                        );
                    };
                }
            }

            /// <summary>An SDL_app top-level window still alive in this process.</summary>
            public bool HasLiveGameWindow() => TeardownDiagnostics.HasLiveSdlWindow();

            public string DescribePostTeardown() => TeardownDiagnostics.Describe();
        }
    }
}
