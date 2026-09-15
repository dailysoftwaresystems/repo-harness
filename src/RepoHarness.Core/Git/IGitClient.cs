using RepoHarness.Core.Results;

namespace RepoHarness.Core.Git;

/// <summary>
/// Access to git. Wraps the git command line rather than a library binding: the
/// harness depends on git's own worktree semantics, and git is already a hard
/// prerequisite of every repository it serves.
/// </summary>
/// <remarks>
/// Every path this interface returns comes from git in one resolved form, with symbolic
/// links followed, so paths from different calls can be compared with each other. A path
/// the caller typed is only ever used to tell git where to start.
/// </remarks>
public interface IGitClient
{
    /// <summary>Whether a usable git executable is on PATH.</summary>
    bool IsInstalled();

    /// <summary>Whether <paramref name="directory"/> is inside a work tree.</summary>
    /// <exception cref="HarnessException">
    /// git could not inspect the directory, for example because it refuses a repository
    /// owned by another user. That is not the same answer as "not a repository".
    /// </exception>
    Task<bool> IsRepositoryAsync(string directory, CancellationToken cancellationToken = default);

    /// <summary>
    /// Root of the work tree containing <paramref name="directory"/>, or <see langword="null"/>
    /// when it is not inside a repository. For a linked worktree this is the worktree's own
    /// root, not the main checkout.
    /// </summary>
    /// <exception cref="HarnessException">git could not inspect the directory.</exception>
    Task<string?> GetRepositoryRootAsync(string directory, CancellationToken cancellationToken = default);

    /// <summary>
    /// The repository's main worktree, even when <paramref name="directory"/> is inside a
    /// linked one, or <see langword="null"/> when it is not inside a repository. This is
    /// where gitignored harness state (ssh secrets, the run lock) actually lives.
    /// </summary>
    /// <exception cref="HarnessException">git could not inspect the directory.</exception>
    Task<GitWorktree?> GetMainWorktreeAsync(string directory, CancellationToken cancellationToken = default);

    /// <summary>Whether the work tree has uncommitted changes, including untracked files.</summary>
    /// <exception cref="HarnessException">git could not read the status.</exception>
    Task<bool> IsDirtyAsync(string directory, CancellationToken cancellationToken = default);

    /// <summary>
    /// Porcelain status entries, one per changed path: two status characters and a space, then the
    /// path; a rename or copy is one entry, with its new path. Untracked files and changed submodules
    /// are listed even where configuration would hide them; an untracked directory is one entry, and
    /// an ignored file is none.
    /// </summary>
    /// <exception cref="HarnessException">git could not read the status.</exception>
    Task<IReadOnlyList<string>> GetStatusAsync(string directory, CancellationToken cancellationToken = default);

    /// <summary>Lists every worktree attached to the repository, the main worktree first.</summary>
    Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(string directory, CancellationToken cancellationToken = default);

    /// <summary>
    /// Where <paramref name="directory"/> sits in its repository, or <see langword="null"/> when it is
    /// not inside one.
    /// </summary>
    /// <exception cref="HarnessException">git could not inspect the directory.</exception>
    Task<GitLocation?> GetLocationAsync(string directory, CancellationToken cancellationToken = default);

    /// <summary>Every entry of the work tree's index, with the flags that can hide an edit from status.</summary>
    /// <exception cref="HarnessException">git could not read the index.</exception>
    Task<IReadOnlyList<GitIndexEntry>> ListIndexAsync(string directory, CancellationToken cancellationToken = default);

    /// <summary>
    /// The object ids git would store for <paramref name="paths"/>, which are relative to
    /// <paramref name="directory"/>, in the same order, with the filters <c>git add</c> applies.
    /// </summary>
    /// <exception cref="HarnessException">git could not hash a file.</exception>
    Task<IReadOnlyList<string>> HashFilesAsync(
        string directory,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default);

    /// <summary>How many commits <c>git rev-list</c> selects from <paramref name="revisions"/>.</summary>
    /// <exception cref="HarnessException">git could not walk the history.</exception>
    Task<int> CountCommitsAsync(
        string directory,
        IReadOnlyList<string> revisions,
        CancellationToken cancellationToken = default);

    /// <summary>Whether a path is ignored by git's ignore rules.</summary>
    Task<bool> IsIgnoredAsync(string directory, string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// The full id of the commit <paramref name="reference"/> names, or <see langword="null"/> when it
    /// names none.
    /// </summary>
    /// <exception cref="HarnessException">git could not answer at all.</exception>
    Task<string?> ResolveCommitAsync(string directory, string reference, CancellationToken cancellationToken = default);

    /// <summary>
    /// A file's content at <paramref name="commit"/>, or <see langword="null"/> when the file did not
    /// exist there.
    /// </summary>
    /// <param name="directory">The repository's root, which <paramref name="relativePath"/> is relative to.</param>
    /// <param name="commit">A commit id, as <see cref="ResolveCommitAsync"/> returns.</param>
    /// <param name="relativePath">The file, relative to the repository root.</param>
    /// <param name="cancellationToken">Cancels the git processes.</param>
    /// <exception cref="HarnessException">git could not read the commit.</exception>
    Task<string?> ReadFileAtCommitAsync(
        string directory,
        string commit,
        string relativePath,
        CancellationToken cancellationToken = default);

    /// <summary>Runs an arbitrary git subcommand, returning its exit code and output.</summary>
    Task<GitCommandResult> RunAsync(
        string directory,
        IReadOnlyList<string> arguments,
        bool echoOutput = false,
        CancellationToken cancellationToken = default);
}
