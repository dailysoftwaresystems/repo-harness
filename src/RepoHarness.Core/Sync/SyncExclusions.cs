using RepoHarness.Core.Configuration;
using RepoHarness.Core.Git;
using RepoHarness.Core.Results;

using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Repository;

namespace RepoHarness.Core.Sync;

/// <summary>
/// What a sync withholds, and equally what it must never delete.
/// </summary>
/// <remarks>
/// The two are one list on purpose. A path the harness will not write is one it cannot know the
/// source lacks, so deleting it would remove the host's own state rather than a file the source gave
/// up: its git repository, its harness configuration, its worktrees and the build directories that
/// make an incremental build possible.
/// </remarks>
/// <summary>
/// What a look for rooted entries matching nothing found, and whether it could finish looking.
/// </summary>
/// <param name="MatchingNothing">The entry names that protect nothing here though the name exists deeper.</param>
/// <param name="Incomplete">
/// Why the tree could not be read all the way down, or <see langword="null"/> when it was. A check
/// that stopped early has established nothing about what it did not reach, and saying so is the
/// difference between this and a clean answer.
/// </param>
public sealed record RootedEntryReport(IReadOnlyList<string> MatchingNothing, string? Incomplete);

public sealed class SyncExclusions
{
    private readonly string[] _withheld;
    private readonly string[] _excluded;
    private readonly string[] _neverTransfer;
    private readonly string[] _coveredByConfiguration;

    /// <summary>Builds the policy from configuration.</summary>
    /// <param name="sync">The sync section.</param>
    /// <param name="worktreesRoot">The configured worktrees root, withheld with the rest.</param>
    /// <param name="gitIgnored">
    /// What git ignores in the source tree, withheld with the rest. <c>sync.exclude</c> is documented
    /// as naming paths to withhold <em>in addition to</em> these, and without them a repository's
    /// ignored files — a local <c>.env</c>, a virtual environment, an editor's cache — are copied to
    /// every host the moment a leg is placed on one. Withheld rather than merely excluded, because a
    /// path that is local to a machine is local on the far side too: deleting the host's own copy of
    /// one would remove that machine's state rather than a file this tree gave up.
    /// </param>
    public SyncExclusions(SyncConfig sync, string worktreesRoot, IReadOnlyList<string>? gitIgnored = null)
    {
        ArgumentNullException.ThrowIfNull(sync);

        // Kept apart from the rest of the withheld list, because only these came from the setting
        // a message about them names. A worktrees root or a git-ignored path reported as
        // 'sync.neverTransfer names ...' would send a reader to a line that is not in the file, and
        // the fix it suggests would be wrong for it.
        _neverTransfer = [.. sync.EffectiveNeverTransfer
            .Select(Normalize)
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.Ordinal)];

