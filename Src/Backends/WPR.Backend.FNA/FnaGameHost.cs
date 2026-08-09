using System;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using WPR.Abstractions.Hosting;
using WPR.Models;
using WPR.Xna.Rhi;

namespace WPR.Backend.FNA
{
    /// <summary>
    /// FNA implementation of <see cref="IGameHost"/> — the Stage-4 seam over the FNA game-loop
    /// driver. It adapts the (still-static, verbatim-moved) <see cref="WPR.ApplicationLaunch"/>
    /// to the <c>WPR.Abstractions</c> contract so launchers drive games through the abstraction
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
        private readonly Action<Game>? _onGameCreated;
        private GameHostState _state = GameHostState.NotStarted;

        public FnaGameHost(
            Application app,
            Action<DisplayOrientation>? requestOrientation = null,
            Action<Game>? onGameCreated = null)
        {
            _app = app ?? throw new ArgumentNullException(nameof(app));
            _requestOrientation = requestOrientation;
            _onGameCreated = onGameCreated;
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

            // 5c-0 (docs/STAGE5C-SCOPE.md): publish the FNA graphics RHI so the WPR-owned XNA
            // runtime (WPR.Framework.Xna) can reach the GPU through IGraphicsBackend without
            // referencing FNA. Registered here — before the game constructs its GraphicsDevice —
            // and cleared in finally (a backend registry must not outlive the run; ADR Risk #1).
            // No consumer yet: GraphicsDevice/Texture2D/… start routing through it in 5c-1/5c-2.
            XnaBackend.SetGraphics(new FnaGraphicsBackend());
            XnaBackend.SetAudio(new FnaAudioBackend());
            XnaBackend.SetXact(new FnaXactBackend());
            XnaBackend.SetMedia(new FnaMediaBackend());
            XnaBackend.SetInput(new FnaInputBackend());
            XnaBackend.SetStorage(new FnaStorageBackend());
            XnaBackend.SetTitleLocation(() => Microsoft.Xna.Framework.TitleLocation.Path);
            XnaBackend.SetLogInfo(msg => Microsoft.Xna.Framework.FNALoggerEXT.LogInfo?.Invoke(msg));
            XnaBackend.SetLogWarn(msg => Microsoft.Xna.Framework.FNALoggerEXT.LogWarn?.Invoke(msg));
            XnaBackend.SetBackBufferSizeHook((w, h) =>
            {
                Microsoft.Xna.Framework.Input.Mouse.INTERNAL_BackBufferWidth = w;
                Microsoft.Xna.Framework.Input.Mouse.INTERNAL_BackBufferHeight = h;
                Microsoft.Xna.Framework.Input.Touch.TouchPanel.DisplayWidth = w;
                Microsoft.Xna.Framework.Input.Touch.TouchPanel.DisplayHeight = h;
            });
            try
            {
                await WPR.ApplicationLaunch.Start(_app, _requestOrientation, _onGameCreated);
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
