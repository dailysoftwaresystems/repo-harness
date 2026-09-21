using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RepoHarness.Core.Anchors;
using RepoHarness.Core.Commands;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Git;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Tools;

namespace RepoHarness.Tests;

public sealed class VerifyGitServiceTests
{
    [Fact]
    public async Task VerifyAsync_ReportsNotARepository_OutsideOne()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();

        var status = await harness.VerifyGitService.VerifyAsync(
            temp.Path,
            TestContext.Current.CancellationToken);

        Assert.Equal(VerifyGitStatus.NotAGitRepository, status);
        Assert.Equal(2, (int)status);
    }

    [Fact]
    public async Task VerifyAsync_ReportsSuccess_InsideARepository()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, TestContext.Current.CancellationToken);

        var status = await harness.VerifyGitService.VerifyAsync(
            temp.Path,
            TestContext.Current.CancellationToken);

        Assert.Equal(VerifyGitStatus.Success, status);
        Assert.Equal(0, (int)status);
    }

    [Fact]
    public void StatusValues_MatchTheDocumentedExitCodes()
    {
        // The command's entire contract is these three numbers.
        Assert.Equal(0, (int)VerifyGitStatus.Success);
        Assert.Equal(1, (int)VerifyGitStatus.GitNotInstalled);
        Assert.Equal(2, (int)VerifyGitStatus.NotAGitRepository);
    }
}

public sealed class InitServiceTests
{
    [Fact]
    public async Task InitializeAsync_Refuses_OutsideAGitRepository()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();

        var outcome = await harness.InitService.InitializeAsync(
            temp.Path,
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
        Assert.False(Directory.Exists(temp.Combine(HarnessLayout.DirectoryName)));
    }

