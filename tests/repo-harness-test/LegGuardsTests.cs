using RepoHarness.Core.Execution;

namespace RepoHarness.Tests;

/// <summary>
/// One scope watches a span of work for every verb. What it must never do is turn "nobody looked"
/// into "nothing found", and what it must never do to a verb that asked for nothing is fail it.
/// </summary>
public sealed class LegGuardsTests
{
    private static readonly PhaseResult Passed = new(
        "leg", "step", ExitCode: 0, Stalled: false, StallSeconds: 0, Witnessed: null,
        Duration: TimeSpan.Zero, ClockDrift: TimeSpan.Zero, ClockStepped: false,
        Timings: [], LogFile: "log", Output: string.Empty);

    /// <summary>
    /// A set that is empty because nothing the build reads is tracked is not a set nobody could
    /// establish. Read as unmeasured it would fail every build in a repository that happens not to
    /// track the language it builds \u2014 which is what this did before a test existed for it.
    /// </summary>
    [Fact]
    public void AnEmptySetWithNoReason_ContributesNothing()
    {
        var report = new LegGuardReport(Inputs: null, Contention: null);

        Assert.Equal(LegVerdict.Passed, report.Decide("leg", [ReachedVerdict.Of(LegVerdict.Passed)]).Verdict);
    }

    /// <summary>
    /// A set that could not be established is the opposite, and must not read as a tree that held
    /// still.
    /// </summary>
    [Fact]
    public void ASetNobodyCouldEstablish_IsUnmeasured_NotClean()
    {
        var report = new LegGuardReport(
            new InputComparison(InputChange.Unmeasured, [], "git could not be asked"),
            Contention: null);

        Assert.Equal(LegVerdict.Unmeasured, report.Decide("leg", [ReachedVerdict.Of(LegVerdict.Passed)]).Verdict);
    }

    /// <summary>
    /// The worst of everything that decided, whichever of them it was: a verb holds its work to
    /// things a phase knows nothing about, and a build's witness is one of them.
    /// </summary>
    [Fact]
    public void TheWorstOfWhatEveryoneSaw_Wins_WhoeverSawIt()
    {
        var report = new LegGuardReport(
            new InputComparison(InputChange.Moved, ["src/a.c"], "src/a.c moved"),
            Contention: null);

        var decided = report.Decide("leg", [ReachedVerdict.Of(LegVerdict.Passed)]);

        Assert.NotEqual(LegVerdict.Passed, decided.Verdict);
        Assert.Contains("src/a.c", decided.Detail, StringComparison.Ordinal);
    }

    /// <summary>A span that decided nothing has no verdict, and says so rather than inventing one.</summary>
    [Fact]
    public void ASpanThatDecidedNothing_IsRefused()
        => Assert.Throws<ArgumentException>(() => new LegGuardReport(null, null).Decide("leg", []));
}
