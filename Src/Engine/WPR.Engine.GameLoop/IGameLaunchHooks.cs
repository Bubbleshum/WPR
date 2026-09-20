using System;
using Microsoft.Xna.Framework;

namespace WPR.Engine.GameLoop
{
    /// <summary>
    /// The backend-specific steps inside an XNA game launch. <see cref="WPR.ApplicationLaunch"/>
    /// owns the launch sequence — install folder, diagnostics, the collectible
    /// <c>AssemblyLoadContext</c>, the WP7 lifecycle signals, and the ordered teardown — and it
    /// is entirely FNA-free. What it cannot do itself is the handful of things that need the
    /// windowing backend: telling it where the title's content lives, decorating the window once
    /// it exists, and asking whether that window survived <c>Game.Dispose</c>. Those come in
    /// through this interface, implemented by <c>WPR.Backend.FNA.FnaGameHost</c>.
    ///
    /// <para><b>Why hooks and not a second <c>IGameHost</c>.</b> Every member here is called at
    /// a fixed point in a sequence whose ordering is load-bearing (the teardown order is what
    /// keeps the ALC-unload / stuck-audio / duplicate-static regressions closed — see the remarks
    /// throughout <c>ApplicationLaunch.Start</c>). Keeping the sequence in one place and letting
    /// the backend fill in the steps preserves that ordering by construction; an implementation
    /// that owned the sequence itself would have to re-derive it.</para>
    ///
    /// <para>Naming <see cref="Game"/> here is fine: it is a <c>WPR.Framework.Xna</c> type, not a
    /// backend one, and this contract is consumed by the backend rather than by the framework, so
    /// the "no game-facing identity in an engine contract" rule (which exists to stop the
    /// framework needing the engine back) does not apply.</para>
    /// </summary>
    public interface IGameLaunchHooks
    {
        /// <summary>
        /// The title's content root — the per-game install folder. Called once, before the game
        /// assembly is loaded. FNA reads this through <c>TitleContainer</c> for every content
        /// stream, so it must be set before the game constructs anything.
        /// </summary>
        void SetTitleLocation(string installFolder);

        /// <summary>
        /// Samples whatever the backend wants to compare against at teardown (thread count, live
        /// windows). Debug-only diagnostics; must never throw.
        /// </summary>
        void RecordLaunchBaseline();

        /// <summary>
        /// The <c>Game</c> instance exists and its window handle is valid, but nothing has run
        /// yet. Where the backend decorates the window (the per-game icon) and wires anything that
        /// needs the concrete <c>Game</c>. Runs BEFORE the WP7 lifecycle is primed; a throw here is
        /// caught and logged, never fatal to the launch.
        /// </summary>
        void OnGameCreated(Game game);

        /// <summary>
        /// The last hook before <c>Game.Run()</c>: the cold-start <c>Launching</c>/<c>Activated</c>
        /// signals have been primed, the graphics device has NOT been created. Where the backend
        /// subscribes to <c>GraphicsDeviceManager.PreparingDeviceSettings</c> and the like.
        /// </summary>
        void OnBeforeRun(Game game);

        /// <summary>
        /// True if the game's top-level window still exists in this process after
        /// <c>Game.Dispose</c>. A surviving window is proof the game never destroyed it, which is
        /// what lets the launch core force-dispose it without risking a double free.
        /// </summary>
        bool HasLiveGameWindow();

        /// <summary>One log-ready line describing what the process still holds after teardown
        /// (threads, windows). Never throws.</summary>
        string DescribePostTeardown();
    }
}
