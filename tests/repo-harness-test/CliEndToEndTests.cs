using System.Text.Json;
using System.Text.RegularExpressions;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Git;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Results;

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
                    Os = platform.PlatformKey == PlatformNames.Linux ? PlatformNames.MacOs : PlatformNames.Linux,
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