    [Fact]
    public async Task InitializeAsync_CreatesTheFullLayout()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, TestContext.Current.CancellationToken);

        var outcome = await harness.InitService.InitializeAsync(
            temp.Path,
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded);
        Assert.True(File.Exists(temp.Combine(".harness-config", "config.json")));
        Assert.False(File.Exists(temp.Combine(".harness-config", "worktrees", ".gitkeep")), "the worktrees root must never hold a placeholder");
        Assert.True(File.Exists(temp.Combine(".harness-config", "sshItems", ".gitkeep")));
        Assert.True(File.Exists(temp.Combine(".harness-config", "wslDistros", ".gitkeep")));
        Assert.True(Directory.Exists(temp.Combine(".harness-config", "runner", "actions")));
        Assert.True(File.Exists(temp.Combine(".harness-config", "runner", ".env", ".gitkeep")));
        Assert.True(File.Exists(temp.Combine(".harness-config", "runner", ".secrets", ".gitkeep")));
        Assert.True(File.Exists(temp.Combine(".gitignore")));

        // A first init declares no leg, so there is nothing to install for and nothing to say about it.
        Assert.DoesNotContain(outcome.Details!, line => line.StartsWith("tools", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InitializeAsync_CreatesBothAnchorRegistries_FromTheSkeleton()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, TestContext.Current.CancellationToken);

        var outcome = await harness.InitService.InitializeAsync(temp.Path, TestContext.Current.CancellationToken);

        var settings = new AnchorSettings();
        Assert.Equal(
            AnchorRegistrySkeleton.Render(AnchorRegistryKind.Pending, settings),
            File.ReadAllText(temp.Combine(".plans", "_deferred-anchor-registry.md")));
        Assert.Equal(
            AnchorRegistrySkeleton.Render(AnchorRegistryKind.Done, settings),
            File.ReadAllText(temp.Combine(".plans", "_deferred-anchor-registry-done.md")));
        Assert.Contains($"created {AnchorSettings.DefaultPendingAnchorsPath}", outcome.Details!);
    }

    [Fact]
    public async Task InitializeAsync_NeverTouchesAnExistingRegistry_AndCreatesOnlyTheMissingOne()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, TestContext.Current.CancellationToken);
        var pending = temp.WriteFile(Path.Combine(".plans", "_deferred-anchor-registry.md"), "somebody's record\n");

        var outcome = await harness.InitService.InitializeAsync(temp.Path, TestContext.Current.CancellationToken);

        Assert.Equal("somebody's record\n", File.ReadAllText(pending));
        Assert.True(File.Exists(temp.Combine(".plans", "_deferred-anchor-registry-done.md")));
        Assert.Contains($"kept    {AnchorSettings.DefaultPendingAnchorsPath} (already present)", outcome.Details!);
        Assert.Contains($"created {AnchorSettings.DefaultDoneAnchorsPath}", outcome.Details!);
    }

    [Fact]
    public async Task InitializeAsync_CreatesTheRegistriesTheConfigurationNames()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, TestContext.Current.CancellationToken);
        harness.WriteConfig(temp.Path, new HarnessConfig
        {
            Anchors = new AnchorSettings { PendingAnchorsPath = "docs/work.md", DoneAnchorsPath = "docs/work-done.md" },
        });

        await harness.InitService.InitializeAsync(temp.Path, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(temp.Combine("docs", "work.md")));
        Assert.True(File.Exists(temp.Combine("docs", "work-done.md")));
        Assert.False(Directory.Exists(temp.Combine(".plans")));
    }

    /// <summary>
    /// Provisioning is the last thing init does, and it can now stop there to ask for a superuser
    /// password. Ctrl+C is how somebody without one answers, so what init had already written stays
    /// written and is still listed; only the exit code reports that the last step was stopped.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_ReportsAnInterruptedToolCheck_WithoutLosingWhatItCreated()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, TestContext.Current.CancellationToken);
        harness.WriteConfig(temp.Path, OneLeg());

        // Ctrl+C: the run's own token is cancelled, and the cancellation surfaces from provisioning.
        using var interrupted = new CancellationTokenSource();

        harness.ToolProvisionService
            .ProvisionAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<Task<ToolProvisionReport>>(_ =>
            {
                interrupted.Cancel();
                throw new OperationCanceledException(interrupted.Token);
            });

        var outcome = await harness.InitService.InitializeAsync(temp.Path, installTools: true, interrupted.Token);

        Assert.Equal(HarnessExit.Cancelled, outcome.ExitCode);
        Assert.Contains(
            outcome.Details!,
            line => line.Contains("interrupted before finishing", StringComparison.Ordinal));

        // The repository is initialised either way, and the list says so: the exit code is about the
        // one step that stopped, not about the ones that finished.
        Assert.True(File.Exists(temp.Combine(".harness-config", "config.json")));
        Assert.Contains(outcome.Details!, line => line.Contains(".gitignore", StringComparison.Ordinal));
    }

    /// <summary>
    /// Tolerating an interruption must not become tolerating everything: a fault in provisioning is
    /// still a fault, and is still reported rather than swallowed into a green init.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_StillFails_WhenTheToolCheckThrowsSomethingUnexpected()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, TestContext.Current.CancellationToken);
        harness.WriteConfig(temp.Path, OneLeg());

        harness.ToolProvisionService
            .ProvisionAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("something nobody expected"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.InitService.InitializeAsync(temp.Path, installTools: true, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A budget inside provisioning cancels its own work and is a failure like any other. Excusing it
    /// as an interruption would report a green init for a host that stopped answering, because both
    /// arrive as the same exception type and only the run's own token tells them apart.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_StillFails_WhenTheToolCheckCancelsItsOwnWork()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, TestContext.Current.CancellationToken);
        harness.WriteConfig(temp.Path, OneLeg());

        // Nobody pressed anything: the run's token is untouched.
        harness.ToolProvisionService
            .ProvisionAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Throws(new OperationCanceledException(new CancellationToken(canceled: true)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.InitService.InitializeAsync(temp.Path, installTools: true, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Adopting the harness's files changes one tree, and an install changes machines, so init
    /// installs nothing unless asked - and says how, where there is a leg to install for.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InitializeAsync_InstallsTools_OnlyWhenAskedTo(bool installTools)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, token);
        harness.WriteConfig(temp.Path, OneLeg());

        var outcome = await harness.InitService.InitializeAsync(temp.Path, installTools, token);

        Assert.True(outcome.Succeeded, outcome.Message);
        await harness.ToolProvisionService
            .Received(installTools ? 1 : 0)
            .ProvisionAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());

        var pointer = outcome.Details!.Where(line => line.StartsWith("tools   not checked", StringComparison.Ordinal)).ToList();

        if (installTools)
        {
            Assert.Empty(pointer);
            return;
        }

        Assert.Equal(
            "tools   not checked; 'DssHarness install-missing-tools --dry-run' lists what each leg's host is missing, "
            + "and 'DssHarness install-missing-tools' or 'DssHarness init --install-tools' installs it",
            Assert.Single(pointer));
    }

    /// <summary>
    /// Where git cannot say which rules decide the managed paths, init says so and finishes: the
    /// tree is initialised either way, and a note that was never checked is not passed off as none.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_SaysSo_WhenGitCannotBeAskedAboutTheRules()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(temp.Path, token);

        var git = new InterceptingGitClient(harness.GitClient)
        {
            AfterExplainIgnored = (_, _) => throw new HarnessException(HarnessExit.CommandFailed, "git could not answer"),
        };

        var init = new InitService(
            harness.FileSystem,
            harness.RepositoryLocator,
            harness.ConfigStore,
            harness.GitIgnoreManager,
            harness.ProjectDetector,
            harness.VerifyGitService,
            harness.AnchorRegistryLocator,
            harness.ToolProvisionService,
            new ManagedIgnoreCheck(git, harness.FileSystem, harness.Platform, harness.Output),
            harness.Platform);

        var outcome = await init.InitializeAsync(temp.Path, token);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Contains(
            "note    could not ask git which rules decide the paths the managed block keeps: git could not answer",
            outcome.Details!);
    }

    /// <summary>A configuration with one leg, which is what makes init check tools at all.</summary>
    private static HarnessConfig OneLeg() => new()
    {
        BuildConfigs = { ["debug"] = new BuildConfiguration() },
        Legs = { ["here"] = new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug" } },
    };

    [Fact]
    public async Task InitializeAsync_IsIdempotent()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, TestContext.Current.CancellationToken);

        await harness.InitService.InitializeAsync(temp.Path, TestContext.Current.CancellationToken);
        var configBefore = File.ReadAllText(temp.Combine(".harness-config", "config.json"));
        var ignoreBefore = File.ReadAllText(temp.Combine(".gitignore"));

        var second = await harness.InitService.InitializeAsync(
            temp.Path,
            TestContext.Current.CancellationToken);

        Assert.True(second.Succeeded);
        Assert.Equal(configBefore, File.ReadAllText(temp.Combine(".harness-config", "config.json")));
        Assert.Equal(ignoreBefore, File.ReadAllText(temp.Combine(".gitignore")));
    }

    [Fact]
    public async Task InitializeAsync_SeedsConfigurationFromTheDetectedProject()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, TestContext.Current.CancellationToken);
        temp.WriteFile("CMakeLists.txt");

        await harness.InitService.InitializeAsync(temp.Path, TestContext.Current.CancellationToken);

        var config = harness.ConfigStore.Load(temp.Combine(".harness-config", "config.json"));

        Assert.Equal("cmake", Assert.Single(config.Projects).Type);
        Assert.Contains("msvc", config.Toolchains.Keys);
        Assert.Contains("debug", config.BuildConfigs.Keys);

        // The seeded legs are for the machine init ran on, as measured, not for a guess.
        Assert.NotEmpty(config.Legs);
        Assert.All(config.Legs.Values, leg =>
        {
            Assert.Equal(harness.Platform.PlatformKey, leg.Os);
            Assert.Equal(harness.Platform.Processor, leg.Processor);
        });
    }

    [Fact]
    public async Task InitializeAsync_PreservesExistingGitIgnoreRules()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, TestContext.Current.CancellationToken);
        File.WriteAllText(temp.Combine(".gitignore"), "# mine\nbuild-output/\n");

        await harness.InitService.InitializeAsync(temp.Path, TestContext.Current.CancellationToken);

        var ignore = File.ReadAllText(temp.Combine(".gitignore"));
        Assert.Contains("build-output/", ignore, StringComparison.Ordinal);
        Assert.Contains("/.harness-config/lock.json", ignore, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(".worktrees")]
    public async Task TheWorktreesRoot_ReadsAsIgnored_HoweverItIsAsked_AndWhetherOrNotItExists(string? configuredRoot)
    {
        // Excluding only the root's contents made its own answer depend on a trailing slash, and it
        // failed toward not ignored: a consumer asking for '<root>' without the slash got the wrong
        // answer silently, and the root held whole checkouts. Every spelling a consumer could reach
        // for is asked here, before the root exists and after a worktree is in it.
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;

        await harness.InitializeGitRepositoryAsync(temp.Path, token);

        if (configuredRoot is not null)
        {
            harness.WriteConfig(temp.Path, new HarnessConfig { Worktrees = new WorktreeSettings { Root = configuredRoot } });
        }

        await harness.InitService.InitializeAsync(temp.Path, token);
        await harness.CommitAllAsync(temp.Path, "harness", token);

        var root = configuredRoot ?? $"{HarnessLayout.DirectoryName}/{HarnessLayout.WorktreesDirectoryName}";

        // init leaves the root to the worktree commands, which create it the first time they need it.
        Assert.False(Directory.Exists(Path.Combine(temp.Path, root)), "init created the worktrees root");
        Assert.True(await IsIgnoredAsync(harness, temp.Path, $"{root}/", token), "absent, asked with a slash");
        Assert.True(await IsIgnoredAsync(harness, temp.Path, root, token), "absent, asked without a slash");

        Directory.CreateDirectory(Path.Combine(temp.Path, root, "wt-a", "src"));
        File.WriteAllText(Path.Combine(temp.Path, root, "wt-a", "src", "main.c"), "int main(void) { return 0; }");

        Assert.True(await IsIgnoredAsync(harness, temp.Path, $"{root}/", token), "present, asked with a slash");
        Assert.True(await IsIgnoredAsync(harness, temp.Path, root, token), "present, asked without a slash");
        Assert.True(await IsIgnoredAsync(harness, temp.Path, $"{root}/wt-a/src/main.c", token), "a file inside a worktree");

        var ignore = File.ReadAllText(temp.Combine(".gitignore"));
        Assert.Contains($"/{root}\n", ignore, StringComparison.Ordinal);
        Assert.DoesNotContain($"/{root}/", ignore, StringComparison.Ordinal);
        Assert.DoesNotContain($"!/{root}", ignore, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFreshlyInitialisedRepository_IsNotRefusedBySync_BeforeItsPlaceholdersAreCommitted()
    {
        // The placeholder shape showed an uncommitted worktrees/.gitkeep as untracked, and a path sync
        // withholds holding an untracked file is exactly what its no-longer-ignored guard refuses.
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;

        await harness.InitializeGitRepositoryAsync(temp.Path, token);
        await harness.InitService.InitializeAsync(temp.Path, token);

        var config = new HarnessConfig();
        var exclusions = new Core.Sync.SyncExclusions(config.Sync, config.Worktrees.Root);

        await exclusions.RefuseWhenNoLongerIgnoredAsync(harness.GitClient, temp.Path, token);
    }

    /// <summary>
    /// A repository that already ignored a managed path by hand keeps its rule. One that says what the
    /// block says changes nothing and is not named; one the block overrules is named as doing
    /// nothing there, so whoever wrote it knows it no longer counts.
    /// </summary>
    [Fact]
    public async Task AHandWrittenRuleForAManagedPath_IsReportedOnlyWhereItTurnsIt_AndLeftWhereItIs()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;

        await harness.InitializeGitRepositoryAsync(temp.Path, token);
        temp.WriteFile(".gitignore", "bin/\n.harness-config/worktrees/\n!/.harness-config/sshItems/*\n");

        var outcome = await harness.InitService.InitializeAsync(temp.Path, token);

        Assert.True(outcome.Succeeded, outcome.Message);

        var note = Assert.Single(outcome.Details!, line => line.StartsWith("note", StringComparison.Ordinal));

        Assert.Equal(
            "note    .gitignore line 3 ('!/.harness-config/sshItems/*') re-includes '.harness-config/sshItems/<any>', "
            + "'.harness-config/sshItems/<any>/<any>', which the managed block ignores; a later rule decides them, so this "
            + "one does nothing there",
            note);

        // Reported, never removed: hand-written rules are the repository's own.
        var ignore = File.ReadAllText(temp.Combine(".gitignore"));
        Assert.StartsWith("bin/\n.harness-config/worktrees/\n!/.harness-config/sshItems/*\n", ignore, StringComparison.Ordinal);
    }

    private static async Task<bool> IsIgnoredAsync(HarnessFactory harness, string root, string path, CancellationToken token)
    {
        // check-ignore exits 0 for an ignored path and 1 for one that is not; anything else is git
        // unable to answer, which must fail the test rather than read as either.
        var result = await harness.GitClient.RunAsync(root, ["check-ignore", "-q", "--", path], cancellationToken: token);

        Assert.True(result.ExitCode is 0 or 1, $"git check-ignore could not answer for '{path}': {result.FailureMessage}");

        return result.ExitCode == 0;
    }

    [Fact]
    public async Task InitializedRepository_TracksOnlyThePlaceholders_AndIgnoresTheRest()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, TestContext.Current.CancellationToken);
        await harness.InitService.InitializeAsync(temp.Path, TestContext.Current.CancellationToken);

        // Write the kinds of files that must never reach the repository: a lock, a run's logs, a
        // worktree's contents, a host's connection data, and a value a runner reads.
        File.WriteAllText(temp.Combine(".harness-config", "lock.json"), "{}");
        Directory.CreateDirectory(temp.Combine(".harness-config", "runs", "20260916-101500-abcdef12", "leg"));
        File.WriteAllText(temp.Combine(".harness-config", "runs", "20260916-101500-abcdef12", "leg", "build.log"), "x");
        Directory.CreateDirectory(temp.Combine(".harness-config", "worktrees", "wt-a", "nested"));
        File.WriteAllText(temp.Combine(".harness-config", "worktrees", "wt-a", "nested", "file.txt"), "x");
        Directory.CreateDirectory(temp.Combine(".harness-config", "sshItems", "probe-host"));
        File.WriteAllText(temp.Combine(".harness-config", "sshItems", "probe-host", ".key"), "secret");
        Directory.CreateDirectory(temp.Combine(".harness-config", "wslDistros", "probe-distro"));
        File.WriteAllText(temp.Combine(".harness-config", "wslDistros", "probe-distro", ".env"), "secret");
        File.WriteAllText(temp.Combine(".harness-config", "runner", ".secrets", "token.env"), "secret");

        await harness.RunGitAsync(temp.Path, ["add", "-A"], TestContext.Current.CancellationToken);
        var tracked = await harness.RunGitAsync(
            temp.Path,
            ["ls-files"],
            TestContext.Current.CancellationToken);

        var files = tracked.OutputLines;

        Assert.Contains(".harness-config/config.json", files);
        Assert.DoesNotContain(".harness-config/worktrees/.gitkeep", files);
        Assert.Contains(".harness-config/sshItems/.gitkeep", files);
        Assert.Contains(".harness-config/wslDistros/.gitkeep", files);
        Assert.Contains(".harness-config/runner/.env/.gitkeep", files);
        Assert.Contains(".harness-config/runner/.secrets/.gitkeep", files);

        Assert.DoesNotContain(".harness-config/lock.json", files);
        Assert.DoesNotContain(files, file => file.Contains("wt-a", StringComparison.Ordinal));
        Assert.DoesNotContain(files, file => file.Contains("/runs/", StringComparison.Ordinal));
        Assert.DoesNotContain(files, file => file.Contains("probe-host", StringComparison.Ordinal));
        Assert.DoesNotContain(files, file => file.Contains("probe-distro", StringComparison.Ordinal));
        Assert.DoesNotContain(files, file => file.EndsWith("token.env", StringComparison.Ordinal));
    }

    /// <summary>
    /// A lane adopting the harness adopts it on its own branch: its configuration - the main
    /// checkout's, which it was running with - its .gitignore and its placeholders are written in the
    /// worktree, and the main checkout is left exactly as it was. Written there instead, the lane's
    /// .gitignore never changed and main's did.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_FromAWorktree_WritesThatTree_AndLeavesTheMainCheckoutAsItIs()
    {
        using var repository = new TempDirectory();
        using var elsewhere = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;

        await harness.InitializeGitRepositoryAsync(repository.Path, token);

        // The lane's branch predates the harness; main adopts it afterwards.
        var worktree = elsewhere.Combine("lane");
        await harness.RunGitAsync(repository.Path, ["worktree", "add", "--detach", worktree], token);
        await harness.InitService.InitializeAsync(repository.Path, token);
        harness.WriteConfig(repository.Path, new HarnessConfig { Legs = { ["lane-leg"] = new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug" } }, BuildConfigs = { ["debug"] = new BuildConfiguration() } });
        await harness.CommitAllAsync(repository.Path, "harness", token);

        var mainIgnore = File.ReadAllText(repository.Combine(".gitignore"));
        var mainConfig = File.ReadAllText(repository.Combine(".harness-config", "config.json"));

        var outcome = await harness.InitService.InitializeAsync(worktree, token);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Contains(worktree, outcome.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(mainConfig, File.ReadAllText(Path.Combine(worktree, ".harness-config", "config.json")));
        Assert.Contains(
            outcome.Details!,
            line => line == "created .harness-config/config.json (copied from the main checkout's, which this worktree was running with)");
        Assert.Contains(GitIgnoreManager.BeginMarker, File.ReadAllText(Path.Combine(worktree, ".gitignore")), StringComparison.Ordinal);

        foreach (var directory in HarnessLayout.PlaceholderDirectories)
        {
            Assert.True(File.Exists(Path.Combine(worktree, ".harness-config", directory, ".gitkeep")), directory);
        }

        Assert.Contains(
            outcome.Details!,
            line => line.StartsWith("note    this is a worktree", StringComparison.Ordinal)
                && line.Contains(repository.Combine(".harness-config"), StringComparison.OrdinalIgnoreCase));

        // Nothing written in the main checkout: its files are as they were, and git sees no change.
        Assert.Equal(mainIgnore, File.ReadAllText(repository.Combine(".gitignore")));
        Assert.Empty((await harness.RunGitAsync(repository.Path, ["status", "--porcelain"], token)).OutputLines);
    }

    [Fact]
    public async Task InitializeAsync_FromAWorktreeWhoseBranchHasNoRegistries_CreatesThemInThatWorktree()
    {
        // A registry git tracks belongs to the branch, and every anchor command run in the worktree
        // looks for it there. Created in the main checkout instead, it would be found by none of them,
        // and running init again would only report it as already present.
        using var repository = new TempDirectory();
        using var elsewhere = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(repository.Path, cancellationToken);

        var worktree = elsewhere.Combine("wt");
        await harness.RunGitAsync(repository.Path, ["worktree", "add", "--detach", worktree], cancellationToken);

        var outcome = await harness.InitService.InitializeAsync(worktree, cancellationToken);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.False(Directory.Exists(repository.Combine(".plans")));
        Assert.False(Directory.Exists(repository.Combine(".harness-config")));
        Assert.True(File.Exists(Path.Combine(worktree, ".harness-config", "config.json")));
        Assert.True(File.Exists(Path.Combine(worktree, ".plans", "_deferred-anchor-registry-done.md")));

        await harness.AnchorRegistryService.WriteAsync(
            worktree, new AnchorWriteRequest("D-AREA-TOPIC-ONE", "P1", "t"), dryRun: false, cancellationToken);

        Assert.Contains(
            "D-AREA-TOPIC-ONE",
            File.ReadAllText(Path.Combine(worktree, ".plans", "_deferred-anchor-registry.md")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAsync_FromAWorktree_CreatesAnIgnoredRegistryInTheMainCheckout()
    {
        using var repository = new TempDirectory();
        using var elsewhere = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await harness.InitializeGitRepositoryAsync(repository.Path, cancellationToken);
        repository.WriteFile(".gitignore", "local/\n");
        await harness.CommitAllAsync(repository.Path, "ignore local", cancellationToken);

        var worktree = elsewhere.Combine("wt");
        await harness.RunGitAsync(repository.Path, ["worktree", "add", "--detach", worktree], cancellationToken);
        harness.WriteConfig(repository.Path, new HarnessConfig
        {
            Anchors = new AnchorSettings { PendingAnchorsPath = "local/pending.md", DoneAnchorsPath = "local/done.md" },
        });

        var outcome = await harness.InitService.InitializeAsync(worktree, cancellationToken);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.True(File.Exists(repository.Combine("local", "pending.md")));
        Assert.True(File.Exists(repository.Combine("local", "done.md")));
        Assert.False(Directory.Exists(Path.Combine(worktree, "local")));
    }
}
