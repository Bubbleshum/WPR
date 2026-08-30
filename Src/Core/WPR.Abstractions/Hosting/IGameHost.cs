using System;

namespace WPR.Abstractions.Hosting;

/// <summary>Lifecycle state of a hosted game.</summary>
public enum GameHostState
{
    NotStarted,
    Running,
    Paused,
    ShuttingDown,
    Stopped,
}

/// <summary>
/// Ordered phases of game teardown. The order is load-bearing: it mirrors the
/// reflective dispose sequence in the legacy <c>ApplicationLaunch.cs</c> that exists
/// to prevent the ALC-unload-failure, audio-keeps-playing, and duplicate-static-key
/// regressions (see Plans/STAGE5-SIZING.md Risk #1). An <see cref="IGameHost"/>
/// implementation MUST run these in ascending numeric order during shutdown.
/// </summary>
public enum TeardownPhase
{
    /// <summary>1 — stop music/media and dispose the audio engine (FAudio context).</summary>
    StopAudio = 0,

    /// <summary>2 — clear the content-reader/type caches.</summary>
    ClearContentCaches = 1,

    /// <summary>3 — dispose the game object and release the graphics device.</summary>
    DisposeGame = 2,
}

/// <summary>
/// Hosts and drives a game's run loop. This is the contract that lets the FNA
/// game-loop driver (today inlined in <c>ApplicationLaunch.cs</c>) move into
/// WPR.Backend.FNA (Stage 5b) while Runtime keeps only assembly-load-context and
/// lifecycle coordination — expressed here, not against FNA's <c>Game</c>.
///
/// Implementations MUST honour <see cref="TeardownPhase"/> ordering in
/// <see cref="Shutdown"/>; getting it wrong silently regresses the bugs that
/// ordering was built to fix.
/// </summary>
public interface IGameHost
{
    GameHostState State { get; }

    /// <summary>Raised when the game is brought to the foreground (WP7 Activated).</summary>
    event Action? Activated;

    /// <summary>Raised when the game is sent to the background (WP7 Deactivated).</summary>
    event Action? Deactivated;

    /// <summary>Runs the game loop. Blocks until the game exits or <see cref="RequestExit"/>.</summary>
    void Run();

    /// <summary>Signals the loop to exit at the next safe point.</summary>
    void RequestExit();

    /// <summary>
    /// Tears the game down, executing every <see cref="TeardownPhase"/> in ascending
    /// order. Idempotent.
    /// </summary>
    void Shutdown();
}
