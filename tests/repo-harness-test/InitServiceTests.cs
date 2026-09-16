using RepoHarness.Core.Anchors;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Git;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;

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

        Directory.CreateDirectory(Path.Combine(temp.Path, root, "wt-a", "src"));
        File.WriteAllText(Path.Combine(temp.Path, root, "wt-a", "src", "main.c"), "int main(void) { return 0; }");

        Assert.True(await IsIgnoredAsync(harness, temp.Path, $"{root}/", token), "present, asked with a slash");
        Assert.True(await IsIgnoredAsync(harness, temp.Path, root, token), "present, asked without a slash");
        Assert.True(await IsIgnoredAsync(harness, temp.Path, $"{root}/wt-a/src/main.c", token), "a file inside a worktree");

        var ignore = File.ReadAllText(temp.Combine(".gitignore"));
        Assert.Contains($"/{root}/\n", ignore, StringComparison.Ordinal);
        Assert.DoesNotContain($"/{root}/*", ignore, StringComparison.Ordinal);
        Assert.DoesNotContain($"!/{root}/", ignore, StringComparison.Ordinal);
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

    [Fact]
    public async Task AHandWrittenRuleForAManagedPath_IsReported_AndLeftWhereItIs()
    {
        // A repository that already ignored the root by hand keeps its own rule and gains the
        // managed one, and two statements of one path is a state nothing else points out.
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;

        await harness.InitializeGitRepositoryAsync(temp.Path, token);
        temp.WriteFile(".gitignore", "bin/\n.harness-config/worktrees/\n!/.harness-config/sshItems/*\n");

        var outcome = await harness.InitService.InitializeAsync(temp.Path, token);

        Assert.True(outcome.Succeeded, outcome.Message);

        var notes = outcome.Details!.Where(line => line.StartsWith("note", StringComparison.Ordinal)).ToList();

        Assert.Equal(2, notes.Count);
        Assert.Contains(notes, note => note.Contains("line 2", StringComparison.Ordinal)
            && note.Contains("repeats the managed block's rule for '.harness-config/worktrees'", StringComparison.Ordinal));
        Assert.Contains(notes, note => note.Contains("line 3", StringComparison.Ordinal)
            && note.Contains("re-includes '.harness-config/sshItems', which the managed block ignores", StringComparison.Ordinal));

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

    [Fact]
    public async Task InitializeAsync_TargetsTheMainCheckout_WhenRunFromInsideAWorktree()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;

        await harness.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        await harness.InitService.InitializeAsync(temp.Path, cancellationToken);
        await harness.RunGitAsync(temp.Path, ["add", "-A"], cancellationToken);
        await harness.RunGitAsync(temp.Path, ["commit", "--quiet", "-m", "harness"], cancellationToken);

        var worktreePath = temp.Combine(".harness-config", "worktrees", "wt-a");
        await harness.RunGitAsync(
            temp.Path,
            ["worktree", "add", "--detach", worktreePath],
            cancellationToken);

        // Running init from inside the worktree must not create a second, nested
        // harness state directory: the worktree already carries the tracked parts.
        var outcome = await harness.InitService.InitializeAsync(worktreePath, cancellationToken);

        Assert.True(outcome.Succeeded);
        Assert.False(Directory.Exists(Path.Combine(worktreePath, ".harness-config", "worktrees", "wt-a")));
        Assert.Contains(temp.Path, outcome.Message, StringComparison.OrdinalIgnoreCase);
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
