using RepoHarness.Core.Configuration;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// What delete-worktree finds before it deletes anything, against real git: every kind of work a
/// deletion would lose, and the worktrees it must still delete without --force.
/// </summary>
public sealed class WorktreeDeletionTests
{
    private static readonly WorktreeSettings Relaxed = new() { PathBudgetReserve = 5, PathBudgetMargin = 2 };

    [Fact]
    public async Task ALockedWorktree_IsRefusedWithItsReason_AndForcedRemovesItAndItsRecord()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "locked");
        await harness.RunGitAsync(temp.Path, ["worktree", "lock", "--reason", "on a USB disk", path], cancellationToken);

        var refused = await harness.WorktreeService.DeleteAsync(temp.Path, "locked", force: false, cancellationToken);

        Assert.Equal(HarnessExit.Refused, refused.Outcome.ExitCode);
        Assert.Contains("it is locked: on a USB disk", refused.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains("git worktree unlock", refused.Outcome.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(path));

        var forced = await harness.WorktreeService.DeleteAsync(temp.Path, "locked", force: true, cancellationToken);

        Assert.True(forced.Succeeded, forced.Outcome.Message);
        Assert.False(Directory.Exists(path));
        await AssertNotRegisteredAsync(harness, temp, path);
    }

    [Fact]
    public async Task ACommitOnlyADetachedHeadNames_IsRefused_WithHowToKeepIt()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "detached");
        File.WriteAllText(Path.Combine(path, "work.txt"), "committed here only");
        await harness.CommitAllAsync(path, "work", cancellationToken);
        var head = (await harness.GitClient.ResolveCommitAsync(path, "HEAD", cancellationToken))![..12];

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "detached", force: false, cancellationToken);

        Assert.Equal(HarnessExit.Refused, outcome.Outcome.ExitCode);
        Assert.Contains($"1 commit(s) up to {head}", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains($"git branch <name> {head}", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public async Task ACommitOnABranch_IsNotRefused_AndTheBranchKeepsIt()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, new WorktreeSettings { PathBudgetReserve = 5, PathBudgetMargin = 2, Detach = false });
        var path = await CreateAsync(harness, temp, "onbranch");
        File.WriteAllText(Path.Combine(path, "work.txt"), "committed on a branch");
        await harness.CommitAllAsync(path, "work", cancellationToken);
        var head = await harness.GitClient.ResolveCommitAsync(path, "HEAD", cancellationToken);

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "onbranch", force: false, cancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        Assert.Equal(head, await harness.GitClient.ResolveCommitAsync(temp.Path, "refs/heads/onbranch", cancellationToken));
    }

    [Fact]
    public async Task ACommitARemoteTrackingRefContains_IsNotRefused()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "pushed");
        File.WriteAllText(Path.Combine(path, "work.txt"), "pushed elsewhere");
        await harness.CommitAllAsync(path, "work", cancellationToken);
        var head = (await harness.GitClient.ResolveCommitAsync(path, "HEAD", cancellationToken))!;
        await harness.RunGitAsync(temp.Path, ["update-ref", "refs/remotes/origin/pushed", head], cancellationToken);

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "pushed", force: false, cancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
    }

    [Theory]
    [InlineData("--assume-unchanged")]
    [InlineData("--skip-worktree")]
    public async Task AnEditStatusCannotSee_IsRefused(string flag)
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "hidden");
        await harness.RunGitAsync(path, ["update-index", flag, "README.md"], cancellationToken);
        File.WriteAllText(Path.Combine(path, "README.md"), "edited where status does not look");
        Assert.Empty(await harness.GitClient.GetStatusAsync(path, cancellationToken));

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "hidden", force: false, cancellationToken);

        Assert.Equal(HarnessExit.Refused, outcome.Outcome.ExitCode);
        Assert.Contains("1 uncommitted change(s) that would be lost: README.md", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(path, "README.md")));
    }

    [Fact]
    public async Task ASkipWorktreeFileAbsentFromDisk_IsNotAChange()
    {
        // A sparse checkout leaves files it does not want absent, and nothing absent can be lost.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "sparse");
        await harness.RunGitAsync(path, ["update-index", "--skip-worktree", "README.md"], cancellationToken);
        File.Delete(Path.Combine(path, "README.md"));

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "sparse", force: false, cancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task AFreshSubmoduleWithNothingNew_IsDeletedWithoutForce()
    {
        // Refusing every submodule would leave --force, which skips every other check, as the
        // only way to delete such a worktree.
        using var temp = new TempDirectory();
        using var library = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, path) = await PrepareWithSubmoduleAsync(temp, library, "fresh");

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "fresh", force: false, cancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        Assert.False(Directory.Exists(path));
        await AssertNotRegisteredAsync(harness, temp, path);
    }

    [Fact]
    public async Task ASubmoduleCommitNoRemoteTrackingRefContains_IsRefused()
    {
        // The submodule's repository lives in this worktree's git directory, so the commit is
        // deleted with it, even once the superproject records it.
        using var temp = new TempDirectory();
        using var library = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, path) = await PrepareWithSubmoduleAsync(temp, library, "subcommit");
        var submodule = Path.Combine(path, "lib");
        File.WriteAllText(Path.Combine(submodule, "feature.txt"), "only in this worktree");
        await harness.RunGitAsync(submodule, ["add", "feature.txt"], cancellationToken);
        await harness.RunGitAsync(
            submodule,
            ["-c", "user.email=harness@test.invalid", "-c", "user.name=Harness Test", "commit", "--quiet", "-m", "feature"],
            cancellationToken);
        await harness.CommitAllAsync(path, "record the submodule's commit", cancellationToken);

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "subcommit", force: false, cancellationToken);

        Assert.Equal(HarnessExit.Refused, outcome.Outcome.ExitCode);
        Assert.Contains("submodule 'lib' holds 1 commit(s) no remote-tracking ref contains", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(submodule, "feature.txt")));
    }

    [Fact]
    public async Task UncommittedWorkInASubmodule_IsRefused_ThroughTheStatus()
    {
        using var temp = new TempDirectory();
        using var library = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, path) = await PrepareWithSubmoduleAsync(temp, library, "subdirty");
        File.WriteAllText(Path.Combine(path, "lib", "README.md"), "changed inside the submodule");

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "subdirty", force: false, cancellationToken);

        Assert.Equal(HarnessExit.Refused, outcome.Outcome.ExitCode);
        Assert.Contains("1 uncommitted change(s) that would be lost: lib", outcome.Outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChangedSubmodule_IsRefused_EvenWhenConfigurationIgnoresIt()
    {
        // This setting hides the change from a plain status, and a worktree holding submodules is
        // removed past git's own check, so only --ignore-submodules=none stands in the way.
        using var temp = new TempDirectory();
        using var library = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, path) = await PrepareWithSubmoduleAsync(temp, library, "subhidden");
        await harness.RunGitAsync(temp.Path, ["config", "submodule.lib.ignore", "all"], cancellationToken);
        File.WriteAllText(Path.Combine(path, "lib", "README.md"), "changed inside the submodule");

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "subhidden", force: false, cancellationToken);

        Assert.Equal(HarnessExit.Refused, outcome.Outcome.ExitCode);
        Assert.Contains("that would be lost: lib", outcome.Outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryReason_IsReportedTogether_OnOneLine()
    {
        // Forcing past one reason must not unknowingly lose what another one guards.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "several");
        File.WriteAllText(Path.Combine(path, "work.txt"), "committed here only");
        await harness.CommitAllAsync(path, "work", cancellationToken);
        File.WriteAllText(Path.Combine(path, "notes.txt"), "never committed");
        await harness.RunGitAsync(temp.Path, ["worktree", "lock", path], cancellationToken);

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "several", force: false, cancellationToken);
        var message = outcome.Outcome.Message;

        Assert.Equal(HarnessExit.Refused, outcome.Outcome.ExitCode);
        Assert.DoesNotContain('\n', message);
        Assert.Contains("1 uncommitted change(s) that would be lost: notes.txt", message, StringComparison.Ordinal);
        Assert.Contains("1 commit(s) up to", message, StringComparison.Ordinal);
        Assert.Contains("it is locked", message, StringComparison.Ordinal);
        Assert.EndsWith("or pass --force to delete it anyway.", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARecordWhoseDirectoryIsGone_IsCleared_SoTheNameCanBeUsedAgain()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "gone");
        harness.FileSystem.DeleteDirectory(path);

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "gone", force: false, cancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        await AssertNotRegisteredAsync(harness, temp, path);

        var recreated = await harness.WorktreeService.CreateAsync(temp.Path, "gone", useRandomName: false, cancellationToken);
        Assert.True(recreated.Succeeded, recreated.Outcome.Message);
    }

    [Fact]
    public async Task AnotherRepositoryAtTheWorktreePath_IsRefusedAndKept_UntilForced()
    {
        // A clone there has a clean status of its own, and deleting it takes its whole history.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var path = HarnessFactory.WorktreePath(temp.Path, "clone");
        await harness.RunGitAsync(temp.Path, ["clone", "--quiet", temp.Path, path], cancellationToken);

        var refused = await harness.WorktreeService.DeleteAsync(temp.Path, "clone", force: false, cancellationToken);

        Assert.Equal(HarnessExit.Refused, refused.Outcome.ExitCode);
        Assert.Contains("'clone' is not a worktree of this repository", refused.Outcome.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Combine(path, ".git")));

        var forced = await harness.WorktreeService.DeleteAsync(temp.Path, "clone", force: true, cancellationToken);

        Assert.True(forced.Succeeded, forced.Outcome.Message);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task AFileNameHoldingANewline_KeepsTheRefusalOnOneLine()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows does not allow a newline in a file name.");

        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "newline");
        File.WriteAllText(Path.Combine(path, "two\nlines.txt"), "never committed");

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "newline", force: false, cancellationToken);

        Assert.Equal(HarnessExit.Refused, outcome.Outcome.ExitCode);
        Assert.DoesNotContain('\n', outcome.Outcome.Message);
        Assert.Contains("\"two\\nlines.txt\"", outcome.Outcome.Message, StringComparison.Ordinal);
    }

    private static async Task<HarnessFactory> PrepareAsync(TempDirectory temp, WorktreeSettings? settings = null)
    {
        var harness = new HarnessFactory();

        await harness.InitializeHarnessAsync(
            temp.Path,
            TestContext.Current.CancellationToken,
            new HarnessConfig { Worktrees = settings ?? Relaxed });

        return harness;
    }

    private static async Task<string> CreateAsync(HarnessFactory harness, TempDirectory temp, string name)
    {
        var outcome = await harness.WorktreeService.CreateAsync(
            temp.Path, name, useRandomName: false, TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        return HarnessFactory.WorktreePath(temp.Path, name);
    }

    /// <summary>
    /// A repository whose commit records the submodule lib, and a worktree of it with lib
    /// initialised, which puts lib's repository in the worktree's own git directory.
    /// </summary>
    private static async Task<(HarnessFactory Harness, string Path)> PrepareWithSubmoduleAsync(
        TempDirectory temp,
        TempDirectory library,
        string name)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.InitializeGitRepositoryAsync(library.Path, cancellationToken);

        // git refuses a submodule from a local path unless file transport is allowed for the command.
        await harness.RunGitAsync(
            temp.Path,
            ["-c", "protocol.file.allow=always", "submodule", "--quiet", "add", library.Path.Replace('\\', '/'), "lib"],
            cancellationToken);
        await harness.CommitAllAsync(temp.Path, "add lib", cancellationToken);

        var path = await CreateAsync(harness, temp, name);
        await harness.RunGitAsync(
            path,
            ["-c", "protocol.file.allow=always", "submodule", "--quiet", "update", "--init"],
            cancellationToken);

        return (harness, path);
    }

    private static async Task AssertNotRegisteredAsync(HarnessFactory harness, TempDirectory temp, string path)
    {
        var worktrees = await harness.GitClient.ListWorktreesAsync(temp.Path, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(worktrees, worktree => PathAssert.AreSame(path, worktree.Path));
    }
}
