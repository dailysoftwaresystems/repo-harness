using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Git;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Worktrees;

/// <summary>What git makes of a directory under the worktrees directory.</summary>
internal enum WorktreeMembership
{
    /// <summary>A worktree of this repository, rooted at the directory.</summary>
    OfThisRepository,

    /// <summary>Not the root of any work tree git can find, such as a worktree whose .git file is gone.</summary>
    NotAWorktree,

    /// <summary>The root of a work tree of another repository, such as a clone.</summary>
    OfAnotherRepository,
}

/// <summary>Which repository a directory belongs to, and where git keeps its record of it.</summary>
/// <param name="Membership">What git makes of the directory.</param>
/// <param name="AdministrativeDirectory">
/// The worktree's own git directory, when it is a worktree of this repository. git's record of the
/// worktree lasts exactly as long as this directory does.
/// </param>
internal sealed record WorktreeIdentity(WorktreeMembership Membership, string? AdministrativeDirectory);

/// <summary>A submodule whose repository, deleted with the worktree, holds work found nowhere else.</summary>
/// <param name="Path">The submodule's path below the worktree's root.</param>
/// <param name="Commits">Commits on its HEAD, branches or tags that no remote-tracking ref contains.</param>
/// <param name="HasStash">Whether its repository holds a stash.</param>
internal sealed record SubmoduleLoss(string Path, int Commits, bool HasStash);

/// <summary>Everything that stops a worktree being deleted without --force, found before anything is touched.</summary>
/// <param name="Changes">Paths with uncommitted changes, including edits status cannot see.</param>
/// <param name="Commits">Commits that no branch, tag, remote-tracking ref or stash contains.</param>
/// <param name="Head">The worktree's HEAD commit, or <see langword="null"/> when it has none.</param>
/// <param name="Submodules">Submodules whose repositories hold work found nowhere else.</param>
/// <param name="LockReason">Why the worktree is locked, or <see langword="null"/> when it is not.</param>
/// <param name="HoldsSubmodules">Whether git counts the worktree as holding submodules, which plain removal refuses.</param>
internal sealed record WorktreeFindings(
    IReadOnlyList<string> Changes,
    int Commits,
    string? Head,
    IReadOnlyList<SubmoduleLoss> Submodules,
    string? LockReason,
    bool HoldsSubmodules)
{
    /// <summary>Whether anything was found that deleting without --force must not override.</summary>
    public bool StopsDeletion => Changes.Count > 0 || Commits > 0 || Submodules.Count > 0 || LockReason is not null;
}

/// <summary>
/// Finds what deleting a worktree would lose, by asking git. Every question that fails throws
/// <see cref="HarnessException"/> with <see cref="HarnessExit.CommandFailed"/>, because a question
/// git could not answer is not a worktree with nothing to lose.
/// </summary>
internal sealed class WorktreeInspector(IGitClient gitClient, IFileSystem fileSystem, IHostPlatform platform)
{
    private readonly IGitClient _gitClient = gitClient;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHostPlatform _platform = platform;

    /// <summary>What git makes of <paramref name="path"/>, compared with the main checkout's repository.</summary>
    public async Task<WorktreeIdentity> IdentifyAsync(
        string mainCheckoutRoot,
        string path,
        CancellationToken cancellationToken)
    {
        var main = await _gitClient.GetLocationAsync(mainCheckoutRoot, cancellationToken).ConfigureAwait(false)
            ?? throw new HarnessException(
                HarnessExit.CommandFailed,
                $"git no longer finds a repository at '{mainCheckoutRoot}'.");

        var here = await _gitClient.GetLocationAsync(path, cancellationToken).ConfigureAwait(false);

        // A directory that is not the root of a work tree, such as a worktree whose .git file is
        // gone, is answered for by whatever repository encloses it, and that answer says nothing
        // about the files in the directory.
        if (here is null || here.Prefix.Length != 0)
        {
            return new WorktreeIdentity(WorktreeMembership.NotAWorktree, null);
        }

        // A linked worktree's git directory sits in the worktrees directory of its repository's
        // git directory. Both paths come from git, resolved the same way, so no link in the path
        // the harness spelled can make them differ.
        var parent = Path.GetDirectoryName(here.GitDirectory);
        var grandparent = parent is null ? null : Path.GetDirectoryName(parent);

        var ofThisRepository = grandparent is not null
            && string.Equals(Path.GetFileName(parent), "worktrees", StringComparison.Ordinal)
            && string.Equals(grandparent, main.GitDirectory, _platform.PathComparison);

        return ofThisRepository
            ? new WorktreeIdentity(WorktreeMembership.OfThisRepository, here.GitDirectory)
            : new WorktreeIdentity(WorktreeMembership.OfAnotherRepository, null);
    }

