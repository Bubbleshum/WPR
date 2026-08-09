using System;

namespace WPR.Abstractions.Timing;

/// <summary>Elapsed and total time for a single frame.</summary>
public readonly record struct TimeSnapshot(TimeSpan Total, TimeSpan Elapsed);

/// <summary>
/// The game-loop clock. Backs the XNA <c>GameTime</c> reimplementation (Stage 5c).
/// A backend/platform provider implements it; the host advances it once per frame.
/// </summary>
public interface ITimer
{
    bool IsRunning { get; }

    /// <summary>Advances the clock and returns the timing for this frame.</summary>
    TimeSnapshot Tick();

    void Start();
    void Stop();
    void Reset();
}
