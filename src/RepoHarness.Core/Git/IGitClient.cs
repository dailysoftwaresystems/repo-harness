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
    /// linked one, or <see langword="null"/> when it is not inside a repository - or when git
    /// names a git directory as the main worktree that records no checkout, as one made with
    /// --separate-git-dir does not, and <paramref name="directory"/> is in a linked worktree of
    /// it, where nothing says which checkout is the main one. This is where gitignored harness
    /// state (ssh secrets, the run lock) actually lives.
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

    /// <summary>
    /// The index entries below <paramref name="directory"/>, with paths relative to it, and the flags
    /// that can hide an edit from status.
    /// </summary>
    /// <exception cref="HarnessException">git could not read the index.</exception>
    Task<IReadOnlyList<GitIndexEntry>> ListIndexAsync(string directory, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes the index of the repository at <paramref name="directory"/> hold exactly <paramref name="paths"/>:
    /// each one's bytes as they stand on disk, and no entry they do not name. A path with no file at it is
    /// not held.
    /// </summary>
    /// <param name="directory">The top of the repository's own work tree.</param>
    /// <param name="paths">The files the index is to hold, relative to <paramref name="directory"/>, with forward separators.</param>
    /// <param name="cancellationToken">Cancels the git processes.</param>
    /// <exception cref="HarnessException">
    /// <paramref name="directory"/> is not the top of a repository of its own, or git could not read the
    /// files or write the index.
    /// </exception>
    /// <remarks>
    /// For a tree whose files some other process placed, and whose index is its record of which files
    /// are its own: a copy a sync made has its files written and none of them staged, and everything that
    /// reads "the files git tracks" there then reads none. The bytes are read through no filter and no
    /// line-ending conversion, and the index is built whole beside the one git reads and then put in its
    /// place, so neither a filter the machine cannot run nor a lock a stopped git left behind stops it.
    /// </remarks>
    Task IndexExactlyAsync(string directory, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default);

    /// <summary>The index file git uses for the work tree at <paramref name="directory"/>, as an absolute path.</summary>
    /// <exception cref="HarnessException">git could not say.</exception>
    Task<string> GetIndexFileAsync(string directory, CancellationToken cancellationToken = default);

    /// <summary>
    /// Which files marked assume-unchanged or skip-worktree differ from the index, as status would
    /// see them without the marks, line-ending rules included. The marks are cleared in
    /// <paramref name="indexCopy"/>, a copy of the work tree's index that git may change, never in the
    /// index itself. The paths are relative to <paramref name="directory"/>, and must exist on disk.
    /// </summary>
    /// <exception cref="HarnessException">git could not answer.</exception>
    Task<IReadOnlyList<string>> FindEditedFilesAsync(
        string directory,
        string indexCopy,
        IReadOnlyList<string> assumedUnchanged,
        IReadOnlyList<string> skipWorktree,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The git directory of the repository at <paramref name="path"/>, or of the one a .git file there
    /// points to, or <see langword="null"/> when <paramref name="path"/> is neither.
    /// </summary>
    /// <exception cref="HarnessException">git could not look.</exception>
    Task<string?> ResolveGitDirectoryAsync(string directory, string path, CancellationToken cancellationToken = default);

    /// <summary>How many commits <c>git rev-list</c> selects from <paramref name="revisions"/>.</summary>
    /// <exception cref="HarnessException">git could not walk the history.</exception>
    Task<int> CountCommitsAsync(
        string directory,
        IReadOnlyList<string> revisions,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// What <see cref="CountCommitsAsync"/> answers, asked of the repository whose git directory is
    /// <paramref name="gitDirectory"/>, without its work tree, which may be gone: a submodule's
    /// repository after its checkout was removed, for one.
    /// </summary>
    /// <exception cref="HarnessException">git could not walk the history.</exception>
    Task<int> CountRepositoryCommitsAsync(
        string gitDirectory,
        IReadOnlyList<string> revisions,
        CancellationToken cancellationToken = default);

    /// <summary>Whether the repository whose git directory is <paramref name="gitDirectory"/> holds a stash.</summary>
    /// <exception cref="HarnessException">git could not look.</exception>
    Task<bool> HasStashAsync(string gitDirectory, CancellationToken cancellationToken = default);

    /// <summary>Whether a path is ignored by git's ignore rules.</summary>
    Task<bool> IsIgnoredAsync(string directory, string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Which rule decides each of <paramref name="paths"/> in the work tree at
    /// <paramref name="directory"/>, whether or not the path exists or is tracked.
    /// </summary>
    /// <param name="directory">The work tree's root.</param>
    /// <param name="paths">Paths relative to that root, with forward separators.</param>
    /// <param name="cancellationToken">Stops the question.</param>
    /// <returns>
    /// One decision per path, in the order asked. A path git would not answer about, where it answered
    /// about others, says why in <see cref="IgnoreDecision.Unanswered"/>.
    /// </returns>
    /// <exception cref="HarnessException">git could answer about none of them, or answered in another form or about other paths.</exception>
    /// <remarks>
    /// git's own answer rather than a reading of the rules: what a rule matches depends on its file's
    /// directory, on every rule before and after it, and on whether a directory above the path is
    /// already excluded, where no re-include reaches. Asked without the index, so a tracked file is
    /// judged by the rules alone. git names no re-include that matched a directory above the path, and
    /// matches a rule ending in <c>/</c> against the path itself only where it is a directory that
    /// exists - never a link to one.
    /// </remarks>
    Task<IReadOnlyList<IgnoreDecision>> ExplainIgnoredAsync(
        string directory,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The full id of the commit <paramref name="reference"/> names, or <see langword="null"/> when it
    /// names none.
    /// </summary>
    /// <exception cref="HarnessException">git could not answer at all.</exception>
    Task<string?> ResolveCommitAsync(string directory, string reference, CancellationToken cancellationToken = default);

    /// <summary>
    /// A file's content at <paramref name="commit"/>, or <see langword="null"/> when no file was
    /// there.
    /// </summary>
    /// <param name="directory">The repository's root, which <paramref name="relativePath"/> is relative to.</param>
    /// <param name="commit">A commit id, as <see cref="ResolveCommitAsync"/> returns.</param>
    /// <param name="relativePath">The file, relative to the repository root.</param>
    /// <param name="cancellationToken">Cancels the git processes.</param>
    /// <exception cref="HarnessException">
    /// git could not read the commit, or lists the file there and could not read it.
    /// </exception>
    Task<string?> ReadFileAtCommitAsync(
        string directory,
        string commit,
        string relativePath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Each file's content at <paramref name="commit"/>, keyed by its path as given, with
    /// <see langword="null"/> for one that is not a file there. All of them are read by one git
    /// process; a path that names no file costs one listing of the commit more, which is what tells it
    /// from a file git lists and cannot read.
    /// </summary>
    /// <param name="directory">The repository's root, which the paths are relative to.</param>
    /// <param name="commit">A commit id, as <see cref="ResolveCommitAsync"/> returns.</param>
    /// <param name="relativePaths">The files, relative to the repository root and spelled as git spells them.</param>
    /// <param name="cancellationToken">Cancels the git processes.</param>
    /// <exception cref="HarnessException">
    /// git could not read the commit, or lists one of the files there and could not read it.
    /// </exception>
    Task<IReadOnlyDictionary<string, string?>> ReadFilesAtCommitAsync(
        string directory,
        string commit,
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The files <paramref name="commit"/> holds, from the repository's root: a symbolic link is one;
    /// a directory is not, nor is a submodule's entry, which names a commit in another repository.
    /// </summary>
    /// <param name="directory">A directory inside the repository.</param>
    /// <param name="commit">A commit id, as <see cref="ResolveCommitAsync"/> returns.</param>
    /// <param name="cancellationToken">Cancels the git process.</param>
    /// <exception cref="HarnessException">git could not list the commit.</exception>
    Task<IReadOnlyList<GitName>> ListFilesAtCommitAsync(
        string directory,
        string commit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The names a git command lists, in its <c>-z</c> form - separated by NUL, with nothing quoted -
    /// each as git holds it.
    /// </summary>
    /// <param name="directory">The directory git runs in.</param>
    /// <param name="arguments">A git command that lists names and nothing else, with <c>-z</c>.</param>
    /// <param name="cancellationToken">Cancels the git process.</param>
    /// <exception cref="HarnessException">git failed.</exception>
    Task<IReadOnlyList<GitName>> ListNamesAsync(
        string directory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);

    /// <summary>Runs an arbitrary git subcommand, returning its exit code and output.</summary>
    Task<GitCommandResult> RunAsync(
        string directory,
        IReadOnlyList<string> arguments,
        bool echoOutput = false,
        CancellationToken cancellationToken = default);
}
