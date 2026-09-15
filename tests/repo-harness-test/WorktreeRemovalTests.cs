using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Git;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;
using RepoHarness.Core.Worktrees;

namespace RepoHarness.Tests;

/// <summary>
/// How delete-worktree removes a worktree once it has decided to: through a linked worktrees
/// directory, and when a delete, an interruption or git's record-keeping goes wrong part way.
/// </summary>
public sealed class WorktreeRemovalTests
{
    private static readonly WorktreeSettings Relaxed = new() { PathBudgetReserve = 5, PathBudgetMargin = 2 };

    [Fact]
    public async Task AWorktreeReachedThroughALinkedWorktreesDirectory_IsDeletedWithoutForce()
    {
        // git reports the worktree's paths with the link resolved, so a check comparing them with
        // the path the harness spelled would refuse every clean worktree here.
        using var temp = new TempDirectory();
        using var elsewhere = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var worktrees = Path.GetDirectoryName(HarnessFactory.WorktreePath(temp.Path, "linked"))!;
        harness.FileSystem.DeleteDirectory(worktrees);
        await LinkDirectoryAsync(harness, worktrees, elsewhere.Path);

        var created = await harness.WorktreeService.CreateAsync(temp.Path, "linked", useRandomName: false, cancellationToken);
        Assert.True(created.Succeeded, created.Outcome.Message);

        var outcome = await harness.WorktreeService.DeleteAsync(temp.Path, "linked", force: false, cancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        Assert.False(Directory.Exists(Path.Combine(elsewhere.Path, "linked")));
        Assert.Single(await harness.GitClient.ListWorktreesAsync(temp.Path, cancellationToken));
    }

    [Fact]
    public async Task AFallbackDeleteThatCannotFinish_FailsWithWhatToDoNext()
    {
        // A file another program holds open is no defect in the tool, and part of the worktree may
        // already be gone, which the message has to say.
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "held");

        // Without its .git file git cannot remove the worktree, which leaves the directory to the fallback.
        File.Delete(Path.Combine(path, ".git"));
        var service = Service(harness, harness.GitClient, new UndeletableFileSystem(harness.FileSystem));

        var outcome = await service.DeleteAsync(temp.Path, "held", force: true, cancellationToken);

        Assert.Equal(HarnessExit.CommandFailed, outcome.Outcome.ExitCode);
        Assert.StartsWith("Could not finish deleting '", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains("; part of it may already be gone: ", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.Contains("delete-worktree held --force", outcome.Outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInterruptionAfterTheChecks_DeletesNothing()
    {
        // Every check completes, and the interruption arrives just after them: removal must not start.
        using var temp = new TempDirectory();
        using var interruption = new CancellationTokenSource();
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "stopped");
        var git = new InterceptingGitClient(harness.GitClient) { IgnoreCancellation = true, BeforeEveryCall = interruption.Cancel };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(harness, git).DeleteAsync(temp.Path, "stopped", force: false, interruption.Token));

        Assert.True(Directory.Exists(path));
        Assert.DoesNotContain(git.Runs, run => run.Arguments is ["worktree", "remove", ..]);
        Assert.Equal(2, (await harness.GitClient.ListWorktreesAsync(temp.Path, TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task AnInterruptionDuringRemoval_DoesNotStopIt()
    {
        // git stopped halfway would leave the files and git's record partly gone.
        using var temp = new TempDirectory();
        using var interruption = new CancellationTokenSource();
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "finished");
        var git = new InterceptingGitClient(harness.GitClient)
        {
            BeforeRun = arguments =>
            {
                if (arguments is ["worktree", "remove", ..])
                {
                    interruption.Cancel();
                }
            },
        };

        var outcome = await Service(harness, git).DeleteAsync(temp.Path, "finished", force: false, interruption.Token);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        Assert.False(Directory.Exists(path));
        var removal = Assert.Single(git.Runs, run => run.Arguments is ["worktree", "remove", ..]);
        Assert.False(removal.Cancellable);
    }

    [Fact]
    public async Task AFailureReportedWhileClearingTheRecord_IsCheckedAgainstTheRecordItself()
    {
        // With the directory gone, git clears the record; what decides the outcome is whether the
        // record is gone, not what git's exit code said.
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "cleared");
        File.Delete(Path.Combine(path, ".git"));
        var removals = 0;
        var git = new InterceptingGitClient(harness.GitClient)
        {
            AfterRun = (arguments, result) => arguments is ["worktree", "remove", ..] && ++removals == 2
                ? new GitCommandResult(1, string.Empty, "error: reported failure after clearing the record")
                : result,
        };

        var outcome = await Service(harness, git).DeleteAsync(temp.Path, "cleared", force: true, TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded, outcome.Outcome.Message);
        Assert.Equal(2, removals);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task ARecordThatSurvivesTheDeletion_IsReported()
    {
        using var temp = new TempDirectory();
        var harness = await PrepareAsync(temp);
        var path = await CreateAsync(harness, temp, "kept");
        File.Delete(Path.Combine(path, ".git"));
        var removals = 0;
        var git = new InterceptingGitClient(harness.GitClient)
        {
            InsteadOfRun = arguments => arguments is ["worktree", "remove", ..] && ++removals == 2
                ? new GitCommandResult(1, string.Empty, "error: could not clear the record")
                : null,
        };

        var outcome = await Service(harness, git).DeleteAsync(temp.Path, "kept", force: true, TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.CommandFailed, outcome.Outcome.ExitCode);
        Assert.Contains("git still has it registered", outcome.Outcome.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(path));
    }

    private static WorktreeService Service(HarnessFactory harness, IGitClient git, IFileSystem? fileSystem = null)
        => new(harness.ContextLoader, git, fileSystem ?? harness.FileSystem, harness.PathBudget, harness.Platform);

    private static async Task<HarnessFactory> PrepareAsync(TempDirectory temp)
    {
        var harness = new HarnessFactory();

        await harness.InitializeHarnessAsync(
            temp.Path,
            TestContext.Current.CancellationToken,
            new HarnessConfig { Worktrees = Relaxed });

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
    /// Makes <paramref name="link"/> lead to <paramref name="target"/>: a junction on Windows, which
    /// needs no privilege there, and a symbolic link elsewhere.
    /// </summary>
    private static async Task LinkDirectoryAsync(HarnessFactory harness, string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        var result = await harness.ProcessRunner.RunAsync(
            new ProcessRequest { FileName = "cmd", Arguments = ["/c", "mklink", "/J", link, target] },
            TestContext.Current.CancellationToken);

        Assert.True(result.ExitCode == 0, $"mklink /J failed: {result.StandardError}{result.StandardOutput}");
    }
}

/// <summary>The real file system, except that a directory cannot be deleted, as while another program holds a file in it open.</summary>
internal sealed class UndeletableFileSystem(IFileSystem inner) : IFileSystem
{
    public bool FileExists(string path) => inner.FileExists(path);

    public bool DirectoryExists(string path) => inner.DirectoryExists(path);

    public void CreateDirectory(string path) => inner.CreateDirectory(path);

    public void DeleteFile(string path) => inner.DeleteFile(path);

    public void DeleteDirectory(string path)
        => throw new IOException("The process cannot access the file because it is being used by another process.");

    public IEnumerable<string> EnumerateFiles(string path, bool recursive) => inner.EnumerateFiles(path, recursive);

    public IEnumerable<string> EnumerateDirectories(string path) => inner.EnumerateDirectories(path);

    public string ReadAllText(string path) => inner.ReadAllText(path);

    public void WriteAllTextAtomic(string path, string contents) => inner.WriteAllTextAtomic(path, contents);

    public void ProtectSecretFile(string path) => inner.ProtectSecretFile(path);
}

/// <summary>
/// The real git client, with hooks a test uses to interrupt a command at a chosen moment, to change
/// what git answers, and to see what git was asked and whether that call could still be cancelled.
/// </summary>
internal sealed class InterceptingGitClient(IGitClient inner) : IGitClient
{
    private readonly List<(string[] Arguments, bool Cancellable)> _runs = [];

    /// <summary>Runs at the start of every call.</summary>
    public Action? BeforeEveryCall { get; init; }

    /// <summary>Runs before each arbitrary git command, with its arguments.</summary>
    public Action<IReadOnlyList<string>>? BeforeRun { get; init; }

    /// <summary>An answer to give instead of running a command, or <see langword="null"/> to run it.</summary>
    public Func<IReadOnlyList<string>, GitCommandResult?>? InsteadOfRun { get; init; }

    /// <summary>Replaces the result of each arbitrary git command once it has run.</summary>
    public Func<IReadOnlyList<string>, GitCommandResult, GitCommandResult>? AfterRun { get; init; }

    /// <summary>Whether calls reach git with their cancellation taken away.</summary>
    public bool IgnoreCancellation { get; init; }

    /// <summary>Every arbitrary git command, and whether its call could still be cancelled.</summary>
    public IReadOnlyList<(string[] Arguments, bool Cancellable)> Runs
    {
        get
        {
            lock (_runs)
            {
                return [.. _runs];
            }
        }
    }

    public bool IsInstalled() => inner.IsInstalled();

    public Task<bool> IsRepositoryAsync(string directory, CancellationToken cancellationToken = default)
        => Call(() => inner.IsRepositoryAsync(directory, Token(cancellationToken)));

    public Task<string?> GetRepositoryRootAsync(string directory, CancellationToken cancellationToken = default)
        => Call(() => inner.GetRepositoryRootAsync(directory, Token(cancellationToken)));

    public Task<GitWorktree?> GetMainWorktreeAsync(string directory, CancellationToken cancellationToken = default)
        => Call(() => inner.GetMainWorktreeAsync(directory, Token(cancellationToken)));

    public Task<bool> IsDirtyAsync(string directory, CancellationToken cancellationToken = default)
        => Call(() => inner.IsDirtyAsync(directory, Token(cancellationToken)));

    public Task<IReadOnlyList<string>> GetStatusAsync(string directory, CancellationToken cancellationToken = default)
        => Call(() => inner.GetStatusAsync(directory, Token(cancellationToken)));

    public Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(string directory, CancellationToken cancellationToken = default)
        => Call(() => inner.ListWorktreesAsync(directory, Token(cancellationToken)));

    public Task<GitLocation?> GetLocationAsync(string directory, CancellationToken cancellationToken = default)
        => Call(() => inner.GetLocationAsync(directory, Token(cancellationToken)));

    public Task<IReadOnlyList<GitIndexEntry>> ListIndexAsync(string directory, CancellationToken cancellationToken = default)
        => Call(() => inner.ListIndexAsync(directory, Token(cancellationToken)));

    public Task<IReadOnlyList<string>> HashFilesAsync(
        string directory,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default)
        => Call(() => inner.HashFilesAsync(directory, paths, Token(cancellationToken)));

    public Task<int> CountCommitsAsync(
        string directory,
        IReadOnlyList<string> revisions,
        CancellationToken cancellationToken = default)
        => Call(() => inner.CountCommitsAsync(directory, revisions, Token(cancellationToken)));

    public Task<bool> IsIgnoredAsync(string directory, string path, CancellationToken cancellationToken = default)
        => Call(() => inner.IsIgnoredAsync(directory, path, Token(cancellationToken)));

    public Task<string?> ResolveCommitAsync(string directory, string reference, CancellationToken cancellationToken = default)
        => Call(() => inner.ResolveCommitAsync(directory, reference, Token(cancellationToken)));

    public Task<string?> ReadFileAtCommitAsync(
        string directory,
        string commit,
        string relativePath,
        CancellationToken cancellationToken = default)
        => Call(() => inner.ReadFileAtCommitAsync(directory, commit, relativePath, Token(cancellationToken)));

    public async Task<GitCommandResult> RunAsync(
        string directory,
        IReadOnlyList<string> arguments,
        bool echoOutput = false,
        CancellationToken cancellationToken = default)
    {
        BeforeEveryCall?.Invoke();
        BeforeRun?.Invoke(arguments);

        lock (_runs)
        {
            _runs.Add(([.. arguments], cancellationToken.CanBeCanceled));
        }

        if (InsteadOfRun?.Invoke(arguments) is { } answer)
        {
            return answer;
        }

        var result = await inner.RunAsync(directory, arguments, echoOutput, Token(cancellationToken));
        return AfterRun is null ? result : AfterRun(arguments, result);
    }

    private CancellationToken Token(CancellationToken cancellationToken)
        => IgnoreCancellation ? CancellationToken.None : cancellationToken;

    private Task<T> Call<T>(Func<Task<T>> call)
    {
        BeforeEveryCall?.Invoke();
        return call();
    }
}
