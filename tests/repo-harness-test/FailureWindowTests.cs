using RepoHarness.Core.Runners;

namespace RepoHarness.Tests;

/// <summary>The failing unit's own execution window, and what counts as recorded inside it.</summary>
public sealed class FailureWindowTests
{
    private static readonly DateTimeOffset Origin = new(2026, 9, 16, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Extract_TakesTheUnitsOwnOutput_FromItsStartToItsVerdict()
    {
        var window = FailureWindow.Extract(Output(), "corpus/case-7");

        Assert.NotNull(window);
        Assert.Equal(At(10), window.Start);
        Assert.Equal(At(40), window.End);

        // Another leg writing into the same stream between those edges is not evidence about this
        // unit, so its lines are not part of the window.
        Assert.Equal(
            ["case-7 starting", "case-7 working", "case-7 failed"],
            window.Lines.Select(line => line.Text).ToArray());
    }

    [Fact]
    public void Extract_ReturnsNull_WhenTheUnitNeverStartedOrNeverReachedAVerdict()
    {
        Assert.Null(FailureWindow.Extract(Output(), "corpus/case-99"));

        Assert.Null(FailureWindow.Extract(
            [
                new RunOutputLine(At(10), "corpus/case-7", RunOutputKind.Start, "case-7 starting"),
                new RunOutputLine(At(20), "corpus/case-7", RunOutputKind.Output, "case-7 working"),
            ],
            "corpus/case-7"));
    }

    [Fact]
    public void CountSteps_CountsOnlyStepsRecordedInsideTheWindow()
    {
        // The measurement this exists for: a once-per-run sample charged genuine-looking failures to
        // the tool under test on a loaded machine and excused them on a quiet one, from the same
        // configuration on the same day, because a sample says nothing about the interval that
        // actually failed.
        var window = FailureWindow.Extract(Output(), "corpus/case-7");

        Assert.NotNull(window);

        RunStep[] steps =
        [
            new(At(5), TimeSpan.FromSeconds(30), "before the unit started"),
            new(At(15), TimeSpan.FromSeconds(30), "inside the window"),
            new(At(35), TimeSpan.FromSeconds(30), "inside the window"),
            new(At(60), TimeSpan.FromSeconds(30), "after the verdict"),
        ];

        Assert.Equal(2, window.CountSteps(steps, minStepSeconds: 5));
        Assert.False(window.Contains(steps[0]));
        Assert.False(window.Contains(steps[3]));
    }

    [Fact]
    public void CountSteps_IgnoresAStepShorterThanTheBound()
    {
        var window = FailureWindow.Extract(Output(), "corpus/case-7");

        Assert.NotNull(window);

        RunStep[] steps =
        [
            new(At(15), TimeSpan.FromSeconds(1), "too short to mean anything"),
            new(At(35), TimeSpan.FromSeconds(30), "long enough"),
        ];

        Assert.Equal(1, window.CountSteps(steps, minStepSeconds: 5));
    }

    /// <summary>The edges of the window sit exactly on the start and verdict lines.</summary>
    [Fact]
    public void Contains_IncludesTheEdgesThemselves()
    {
        var window = FailureWindow.Extract(Output(), "corpus/case-7");

        Assert.NotNull(window);
        Assert.True(window.Contains(new RunStep(At(10), TimeSpan.FromSeconds(30), "at the start")));
        Assert.True(window.Contains(new RunStep(At(40), TimeSpan.FromSeconds(30), "at the verdict")));
    }

    internal static DateTimeOffset At(int second) => Origin.AddSeconds(second);

    internal static IReadOnlyList<RunOutputLine> Output() =>
    [
        new(At(0), null, RunOutputKind.Output, "run starting"),
        new(At(5), "corpus/case-1", RunOutputKind.Start, "case-1 starting"),
        new(At(10), "corpus/case-7", RunOutputKind.Start, "case-7 starting"),
        new(At(20), "corpus/case-7", RunOutputKind.Output, "case-7 working"),
        new(At(25), "corpus/case-1", RunOutputKind.Output, "case-1 working"),
        new(At(40), "corpus/case-7", RunOutputKind.Verdict, "case-7 failed"),
        new(At(50), "corpus/case-1", RunOutputKind.Verdict, "case-1 passed"),
    ];
}
