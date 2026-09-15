namespace RepoHarness.Core.Repository;

/// <summary>
/// Where every piece of harness state lives, derived from one repository.
/// </summary>
/// <param name="RepositoryRoot">
/// Root of the tree the command is acting on. For a linked worktree this is the
/// worktree's own root.
/// </param>
/// <param name="MainCheckoutRoot">
/// Root of the originating checkout. Equals <paramref name="RepositoryRoot"/> unless
/// the command is running inside a worktree.
/// </param>
public sealed record HarnessLayout(string RepositoryRoot, string MainCheckoutRoot)
{
    /// <summary>Name of the harness directory, at the root of a repository.</summary>
    public const string DirectoryName = ".harness-config";

    /// <summary>Name of the worktrees directory inside the harness directory.</summary>
    public const string WorktreesDirectoryName = "worktrees";

    /// <summary>Name of the ssh directory inside the harness directory.</summary>
    public const string SshDirectoryName = "ssh";

    /// <summary>Name of the configuration file.</summary>
    public const string ConfigFileName = "config.json";

    /// <summary>Name of the run lock file.</summary>
    public const string LockFileName = "lock.json";

    /// <summary>Placeholder that keeps an otherwise-ignored directory in git.</summary>
    public const string GitKeepFileName = ".gitkeep";

    /// <summary>
    /// Whether the command is running inside a linked worktree.
    /// </summary>
    /// <param name="platform">
    /// Supplies how paths compare. Hardcoding case-insensitivity here would report
    /// two genuinely different directories on Linux as the same tree.
    /// </param>
    public bool IsWorktree(Platform.IHostPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(platform);

        return !string.Equals(
            Path.TrimEndingDirectorySeparator(RepositoryRoot),
            Path.TrimEndingDirectorySeparator(MainCheckoutRoot),
            platform.PathComparison);
    }

    /// <summary>The harness directory of the tree being acted on.</summary>
    public string HarnessDirectory => Path.Combine(RepositoryRoot, DirectoryName);

    /// <summary>
    /// Configuration file of the tree being acted on. Tracked by git.
    /// </summary>
    /// <remarks>
    /// Services deliberately load configuration from <see cref="MainHarnessDirectory"/>
    /// instead: a worktree's checked-out copy is not the one the harness maintains.
    /// Do not substitute this for that.
    /// </remarks>
    public string ConfigFile => Path.Combine(HarnessDirectory, ConfigFileName);

    /// <summary>
    /// The harness directory of the main checkout. Gitignored state lives here and
    /// nowhere else: a worktree's checkout contains the tracked parts of
    /// <c>.harness-config</c> but never the ignored ones.
    /// </summary>
    public string MainHarnessDirectory => Path.Combine(MainCheckoutRoot, DirectoryName);

    /// <summary>
    /// Worktrees live under the main checkout, never under another worktree, so
    /// running <c>create-worktree</c> from inside a worktree cannot nest them.
    /// </summary>
    public string WorktreesDirectory => Path.Combine(MainHarnessDirectory, WorktreesDirectoryName);

    /// <summary>
    /// SSH configuration, resolved against the main checkout because the secrets it
    /// holds are gitignored and therefore absent from every worktree's checkout.
    /// </summary>
    public string SshDirectory => Path.Combine(MainHarnessDirectory, SshDirectoryName);

    /// <summary>
    /// The run lock, resolved against the main checkout so that a run started from
    /// inside a worktree and one started from the root contend over the same file.
    /// </summary>
    public string LockFile => Path.Combine(MainHarnessDirectory, LockFileName);

    /// <summary>
    /// Directory of one named worktree. Callers validate the name first; the worktree
    /// service also checks containment before it deletes anything beneath this path.
    /// </summary>
    public string WorktreePath(string name) => Path.Combine(WorktreesDirectory, name);
}