    /// <summary>Everything that stops deleting the worktree at <paramref name="path"/> without --force.</summary>
    public async Task<WorktreeFindings> FindAsync(
        string path,
        string administrativeDirectory,
        CancellationToken cancellationToken)
    {
        var status = await _gitClient.GetStatusAsync(path, cancellationToken).ConfigureAwait(false);
        var index = await _gitClient.ListIndexAsync(path, cancellationToken).ConfigureAwait(false);

        // Each status entry is two status characters and a space, then the path.
        List<string> changes = [.. status.Select(entry => entry[3..])];
        changes.AddRange(await FindHiddenEditsAsync(path, index, cancellationToken).ConfigureAwait(false));

        var head = await _gitClient.ResolveCommitAsync(path, "HEAD", cancellationToken).ConfigureAwait(false);
        var commits = head is null
            ? 0
            : await CountUnreferencedCommitsAsync(path, head, cancellationToken).ConfigureAwait(false);

        var submodules = new List<SubmoduleLoss>();
        var populated = await FindSubmoduleLossesAsync(path, string.Empty, index, submodules, cancellationToken)
            .ConfigureAwait(false);

        // git's own rule for a worktree holding submodules: a modules directory in its git
        // directory, or a populated submodule in its index.
        var holdsSubmodules = populated
            || _fileSystem.DirectoryExists(Path.Combine(administrativeDirectory, "modules"));

        return new WorktreeFindings(
            changes,
            commits,
            head,
            submodules,
            ReadLockReason(administrativeDirectory),
            holdsSubmodules);
    }

    /// <summary>
    /// How many commits reachable from <paramref name="head"/> no branch, tag, remote-tracking ref or
    /// stash reaches. Deleting a worktree deletes its HEAD, which is then the only thing naming them.
    /// </summary>
    public async Task<int> CountUnreferencedCommitsAsync(
        string directory,
        string head,
        CancellationToken cancellationToken)
    {
        var stash = await _gitClient.ResolveCommitAsync(directory, "refs/stash", cancellationToken).ConfigureAwait(false);

        // Not --all: it includes the HEAD of every worktree, this one's among them.
        List<string> revisions = [head, "--not", "--branches", "--tags", "--remotes"];

        if (stash is not null)
        {
            revisions.Add(stash);
        }

        return await _gitClient.CountCommitsAsync(directory, revisions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Files marked assume-unchanged or skip-worktree whose content on disk differs from the index.
    /// Status never compares such a file, so an edit to one is invisible to it.
    /// </summary>
    private async Task<IReadOnlyList<string>> FindHiddenEditsAsync(
        string root,
        IReadOnlyList<GitIndexEntry> index,
        CancellationToken cancellationToken)
    {
        // A skip-worktree file absent from disk, as a sparse checkout leaves it, holds nothing to lose.
        var candidates = index
            .Where(entry => entry.Stage == 0 && entry.IsRegularFile && (entry.IsAssumedUnchanged || entry.IsSkipWorktree))
            .Where(entry => _fileSystem.FileExists(Path.Combine(root, entry.Path)))
            .ToList();

        if (candidates.Count == 0)
        {
            return [];
        }

        var hashes = await _gitClient
            .HashFilesAsync(root, [.. candidates.Select(entry => entry.Path)], cancellationToken)
            .ConfigureAwait(false);

        return [.. candidates
            .Where((entry, position) => !string.Equals(entry.ObjectId, hashes[position], StringComparison.Ordinal))
            .Select(entry => entry.Path)];
    }

    /// <summary>
    /// Adds every populated submodule under <paramref name="root"/>, nested ones included, whose
    /// repository holds work no remote-tracking ref has, and reports whether any is populated.
    /// </summary>
    private async Task<bool> FindSubmoduleLossesAsync(
        string root,
        string shownPrefix,
        IReadOnlyList<GitIndexEntry> index,
        List<SubmoduleLoss> losses,
        CancellationToken cancellationToken)
    {
        var populated = false;

        foreach (var entry in index.Where(entry => entry.IsSubmodule && entry.Stage == 0))
        {
            var directory = Path.Combine(root, entry.Path);
            var gitEntry = Path.Combine(directory, ".git");

            if (!_fileSystem.FileExists(gitEntry) && !_fileSystem.DirectoryExists(gitEntry))
            {
                continue;
            }

            populated = true;
            var shown = shownPrefix + entry.Path;

            // A linked worktree keeps its submodules' repositories in its own git directory, so
            // their branches, tags and stash are deleted with it. Only what a remote-tracking ref
            // contains exists anywhere else. Uncommitted work inside one is already in the status.
            var commits = await _gitClient
                .CountCommitsAsync(directory, ["HEAD", "--branches", "--tags", "--not", "--remotes"], cancellationToken)
                .ConfigureAwait(false);
            var hasStash = await _gitClient
                .ResolveCommitAsync(directory, "refs/stash", cancellationToken)
                .ConfigureAwait(false) is not null;

            if (commits > 0 || hasStash)
            {
                losses.Add(new SubmoduleLoss(shown, commits, hasStash));
            }

            var nested = await _gitClient.ListIndexAsync(directory, cancellationToken).ConfigureAwait(false);
            await FindSubmoduleLossesAsync(directory, shown + "/", nested, losses, cancellationToken).ConfigureAwait(false);
        }

        return populated;
    }

    /// <summary>Why the worktree is locked, or <see langword="null"/> when it is not.</summary>
    private string? ReadLockReason(string administrativeDirectory)
    {
        // git records a lock as a file in the worktree's git directory, holding the reason given.
        var lockFile = Path.Combine(administrativeDirectory, "locked");

        try
        {
            return _fileSystem.FileExists(lockFile) ? _fileSystem.ReadAllText(lockFile).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"Could not read whether the worktree is locked, from '{lockFile}': {ex.Message}");
        }
    }
}
