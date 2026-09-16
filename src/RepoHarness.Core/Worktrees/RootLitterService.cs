using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Git;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Worktrees;

/// <summary>
/// Reports files left loose at the root of a checkout.
/// </summary>
/// <remarks>
/// Real work lands in the directories a repository declares. A file loose at the root is a probe
/// artefact, a redirected output, or a path that leaked from another machine, and every one of them
/// is invisible in a diff and survives into a commit nobody meant to make. There is no allowlist on
/// purpose: an allowlist is an escape every lane takes, and the guard this replaces was measured
/// letting every one of its subjects through that way.
/// </remarks>
public interface IRootLitterService
{
    /// <summary>Finds the litter at the root of the tree containing <paramref name="startDirectory"/>.</summary>
    /// <param name="startDirectory">A directory inside the repository.</param>
    /// <param name="cancellationToken">Stops the check.</param>
    /// <exception cref="HarnessException">
    /// The check could not run: git could not be reached, or could not report the tree's status. A
    /// status that could not be read is a refusal, never an empty list: git exiting non-zero on a
    /// damaged index was measured reading as "no untracked files" over a genuinely dirty root.
    /// </exception>
    Task<RootLitterReport> CheckAsync(string startDirectory, CancellationToken cancellationToken = default);
}

/// <summary>What the root of a checkout holds that it should not.</summary>
/// <param name="Root">The checkout root that was examined.</param>
/// <param name="Untracked">
/// Files directly at the root that git does not track, whether or not an ignore rule matches them.
/// Ignored junk is still junk: a rule written for build output also hides every probe artefact
/// sharing its extension, which is how seven of them once sat at a root the guard called clean.
/// </param>
/// <param name="Malformed">
/// Entries at the root whose name holds a character no path on this machine should contain, such as
/// the <c>C:</c> directory a POSIX tool creates when a Windows path reaches it unconverted. Found by
/// listing the directory rather than by asking git, so it is reported even when nothing tracks it
/// and even when it is a directory.
/// </param>
public sealed record RootLitterReport(
    string Root,
    IReadOnlyList<string> Untracked,
    IReadOnlyList<string> Malformed)
{
    /// <summary>Everything found, in one list, in the order it should be read.</summary>
    public IReadOnlyList<string> All => [.. Untracked.Concat(Malformed).Distinct(StringComparer.Ordinal)];

    /// <summary>Whether the root is clean.</summary>
    public bool IsClean => All.Count == 0;
}

/// <inheritdoc cref="IRootLitterService"/>
public sealed class RootLitterService(
    IHarnessContextLoader contextLoader,
    IGitClient gitClient,
    IFileSystem fileSystem) : IRootLitterService
{
    /// <summary>The command this service reports under.</summary>
    public const string CommandName = "check-root-litter";

    /// <summary>
    /// Characters that cannot occur in a path on every platform this tool runs on, and whose
    /// presence in a root entry's name therefore means a path from somewhere else was taken
    /// literally. A colon reaches a POSIX filesystem as part of a drive letter; a backslash reaches
    /// it as a whole Windows path collapsed into one name.
    /// </summary>
    private static readonly char[] ForeignPathCharacters = [':', '\\'];

    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly IGitClient _gitClient = gitClient;
    private readonly IFileSystem _fileSystem = fileSystem;

    /// <inheritdoc/>
    public async Task<RootLitterReport> CheckAsync(
        string startDirectory,
        CancellationToken cancellationToken = default)
    {
        var context = await _contextLoader.LoadAsync(startDirectory, cancellationToken).ConfigureAwait(false);
        var root = context.Layout.RepositoryRoot;

        // --ignored=matching, so a file an ignore rule covers is still reported. Asked through the
        // git client, which clears GIT_DIR, GIT_WORK_TREE and GIT_INDEX_FILE first: with any of
        // them inherited this answers for a tree nobody named, which was measured convicting a
        // tracked file and, the other way round, calling a dirty root clean.
        // -z, so paths arrive NUL separated and exactly as they are on disk. Without it git applies
        // C-style quoting to anything outside ASCII, and an accented file name comes back as
        // "r\303\251sum\303\251.txt" — a name that is neither what is on disk nor anything a reader
        // can delete. --no-optional-locks because this only reads: a check run in a loop would
        // otherwise keep rewriting the index of a tree nobody asked it to touch.
        var status = await _gitClient
            .RunAsync(
                root,
                ["--no-optional-locks", "status", "--porcelain=v1", "--untracked-files=normal", "--ignored=matching", "-z"],
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!status.Succeeded)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"git could not report the status of '{root}', so whether its root is clean is unknown: "
                + status.FailureMessage);
        }

        var untracked = status.StandardOutput
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(entry => entry.StartsWith("?? ", StringComparison.Ordinal)
                || entry.StartsWith("!! ", StringComparison.Ordinal))
            .Select(entry => entry[3..])

            // Anything holding a separator is below the root, and git reports an untracked
            // directory as one entry ending in a separator, so this is also what keeps directories
            // out. That is deliberate: an ignored tree such as a build directory is legitimate, and
            // collapses to exactly one such entry.
            .Where(entry => !entry.Contains('/', StringComparison.Ordinal)
                && !entry.Contains('\\', StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToList();

        return new RootLitterReport(root, untracked, FindMalformed(root));
    }

    /// <summary>
    /// Lists root entries whose names hold a character that should never reach this filesystem.
    /// Independent of git on purpose: the one such directory ever seen was created by a tool git had
    /// been told to ignore, so asking git would have found nothing.
    /// </summary>
    private List<string> FindMalformed(string root)
    {
        var entries = _fileSystem
            .EnumerateDirectories(root)
            .Select(directory => Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)))
            .Concat(_fileSystem
                .EnumerateFiles(root, recursive: false)
                .Select(Path.GetFileName));

        return [.. entries
            .Where(name => !string.IsNullOrEmpty(name) && name.IndexOfAny(ForeignPathCharacters) >= 0)
            .Select(name => name!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)];
    }
}
