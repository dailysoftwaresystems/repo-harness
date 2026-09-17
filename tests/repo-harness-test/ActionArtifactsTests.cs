using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Runners;

namespace RepoHarness.Tests;

/// <summary>
/// What an action's steps write while they run, what survives, and where a grouped action lives.
/// A step that can neither hand anything to the next step nor prove it produced anything is a
/// wrapper around a program, and a wrapper that reports success without doing what it was asked is
/// indistinguishable from one that never ran.
/// </summary>
public sealed class ActionArtifactsTests
{
    private const string RunId = "20260917-100000-0a1b2c3d";
    private const string Leg = "lin-gcc-release";

    /// <summary>
    /// Both directories are keyed by the run, and neither is written into directly. Two runs of one
    /// action on one machine — two legs, or a retry — would otherwise share a directory, and the
    /// second would measure what the first left behind.
    /// </summary>
    [Fact]
    public void AnActionsDirectories_AreKeyedByTheRun_AndSitUnderTheActionsOwnDirectory()
    {
        var build = HarnessLayout.ActionBuildRelative("roundtrip", RunId, Leg).Replace('\\', '/');
        var artifacts = HarnessLayout.ActionArtifactsRelative("roundtrip", RunId, Leg).Replace('\\', '/');

        // The leg too: one run places many legs and they share its id, so keyed by the run
        // alone two legs running one action on one machine would write into one directory.
        Assert.Equal($".harness-config/runner/actions/roundtrip/build/{RunId}/{Leg}", build);
        Assert.Equal($".harness-config/runner/actions/roundtrip/artifacts/{RunId}/{Leg}", artifacts);
    }

    /// <summary>
    /// A grouped action keeps every segment above its own. Taking only the leaf would send a step
    /// that writes into its action's directory to one at the top of the actions tree that nothing
    /// created.
    /// </summary>
    [Fact]
    public void AGroupedActionsDirectories_KeepTheirGrouping()
    {
        var build = HarnessLayout
            .ActionBuildRelative("real-examples/c/probe-nest", RunId, Leg)
            .Replace('\\', '/');

        Assert.Equal(
            $".harness-config/runner/actions/real-examples/c/probe-nest/build/{RunId}/{Leg}",
            build);
    }

    /// <summary>
    /// A step of three commands is three phases sharing one directory, because it is one step's
    /// work. Keyed by the phase name instead, each command would write somewhere the next could not
    /// find.
    /// </summary>
    [Fact]
    public void EveryPhaseOfOneStep_CarriesTheStepsOwnName()
    {
        var step = new ActionStep
        {
            Name = "pack",
            Commands =
            [
                new ActionCommand("git --version", 1, ["git", "--version"]),
                new ActionCommand("git status", 2, ["git", "status"]),
            ],
        };

        var phases = step.ToPhases("roundtrip");

        Assert.All(phases, phase => Assert.Equal("pack", phase.StepName));

        // And the phases are still told apart in the ledger and in their log file names.
        Assert.Equal(["pack (1/2)", "pack (2/2)"], phases.Select(phase => phase.Name));
    }

    /// <summary>
    /// The outputs are checked against the last command, for the reason the success pattern is:
    /// that is the one whose finishing means the step did its work.
    /// </summary>
    [Fact]
    public void ADeclaredOutput_IsCheckedAgainstTheCommandWhoseFinishingMeansTheStepIsDone()
    {
        var step = new ActionStep
        {
            Name = "pack",
            Outputs = ["payload.txt"],
            Commands =
            [
                new ActionCommand("git --version", 1, ["git", "--version"]),
                new ActionCommand("git status", 2, ["git", "status"]),
            ],
        };

        var phases = step.ToPhases("roundtrip");

        Assert.Empty(phases[0].Outputs);
        Assert.Equal(["payload.txt"], phases[1].Outputs);
    }

