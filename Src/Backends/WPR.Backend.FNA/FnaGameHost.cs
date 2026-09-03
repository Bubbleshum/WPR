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
    /// FNA implementation of <see cref="IGameHost"/> — the Stage-4 seam over the FNA game-loop
    /// driver. It adapts the (still-static, verbatim-moved) <see cref="WPR.ApplicationLaunch"/>
    /// to the <c>WPR.Engine.GameLoop</c> contract so launchers drive games through the abstraction
    /// instead of a static call into the FNA layer.
    ///
    /// <para>Deliberately thin for this step: <see cref="RunAsync"/> returns the <em>exact same
    /// Task</em> the launchers used before (<c>ApplicationLaunch.Start</c>), so the async/threading
    /// model and — critically — the load-bearing teardown ordering inside that method are byte-for-byte
    /// unchanged (ADR Risk #1). Stage 5b promotes the static <c>ApplicationLaunch</c> body into this
    /// instance and splits ALC/lifecycle coordination back into <c>WPR.Runtime</c> behind this
    /// interface; only then do <see cref="Shutdown"/>/<see cref="Activated"/> gain real teardown-phase
    /// and lifecycle wiring. Until then <see cref="Shutdown"/> == <see cref="RequestExit"/> (the
    /// teardown still runs in <c>ApplicationLaunch</c>'s finally when the loop unwinds).</para>
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
        /// Everything the hook used to do now happens inside this assembly: the icon is applied by
        /// <see cref="GameWindowIcon.ApplyTo"/> and the tilt emulator is attached by
        /// <see cref="Input.KeyboardEmulation.AttachTo"/>.
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

        // Declared by the contract; not yet raised. Wired in 5b when ApplicationLaunch's
        // Game.Activated / PhoneApplicationService hooks move onto this instance.
        public event Action? Activated;
        public event Action? Deactivated;

        /// <summary>
        /// Runs the game loop and completes when the game exits. This is the launchers' entry point;
        /// the returned Task is identical to the legacy <c>ApplicationLaunch.Start</c> call, so nothing
        /// about the run/teardown behaviour changes.
        /// </summary>
        public async Task RunAsync()
        {
            _state = GameHostState.Running;

            // 5c-0 (Plans/STAGE5C-SCOPE.md): publish the FNA graphics RHI so the WPR-owned XNA
            // runtime (WPR.Framework.Xna) can reach the GPU through IGraphicsBackend without
            // referencing FNA. Registered here — before the game constructs its GraphicsDevice —
            // and cleared in finally (a backend registry must not outlive the run; ADR Risk #1).
            // No consumer yet: GraphicsDevice/Texture2D/… start routing through it in 5c-1/5c-2.
            // Apply the platform's declared graphics driver BEFORE anything creates a device:
            // FNA3D reads the force hint exactly once, inside FNA3D_PrepareWindowAttributes.
            // Nothing happens when no platform declared one — GraphicsDriver.Unspecified means
            // "leave the lever alone", which is not the same instruction as "clear it" and is what
            // keeps the desktop on the D3D11 path it never had to ask for.
            if (WPR.Engine.Graphics.GraphicsDriverPreference.HasPreference)
            {
                GraphicsDriverSelection.Apply(
                    WPR.Engine.Graphics.GraphicsDriverPreference.ResolveDriverName());
            }

            XnaBackend.SetGraphics(new FnaGraphicsBackend());
            // Spine relocation step 1: Game and GraphicsDeviceManager reach the window and the
            // event pump through this instead of naming FNAPlatform. Registered FIRST — Game's
            // ctor calls CreateWindow, so an unset slot here is a launch failure, not a late one.
            XnaBackend.SetPlatform(new FnaPlatformBackend());
            // Synthetic touch is layered over the real input backend rather than beside it: the
            // injector has to be the last writer inside UpdateTouchPanelState, which only a
            // decorator can guarantee. See SyntheticTouchInputBackend for why a GameComponent
            // cannot do this. With no emulation host (Android) or no bindings, the plain backend
            // is registered and the touch pipeline behaves exactly as it always has.
            IInputBackend input = new FnaInputBackend();
            IKeyboardEmulationHost? keyboardHost = XnaBackend.KeyboardEmulation;
            if (keyboardHost != null)
            {
                input = new Input.SyntheticTouchInputBackend(input, keyboardHost);
            }
            XnaBackend.SetInput(input);
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
            // GamerServices lives in WPR.Framework.Xna since patcher version 19, so it can no
            // longer call FNA's WprGameThread / WprActivationGuard directly (FNA.Core references
            // WPR.Framework.Xna, so a reference back would be circular). It goes through these
            // two hooks instead — the same inversion every other slot on XnaBackend uses.
            XnaBackend.SetGameThreadPost(Microsoft.Xna.Framework.WprGameThread.Post);
            XnaBackend.SetSuppressFocusActivation(
                Microsoft.Xna.Framework.WprActivationGuard.SuppressFocusActivation);
            XnaBackend.SetBackBufferSizeHook((w, h) =>
            {
                Microsoft.Xna.Framework.Input.Mouse.INTERNAL_BackBufferWidth = w;
                Microsoft.Xna.Framework.Input.Mouse.INTERNAL_BackBufferHeight = h;
                Microsoft.Xna.Framework.Input.Touch.TouchPanel.DisplayWidth = w;
                Microsoft.Xna.Framework.Input.Touch.TouchPanel.DisplayHeight = h;
            });
            try
            {
                // The per-Game setup the launcher used to inject as a hook. Both steps need the
                // Game instance and both are backend work (an SDL window handle; FNA GameComponents),
                // so they live here rather than in a head. Order matches the old lambda.
                await WPR.ApplicationLaunch.Start(_app, _requestOrientation, game =>
                {
                    _windowIcon?.ApplyTo(game);
                    Input.KeyboardEmulation.AttachTo(game);
                });
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
        /// DisposeGame, plus ALC unload) currently executes in <c>ApplicationLaunch</c>'s finally as
        /// the loop unwinds — preserved verbatim. Becomes an explicit <see cref="TeardownPhase"/>
        /// sequence here in 5b.
        /// </summary>
        public void Shutdown() => RequestExit();

        // Suppress "event never used" until 5b wires them; keeps the public surface honest.
        private void TouchEvents() { Activated?.Invoke(); Deactivated?.Invoke(); }
    }
}
