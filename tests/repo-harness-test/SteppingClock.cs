namespace RepoHarness.Tests;

/// <summary>
/// A wall clock a test can step, as the measured host's steps by about 25 seconds every few seconds,
/// without moving this machine's own: the system's time, plus however far it has been stepped.
/// </summary>
/// <remarks>
/// Only the wall clock moves. The monotonic clock is left as the system's, which is what a stepped
/// host looks like: the time of day jumps and the time that has passed does not.
/// </remarks>
internal sealed class SteppingClock : TimeProvider
{
    /// <summary>How far this clock has been stepped from the system's.</summary>
    public TimeSpan Stepped { get; private set; }

    /// <summary>Steps the clock by <paramref name="step"/>, forward or back.</summary>
    /// <param name="step">How far.</param>
    public void Step(TimeSpan step) => Stepped += step;

    /// <inheritdoc/>
    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Stepped;
}
