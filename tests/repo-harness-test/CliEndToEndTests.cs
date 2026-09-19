using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Git;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Results;
using RepoHarness.Core.Runs;
using RepoHarness.Core.Tools;

namespace RepoHarness.Tests;

/// <summary>
/// End-to-end coverage of the built CLI: parsing, wiring and exit codes.
/// </summary>
public sealed partial class CliEndToEndTests
{
    [Fact]
    public async Task Help_ListsEveryCommand()
    {
        var result = await CliRunner.RunAsync(["--help"], TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);

        string[] commands =
        [
            "init", "verify-git", "create-worktree", "delete-worktree", "list-worktree",
            "check-root-litter", "check-anchor-citations", "fix-line-endings", "check-ci-legs",
            "legs", "install-missing-tools", "sync", "build", "test", "run", "host-exec", "help",
        ];

        foreach (var command in commands)
        {
            Assert.Contains(command, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// <c>init</c> and <c>install-missing-tools</c> share one provisioning engine, and either of them
    /// can stop to ask for a superuser password. The flag that refuses to ask therefore has to reach
    /// both: declared on one command alone, the other would turn it away as an option it does not know.
    /// </summary>
    [Theory]
    [InlineData("init")]
    [InlineData("install-missing-tools")]
    public async Task NoPrompt_IsOfferedByEveryCommandThatCanProvision(string command)
    {
        var result = await CliRunner.RunAsync([command, "--help"], TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--no-prompt", result.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// One sync reaches every host, and taking a directory over deletes what the source does not
    /// have. Saying which machine that applies to is the whole point, so the flag cannot be a bare
    /// yes that a reader could take to mean "this one" while it means "all of them".
    /// </summary>
    [Fact]
    public async Task Adopt_RefusesToBeABareYes_AndMustNameItsHosts()
    {
        // In a directory of its own, deliberately. Without one the real command line runs with this
        // process's working directory, which is inside this repository: were the option's arity ever
        // to allow none, this test would sync against whatever hosts this tree declares.
        using var temp = new TempDirectory();

        var result = await CliRunner.RunAsync(
            ["sync", "--adopt"],
            TestContext.Current.CancellationToken,
            workingDirectory: temp.Path);

        Assert.Equal(HarnessExit.UsageError, result.ExitCode);
        Assert.Contains("--adopt", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("argument", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Version_IsReportedWithoutBuildMetadata()
    {
        // The release pipelines compare this with the version they published. The SDK
        // appends the commit by default, and then the two would never match.
        var result = await CliRunner.RunAsync(["--version"], TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Matches(BareVersionPattern(), result.TrimmedOutput);
    }

    [Fact]
    public async Task AnUnknownCommand_IsAUsageError_ReportedOnStandardError()
    {
        var result = await CliRunner.RunAsync(["definitely-not-a-command"], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, result.ExitCode);
        Assert.NotEmpty(result.StandardError.Trim());
    }

    [Fact]
    public async Task AMissingDirectory_IsAUsageError()
    {
        using var temp = new TempDirectory();

        var result = await CliRunner.RunAsync(
            ["list-worktree", "-C", temp.Combine("absent")],
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, result.ExitCode);
        Assert.Contains("does not exist", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARelativeDirectory_IsResolvedAgainstTheWorkingDirectory()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = Directory.CreateDirectory(temp.Combine("repo")).FullName;
        await new HarnessFactory().InitializeGitRepositoryAsync(repository, cancellationToken);

        var result = await CliRunner.RunAsync(["verify-git", "-C", "repo"], cancellationToken, workingDirectory: temp.Path);

        Assert.Equal((int)VerifyGitStatus.Success, result.ExitCode);
        Assert.Contains(repository, result.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyGit_ReturnsNotARepository_OutsideOne()
    {
        using var temp = new TempDirectory();

        var result = await CliRunner.RunAsync(["verify-git", "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.Equal((int)VerifyGitStatus.NotAGitRepository, result.ExitCode);
    }

    [Fact]
    public async Task VerifyGit_Succeeds_InsideARepository()
    {
        using var temp = new TempDirectory();
        await new HarnessFactory().InitializeGitRepositoryAsync(temp.Path, TestContext.Current.CancellationToken);

        var result = await CliRunner.RunAsync(["verify-git", "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.Equal((int)VerifyGitStatus.Success, result.ExitCode);
        Assert.Contains("verify-git: OK", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyGit_UsesTheWorkingDirectory_WhenNoDirectoryIsGiven()
    {
        using var temp = new TempDirectory();

        var result = await CliRunner.RunAsync(["verify-git"], TestContext.Current.CancellationToken, workingDirectory: temp.Path);

        Assert.Equal((int)VerifyGitStatus.NotAGitRepository, result.ExitCode);
    }

    [Fact]
    public async Task Init_CreatesTheLayout_AndIsIdempotent()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await new HarnessFactory().InitializeGitRepositoryAsync(temp.Path, cancellationToken);

        var first = await CliRunner.RunAsync(["init", "-C", temp.Path], cancellationToken);

        Assert.Equal(HarnessExit.Success, first.ExitCode);
        Assert.True(File.Exists(HarnessFactory.ConfigPath(temp.Path)));

        var second = await CliRunner.RunAsync(["init", "-C", temp.Path], cancellationToken);

        Assert.Equal(HarnessExit.Success, second.ExitCode);
        Assert.Contains("already present", second.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Init_Refuses_OutsideAGitRepository()
    {
        using var temp = new TempDirectory();

        var result = await CliRunner.RunAsync(["init", "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Refused, result.ExitCode);
        Assert.False(Directory.Exists(temp.Combine(".harness-config")));
    }

    [Fact]
    public async Task Failures_AreReportedOnStandardError_SoOutputStaysPipeable()
    {
        using var temp = new TempDirectory();

        var result = await CliRunner.RunAsync(["verify-git", "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.Contains("FAIL", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("FAIL", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Worktrees_CanBeCreatedListedAndDeleted()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await PrepareRepositoryAsync(temp);

        var created = await CliRunner.RunAsync(["create-worktree", "wt", "-C", temp.Path], cancellationToken);
        Assert.Equal(HarnessExit.Success, created.ExitCode);
        Assert.Contains("created worktree 'wt'", created.StandardOutput, StringComparison.Ordinal);
        Assert.True(Directory.Exists(HarnessFactory.WorktreePath(temp.Path, "wt")));

        var listed = await CliRunner.RunAsync(["list-worktree", "-C", temp.Path], cancellationToken);
        Assert.Equal(HarnessExit.Success, listed.ExitCode);
        Assert.Contains("list-worktree: wt", listed.StandardOutput, StringComparison.Ordinal);

        var deleted = await CliRunner.RunAsync(["delete-worktree", "wt", "-C", temp.Path], cancellationToken);
        Assert.Equal(HarnessExit.Success, deleted.ExitCode);
        Assert.False(Directory.Exists(HarnessFactory.WorktreePath(temp.Path, "wt")));

        var empty = await CliRunner.RunAsync(["list-worktree", "-C", temp.Path], cancellationToken);
        Assert.Contains("no worktrees", empty.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateWorktree_WithRandom_ReportsTheNameItChose()
    {
        using var temp = new TempDirectory();
        await PrepareRepositoryAsync(temp);

        var result = await CliRunner.RunAsync(["create-worktree", "--random", "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, result.ExitCode);
        var match = CreatedNamePattern().Match(result.StandardOutput);
        Assert.True(match.Success, result.StandardOutput);
        Assert.True(Directory.Exists(HarnessFactory.WorktreePath(temp.Path, match.Groups["name"].Value)));
    }

    [Fact]
    public async Task CreateWorktree_WithNeitherANameNorRandom_IsAUsageError()
    {
        using var temp = new TempDirectory();
        await PrepareRepositoryAsync(temp);

        var result = await CliRunner.RunAsync(["create-worktree", "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, result.ExitCode);
    }

    [Fact]
    public async Task CreateWorktree_WithANameAndRandom_IsAUsageError_EvenBeforeInit()
    {
        using var temp = new TempDirectory();
        await new HarnessFactory().InitializeGitRepositoryAsync(temp.Path, TestContext.Current.CancellationToken);

        var result = await CliRunner.RunAsync(
            ["create-worktree", "wt", "--random", "-C", temp.Path],
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, result.ExitCode);
    }

    [Fact]
    public async Task ListWorktree_ReportsNotInitialised_BeforeInit()
    {
        using var temp = new TempDirectory();
        await new HarnessFactory().InitializeGitRepositoryAsync(temp.Path, TestContext.Current.CancellationToken);

        var result = await CliRunner.RunAsync(["list-worktree", "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.NotInitialized, result.ExitCode);
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("""{ "unknownSetting": 1 }""")]
    [InlineData("""{ "defaults": { "buildCores": 0 } }""")]
    public async Task AnInvalidConfiguration_IsReportedAsSuch(string json)
    {
        using var temp = new TempDirectory();
        await PrepareRepositoryAsync(temp);
        File.WriteAllText(HarnessFactory.ConfigPath(temp.Path), json);

        var result = await CliRunner.RunAsync(["list-worktree", "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.ConfigInvalid, result.ExitCode);
        Assert.Contains("config.json", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteWorktree_OfAWorktreeThatDoesNotExist_IsRefused()
    {
        using var temp = new TempDirectory();
        await PrepareRepositoryAsync(temp);

        var result = await CliRunner.RunAsync(["delete-worktree", "missing", "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Refused, result.ExitCode);
    }

    [Fact]
    public async Task DeleteWorktree_WithUncommittedWork_IsRefused_UntilForced()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await PrepareRepositoryAsync(temp);
        var path = HarnessFactory.WorktreePath(temp.Path, "wt");

        var created = await CliRunner.RunAsync(["create-worktree", "wt", "-C", temp.Path], cancellationToken);
        Assert.Equal(HarnessExit.Success, created.ExitCode);
        File.WriteAllText(Path.Combine(path, "notes.txt"), "never committed");

        var refused = await CliRunner.RunAsync(["delete-worktree", "wt", "-C", temp.Path], cancellationToken);

        // One line on standard error, naming what would be lost and the way past.
        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            "delete-worktree: FAIL - Worktree 'wt' was not deleted, because it has 1 uncommitted change(s) that would be lost: notes.txt (commit them to a branch, or run 'git stash -u'); fix that, or pass --force to delete it anyway.",
            Assert.Single(refused.StandardError.ReplaceLineEndings("\n").Trim().Split('\n')));
        Assert.True(File.Exists(Path.Combine(path, "notes.txt")), "The refused delete removed uncommitted work.");

        var forced = await CliRunner.RunAsync(["delete-worktree", "wt", "--force", "-C", temp.Path], cancellationToken);

        Assert.Equal(HarnessExit.Success, forced.ExitCode);
        Assert.False(Directory.Exists(path));
    }

    /// <summary>
    /// The defect that rode the layout change: a configuration one verb called valid and another
    /// refused. Both verbs read the same file through the same reader, so a spelling wrong enough
    /// for one is wrong for the other, and the run that finds out is never the first to say so.
    /// </summary>
    [Theory]
    [InlineData("corpus.yml")]
    [InlineData("../outside.yml")]
    [InlineData("a/../../outside.yml")]
    [InlineData("corpus/steps.yml")]
    [InlineData("corpus/nested/corpus.yml")]
    public async Task LegsAndRun_RefuseTheSameBadActionPath_WithTheSameExitCode(string action)
    {
        using var temp = new TempDirectory();
        await PrepareRepositoryAsync(temp);
        WriteRunner(temp, action);

        var legs = await CliRunner.RunAsync(["legs", "-C", temp.Path], TestContext.Current.CancellationToken);
        var run = await CliRunner.RunAsync(["run", "corpus", "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.ConfigInvalid, legs.ExitCode);
        Assert.Equal(legs.ExitCode, run.ExitCode);
    }

    /// <summary>
    /// The spelling is legal, so the configuration reader passes it; only the file system knows the
    /// action is not there. Both verbs still answer the same way, because both resolve the path
    /// before a leg is placed rather than leaving it to whichever one happened to open the file.
    /// </summary>
    [Fact]
    public async Task LegsAndRun_RefuseAnActionThatIsNotThere_WithTheSameExitCode()
    {
        using var temp = new TempDirectory();
        await PrepareRepositoryAsync(temp);
        WriteRunner(temp, "corpus/corpus.yml");

        var legs = await CliRunner.RunAsync(["legs", "-C", temp.Path], TestContext.Current.CancellationToken);
        var run = await CliRunner.RunAsync(["run", "corpus", "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.ConfigInvalid, legs.ExitCode);
        Assert.Equal(legs.ExitCode, run.ExitCode);
        Assert.Contains("corpus", legs.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Init_ScaffoldsTheActionsDirectory_WithAPlaceholderGitTracks()
    {
        using var temp = new TempDirectory();
        await PrepareRepositoryAsync(temp);

        var placeholder = Path.Combine(
            temp.Path,
            ".harness-config",
            "runner",
            "actions",
            ".gitkeep");

        Assert.True(File.Exists(placeholder), $"init left no placeholder at '{placeholder}'.");
    }

    [Theory]
    [InlineData("runners")]
    [InlineData("layout")]
    public async Task Help_PrintsTheDirectoryPerActionLayout(string topic)
    {
        var result = await CliRunner.RunAsync(["help", topic], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, result.ExitCode);
        Assert.Contains("actions/<name>/<name>.yml", result.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>Declares one runner naming <paramref name="action"/>, over the seeded config.</summary>
    private static void WriteRunner(TempDirectory temp, string action)
        => File.WriteAllText(
            HarnessFactory.ConfigPath(temp.Path),
            $$"""
            {
              "predefinedRunners": { "corpus": { "action": {{System.Text.Json.JsonSerializer.Serialize(action)}} } }
            }
            """);

    /// <summary>
    /// The line a migration's acceptance gate reads. A leg that reached no verdict used to be
    /// counted among the legs that passed, so a run where nothing ran at all printed
    /// "OK - n leg(s) passed" and exited 0 — which is the one number a gate compares.
    /// </summary>
    [Fact]
    public async Task ARunWhereALegReachedNoVerdict_IsNotReportedAsPassed()
    {
        using var temp = new TempDirectory();
        await PrepareRunnerAsync(temp);

        var result = await CliRunner.RunAsync(
            ["run", "probe", "--legs", "native,elsewhere", "-C", temp.Path],
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Incomplete, result.ExitCode);
        Assert.DoesNotContain("OK -", result.StandardOutput, StringComparison.Ordinal);

        // Both halves are named: how many did report, and which ones did not.
        Assert.Contains("1 of 2 leg(s) passed", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("elsewhere", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunWhereEveryLegReported_IsStillReportedAsPassed()
    {
        using var temp = new TempDirectory();
        await PrepareRunnerAsync(temp);

        var result = await CliRunner.RunAsync(
            ["run", "probe", "--legs", "native", "-C", temp.Path],
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, result.ExitCode);
        Assert.Contains("1 leg(s) passed", result.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// A run that did not succeed says why on its FAIL line whether or not --json was asked for.
    /// The JSON branch composed no message at all, so it printed "FAIL - " and nothing after the
    /// dash - and a host is always asked for JSON, so every failure on another machine read that
    /// way on the machine that sent it.
    /// </summary>
    [Fact]
    public async Task ARunThatDidNotSucceed_SaysWhyOnItsFailLine_WithOrWithoutJson()
    {
        using var temp = new TempDirectory();
        await PrepareRunnerAsync(temp);
        var token = TestContext.Current.CancellationToken;

        var table = await CliRunner.RunAsync(["run", "probe", "--legs", "native,elsewhere", "-C", temp.Path], token);
        var json = await CliRunner.RunAsync(["run", "probe", "--legs", "native,elsewhere", "--json", "-C", temp.Path], token);

        const string Expected = "run: FAIL - 1 of 2 leg(s) passed; 1 did no work: elsewhere";

        Assert.Equal(HarnessExit.Incomplete, table.ExitCode);
        Assert.Equal(HarnessExit.Incomplete, json.ExitCode);
        Assert.Contains(Expected, table.StandardError, StringComparison.Ordinal);
        Assert.Contains(Expected, json.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// The description names where worktrees go by default and that configuration may move them,
    /// rather than stating the default as the only place: a repository that sets worktrees.root
    /// was told, by the command it runs, somewhere its worktrees are not.
    /// </summary>
    [Fact]
    public async Task CreateWorktree_DescribesTheRootAsTheDefault_NotAsTheOnlyPlace()
    {
        var result = await CliRunner.RunAsync(["create-worktree", "--help"], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, result.ExitCode);
        Assert.Contains(WorktreeSettings.DefaultRoot, result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("worktrees.root", result.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// B2's whole point is that a green nobody earned must not be reported, and the JSON ledger is
    /// the machine-readable half of that — the half a gate actually parses.
    /// </summary>
    [Fact]
    public async Task TheJsonLedgerOfARunWithAnUnreportedLeg_SaysItIsNotComplete()
    {
        using var temp = new TempDirectory();
        await PrepareRunnerAsync(temp);

        var result = await CliRunner.RunAsync(
            ["run", "probe", "--legs", "native,elsewhere", "--json", "-C", temp.Path],
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Incomplete, result.ExitCode);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;

        // The document and the process agree, and both say the same thing the table said.
        Assert.Equal(HarnessExit.Incomplete, root.GetProperty("exitCode").GetInt32());
        Assert.True(root.GetProperty("passed").GetBoolean(), "nothing failed, so this is not a red run");
        Assert.False(root.GetProperty("complete").GetBoolean());
        Assert.False(root.GetProperty("cancelled").GetBoolean());

        var legs = root.GetProperty("legs").EnumerateArray().Select(leg => leg.GetProperty("leg").GetString()).ToList();
        Assert.Contains("native", legs);
        Assert.Contains("elsewhere", legs);
    }

    /// <summary>
    /// Asked for data, a run is answered with data whatever ended it. A selection no host can take
    /// answered with text, which the machine that dispatched the leg read as a host whose answer
    /// could not be read - where the host had said exactly why each leg could not run.
    /// </summary>
    [Fact]
    public async Task ARunNoHostCanTake_AnswersWithTheLedger_NamingEachLegsVerdict()
    {
        using var temp = new TempDirectory();
        await PrepareRunnerAsync(temp);

        var result = await CliRunner.RunAsync(
            ["run", "probe", "--legs", "elsewhere", "--json", "-C", temp.Path],
            TestContext.Current.CancellationToken);

        Assert.Equal(LegsExit.Unavailable, result.ExitCode);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;

        Assert.Equal(LegsExit.Unavailable, root.GetProperty("exitCode").GetInt32());
        Assert.Equal("no selected leg can run", root.GetProperty("summary").GetString());
        Assert.False(root.GetProperty("passed").GetBoolean());
        Assert.False(root.GetProperty("complete").GetBoolean());

        var leg = Assert.Single(root.GetProperty("legs").EnumerateArray());
        Assert.Equal("elsewhere", leg.GetProperty("leg").GetString());
        Assert.Equal("skipped-unavailable", leg.GetProperty("verdict").GetString());
    }

    /// <summary>
    /// And a refusal of the whole run answers with the ledger too, carrying the refusal's own code
    /// and words - as its FAIL line does, on standard error, where a reader of the terminal sees it.
    /// </summary>
    [Fact]
    public async Task ARefusedRun_AnswersWithTheLedger_WithTheRefusalsCodeAndWords()
    {
        using var temp = new TempDirectory();
        await PrepareRunnerAsync(temp);

        // A program this machine has and the repository never declared: the leg is placed, then
        // refused before anything starts.
        temp.WriteFile(
            Path.Combine(".harness-config", "runner", "actions", "probe", "probe.yml"),
            "name: probe\nsteps:\n  - name: version\n    run: git --version\n");

        var result = await CliRunner.RunAsync(
            ["run", "probe", "--legs", "native", "--json", "-C", temp.Path],
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Refused, result.ExitCode);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;

        Assert.Equal(HarnessExit.Refused, root.GetProperty("exitCode").GetInt32());
        Assert.Contains("'git' is not declared under 'tools'", root.GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.False(root.GetProperty("passed").GetBoolean());
        Assert.False(root.GetProperty("complete").GetBoolean());
        Assert.False(root.TryGetProperty("verdict", out _), "a stopped run reached no verdict of its own");
        Assert.Empty(root.GetProperty("legs").EnumerateArray());
        Assert.Contains("'git' is not declared under 'tools'", result.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// A step for another operating system is left out of a leg - here one starting a program nobody
    /// declared, which would refuse the whole run where the step runs - and the leg passes on the
    /// steps it did run, naming the one it left out as it runs and on its line.
    /// </summary>
    [Fact]
    public async Task AStepForAnotherSystem_IsLeftOut_AndNamedOnTheLegsLine()
    {
        using var temp = new TempDirectory();
        await PrepareRunnerAsync(temp);

        temp.WriteFile(
            Path.Combine(".harness-config", "runner", "actions", "probe", "probe.yml"),
            $"name: probe\nsteps:\n  - name: version\n    run: dotnet --version\n"
            + $"  - name: fetch\n    runOn: [{ElsewhereOs}]\n    run: curl https://example.invalid\n");

        var result = await CliRunner.RunAsync(
            ["run", "probe", "--legs", "native", "--json", "-C", temp.Path],
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, result.ExitCode);
        Assert.Contains($"native: skipped 'fetch', which runs on {ElsewhereOs} only", result.StandardError, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var leg = Assert.Single(document.RootElement.GetProperty("legs").EnumerateArray());

        Assert.Equal("passed", leg.GetProperty("verdict").GetString());
        Assert.Equal(["fetch"], leg.GetProperty("skippedSteps").EnumerateArray().Select(step => step.GetString()));
    }

    /// <summary>
    /// --dry-run reaches the leg's host and installs nothing: the tool it would install is named
    /// with the command that would run, and the answer is still "not provisioned". The install
    /// declared here is harmless, so a dry run that ran it anyway fails this rather than a machine.
    /// </summary>
    [Fact]
    public async Task InstallMissingTools_DryRun_NamesTheCommand_AndRunsNothing()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;

        await harness.InitializeHarnessAsync(temp.Path, token, new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Legs = { ["native"] = new LegConfig { Os = harness.Platform.PlatformKey, Processor = harness.Platform.Processor, Config = "debug" } },
            Tools = { new ToolConfig { Name = "dssharness-absent-tool", Install = { ["all"] = new ToolInstall { Command = ["dotnet", "--version"] } } } },
        });

        var result = await CliRunner.RunAsync(["install-missing-tools", "--dry-run", "--json", "-C", temp.Path], token);

        Assert.Equal(ToolsExit.NotProvisioned, result.ExitCode);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var tool = Assert.Single(Assert.Single(document.RootElement.GetProperty("legs").EnumerateArray()).GetProperty("tools").EnumerateArray());

        Assert.True(document.RootElement.GetProperty("dryRun").GetBoolean());
        Assert.Equal("would install", tool.GetProperty("state").GetString());
        Assert.Equal("would run 'dotnet --version'", tool.GetProperty("detail").GetString());
    }

    /// <summary>
    /// init installs tools only when given --install-tools, and otherwise says how.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Init_InstallsTools_OnlyWithTheFlag(bool installTools)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;

        await harness.InitializeHarnessAsync(temp.Path, token, new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Legs = { ["native"] = new LegConfig { Os = harness.Platform.PlatformKey, Processor = harness.Platform.Processor, Config = "debug" } },
        });

        var result = await CliRunner.RunAsync(installTools ? ["init", "--install-tools", "-C", temp.Path] : ["init", "-C", temp.Path], token);

        Assert.Equal(HarnessExit.Success, result.ExitCode);
        Assert.Equal(installTools, result.StandardOutput.Contains("tools   native on local", StringComparison.Ordinal));
        Assert.Equal(!installTools, result.StandardOutput.Contains("tools   not checked", StringComparison.Ordinal));
    }

    /// <summary>
    /// --input gives a declared input its value over the default - here the program the step starts,
    /// which as the default is one nobody declared - and a value for an input the action does not
    /// declare refuses the run before anything starts, naming what it does declare.
    /// </summary>
    [Fact]
    public async Task AnInputGivenOnTheCommandLine_ReachesTheStep_AndAnUndeclaredOneIsRefused()
    {
        using var temp = new TempDirectory();
        await PrepareRunnerAsync(temp);
        var token = TestContext.Current.CancellationToken;

        temp.WriteFile(
            Path.Combine(".harness-config", "runner", "actions", "probe", "probe.yml"),
            "name: probe\ninputs:\n  program:\n    default: curl\nsteps:\n  - name: version\n    run: \"{program} --version\"\n");

        var given = await CliRunner.RunAsync(["run", "probe", "--legs", "native", "--input", "program=dotnet", "-C", temp.Path], token);
        var undeclared = await CliRunner.RunAsync(
            ["run", "probe", "--legs", "native", "--input", "programme=dotnet", "--json", "-C", temp.Path],
            token);

        Assert.Equal(HarnessExit.Success, given.ExitCode);
        Assert.Equal(HarnessExit.UsageError, undeclared.ExitCode);
        Assert.Contains("--input names 'programme'", undeclared.StandardError, StringComparison.Ordinal);
        Assert.Contains("it declares program.", undeclared.StandardError, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(undeclared.StandardOutput);

        Assert.Equal(HarnessExit.UsageError, document.RootElement.GetProperty("exitCode").GetInt32());
        Assert.False(document.RootElement.TryGetProperty("runDirectory", out _), "no run began, so none has records");
    }

    /// <summary>
    /// A leg on whose operating system no step runs would pass having run nothing, so the run is
    /// refused before a host is measured or a run begins, naming the leg. Left to each leg, one
    /// that no host can take made the run merely incomplete, and one a host could take passed.
    /// </summary>
    [Fact]
    public async Task ARunWithALegOnWhoseSystemNoStepRuns_IsRefusedBeforeAnythingStarts()
    {
        using var temp = new TempDirectory();
        await PrepareRunnerAsync(temp);

        temp.WriteFile(
            Path.Combine(".harness-config", "runner", "actions", "probe", "probe.yml"),
            $"name: probe\nsteps:\n  - name: version\n    runOn: [{new HarnessFactory().Platform.PlatformKey}]\n    run: dotnet --version\n");

        var result = await CliRunner.RunAsync(
            ["run", "probe", "--legs", "native,elsewhere", "--json", "-C", temp.Path],
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Refused, result.ExitCode);
        Assert.Contains($"leg 'elsewhere' ({ElsewhereOs})", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("leg 'native'", result.StandardError, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(result.StandardOutput);

        Assert.Equal(HarnessExit.Refused, document.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Empty(document.RootElement.GetProperty("legs").EnumerateArray());
        Assert.False(document.RootElement.TryGetProperty("runDirectory", out _), "no run began, so none has records");
    }

    /// <summary>
    /// The document is the whole of standard output from the command's first line: 'run' reads its
    /// action before any leg is surveyed, and under --verbose says so, which went to standard output
    /// in front of the document a script was about to parse.
    /// </summary>
    [Fact]
    public async Task UnderVerbose_TheLedgerIsStillTheWholeOfStandardOutput()
    {
        using var temp = new TempDirectory();
        await PrepareRunnerAsync(temp);

        var result = await CliRunner.RunAsync(
            ["run", "probe", "--legs", "native", "--json", "--verbose", "-C", temp.Path],
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, result.ExitCode);

        using var document = JsonDocument.Parse(result.StandardOutput);

        Assert.Equal("native", Assert.Single(document.RootElement.GetProperty("legs").EnumerateArray()).GetProperty("leg").GetString());
        Assert.Contains("reading action file", result.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// And before any leg is placed, too: a leg nobody declared is a usage error, answered with a
    /// ledger holding no legs - so a script reading standard output has one document to read on
    /// every exit, where it had nothing at all.
    /// </summary>
    [Fact]
    public async Task ARunStoppedBeforeAnyLegWasPlaced_StillAnswersWithTheLedger()
    {
        using var temp = new TempDirectory();
        await PrepareRunnerAsync(temp);

        var result = await CliRunner.RunAsync(
            ["test", "--no-build", "--legs", "no-such-leg", "--json", "-C", temp.Path],
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, result.ExitCode);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;

        Assert.Equal(HarnessExit.UsageError, root.GetProperty("exitCode").GetInt32());
        Assert.Contains("no-such-leg", root.GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.Empty(root.GetProperty("legs").EnumerateArray());
        Assert.Contains("no-such-leg", result.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// The chain the consumer's gate broke on, end to end: a leg whose test runner no directory on
    /// this machine holds is turned away by the survey, naming the program, and a test run that
    /// selects it reports it as skipped for a missing tool - incomplete, exit 21 - rather than starting
    /// it and poisoning the run, exit 70. Its sibling still runs. And only a command that starts the
    /// runner turns the leg away for it: a runner whose steps run something else runs there.
    /// </summary>
    [Fact]
    public async Task ALegWhoseTestRunnerIsNowhere_IsTurnedAwayForItsTests_AndOnlyForThem()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var platform = harness.Platform;
        var token = TestContext.Current.CancellationToken;
        var missing = "rh-missing-" + Guid.NewGuid().ToString("N")[..8];

        await harness.InitializeHarnessAsync(temp.Path, token, new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Tools = { new ToolConfig { Name = "dotnet" } },
            Legs =
            {
                ["native"] = new LegConfig
                {
                    Os = platform.PlatformKey,
                    Processor = platform.Processor,
                    Config = "debug",
                    Test = new TestConfig { All = new TestInvocation { Runner = "dotnet", Args = ["--version"], SuccessPattern = @"^\d+\.\d+" } },
                },
                ["broken"] = new LegConfig
                {
                    Os = platform.PlatformKey,
                    Processor = platform.Processor,
                    Config = "debug",
                    Test = new TestConfig { All = new TestInvocation { Runner = missing, SuccessPattern = "passed" } },
                },
            },
            PredefinedRunners = { ["probe"] = new RunnerConfig { Action = "probe/probe.yml" } },
        });

        temp.WriteFile(
            Path.Combine(".harness-config", "runner", "actions", "probe", "probe.yml"),
            "name: probe\nsteps:\n  - name: version\n    run: dotnet --version\n");

        var legs = await CliRunner.RunAsync(["legs", "-C", temp.Path], token);

        Assert.Contains($"'{missing}' is not installed there", legs.StandardError, StringComparison.Ordinal);

        var test = await CliRunner.RunAsync(["test", "--no-build", "--legs", "native,broken", "--json", "-C", temp.Path], token);

        Assert.Equal(HarnessExit.Incomplete, test.ExitCode);

        using (var document = JsonDocument.Parse(test.StandardOutput))
        {
            var verdicts = document.RootElement.GetProperty("legs").EnumerateArray()
                .ToDictionary(leg => leg.GetProperty("leg").GetString()!, leg => leg.GetProperty("verdict").GetString());

            Assert.Equal("passed", verdicts["native"]);
            Assert.Equal("skipped-tool-missing", verdicts["broken"]);
        }

        // A runner whose steps never start the test runner is not turned away for it.
        var run = await CliRunner.RunAsync(["run", "probe", "--legs", "broken", "--json", "-C", temp.Path], token);

        Assert.Equal(HarnessExit.Success, run.ExitCode);
    }

    /// <summary>
    /// A leg testing a build it does not make names the compilers that build was configured with,
    /// read from what CMake answered then: its verdict is about binaries they produced.
    /// </summary>
    [Fact]
    public async Task ATestOfABuildItDoesNotMake_NamesTheCompilersItsDirectoryWasConfiguredWith()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var platform = harness.Platform;
        var token = TestContext.Current.CancellationToken;

        await harness.InitializeHarnessAsync(temp.Path, token, new HarnessConfig
        {
            Toolchains = { ["cc"] = new ToolchainConfig { Platforms = [platform.PlatformKey], Env = { ["CC"] = "cc" } } },
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Projects =
            {
                new ProjectConfig
                {
                    Name = "app",
                    Type = "cmake",
                    Path = ".",
                    Test = new TestConfig { All = new TestInvocation { Runner = "dotnet", Args = ["--version"], SuccessPattern = @"^\d+\.\d+" } },
                },
            },
            Legs = { ["native"] = new LegConfig { Os = platform.PlatformKey, Processor = platform.Processor, Config = "debug", Toolchain = "cc" } },
        });

        var replies = Path.Combine("build", $"{platform.Processor}-cc-debug", ".cmake", "api", "v1", "reply");
        temp.WriteFile(Path.Combine(replies, "index-2026-09-19T16-16-05-0385.json"), """{ "reply": { "toolchains-v1": { "jsonFile": "toolchains-v1-a.json" } } }""");
        temp.WriteFile(
            Path.Combine(replies, "toolchains-v1-a.json"),
            """{ "toolchains": [ { "language": "C", "compiler": { "id": "GNU", "version": "13.2.0" } } ] }""");

        var result = await CliRunner.RunAsync(["test", "--no-build", "--legs", "native", "--json", "-C", temp.Path], token);

        Assert.Equal(HarnessExit.Success, result.ExitCode);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var compiler = Assert.Single(Assert.Single(document.RootElement.GetProperty("legs").EnumerateArray()).GetProperty("compilers").EnumerateArray());

        Assert.Equal("GNU", compiler.GetProperty("id").GetString());
        Assert.Equal("13.2.0", compiler.GetProperty("version").GetString());
    }

    /// <summary>
    /// A real configure, by the CMake on this machine: the compiler it resolved is named on the leg's
    /// line by build and by a runner that builds, read back from what CMake itself wrote. Skipped
    /// where this machine has no CMake, no Ninja or no C compiler.
    /// </summary>
    [Fact]
    public async Task ARealConfigure_IsNamedOnTheLegsLine_ByBuildAndByARunnerThatBuilds()
    {
        var harness = new HarnessFactory();
        var platform = harness.Platform;
        var compiler = OperatingSystem.IsWindows() ? "gcc" : "cc";

        Assert.SkipUnless(
            harness.ProcessRunner.FindExecutable("cmake") is not null
                && harness.ProcessRunner.FindExecutable("ninja") is not null
                && harness.ProcessRunner.FindExecutable(compiler) is not null,
            $"This machine lacks cmake, ninja or {compiler}, which a real configure needs.");

        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;

        await harness.InitializeHarnessAsync(temp.Path, token, new HarnessConfig
        {
            Toolchains = { ["cc"] = new ToolchainConfig { Platforms = [platform.PlatformKey], Generator = "Ninja", Env = { ["CC"] = compiler } } },
            BuildConfigs = { ["debug"] = new BuildConfiguration { CmakeBuildType = "Debug" } },
            Projects =
            {
                new ProjectConfig
                {
                    Name = "app",
                    Type = "cmake",
                    Path = ".",
                    BuildOutputs = [BuildOutput.Keyed([new("windows", "probe.exe"), new("all", "probe")])],
                },
            },
            Tools = { new ToolConfig { Name = "dotnet" } },
            Legs = { ["native"] = new LegConfig { Os = platform.PlatformKey, Processor = platform.Processor, Config = "debug", Toolchain = "cc" } },
            PredefinedRunners = { ["probe"] = new RunnerConfig { Action = "probe/probe.yml", RequireBuild = true } },
        });

        temp.WriteFile("CMakeLists.txt", "cmake_minimum_required(VERSION 3.20)\nproject(probe C)\nadd_executable(probe main.c)\n");
        temp.WriteFile("main.c", "int main(void) { return 0; }\n");
        temp.WriteFile(Path.Combine(".harness-config", "runner", "actions", "probe", "probe.yml"), "name: probe\nsteps:\n  - name: version\n    run: dotnet --version\n");

        var build = await CliRunner.RunAsync(["build", "--legs", "native", "--json", "-C", temp.Path], token);
        var run = await CliRunner.RunAsync(["run", "probe", "--legs", "native", "--json", "-C", temp.Path], token);

        foreach (var result in new[] { build, run })
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var leg = Assert.Single(document.RootElement.GetProperty("legs").EnumerateArray());
            var configured = Assert.Single(leg.GetProperty("compilers").EnumerateArray());

            Assert.Equal("C", configured.GetProperty("language").GetString());
            Assert.False(string.IsNullOrEmpty(configured.GetProperty("id").GetString()), result.StandardError);
            Assert.Contains("compiler: ", result.StandardError, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Each command asks a host only for what it will start there. A leg whose compiler no machine
    /// has is turned away by the survey and by a build, and still tested when the build is skipped;
    /// a runner whose step starts a program nothing has is turned away before it starts, with the
    /// program named, rather than failing halfway through.
    /// </summary>
    [Fact]
    public async Task EachCommand_TurnsALegAwayOnlyForWhatItWillStart()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var platform = harness.Platform;
        var token = TestContext.Current.CancellationToken;
        var compiler = "rh-missing-cc-" + Guid.NewGuid().ToString("N")[..8];
        var tool = "rh-missing-tool-" + Guid.NewGuid().ToString("N")[..8];

        var toolchain = new ToolchainConfig { Platforms = [platform.PlatformKey] };
        toolchain.Env["CC"] = compiler;

        await harness.InitializeHarnessAsync(temp.Path, token, new HarnessConfig
        {
            Toolchains = { ["cc"] = toolchain },
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Projects =
            {
                new ProjectConfig
                {
                    Name = "app",
                    Type = "cmake",
                    Path = ".",
                    Test = new TestConfig { All = new TestInvocation { Runner = "dotnet", Args = ["--version"], SuccessPattern = @"^\d+\.\d+" } },
                },
            },
            Tools = { new ToolConfig { Name = tool } },
            Legs = { ["compiled"] = new LegConfig { Os = platform.PlatformKey, Processor = platform.Processor, Config = "debug", Toolchain = "cc" } },
            PredefinedRunners = { ["absent"] = new RunnerConfig { Action = "absent/absent.yml" } },
        });

        temp.WriteFile(
            Path.Combine(".harness-config", "runner", "actions", "absent", "absent.yml"),
            $"name: absent\nsteps:\n  - name: use\n    run: {tool} --version\n");

        var legs = await CliRunner.RunAsync(["legs", "--legs", "compiled", "-C", temp.Path], token);

        Assert.Equal(LegsExit.Unavailable, legs.ExitCode);
        Assert.Contains($"'{compiler}'", legs.StandardError, StringComparison.Ordinal);

        var build = await CliRunner.RunAsync(["build", "--legs", "compiled", "-C", temp.Path], token);

        Assert.Equal(LegsExit.Unavailable, build.ExitCode);
        Assert.Contains($"'{compiler}'", build.StandardError, StringComparison.Ordinal);

        var test = await CliRunner.RunAsync(["test", "--no-build", "--legs", "compiled", "-C", temp.Path], token);

        Assert.Equal(HarnessExit.Success, test.ExitCode);

        var run = await CliRunner.RunAsync(["run", "absent", "--legs", "compiled", "-C", temp.Path], token);

        Assert.Equal(LegsExit.Unavailable, run.ExitCode);
        Assert.Contains($"'{tool}' is not installed there", run.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// A runs directory nobody can write - an earlier run under sudo left it to root - refuses the
    /// run, exit 13, naming the file it could not write, and still answers --json with the ledger.
    /// Escaping as an error, it read as exit 70, a defect in this tool.
    /// </summary>
    [Fact]
    public async Task ARunsDirectoryNobodyCanWrite_RefusesTheRun_NamingIt()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;

        await PrepareRunnerAsync(temp);

        var runs = temp.Combine(".harness-config", "runs");

        if (Directory.Exists(runs))
        {
            Directory.Delete(runs, recursive: true);
        }

        await File.WriteAllTextAsync(runs, "not a directory", token);

        var result = await CliRunner.RunAsync(["run", "probe", "--legs", "native", "--json", "-C", temp.Path], token);

        Assert.Equal(HarnessExit.Refused, result.ExitCode);
        Assert.Contains("could not be written", result.StandardError, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(result.StandardOutput);

        Assert.Equal(HarnessExit.Refused, document.RootElement.GetProperty("exitCode").GetInt32());
    }

    /// <summary>
    /// A host's own environment reaches what a leg starts - its test runner and a runner's steps -
    /// beneath what each declares itself. A host running a leg another machine dispatched to it takes
    /// the section that machine names it by, not 'local', which in the configuration the two share is
    /// the machine that dispatched it; and a host that is named in no spelling a host has is a usage
    /// error, never a guess.
    /// </summary>
    [Fact]
    public async Task TheHostsEnvironment_ReachesWhatALegStarts_AsTheHostItRunsAs()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var platform = harness.Platform;
        var token = TestContext.Current.CancellationToken;

        // The test child prints the variable it is given; its own mode is set the way every other
        // variable here is, so what it prints is what the leg's environment held.
        static TestInvocation Printing(string variable, string expected, params (string Name, string Value)[] env)
        {
            var environment = new Dictionary<string, string> { [TestHost.ChildModeVariable] = "print-env" };

            foreach (var (name, value) in env)
            {
                environment[name] = value;
            }

            return new TestInvocation
            {
                Runner = TestHost.DotnetExecutable,
                Args = ["exec", TestHost.AssemblyPath, variable],
                SuccessPattern = $"^{expected}$",
                Env = environment,
            };
        }

        LegConfig Leg(TestInvocation invocation) => new()
        {
            Os = platform.PlatformKey,
            Processor = platform.Processor,
            Config = "debug",
            Test = new TestConfig { All = invocation },
        };

        await harness.InitializeHarnessAsync(temp.Path, token, new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            SshItems = { "pi" },
            Hosts = new HostsConfig
            {
                Local = new LocalHostConfig { Env = { ["RH_HOST_VAR"] = "from-local", ["RH_ORDER"] = "host" } },
                Ssh = { ["pi"] = new SshHostConfig { RepositoryPath = "~/repo", Env = { ["RH_HOST_VAR"] = "from-pi" } } },
            },
            Legs =
            {
                ["host-var"] = Leg(Printing("RH_HOST_VAR", "from-local")),
                ["invocation-wins"] = Leg(Printing("RH_ORDER", "invocation", ("RH_ORDER", "invocation"))),
                ["sent-here"] = Leg(Printing("RH_HOST_VAR", "from-pi")),
            },
            PredefinedRunners =
            {
                ["print"] = new RunnerConfig
                {
                    Phases =
                    [
                        new RunnerPhase
                        {
                            Name = "print",
                            Command = [TestHost.DotnetExecutable, "exec", TestHost.AssemblyPath, "RH_HOST_VAR"],
                            Env = { [TestHost.ChildModeVariable] = "print-env" },
                            SuccessPattern = "^from-local$",
                        },
                    ],
                },
            },
        });

        // One at a time: legs of one variant would share a build directory, which a run refuses.
        foreach (var leg in new[] { "host-var", "invocation-wins" })
        {
            var test = await CliRunner.RunAsync(["test", "--no-build", "--legs", leg, "--json", "-C", temp.Path], token);

            Assert.Equal(HarnessExit.Success, test.ExitCode);
        }

        var sent = await CliRunner.RunAsync(["test", "--no-build", "--legs", "sent-here", "--json", RemoteLegRunner.HereOption, "ssh pi", "-C", temp.Path], token);

        Assert.Equal(HarnessExit.Success, sent.ExitCode);

        var run = await CliRunner.RunAsync(["run", "print", "--legs", "host-var", "--json", "-C", temp.Path], token);

        Assert.Equal(HarnessExit.Success, run.ExitCode);

        var misnamed = await CliRunner.RunAsync(["test", "--no-build", "--legs", "host-var", RemoteLegRunner.HereOption, "pi", "-C", temp.Path], token);

        Assert.Equal(HarnessExit.UsageError, misnamed.ExitCode);
        Assert.Contains("names a host as 'local', 'wsl <distribution>' or 'ssh <name>'", misnamed.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// A host's keepAwake is started while a leg's own work runs, filled in with the DssHarness
    /// process running it. Here the command writes what it was given, and the leg's test waits for
    /// that file before it can pass: the command ran during the work, and was given a process - the
    /// CLI's own, which runs apart from this test and whose id only it knows.
    /// </summary>
    [Fact]
    public async Task AHostsKeepAwake_RunsDuringALegsWork_GivenTheProcessRunningIt()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var platform = harness.Platform;
        var token = TestContext.Current.CancellationToken;
        var awake = temp.Combine("awake.txt");

        await harness.InitializeHarnessAsync(temp.Path, token, new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Hosts = new HostsConfig
            {
                Local = new LocalHostConfig
                {
                    KeepAwake = [TestHost.DotnetExecutable, "exec", TestHost.AssemblyPath, awake, "{pid}"],
                    Env = { [TestHost.ChildModeVariable] = "write-file" },
                },
            },
            Legs =
            {
                ["native"] = new LegConfig
                {
                    Os = platform.PlatformKey,
                    Processor = platform.Processor,
                    Config = "debug",
                    Test = new TestConfig
                    {
                        All = new TestInvocation
                        {
                            Runner = TestHost.DotnetExecutable,
                            Args = ["exec", TestHost.AssemblyPath, awake],
                            Env = new Dictionary<string, string> { [TestHost.ChildModeVariable] = "stream" },
                            SuccessPattern = "second",
                        },
                    },
                },
            },
        });

        var test = await CliRunner.RunAsync(["test", "--no-build", "--legs", "native", "-C", temp.Path], token);

        Assert.Equal(HarnessExit.Success, test.ExitCode);
        Assert.True(
            int.TryParse(await File.ReadAllTextAsync(awake, token), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0,
            "keepAwake was not given the process running the leg");
    }

    /// <summary>The operating system of the leg <see cref="PrepareRunnerAsync"/> declares that no host provides.</summary>
    private static string ElsewhereOs
        => new HarnessFactory().Platform.PlatformKey == PlatformNames.Linux ? PlatformNames.MacOs : PlatformNames.Linux;

    /// <summary>
    /// A repository with one leg this machine can run and one no host can, and a runner that does
    /// something trivial on whichever of them runs.
    /// </summary>
    private static async Task PrepareRunnerAsync(TempDirectory temp)
    {
        var harness = new HarnessFactory();
        var platform = harness.Platform;

        await harness.InitializeHarnessAsync(temp.Path, TestContext.Current.CancellationToken, new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Tools = { new ToolConfig { Name = "dotnet" } },
            Legs =
            {
                ["native"] = new LegConfig { Os = platform.PlatformKey, Processor = platform.Processor, Config = "debug" },

                // An operating system no declared host provides: this machine is the only host.
                ["elsewhere"] = new LegConfig
                {
                    Os = ElsewhereOs,
                    Processor = platform.Processor,
                    Config = "debug",
                },
            },
            PredefinedRunners =
            {
                ["probe"] = new RunnerConfig { Action = "probe/probe.yml" },
            },
        });

        temp.WriteFile(
            Path.Combine(".harness-config", "runner", "actions", "probe", "probe.yml"),
            "name: probe\nsteps:\n  - name: version\n    run: dotnet --version\n");
    }

    /// <summary>An initialised repository whose path budget any temporary directory fits.</summary>
    private static Task PrepareRepositoryAsync(TempDirectory temp)
        => new HarnessFactory().InitializeHarnessAsync(
            temp.Path,
            TestContext.Current.CancellationToken,
            new HarnessConfig { Worktrees = new WorktreeSettings { PathBudgetReserve = 5, PathBudgetMargin = 2 } });

    [GeneratedRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$")]
    private static partial Regex BareVersionPattern();

    [GeneratedRegex("created worktree '(?<name>[a-z0-9]+)'")]
    private static partial Regex CreatedNamePattern();
}
