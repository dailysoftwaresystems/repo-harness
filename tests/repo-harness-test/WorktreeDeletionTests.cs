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
        Assert.Contains("submodule 'lib' holds 1 commit(s) no remote-tracking ref or tag contains", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(submodule, "feature.txt")));
    }

    [Fact]
    public async Task ADeinitialisedSubmoduleWithAnUnpushedCommit_IsRefused()
    {
        // Deinitialising empties the checkout, and status then reports nothing, but the repository,
        // commit and all, stays in the worktree's git directory and is deleted with it.
        using var temp = new TempDirectory();
        using var library = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, path) = await PrepareWithSubmoduleAsync(temp, library, "deinit");
        await CommitInSubmoduleAsync(harness, Path.Combine(path, "lib"));
        await harness.RunGitAsync(path, ["submodule", "--quiet", "deinit", "--force", "lib"], cancellationToken);

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "deinit", force: false, cancellationToken);

        Assert.Equal(HarnessExit.Refused, outcome.Outcome.ExitCode);
        Assert.Contains("submodule 'lib' holds 1 commit(s)", outcome.Outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeinitialisedSubmoduleWithNothingUnpushed_IsDeletedWithoutForce()
    {
        using var temp = new TempDirectory();
        using var library = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, path) = await PrepareWithSubmoduleAsync(temp, library, "deinitok");
        await harness.RunGitAsync(path, ["submodule", "--quiet", "deinit", "--force", "lib"], cancellationToken);

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "deinitok", force: false, cancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task ARecordWhoseDirectoryIsGone_IsRefused_WhenItsSubmoduleRepositoryHoldsAnUnpushedCommit()
    {
        // Clearing the record deletes its git directory, where the submodule's repository lives,
        // and git's own removal checks nothing once the worktree's directory is gone.
        using var temp = new TempDirectory();
        using var library = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, path) = await PrepareWithSubmoduleAsync(temp, library, "subgone");
        await CommitInSubmoduleAsync(harness, Path.Combine(path, "lib"));
        harness.FileSystem.DeleteDirectory(path);

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "subgone", force: false, cancellationToken);

        Assert.Equal(HarnessExit.Refused, outcome.Outcome.ExitCode);
        Assert.Contains("submodule 'lib' holds 1 commit(s)", outcome.Outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANestedSubmoduleWithAnUnpushedCommit_IsRefused()
    {
        using var temp = new TempDirectory();
        using var library = new TempDirectory();
        using var inner = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.InitializeGitRepositoryAsync(inner.Path, cancellationToken);
        await harness.InitializeGitRepositoryAsync(library.Path, cancellationToken);
        await AddSubmoduleAsync(harness, library.Path, inner, "inner");
        await harness.CommitAllAsync(library.Path, "add inner", cancellationToken);
        await AddSubmoduleAsync(harness, temp.Path, library, "lib");
        await harness.CommitAllAsync(temp.Path, "add lib", cancellationToken);
        var path = await CreateAsync(harness, temp, "nested");
        await harness.RunGitAsync(
            path,
            ["-c", "protocol.file.allow=always", "submodule", "--quiet", "update", "--init", "--recursive"],
            cancellationToken);
        await CommitInSubmoduleAsync(harness, Path.Combine(path, "lib", "inner"));

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "nested", force: false, cancellationToken);

        Assert.Equal(HarnessExit.Refused, outcome.Outcome.ExitCode);
        Assert.Contains("submodule 'lib/inner' holds 1 commit(s)", outcome.Outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASubmoduleHoldingAStash_IsRefused()
    {
        using var temp = new TempDirectory();
        using var library = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, path) = await PrepareWithSubmoduleAsync(temp, library, "substash");
        var submodule = Path.Combine(path, "lib");
        File.WriteAllText(Path.Combine(submodule, "README.md"), "stashed inside the submodule");
        await harness.RunGitAsync(
            submodule,
            ["-c", "user.email=harness@test.invalid", "-c", "user.name=Harness Test", "stash", "--quiet"],
            cancellationToken);

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "substash", force: false, cancellationToken);

        Assert.Equal(HarnessExit.Refused, outcome.Outcome.ExitCode);
        Assert.Contains("submodule 'lib' holds a stash", outcome.Outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUpstreamTagOnACommitNoRemoteBranchHolds_DoesNotRefuseASubmodule()
    {
        // A release tag whose branch was deleted names a commit only tags reach. Counting tags
        // among what is at risk would refuse every worktree holding the submodule.
        using var temp = new TempDirectory();
        using var library = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.InitializeGitRepositoryAsync(library.Path, cancellationToken);
        await harness.RunGitAsync(library.Path, ["switch", "--quiet", "-c", "release"], cancellationToken);
        await harness.CommitAllAsync(library.Path, "release", cancellationToken);
        await harness.RunGitAsync(library.Path, ["tag", "v1"], cancellationToken);
        await harness.RunGitAsync(library.Path, ["switch", "--quiet", "-"], cancellationToken);
        await harness.RunGitAsync(library.Path, ["branch", "--quiet", "-D", "release"], cancellationToken);
        await AddSubmoduleAsync(harness, temp.Path, library, "lib");
        await harness.CommitAllAsync(temp.Path, "add lib", cancellationToken);
        var path = await CreateAsync(harness, temp, "tagged");
        await harness.RunGitAsync(path, ["-c", "protocol.file.allow=always", "submodule", "--quiet", "update", "--init"], cancellationToken);

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "tagged", force: false, cancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
    }

    [Fact]
    public async Task AFreshWorktreeOfADetachedMainCheckout_IsDeletedWithoutForce()
    {
        // The main checkout's HEAD names the commit, so a worktree created there loses nothing,
        // though no branch, tag or remote-tracking ref contains it.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.RunGitAsync(temp.Path, ["switch", "--quiet", "--detach"], cancellationToken);
        await harness.CommitAllAsync(temp.Path, "on a detached HEAD", cancellationToken);
        var path = await CreateAsync(harness, temp, "offmain");

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "offmain", force: false, cancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task ACommitTheNewestStashContains_IsNotRefused()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "stashed");
        File.WriteAllText(Path.Combine(path, "work.txt"), "committed on a detached HEAD");
        await harness.CommitAllAsync(path, "work", cancellationToken);
        File.WriteAllText(Path.Combine(path, "work.txt"), "then changed and stashed");
        await harness.RunGitAsync(path, ["stash", "--quiet"], cancellationToken);

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "stashed", force: false, cancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        Assert.NotNull(await harness.GitClient.ResolveCommitAsync(temp.Path, "refs/stash", cancellationToken));
    }

    [Theory]
    [InlineData("--assume-unchanged")]
    [InlineData("--skip-worktree")]
    public async Task AnUntouchedMarkedFileStoredWithCrlf_IsNotAChange_UnderAutocrlf(string flag)
    {
        // core.autocrlf=true is the Git for Windows installer's default. A file whose stored copy
        // already has CRLF endings hashes differently on its own, but status leaves such endings alone.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareWithCrlfFileAsync(temp);
        var path = await CreateAsync(harness, temp, "crlf");
        await harness.RunGitAsync(path, ["update-index", flag, "crlf.txt"], cancellationToken);

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "crlf", force: false, cancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
    }

    [Theory]
    [InlineData("--assume-unchanged")]
    [InlineData("--skip-worktree")]
    public async Task AnEditedMarkedFileStoredWithCrlf_IsRefused_UnderAutocrlf(string flag)
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareWithCrlfFileAsync(temp);
        var path = await CreateAsync(harness, temp, "crlf");
        await harness.RunGitAsync(path, ["update-index", flag, "crlf.txt"], cancellationToken);
        File.WriteAllText(Path.Combine(path, "crlf.txt"), "edited\r\nwhere status does not look\r\n");

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "crlf", force: false, cancellationToken);

        Assert.Equal(HarnessExit.Refused, outcome.Outcome.ExitCode);
        Assert.Contains("1 uncommitted change(s) that would be lost: crlf.txt", outcome.Outcome.Message, StringComparison.Ordinal);
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

    [Fact]
    public async Task AnUnbornWorktreeBesideIt_DoesNotStopADelete()
    {
        // git lists an unborn HEAD as the null object id. Handed to git as a commit, it failed
        // every check that asked about other worktrees' HEADs.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var unborn = await CreateAsync(harness, temp, "orphan");
        await harness.RunGitAsync(unborn, ["checkout", "--quiet", "--orphan", "never-committed"], cancellationToken);
        var path = await CreateAsync(harness, temp, "beside");

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "beside", force: false, cancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task AnUnbornRecordWhoseDirectoryIsGone_IsCleared()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "unborn");
        await harness.RunGitAsync(path, ["checkout", "--quiet", "--orphan", "never-committed"], cancellationToken);
        harness.FileSystem.DeleteDirectory(path);

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "unborn", force: false, cancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        await AssertNotRegisteredAsync(harness, temp, path);
    }

    [Fact]
    public async Task ASubmoduleRepositoryGitCannotRead_FailsAndDeletesNothing()
    {
        // With its HEAD emptied, as a crash can leave it, git no longer reads the repository, while
        // its branch, with a commit found nowhere else, is still on disk.
        using var temp = new TempDirectory();
        using var library = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var (harness, path) = await PrepareWithSubmoduleAsync(temp, library, "corrupt");
        var submodule = Path.Combine(path, "lib");
        await harness.RunGitAsync(submodule, ["switch", "--quiet", "-c", "feature"], cancellationToken);
        await CommitInSubmoduleAsync(harness, submodule);
        var repository = (await harness.RunGitAsync(submodule, ["rev-parse", "--absolute-git-dir"], cancellationToken)).StandardOutput.Trim();
        await harness.RunGitAsync(path, ["submodule", "--quiet", "deinit", "--force", "lib"], cancellationToken);
        File.WriteAllText(Path.Combine(repository, "HEAD"), string.Empty);

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "corrupt", force: false, cancellationToken);

        Assert.Equal(HarnessExit.CommandFailed, outcome.Outcome.ExitCode);
        Assert.Contains("git does not read", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was deleted", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(path));
        Assert.True(File.Exists(Path.Combine(repository, "refs", "heads", "feature")));
    }

    [Fact]
    public async Task AHiddenEditUnderASplitIndex_IsRefused_AndTheIndexFilesAreUntouched()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "split");
        await harness.RunGitAsync(path, ["update-index", "--split-index"], cancellationToken);
        await harness.RunGitAsync(path, ["update-index", "--assume-unchanged", "README.md"], cancellationToken);
        File.WriteAllText(Path.Combine(path, "README.md"), "edited where status does not look");
        var gitDirectory = (await harness.RunGitAsync(path, ["rev-parse", "--absolute-git-dir"], cancellationToken)).StandardOutput.Trim();
        var before = IndexFiles(gitDirectory);
        Assert.Contains(before.Keys, file => file.StartsWith("sharedindex.", StringComparison.Ordinal));

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "split", force: false, cancellationToken);

        Assert.Equal(HarnessExit.Refused, outcome.Outcome.ExitCode);
        Assert.Contains("that would be lost: README.md", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.Equal(before, IndexFiles(gitDirectory));
    }

    [Fact]
    public async Task AnEditedFileOutsideASparseIndexCone_IsRefused()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        temp.WriteFile(Path.Combine("outside", "file.txt"), "outside the cone");
        await harness.CommitAllAsync(temp.Path, "a directory outside the cone", cancellationToken);
        var path = await CreateAsync(harness, temp, "cone");
        await harness.RunGitAsync(path, ["sparse-checkout", "init", "--cone", "--sparse-index"], cancellationToken);
        Assert.False(File.Exists(Path.Combine(path, "outside", "file.txt")));
        Directory.CreateDirectory(Path.Combine(path, "outside"));
        File.WriteAllText(Path.Combine(path, "outside", "file.txt"), "edited outside the cone");

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "cone", force: false, cancellationToken);

        Assert.Equal(HarnessExit.Refused, outcome.Outcome.ExitCode);
        Assert.Contains("outside/file.txt", outcome.Outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWorktreeMovedByHand_IsRefused_AndItsCommitIsStillCounted()
    {
        // git still lists the record at the old path, with this worktree's HEAD. Told apart from the
        // other worktrees by path, that HEAD would pass for another's and hide the commit.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var original = await CreateAsync(harness, temp, "before");
        File.WriteAllText(Path.Combine(original, "work.txt"), "committed here only");
        await harness.CommitAllAsync(original, "work", cancellationToken);
        var head = (await harness.GitClient.ResolveCommitAsync(original, "HEAD", cancellationToken))![..12];
        var moved = HarnessFactory.WorktreePath(temp.Path, "after");
        Directory.Move(original, moved);

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "after", force: false, cancellationToken);

        Assert.Equal(HarnessExit.Refused, outcome.Outcome.ExitCode);
        Assert.Contains($"1 commit(s) up to {head}", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains("as after moving it by hand", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains(" worktree repair ", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(moved));
    }

    /// <summary>The index and every shared index file in a worktree's git directory, by name, with their bytes.</summary>
    private static Dictionary<string, string> IndexFiles(string gitDirectory)
        => Directory.EnumerateFiles(gitDirectory)
            .Where(file => Path.GetFileName(file) is "index" || Path.GetFileName(file).StartsWith("sharedindex.", StringComparison.Ordinal))
            .ToDictionary(file => Path.GetFileName(file), file => Convert.ToBase64String(File.ReadAllBytes(file)), StringComparer.Ordinal);

    /// <summary>Adds <paramref name="library"/> to <paramref name="superproject"/> as the submodule <paramref name="name"/>.</summary>
    private static Task AddSubmoduleAsync(HarnessFactory harness, string superproject, TempDirectory library, string name)
        => harness.RunGitAsync(
            superproject,
            ["-c", "protocol.file.allow=always", "submodule", "--quiet", "add", library.Path.Replace('\\', '/'), name],
            TestContext.Current.CancellationToken);

    /// <summary>Commits a new file inside a checked-out submodule, where no remote-tracking ref has it.</summary>
    private static async Task CommitInSubmoduleAsync(HarnessFactory harness, string submodule)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        File.WriteAllText(Path.Combine(submodule, "feature.txt"), "only in this worktree");

        await harness.RunGitAsync(submodule, ["add", "feature.txt"], cancellationToken);
        await harness.RunGitAsync(
            submodule,
            ["-c", "user.email=harness@test.invalid", "-c", "user.name=Harness Test", "commit", "--quiet", "-m", "feature"],
            cancellationToken);
    }

    /// <summary>
    /// A repository whose commit stores crlf.txt with CRLF endings as they are, set to
    /// core.autocrlf=true afterwards, as the Git for Windows installer sets it.
    /// </summary>
    private static async Task<HarnessFactory> PrepareWithCrlfFileAsync(TempDirectory temp)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        temp.WriteFile("crlf.txt", "stored\r\nwith crlf\r\n");

        await harness.RunGitAsync(temp.Path, ["-c", "core.autocrlf=false", "add", "crlf.txt"], cancellationToken);
        await harness.RunGitAsync(temp.Path, ["-c", "core.autocrlf=false", "commit", "--quiet", "-m", "crlf"], cancellationToken);
        await harness.RunGitAsync(temp.Path, ["config", "core.autocrlf", "true"], cancellationToken);

        return harness;
    }

    private static async Task AssertNotRegisteredAsync(HarnessFactory harness, TempDirectory temp, string path)
    {
        var worktrees = await harness.GitClient.ListWorktreesAsync(temp.Path, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(worktrees, worktree => PathAssert.AreSame(path, worktree.Path));
    }
}