        _withheld = [.. sync.EffectiveNeverTransfer
            .Concat([worktreesRoot])
            .Concat(gitIgnored ?? [])
            .Select(Normalize)
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.Ordinal)];

        _excluded = [.. sync.Exclude
            .Select(Normalize)
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.Ordinal)];

        // What an entry of the configuration's, or the worktrees root, already covers - where the search
        // for a misplaced entry counts no name: see RootedEntriesMatchingNothing.
        _coveredByConfiguration = [.. _neverTransfer
            .Append(Normalize(worktreesRoot))
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Paths that are never transferred and never deleted.</summary>
    public IReadOnlyList<string> Withheld => _withheld;

    /// <summary>Paths that are not transferred, but which a sync may delete from a copy.</summary>
    public IReadOnlyList<string> Excluded => _excluded;

    /// <summary>
    /// Whether <paramref name="relativePath"/> is withheld from transfer. True for a withheld or
    /// excluded path and for everything beneath one.
    /// </summary>
    /// <param name="relativePath">A path relative to the tree root, with forward separators.</param>
    public bool IsWithheldFromTransfer(string relativePath)
        => HarnessDirectorySync.Withholds(relativePath)
            || Matches(_withheld, relativePath)
            || Matches(_excluded, relativePath);

    /// <summary>
    /// Whether <paramref name="relativePath"/> is protected from deletion. True only for the
    /// withheld list: an excluded path is one the source chooses not to send, and a copy that keeps
    /// it for ever is a copy of a tree that no longer exists.
    /// </summary>
    /// <param name="relativePath">A path relative to the tree root, with forward separators.</param>
    public bool IsProtectedFromDeletion(string relativePath)
        => HarnessDirectorySync.Withholds(relativePath) || Matches(_withheld, relativePath);

    /// <summary>
    /// Names every rooted <c>sync.neverTransfer</c> entry that matches nothing here while that name
    /// exists deeper in the tree.
    /// </summary>
    /// <param name="fileSystem">Reads the tree.</param>
    /// <param name="root">The tree the entries are relative to.</param>
    /// <param name="pathComparison">How this platform compares paths, for deciding what is a link.</param>
    /// <param name="cancellationToken">Stops the walk.</param>
    /// <remarks>
    /// A protective rule that silently matches nothing is worse than no rule, because the name in
    /// the file reads as evidence the thing is protected and any reader stops there. Measured on a
    /// consumer's tree: of seventeen entries, three protected nothing at all, and one of them was
    /// <c>.secrets</c> — which protected the root instance while a second <c>.secrets</c> three
    /// levels down, the directory this tool's own design names as where a host credential goes, was
    /// covered by nothing.
    /// <para>
    /// Reported rather than refused, and rather than quietly widened. Widening a bare name to mean
    /// any depth would stop transferring nested directories that builds read; refusing would fail a
    /// tree whose author meant exactly what they wrote. Naming it lets them decide, and the fix is
    /// one they can see: write <c>**/name</c>.
    /// </para>
    /// <para>
    /// A name counts only where nothing the configuration writes covers it: not where a
    /// <c>sync.neverTransfer</c> entry covers the path - the <c>**/name</c> the warning asks for among
    /// them - and not in the worktrees root. There the configuration already keeps it from every sync
    /// by an entry its author wrote, and each worktree is a checkout of its own, whose names are not
    /// this tree's. Counted, a name its author had covered as the warning advised would be named again
    /// on every sync.
    /// </para>
    /// <para>
    /// The search goes where a sync goes, and into the harness's own directory besides. It goes into
    /// nothing a sync withholds - what an entry covers, the worktrees root, what git ignores, what
    /// <c>sync.exclude</c> names - though such a directory's own name is seen from the one holding it,
    /// so a <c>node_modules</c> or a <c>__pycache__</c> git ignores still counts. What lies inside one is
    /// generated, fetched or another checkout's, not something anybody wrote into this tree, and it is
    /// where a tree's size is: measured on a consumer's tree, 68,697 of its 69,890 directories lay under
    /// what its <c>sync.neverTransfer</c> names, 63,888 under <c>build</c>, <c>.worktrees</c> and
    /// <c>.temp</c> alone, and a search that went in spent its whole budget before it reached most of
    /// the tree, and said so on every sync. The harness's own directory is searched all the same, but
    /// for the worktrees root and what an entry covers: git ignores most of it by design, it holds this
    /// tree's connection data, and a <c>.secrets</c> there is the one this was written to find.
    /// </para>
    /// <para>
    /// The walk skips links, and stops at a depth no tree reaches honestly, because this runs before
    /// every sync and a check that is only advisory must not be able to end the command it precedes.
    /// A directory link aimed at an ancestor would otherwise recurse until the stack went, which is a
    /// failure .NET cannot catch; an unreadable directory anywhere under the root would otherwise
    /// leave as exit 70 naming an exception.
    /// </para>
    /// </remarks>
    public RootedEntryReport RootedEntriesMatchingNothing(
        IFileSystem fileSystem,
        string root,
        StringComparison pathComparison,
        CancellationToken cancellationToken = default)
        => RootedEntriesMatchingNothing(fileSystem, root, pathComparison, MostDirectoriesRead, cancellationToken);

    /// <summary>
    /// <see cref="RootedEntriesMatchingNothing(IFileSystem, string, StringComparison, CancellationToken)"/>,
    /// reading at most <paramref name="mostDirectoriesRead"/> directories.
    /// </summary>
    internal RootedEntryReport RootedEntriesMatchingNothing(
        IFileSystem fileSystem,
        string root,
        StringComparison pathComparison,
        int mostDirectoriesRead,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        var rooted = _neverTransfer
            .Where(path => !path.StartsWith(PathPatterns.AnyDepth, StringComparison.Ordinal))
            .Where(path => !path.Contains('/', StringComparison.Ordinal))
            .Where(path => !SyncConfig.NeverTransferFloor.Contains(path, StringComparer.Ordinal))
            .ToList();

        if (rooted.Count == 0 || !fileSystem.DirectoryExists(root))
        {
            return new RootedEntryReport([], null);
        }

        // Absent from the root is what makes an entry worth looking for deeper. A name that is there
        // is doing its job, whatever else in the tree shares the name.
        var absent = rooted
            .Where(name => !fileSystem.DirectoryExists(Path.Combine(root, name))
                && !fileSystem.FileExists(Path.Combine(root, name)))
            .ToHashSet(StringComparer.Ordinal);

        if (absent.Count == 0)
        {
            return new RootedEntryReport([], null);
        }

        var found = new HashSet<string>(StringComparer.Ordinal);
        var budget = mostDirectoriesRead;

        try
        {
            Deeper(fileSystem, root, root, pathComparison, absent, found, 0, ref budget, cancellationToken);
        }
        catch (BudgetSpent)
        {
            return new RootedEntryReport(
                [.. found.Order(StringComparer.Ordinal)],
                $"more than {mostDirectoriesRead} directories under '{root}' would have had to be read, though "
                + "none a sync withholds is read outside the harness's own directory");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // What was found still stands; what was not looked at is said rather than implied. A
            // half-walked tree reported as a clean one is the same silence this check exists to end.
            return new RootedEntryReport(
                [.. found.Order(StringComparer.Ordinal)],
                $"the tree under '{root}' could not be read all the way down: {ex.Message}");
        }

        return new RootedEntryReport([.. found.Order(StringComparer.Ordinal)], null);
    }

    /// <summary>How deep the search for an absent name goes before it stops looking.</summary>
    /// <remarks>
    /// A bound rather than a belief. Links are skipped, so an honest tree cannot reach this; a tree
    /// that does has something the walk should not be following, and stopping is the answer that
    /// cannot take the sync with it.
    /// </remarks>
    private const int DeepestSearch = 64;

    /// <summary>How many directories the search reads before it gives up and says so.</summary>
    /// <remarks>
    /// This runs before every sync, and the names it looks for are often not in the tree at all — the
    /// case where it reads everything it may. A budget keeps an advisory check from costing more than
    /// the transfer it precedes, and exhausting it is reported rather than returned as "found none".
    /// Spent only on the directories the search goes into: see <see cref="RootedEntriesMatchingNothing(IFileSystem, string, StringComparison, CancellationToken)"/>.
    /// </remarks>
    private const int MostDirectoriesRead = 20_000;

    /// <summary>The name of a directory this never reads, wherever it is, whatever the configuration says.</summary>
    /// <remarks>
    /// Git's own directory, at the root and wherever a submodule puts one: it is large, and a name
    /// inside it is git's copy of something rather than a file anybody wrote. What else the search
    /// leaves unread is decided by <see cref="Searches"/>.
    /// </remarks>
    private const string NeverRead = ".git";

    /// <summary>Thrown when the walk has read as many directories as it is allowed to.</summary>
    private sealed class BudgetSpent : Exception;

    /// <summary>
    /// Collects the absent names that exist, as a file or a directory, anywhere below
    /// <paramref name="directory"/> that the search goes into.
    /// </summary>
    private void Deeper(
        IFileSystem fileSystem,
        string root,
        string directory,
        StringComparison pathComparison,
        IReadOnlySet<string> absent,
        HashSet<string> found,
        int depth,
        ref int budget,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (depth > DeepestSearch || found.Count == absent.Count)
        {
            return;
        }

        if (--budget < 0)
        {
            throw new BudgetSpent();
        }

        // Files as well as directories: '.env' and '.secrets' — the two names this check was written
        // for — are usually files, and a walk that only looked at directories would never see them.
        foreach (var file in fileSystem.EnumerateFiles(directory, recursive: false))
        {
            if (Path.GetFileName(file) is { Length: > 0 } name && absent.Contains(name) && Counts(Relative(root, file)))
            {
                found.Add(name);
            }
        }

        foreach (var child in fileSystem.EnumerateDirectories(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(child);
            var relative = Relative(root, child);

            if (name is { Length: > 0 } && absent.Contains(name) && Counts(relative))
            {
                found.Add(name);
            }

            if (string.Equals(name, NeverRead, StringComparison.Ordinal)
                || !Searches(relative)
                || LinkPaths.IsLink(fileSystem, child, pathComparison))
            {
                continue;
            }

            Deeper(fileSystem, root, child, pathComparison, absent, found, depth + 1, ref budget, cancellationToken);
        }
    }

    /// <summary>
    /// Whether a name found at <paramref name="relativePath"/> counts: not where a <c>sync.neverTransfer</c>
    /// entry, or the worktrees root, covers the path.
    /// </summary>
    private bool Counts(string relativePath) => !Matches(_coveredByConfiguration, relativePath);

    /// <summary>
    /// Whether the search goes into the directory at <paramref name="relativePath"/>: where a sync goes,
    /// and anywhere in the harness's own directory a name counts.
    /// </summary>
    private bool Searches(string relativePath)
        => Counts(relativePath)
            && (relativePath == HarnessLayout.DirectoryName
                || relativePath.StartsWith(HarnessLayout.DirectoryName + "/", StringComparison.Ordinal)
                || !IsWithheldFromTransfer(relativePath));

    /// <summary><paramref name="path"/> relative to <paramref name="root"/>, with forward separators.</summary>
    private static string Relative(string root, string path) => Normalize(Path.GetRelativePath(root, path));

    /// <summary>
    /// Refuses when git has stopped ignoring something the configuration withholds.
    /// </summary>
    /// <param name="gitClient">Used to ask git what it ignores.</param>
    /// <param name="root">The source tree.</param>
    /// <param name="cancellationToken">Stops the check.</param>
    /// <exception cref="HarnessException">
    /// A withheld path is no longer ignored, so the two statements of what is local to a machine
    /// disagree.
    /// </exception>
    /// <remarks>
    /// The question asked is narrow on purpose: does a withheld path hold files git neither ignores
    /// nor tracks? Those are the files the ignore rule used to cover. Sync goes on withholding them
    /// either way, so nothing leaks; what has happened is that a rule was removed or renamed, and
    /// the next person to read <c>git status</c> sees a build tree as work to commit while sync
    /// still treats it as machine-local. Reporting that is cheap; discovering it as a committed
    /// build directory is not.
    /// Deliberately not "is this path ignored": a rule such as <c>/dir/*</c> ignores a directory's
    /// contents without ignoring the directory, a path that does not exist is ignored by nothing,
    /// and a placeholder a repository tracks on purpose is not a disagreement. Each of those would
    /// make this refuse a tree that is exactly as its author meant it.
    /// The floor is exempt: <c>.git</c> is never ignored by git and never could be.
    /// </remarks>
    public async Task RefuseWhenNoLongerIgnoredAsync(
        IGitClient gitClient,
        string root,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gitClient);

        var uncovered = new List<string>();

        foreach (var path in _withheld.Where(path => !SyncConfig.NeverTransferFloor.Contains(path, StringComparer.Ordinal)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var status = await gitClient
                .RunAsync(
                    root,
                    ["status", "--porcelain=v1", "--untracked-files=all", "--", path],
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!status.Succeeded)
            {
                // Unreadable is not clean. A status that failed says nothing about whether the rule
                // still covers the path, and reporting it as covered is the answer that hides a
                // build tree becoming committable.
                throw new HarnessException(
                    HarnessExit.CommandFailed,
                    $"Whether git still ignores '{path}' could not be established: {status.FailureMessage}");
            }

            if (status.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Any(line => line.StartsWith("?? ", StringComparison.Ordinal)))
            {
                uncovered.Add(path);
            }
        }

        if (uncovered.Count == 0)
        {
            return;
        }

        throw new HarnessException(
            HarnessExit.Refused,
            $"sync.neverTransfer names {string.Join(", ", uncovered.Select(path => $"'{path}'"))}, "
            + "which now hold files git neither ignores nor tracks. Sync still withholds them, but "
            + "the repository has two disagreeing statements of what is local to a machine, and "
            + "those files read as work to commit: restore the ignore rule, or drop the path from "
            + "sync.neverTransfer.");
    }

    private static bool Matches(string[] paths, string relativePath)
        => PathPatterns.Matches(paths, relativePath);

    private static string Normalize(string path) => PathPatterns.Normalize(path);
}
