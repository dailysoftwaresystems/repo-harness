using RepoHarness.Core.Configuration;
using RepoHarness.Core.Git;
using RepoHarness.Core.Results;

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
public sealed class SyncExclusions
{
    private readonly string[] _withheld;
    private readonly string[] _excluded;

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
        => Matches(_withheld, relativePath) || Matches(_excluded, relativePath);

    /// <summary>
    /// Whether <paramref name="relativePath"/> is protected from deletion. True only for the
    /// withheld list: an excluded path is one the source chooses not to send, and a copy that keeps
    /// it for ever is a copy of a tree that no longer exists.
    /// </summary>
    /// <param name="relativePath">A path relative to the tree root, with forward separators.</param>
    public bool IsProtectedFromDeletion(string relativePath) => Matches(_withheld, relativePath);

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
    {
        var candidate = Normalize(relativePath);

        return paths.Any(path =>
            string.Equals(candidate, path, StringComparison.Ordinal)
            || candidate.StartsWith(path + "/", StringComparison.Ordinal));
    }

    /// <summary>
    /// Puts a path in the one form both sides of a sync agree on: forward separators, no leading or
    /// trailing separator, no <c>./</c> prefix. A Windows source and a Linux copy otherwise share no
    /// spelling and every comparison misses.
    /// </summary>
    private static string Normalize(string path)
    {
        var normalized = (path ?? string.Empty).Replace('\\', '/').Trim('/');

        return normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
    }
}
