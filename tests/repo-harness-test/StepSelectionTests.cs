using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Results;
using RepoHarness.Core.Runners;

namespace RepoHarness.Tests;

/// <summary>
/// Which of an action's steps a run runs: every step that is not manual, the steps a runner names, or the
/// manual steps the command line names - each with what it needs, in the order the file declares them -
/// and every way of naming a step that could not run refused, naming what could.
/// </summary>
public sealed class StepSelectionTests
{
    private const string Action = """
        name: corpus
        steps:
          - name: fetch
            run: |
              git status
          - name: build
            needs: [fetch]
            run: |
              cmake --build build
          - name: prepare
            manual: true
            successPattern: '^prepared'
            run: |
              python3 prepare.py
          - name: bench
            manual: true
            needs: [build, prepare]
            successPattern: '^markdown : '
            run: |
              python3 bench.py
          - name: profile
            manual: true
            successPattern: '^profiled'
            run: |
              python3 profile.py
        """;

    /// <summary>A run that names no step runs every step that is not manual, and lists the manual ones it left out.</summary>
    [Fact]
    public void APlainRun_RunsEveryStepThatIsNotManual()
    {
        var selected = StepSelection.Default.Apply("corpus", Parse(Action));

        Assert.Equal(["fetch", "build"], selected.File.Steps.Select(step => step.Name));
        Assert.Equal(["prepare", "bench", "profile"], selected.Unselected);
        Assert.Empty(selected.Manual);
    }

    /// <summary>
    /// A manual step named on the command line runs alone but for what it needs, however deep - here the
    /// step it needs needs another - in the order the file declares them, not the order they are named.
    /// </summary>
    [Fact]
    public void AManualStepNamed_RunsWithWhatItNeeds_InTheOrderDeclared()
    {
        var selected = new StepSelection { ManualSteps = ["bench"] }.Apply("corpus", Parse(Action));

        Assert.Equal(["fetch", "build", "prepare", "bench"], selected.File.Steps.Select(step => step.Name));
        Assert.Equal(["profile"], selected.Unselected);
        Assert.Equal(["prepare", "bench"], selected.Manual);
    }

    /// <summary>A manual step that needs nothing runs with none of the steps a plain run runs.</summary>
    [Fact]
    public void AManualStepThatNeedsNothing_RunsAlone()
    {
        var selected = new StepSelection { ManualSteps = ["profile"] }.Apply("corpus", Parse(Action));

        Assert.Equal(["profile"], selected.File.Steps.Select(step => step.Name));
        Assert.Equal(["fetch", "build", "prepare", "bench"], selected.Unselected);
    }

    /// <summary>A runner's own steps run in place of a plain run's, each with what it needs, manual or not.</summary>
    [Fact]
    public void ARunnersOwnSteps_RunInPlaceOfAPlainRuns()
    {
        var selected = new StepSelection { RunnerSteps = ["profile", "build"] }.Apply("corpus", Parse(Action));

        Assert.Equal(["fetch", "build", "profile"], selected.File.Steps.Select(step => step.Name));
        Assert.Equal(["profile"], selected.Manual);
    }

    /// <summary>What the command line names wins over what the runner names: it is the more particular ask.</summary>
    [Fact]
    public void ManualStepsNamed_WinOverTheRunnersOwn()
    {
        var selected = new StepSelection { RunnerSteps = ["build"], ManualSteps = ["profile"] }.Apply("corpus", Parse(Action));

        Assert.Equal(["profile"], selected.File.Steps.Select(step => step.Name));
    }

    /// <summary>A name the action does not declare is refused, listing the manual steps it does.</summary>
    [Fact]
    public void AManualStepTheActionLacks_IsRefused_ListingTheManualSteps()
    {
        var refusal = Assert.Throws<HarnessException>(
            () => new StepSelection { ManualSteps = ["benhc"] }.Apply("corpus", Parse(Action)));

        Assert.Equal(HarnessExit.UsageError, refusal.ExitCode);
        Assert.Contains("--manual-step names 'benhc'", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("its manual steps are prepare, bench, profile", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>A step that is not manual is refused by --manual-step: every plain run runs it already.</summary>
    [Fact]
    public void AStepThatIsNotManual_IsRefusedByManualStep()
    {
        var refusal = Assert.Throws<HarnessException>(
            () => new StepSelection { ManualSteps = ["build"] }.Apply("corpus", Parse(Action)));

        Assert.Equal(HarnessExit.UsageError, refusal.ExitCode);
        Assert.Contains("--manual-step names 'build', which is not manual", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("runner 'corpus' that names no step runs it already", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>A runner naming a step its action lacks is a configuration problem, refused naming every step there is.</summary>
    [Fact]
    public void ARunnerNamingAStepTheActionLacks_IsRefused_ListingTheSteps()
    {
        var refusal = Assert.Throws<HarnessException>(
            () => new StepSelection { RunnerSteps = ["benchmark"] }.Apply("corpus", Parse(Action)));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Contains("Runner 'corpus' names 'benchmark' under steps", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("It declares fetch, build, prepare, bench, profile", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>An action whose every step is manual gives a plain run nothing to run, and that is refused rather than passed.</summary>
    [Fact]
    public void AnActionOfManualStepsAlone_RefusesAPlainRun()
    {
        var refusal = Assert.Throws<HarnessException>(() => StepSelection.Default.Apply("corpus", Parse("""
            name: corpus
            steps:
              - name: bench
                manual: true
                successPattern: '^done'
                run: |
                  python3 bench.py
            """)));

        Assert.Equal(HarnessExit.UsageError, refusal.ExitCode);
        Assert.Contains("would run nothing. Name one with --manual-step: bench", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>Names are read as --legs reads them: repeated, or joined by commas, each once.</summary>
    [Fact]
    public void Names_AreReadRepeatedOrCommaJoined_EachOnce()
    {
        Assert.Equal(["a", "b", "c"], StepSelection.Names(["a,b", " c ", "a"]));
        Assert.Empty(StepSelection.Names(null));
    }

    /// <summary>Given and naming nothing - an unset variable - is refused, not read as left out.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(" , ")]
    public void ManualStepNamingNothing_IsRefused(string value)
    {
        var refusal = Assert.Throws<HarnessException>(() => StepSelection.Names([value]));

        Assert.Equal(HarnessExit.UsageError, refusal.ExitCode);
        Assert.Contains("--manual-step was given no step name", refusal.Message, StringComparison.Ordinal);
    }

    private static ActionFile Parse(string text)
        => new ActionFileParser(
                new PhysicalFileSystem(FilePermissionsFactory.Create()),
                new ConsoleHarnessOutput(new StringWriter(), new StringWriter(), verbose: false),
                new HostPlatform())
            .Parse("actions/corpus/corpus.yml", text);
}