    /// <summary>
    /// Exited zero having written nothing it said it would. An exit code alone cannot tell that
    /// apart from work that was done, which is the whole reason a step may declare what it makes.
    /// </summary>
    [Fact]
    public void AStepThatProducedNothingItDeclared_IsUnwitnessed_NotPassed()
    {
        var result = Finished() with { MissingOutputs = ["payload.txt"] };

        Assert.False(result.Passed);
        Assert.Equal(LegVerdict.Unwitnessed, result.Verdict().Verdict);
        Assert.Contains("without producing payload.txt", result.Verdict().Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A pattern that never matched and a file that was never written are both "exited zero having
    /// done nothing", and a reader told only the first goes looking in the output.
    /// </summary>
    [Fact]
    public void AMissingOutputAndAnUnmatchedPattern_AreToldApart()
    {
        var noPattern = (Finished() with { Witnessed = false }).Verdict().Detail;
        var noOutput = (Finished() with { MissingOutputs = ["payload.txt"] }).Verdict().Detail;

        Assert.Contains("success pattern never matched", noPattern, StringComparison.Ordinal);
        Assert.DoesNotContain("success pattern", noOutput, StringComparison.Ordinal);
    }

    /// <summary>A step that produced everything it named is simply passed.</summary>
    [Fact]
    public void AStepThatProducedWhatItDeclared_Passes()
    {
        var result = Finished();

        Assert.True(result.Passed);
        Assert.Equal(LegVerdict.Passed, result.Verdict().Verdict);
    }

    /// <summary>
    /// An output is something the step wrote where the harness put it. A path climbing out of that
    /// directory names a file the harness did not create, cannot clean up, and would move somewhere
    /// else when the step asked to persist it.
    /// </summary>
    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("a/../../escape.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("./payload.txt")]
    public void AnOutputThatLeavesTheStepsOwnDirectory_IsRefusedWhenTheFileIsRead(string output)
    {
        var factory = new HarnessFactory();
        var parser = new ActionFileParser(factory.FileSystem, factory.Output, factory.Platform);

        var refusal = Assert.Throws<HarnessException>(() => parser.Parse("probe.yml", $"""
            name: probe
            steps:
              - name: pack
                outputs:
                  - {output}
                run: |
                  git --version
            """));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Contains("inside the step's own directory", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>Both new keys are read, and persistence is off unless the step asks for it.</summary>
    [Fact]
    public void AStepDeclaringOutputsAndPersistence_IsReadAsBoth()
    {
        var factory = new HarnessFactory();
        var parser = new ActionFileParser(factory.FileSystem, factory.Output, factory.Platform);

        var file = parser.Parse("probe.yml", """
            name: probe
            steps:
              - name: pack
                outputs:
                  - payload.txt
                  - report/summary.json
                persist: true
                run: |
                  git --version
              - name: measure
                run: |
                  git --version
            """);

        Assert.Equal(["payload.txt", "report/summary.json"], file.Steps[0].Outputs);
        Assert.True(file.Steps[0].Persist);

        // Off by default: the build directory is emptied when the action ends, and anything a later
        // run needs has to say so.
        Assert.False(file.Steps[1].Persist);
        Assert.Empty(file.Steps[1].Outputs);
    }

    /// <summary>
    /// The three names a step uses to reach its own directory, the run's, and what survives.
    /// </summary>
    [Fact]
    public void AStepCanNameItsOwnDirectory_TheRunsAndTheOneThatSurvives()
    {
        var paths = new LegPaths("/tree", "/tree/build/v")
        {
            ActionBuild = "/tree/a/build/run1",
            ActionArtifacts = "/tree/a/artifacts/run1",
            StepBuild = "/tree/a/build/run1/pack",
        };

        Assert.Equal("/tree/a/build/run1/pack", LegPathNames.Expand("{stepBuild}", paths, "step"));
        Assert.Equal("/tree/a/build/run1", LegPathNames.Expand("{actionBuild}", paths, "step"));
        Assert.Equal("/tree/a/artifacts/run1", LegPathNames.Expand("{actionArtifacts}", paths, "step"));
    }

    /// <summary>
    /// A runner declaring phases directly owns no action directory, and saying so is better than
    /// expanding to a path that would be created and never cleaned.
    /// </summary>
    [Fact]
    public void ARunnerWithNoActionFile_IsToldItHasNoActionDirectory()
    {
        var paths = new LegPaths("/tree", "/tree/build/v");

        var refusal = Assert.Throws<HarnessException>(
            () => LegPathNames.Expand("{stepBuild}", paths, "phase"));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Contains("declares phases rather than an action", refusal.Message, StringComparison.Ordinal);
    }

    private static PhaseResult Finished() => new(
        "leg", "pack", ExitCode: 0, Stalled: false, StallSeconds: 0, Witnessed: null,
        Duration: TimeSpan.Zero, ClockDrift: TimeSpan.Zero, ClockStepped: false,
        Timings: [], LogFile: "log", Output: string.Empty);
}
