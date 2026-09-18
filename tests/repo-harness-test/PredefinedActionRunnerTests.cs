using RepoHarness.Core.Configuration;
using RepoHarness.Core.Results;
using RepoHarness.Core.Runners;

namespace RepoHarness.Tests;

/// <summary>
/// The two predefined actions an action file may name, and what each of them settles before the
/// first program runs.
/// </summary>
public sealed class PredefinedActionRunnerTests
{
    /// <summary>
    /// The values arrive resolved, and this puts them where the steps can see them. Resolution used
    /// to happen here too, from the file's own defaults only — which is how the environment and the
    /// run lines came to disagree about what an input is worth.
    /// </summary>
    [Fact]
    public async Task ReadInputs_PutsTheResolvedInputsWhereItsStepsCanSeeThem()
    {
        var file = File("corpus.yaml", [new ActionInput("corpus", "real-examples/c", Required: false, null)]);

        var result = await Runner().PerformAsync(
            file,
            "/tree",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["corpus"] = "real-examples/c" },
            TestContext.Current.CancellationToken);

        Assert.Equal("real-examples/c", result.Environment[PredefinedActionRunner.InputPrefix + "CORPUS"]);
        Assert.Equal(["read (harness/read-inputs)"], result.Performed);
    }

    /// <summary>An input nothing resolved reaches no variable, rather than an empty one.</summary>
    [Fact]
    public async Task AnInputNothingResolved_IsSimplyAbsent()
    {
        var file = File("corpus.yaml", [new ActionInput("corpus", Default: null, Required: false, null)]);

        var result = await Runner().PerformAsync(
            file,
            "/tree",
            new Dictionary<string, string>(StringComparer.Ordinal),
            TestContext.Current.CancellationToken);

        Assert.Empty(result.Environment);
    }

    [Fact]
    public async Task Checkout_ConfirmsTheTreeIsTheCommitTheFileNames()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);

        var file = Checkout("HEAD");

        var result = await Runner(harness).PerformAsync(file, temp.Path, NoInputs, cancellationToken);

        Assert.Equal(["fetch (harness/checkout)"], result.Performed);
    }

    [Fact]
    public async Task Checkout_RefusesATreeAtAnotherCommit_RatherThanMovingIt()
    {
        // It confirms rather than moves. The tree a leg runs against is the one sync created and the
        // build compiled; moving it here would change the sources under a run that has already built
        // them, which is exactly the failure the input fingerprinting exists to catch.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);

        var first = await harness.GitClient.RunAsync(temp.Path, ["rev-parse", "HEAD"], cancellationToken: cancellationToken);
        await harness.CommitAllAsync(temp.Path, "a second commit", cancellationToken);

        var refusal = await Assert.ThrowsAsync<HarnessException>(
            () => Runner(harness).PerformAsync(Checkout(first.StandardOutput.Trim()), temp.Path, NoInputs, cancellationToken));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Contains("sync the leg to that commit first", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Checkout_RefusesAReferenceThatNamesNoCommit()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);

        var refusal = await Assert.ThrowsAsync<HarnessException>(
            () => Runner(harness).PerformAsync(Checkout("no-such-ref"), temp.Path, NoInputs, cancellationToken));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Contains("names no commit", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStepThatIsNeitherAction_IsNotPerformedHere()
    {
        // A step carrying a run block is a child process, and this runs none: performing it here as
        // well would run it twice.
        var file = new ActionFile(
            "corpus.yaml",
            "corpus",
            null,
            [],
            [new ActionStep { Name = "measure", Uses = PredefinedAction.None }]);

        var result = await Runner().PerformAsync(file, "/tree", NoInputs, TestContext.Current.CancellationToken);

        Assert.Empty(result.Performed);
    }

    /// <summary>An action declaring nothing, for the checkout tests that care about none of this.</summary>
    private static readonly Dictionary<string, string> NoInputs = new(StringComparer.Ordinal);

    private static PredefinedActionRunner Runner(HarnessFactory? harness = null)
    {
        var factory = harness ?? new HarnessFactory();

        return new PredefinedActionRunner(factory.GitClient, factory.Output);
    }

    private static ActionFile File(string path, IReadOnlyList<ActionInput> inputs)
        => new(path, "corpus", null, inputs, [new ActionStep { Name = "read", Uses = PredefinedAction.ReadInputs }]);

    private static ActionFile Checkout(string reference)
        => new(
            "corpus.yaml",
            "corpus",
            null,
            [],
            [new ActionStep { Name = "fetch", Uses = PredefinedAction.Checkout, Reference = reference }]);
}
