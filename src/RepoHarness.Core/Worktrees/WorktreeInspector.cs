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

/// <summary>A submodule repository, deleted with the worktree, that holds work found nowhere else.</summary>
/// <param name="Path">
/// Where the submodule is checked out below the worktree's root, or, for one no longer checked out,
/// its name among the worktree's submodule repositories.
/// </param>
/// <param name="Commits">Commits on its HEAD or branches that no remote-tracking ref or tag contains.</param>
/// <param name="HasStash">Whether its repository holds a stash.</param>
internal sealed record SubmoduleLoss(string Path, int Commits, bool HasStash);

/// <summary>Everything that stops a worktree being deleted without --force, found before anything is touched.</summary>
/// <param name="Changes">Paths with uncommitted changes, including edits status cannot see.</param>
/// <param name="Commits">
/// Commits that no branch, tag, remote-tracking ref, the newest stash, or other worktree's HEAD contains.
/// </param>
/// <param name="Head">The worktree's HEAD commit, or <see langword="null"/> when it has none.</param>
/// <param name="Submodules">Submodule repositories that hold work found nowhere else.</param>
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
        var main = await MainGitDirectoryAsync(mainCheckoutRoot, cancellationToken).ConfigureAwait(false);
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
            && string.Equals(grandparent, main, _platform.PathComparison);

        return ofThisRepository
            ? new WorktreeIdentity(WorktreeMembership.OfThisRepository, here.GitDirectory)
            : new WorktreeIdentity(WorktreeMembership.OfAnotherRepository, null);
    }

    /// <summary>Everything that stops deleting the worktree at <paramref name="path"/> without --force.</summary>
    public async Task<WorktreeFindings> FindAsync(
        string mainCheckoutRoot,
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
            : await CountUnreferencedCommitsAsync(path, head, ResolveLinks(path), cancellationToken).ConfigureAwait(false);

        var repositories = new List<SubmoduleRepository>();
        var populated = await CollectCheckedOutSubmodulesAsync(mainCheckoutRoot, path, string.Empty, index, repositories, cancellationToken)
            .ConfigureAwait(false);
        await CollectModuleRepositoriesAsync(mainCheckoutRoot, Path.Combine(administrativeDirectory, "modules"), string.Empty, repositories, cancellationToken)
            .ConfigureAwait(false);

        // git's own rule for a worktree holding submodules: a modules directory in its git
        // directory, or a populated submodule in its index.
        var holdsSubmodules = populated
            || _fileSystem.DirectoryExists(Path.Combine(administrativeDirectory, "modules"));

        return new WorktreeFindings(
            changes,
            commits,
            head,
            await FindSubmoduleLossesAsync(repositories, cancellationToken).ConfigureAwait(false),
            ReadLockReason(administrativeDirectory),
            holdsSubmodules);
    }

    /// <summary>
    /// Everything that stops clearing git's record of a worktree whose directory is already gone:
    /// commits only its HEAD names, work in the submodule repositories its git directory still holds,
    /// and a lock. Removing the record deletes those repositories, and git checks none of them.
    /// </summary>
    public async Task<WorktreeFindings> FindForRecordAsync(
        string mainCheckoutRoot,
        GitWorktree record,
        CancellationToken cancellationToken)
    {
        var commits = record.Commit is null
            ? 0
            : await CountUnreferencedCommitsAsync(mainCheckoutRoot, record.Commit, record.Path, cancellationToken).ConfigureAwait(false);

        var repositories = new List<SubmoduleRepository>();

        if (await FindAdministrativeDirectoryAsync(mainCheckoutRoot, record.Path, cancellationToken).ConfigureAwait(false)
            is { } administrativeDirectory)
        {
            await CollectModuleRepositoriesAsync(mainCheckoutRoot, Path.Combine(administrativeDirectory, "modules"), string.Empty, repositories, cancellationToken)
                .ConfigureAwait(false);
        }

        return new WorktreeFindings(
            [],
            commits,
            record.Commit,
            await FindSubmoduleLossesAsync(repositories, cancellationToken).ConfigureAwait(false),
            record.LockReason,
            HoldsSubmodules: false);
    }

    /// <summary>
    /// <paramref name="path"/> with every link along it followed, the form git reports worktree
    /// paths in, so the two compare equal even when the worktrees directory is reached through a link.
    /// </summary>
    public string ResolveLinks(string path)
    {
        try
        {
            return _fileSystem.ResolveLinks(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"Could not follow the links along '{path}': {ex.Message}");
        }
    }

    private async Task<string> MainGitDirectoryAsync(string mainCheckoutRoot, CancellationToken cancellationToken)
    {
        var main = await _gitClient.GetLocationAsync(mainCheckoutRoot, cancellationToken).ConfigureAwait(false);

        return main?.GitDirectory ?? throw new HarnessException(
            HarnessExit.CommandFailed,
            $"git no longer finds a repository at '{mainCheckoutRoot}'.");
    }

    /// <summary>
    /// How many commits reachable from <paramref name="head"/> nothing else names: no branch, tag,
    /// remote-tracking ref, the newest stash, or the HEAD of another worktree, the main checkout's
    /// included. Deleting a worktree deletes its HEAD, which may be all that names them.
    /// </summary>
    private async Task<int> CountUnreferencedCommitsAsync(
        string directory,
        string head,
        string worktreePath,
        CancellationToken cancellationToken)
    {
        var stash = await _gitClient.ResolveCommitAsync(directory, "refs/stash", cancellationToken).ConfigureAwait(false);
        var worktrees = await _gitClient.ListWorktreesAsync(directory, cancellationToken).ConfigureAwait(false);

        // Not --all: it includes the HEAD of this worktree too.
        List<string> revisions = [head, "--not", "--branches", "--tags", "--remotes"];

        revisions.AddRange(worktrees
            .Where(worktree => worktree.Commit is not null && !PathsEqual(worktree.Path, worktreePath))
            .Select(worktree => worktree.Commit!)
            .Distinct(StringComparer.Ordinal));

        if (stash is not null)
        {
            revisions.Add(stash);
        }

        return await _gitClient.CountCommitsAsync(directory, revisions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Files marked assume-unchanged or skip-worktree whose content on disk differs from the index.
    /// Status never compares a marked file, so an edit to one is invisible to it; a copy of the index
    /// with the marks cleared lets status compare them, and the real index is never touched.
    /// </summary>
    private async Task<IReadOnlyList<string>> FindHiddenEditsAsync(
        string root,
        IReadOnlyList<GitIndexEntry> index,
        CancellationToken cancellationToken)
    {
        // A marked file absent from disk, as a sparse checkout leaves a skip-worktree file, holds
        // nothing to lose, and unmarked in the copy it would read as deleted.
        var present = index
            .Where(entry => entry.Stage == 0 && !entry.IsSubmodule && (entry.IsAssumedUnchanged || entry.IsSkipWorktree))
            .Where(entry => _fileSystem.FileExists(Path.Combine(root, entry.Path)))
            .ToList();

        if (present.Count == 0)
        {
            return [];
        }

        var indexFile = await _gitClient.GetIndexFileAsync(root, cancellationToken).ConfigureAwait(false);
        string copy;

        try
        {
            copy = _fileSystem.CopyToTemporaryFile(indexFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"Could not copy the index '{indexFile}' to compare the files it marks: {ex.Message}");
        }

        try
        {
            return await _gitClient.FindEditedFilesAsync(
                root,
                copy,
                [.. present.Where(entry => entry.IsAssumedUnchanged).Select(entry => entry.Path)],
                [.. present.Where(entry => entry.IsSkipWorktree).Select(entry => entry.Path)],
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DeleteTemporaryFile(copy);
        }
    }

    /// <summary>
    /// Adds the repository of every submodule checked out under <paramref name="root"/>, nested ones
    /// included, and reports whether any is checked out. A submodule whose .git is a directory in the
    /// work tree keeps its repository there rather than among the worktree's modules.
    /// </summary>
    private async Task<bool> CollectCheckedOutSubmodulesAsync(
        string mainCheckoutRoot,
        string root,
        string shownPrefix,
        IReadOnlyList<GitIndexEntry> index,
        List<SubmoduleRepository> repositories,
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

            var location = await _gitClient.GetLocationAsync(directory, cancellationToken).ConfigureAwait(false)
                ?? throw new HarnessException(
                    HarnessExit.CommandFailed,
                    $"git does not see the submodule checked out at '{directory}' as a repository.");

            Add(repositories, shown, location.GitDirectory);
            await CollectModuleRepositoriesAsync(mainCheckoutRoot, Path.Combine(location.GitDirectory, "modules"), shown + "/", repositories, cancellationToken)
                .ConfigureAwait(false);

            var nested = await _gitClient.ListIndexAsync(directory, cancellationToken).ConfigureAwait(false);
            await CollectCheckedOutSubmodulesAsync(mainCheckoutRoot, directory, shown + "/", nested, repositories, cancellationToken)
                .ConfigureAwait(false);
        }

        return populated;
    }

    /// <summary>
    /// Adds every repository under a modules directory, nested submodules' included, checked out or
    /// not: a submodule deinitialised in the worktree keeps its repository there, with its work. A
    /// submodule's name can hold slashes, so a directory that is not a repository is part of a name.
    /// </summary>
    private async Task CollectModuleRepositoriesAsync(
        string mainCheckoutRoot,
        string modules,
        string shownPrefix,
        List<SubmoduleRepository> repositories,
        CancellationToken cancellationToken)
    {
        List<string> directories;

        try
        {
            if (!_fileSystem.DirectoryExists(modules))
            {
                return;
            }

            directories = [.. _fileSystem.EnumerateDirectories(modules)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"Could not look for submodule repositories in '{modules}': {ex.Message}");
        }

        foreach (var directory in directories)
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            var shown = shownPrefix + Path.GetFileName(full);

            if (await _gitClient.ResolveGitDirectoryAsync(mainCheckoutRoot, full, cancellationToken).ConfigureAwait(false)
                is { } gitDirectory)
            {
                Add(repositories, shown, gitDirectory);
                await CollectModuleRepositoriesAsync(mainCheckoutRoot, Path.Combine(gitDirectory, "modules"), shown + "/", repositories, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await CollectModuleRepositoriesAsync(mainCheckoutRoot, full, shown + "/", repositories, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>Adds a repository unless it was already found another way.</summary>
    private void Add(List<SubmoduleRepository> repositories, string shown, string gitDirectory)
    {
        if (!repositories.Any(repository => PathsEqual(repository.GitDirectory, gitDirectory)))
        {
            repositories.Add(new SubmoduleRepository(shown, gitDirectory));
        }
    }

    /// <summary>
    /// The repositories holding commits on their HEAD or branches that no remote-tracking ref or tag
    /// contains, or a stash. Only that much exists anywhere else once the worktree's git directory is
    /// deleted. A tag counts as kept, since tags usually come from upstream; a tag made only in the
    /// submodule is not protected.
    /// </summary>
    private async Task<IReadOnlyList<SubmoduleLoss>> FindSubmoduleLossesAsync(
        IReadOnlyList<SubmoduleRepository> repositories,
        CancellationToken cancellationToken)
    {
        var losses = new List<SubmoduleLoss>();

        foreach (var repository in repositories)
        {
            // Asked of the repository alone, since one no longer checked out has no work tree.
            var commits = await _gitClient
                .CountRepositoryCommitsAsync(repository.GitDirectory, ["HEAD", "--branches", "--not", "--remotes", "--tags"], cancellationToken)
                .ConfigureAwait(false);
            var hasStash = await _gitClient.HasStashAsync(repository.GitDirectory, cancellationToken).ConfigureAwait(false);

            if (commits > 0 || hasStash)
            {
                losses.Add(new SubmoduleLoss(repository.Shown, commits, hasStash));
            }
        }

        return losses;
    }

    /// <summary>
    /// The directory git keeps a worktree's record in, found by the worktree path the record names,
    /// or <see langword="null"/> when no record names it. Both paths come from git.
    /// </summary>
    private async Task<string?> FindAdministrativeDirectoryAsync(
        string mainCheckoutRoot,
        string worktreePath,
        CancellationToken cancellationToken)
    {
        var records = Path.Combine(await MainGitDirectoryAsync(mainCheckoutRoot, cancellationToken).ConfigureAwait(false), "worktrees");

        try
        {
            if (!_fileSystem.DirectoryExists(records))
            {
                return null;
            }

            foreach (var directory in _fileSystem.EnumerateDirectories(records))
            {
                var gitdirFile = Path.Combine(directory, "gitdir");

                if (!_fileSystem.FileExists(gitdirFile))
                {
                    continue;
                }

                // The record holds the path of the worktree's .git file: absolute, or relative to
                // the record's own directory when worktree.useRelativePaths is set.
                var dotGit = Path.GetFullPath(Path.Combine(directory, _fileSystem.ReadAllText(gitdirFile).Trim()));
                var recorded = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(dotGit));

                if (recorded is not null && PathsEqual(recorded, worktreePath))
                {
                    return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"Could not read git's worktree records in '{records}': {ex.Message}");
        }
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

    /// <summary>
    /// Deletes a temporary copy. A failure to clean up must not replace the answer, or the failure,
    /// the copy was made for.
    /// </summary>
    private void DeleteTemporaryFile(string path)
    {
        try
        {
            _fileSystem.DeleteFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private bool PathsEqual(string left, string right)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            _platform.PathComparison);

    /// <summary>A submodule repository the worktree's deletion would delete.</summary>
    /// <param name="Shown">How the submodule is named in a refusal.</param>
    /// <param name="GitDirectory">Its git directory, as git reports it.</param>
    private sealed record SubmoduleRepository(string Shown, string GitDirectory);
}
