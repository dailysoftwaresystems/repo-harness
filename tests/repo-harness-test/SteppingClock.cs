using System.Diagnostics;

namespace RepoHarness.Tests;

/// <summary>
/// A wall clock that steps only when a test steps it, as the measured host's steps by about 25 seconds
/// every few seconds, without moving this machine's own: it moves as the monotonic clock does, plus
/// however far it has been stepped.
/// </summary>
/// <remarks>
/// Only the stepping is the wall clock's. The monotonic clock is left as the system's, which is what a
/// stepped host looks like: the time of day jumps and the time that has passed does not.
/// <para>
/// It is taken off the monotonic clock rather than off this machine's wall clock so that a test steps it
/// and nothing else does. A virtual machine's wall clock is not always honest - measured in WSL it
/// stepped back 24.8 seconds and forward again every five seconds - and a phase timed against one is
/// recorded as having spanned a step, which starts the next build from clean for that reason. A test of
/// any other reason to start from clean would then fail over a reason it never meant to check, and a
/// test of the step itself could have its own step cancelled out by the host's.
/// </para>
/// </remarks>
internal sealed class SteppingClock : TimeProvider
{
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;

    private readonly long _from = Stopwatch.GetTimestamp();

    /// <summary>How far this clock has been stepped from the time that has passed.</summary>
    public TimeSpan Stepped { get; private set; }

    /// <summary>Steps the clock by <paramref name="step"/>, forward or back.</summary>
    /// <param name="step">How far.</param>
    public void Step(TimeSpan step) => Stepped += step;

    /// <inheritdoc/>
    public override DateTimeOffset GetUtcNow() => _started + Stopwatch.GetElapsedTime(_from) + Stepped;
}
