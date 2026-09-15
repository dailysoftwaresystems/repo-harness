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
        Assert.True(File.Exists(temp.Combine(".harness-config", "worktrees", ".gitkeep")));
        Assert.True(File.Exists(temp.Combine(".harness-config", "ssh", ".gitkeep")));
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
        Assert.Contains("root", config.Targets.Keys);
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

    [Fact]
    public async Task InitializedRepository_TracksOnlyThePlaceholders_AndIgnoresTheRest()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeGitRepositoryAsync(temp.Path, TestContext.Current.CancellationToken);
        await harness.InitService.InitializeAsync(temp.Path, TestContext.Current.CancellationToken);

        // Write the kinds of files that must never reach the repository: a lock, a
        // worktree's contents, and an ssh secret.
        File.WriteAllText(temp.Combine(".harness-config", "lock.json"), "{}");
        Directory.CreateDirectory(temp.Combine(".harness-config", "worktrees", "wt-a", "nested"));
        File.WriteAllText(temp.Combine(".harness-config", "worktrees", "wt-a", "nested", "file.txt"), "x");
        Directory.CreateDirectory(temp.Combine(".harness-config", "ssh", "vps"));
        File.WriteAllText(temp.Combine(".harness-config", "ssh", "vps", ".secret"), "secret");

        await harness.RunGitAsync(temp.Path, ["add", "-A"], TestContext.Current.CancellationToken);
        var tracked = await harness.RunGitAsync(
            temp.Path,
            ["ls-files"],
            TestContext.Current.CancellationToken);

        var files = tracked.OutputLines;

        Assert.Contains(".harness-config/config.json", files);
        Assert.Contains(".harness-config/worktrees/.gitkeep", files);
        Assert.Contains(".harness-config/ssh/.gitkeep", files);

        Assert.DoesNotContain(".harness-config/lock.json", files);
        Assert.DoesNotContain(files, file => file.Contains("wt-a", StringComparison.Ordinal));
        Assert.DoesNotContain(files, file => file.EndsWith(".secret", StringComparison.Ordinal));
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
