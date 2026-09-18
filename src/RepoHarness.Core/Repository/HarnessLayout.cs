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

    /// <summary>Name of the directory holding one subdirectory per ssh host's connection data.</summary>
    public const string SshItemsDirectoryName = "sshItems";

    /// <summary>Name of the directory holding one subdirectory per WSL distribution's connection data.</summary>
    public const string WslDistrosDirectoryName = "wslDistros";

    /// <summary>Name of the file holding one item's connection settings, as <c>NAME=value</c> lines.</summary>
    public const string ItemEnvFileName = ".env";

    /// <summary>Name of the file holding an ssh item's private key.</summary>
    public const string ItemKeyFileName = ".key";

    /// <summary>Name of the file holding the host keys ssh will accept for an item.</summary>
    public const string ItemKnownHostsFileName = "known_hosts";

    /// <summary>Name of the directory holding runner action files and the values they read.</summary>
    public const string RunnerDirectoryName = "runner";

    /// <summary>Name of the directory holding runner action files. Tracked by git.</summary>
    public const string RunnerActionsDirectoryName = "actions";

    /// <summary>Name of the directory holding values actions read. Gitignored.</summary>
    public const string RunnerEnvDirectoryName = ".env";

    /// <summary>Name of the directory holding secret values actions read. Gitignored.</summary>
    public const string RunnerSecretsDirectoryName = ".secrets";

    /// <summary>Name of the directory holding one subdirectory per run, with its logs. Gitignored.</summary>
    public const string RunsDirectoryName = "runs";

    /// <summary>
    /// Name of the directory an action's steps write into while they run. Gitignored, and emptied
    /// when the action finishes.
    /// </summary>
    public const string ActionBuildDirectoryName = "build";

    /// <summary>
    /// Name of the directory an action's persisted outputs are kept in. Gitignored, and what
    /// survives a run.
    /// </summary>
    public const string ActionArtifactsDirectoryName = "artifacts";

    /// <summary>Name of the configuration file.</summary>
    public const string ConfigFileName = "config.json";

    /// <summary>Name of the run lock file.</summary>
    public const string LockFileName = "lock.json";

    /// <summary>
    /// Where one run of an action writes while it runs, relative to a tree root.
    /// </summary>
    /// <param name="actionDirectory">The action's own directory, relative to the actions directory.</param>
    /// <param name="runId">The run this work belongs to.</param>
    /// <param name="leg">The leg doing it.</param>
    /// <remarks>
    /// Keyed by the run <em>and the leg</em>, never written into directly, and laid out exactly as
    /// this run's logs are. One run places many legs and they share its id, so keyed by the run
    /// alone two legs running one action on one machine would write into one directory and each
    /// would measure what the other left behind — which is the collision this keying exists to
    /// prevent, not an instance of it.
    /// </remarks>
    public static string ActionBuildRelative(string actionDirectory, string runId, string leg)
        => ActionScratchRelative(actionDirectory, ActionBuildDirectoryName, runId, leg);

    /// <summary>
    /// Where one run of an action's persisted outputs are kept, relative to a tree root.
    /// </summary>
    /// <param name="actionDirectory">The action's own directory, relative to the actions directory.</param>
    /// <param name="runId">The run this work belongs to.</param>
    /// <param name="leg">The leg doing it.</param>
    public static string ActionArtifactsRelative(string actionDirectory, string runId, string leg)
        => ActionScratchRelative(actionDirectory, ActionArtifactsDirectoryName, runId, leg);

    /// <summary>
    /// The ignore rule that keeps every action's <paramref name="kind"/> directory out of git, at
    /// whatever depth the action is grouped.
    /// </summary>
    /// <param name="kind"><see cref="ActionBuildDirectoryName"/> or <see cref="ActionArtifactsDirectoryName"/>.</param>
    /// <remarks>
    /// Spelled here once, for init to write and for a run that finds it missing to quote, so the line
    /// somebody is told to add is exactly the line init would have added.
    /// </remarks>
    public static string ActionScratchIgnoreRule(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        return $"/{RunnerActionsDirectoryRelative}/**/{kind}/";
    }

    /// <summary>One of an action's two run-keyed directories, relative to a tree root.</summary>
    private static string ActionScratchRelative(string actionDirectory, string kind, string runId, string leg)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leg);

        return Path.Combine(RunnerActionDirectoryRelative(actionDirectory), kind, runId, leg);
    }

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
    /// <remarks>
    /// The default root. A configuration naming its own is resolved by
    /// <see cref="WorktreesDirectoryUnder"/>, which every command uses: the root spends path budget
    /// before a worktree's own name, and on Windows the difference decides whether any name fits.
    /// </remarks>
    public string WorktreesDirectory => Path.Combine(MainHarnessDirectory, WorktreesDirectoryName);

    /// <summary>The worktrees root a configuration declares, resolved against the main checkout.</summary>
    /// <param name="root">The configured root, relative to the main checkout.</param>
    public string WorktreesDirectoryUnder(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        return Path.GetFullPath(Path.Combine(MainCheckoutRoot, root));
    }

    /// <summary>
    /// The ssh items directory, resolved against the main checkout because the connection data it
    /// holds is gitignored and therefore absent from every worktree's checkout.
    /// </summary>
    public string SshItemsDirectory => Path.Combine(MainHarnessDirectory, SshItemsDirectoryName);

    /// <summary>The WSL distributions directory, resolved against the main checkout for the same reason.</summary>
    public string WslDistrosDirectory => Path.Combine(MainHarnessDirectory, WslDistrosDirectoryName);

    /// <summary>One ssh host's directory, holding its <c>.env</c>, its key and its known hosts.</summary>
    /// <param name="item">The item name, as <c>sshItems</c> declares it.</param>
    public string SshItemDirectory(string item) => Path.Combine(SshItemsDirectory, item);

    /// <summary>One WSL distribution's directory, holding its <c>.env</c>.</summary>
    /// <param name="item">The distribution name, as <c>wslDistros</c> declares it.</param>
    public string WslDistroDirectory(string item) => Path.Combine(WslDistrosDirectory, item);

    /// <summary>The runner directory of the tree being acted on: action files, and the values they read.</summary>
    /// <remarks>
    /// Resolved against the tree rather than the main checkout: action files are tracked, so a
    /// worktree has its own, and a runner must act on the tree it was asked about.
    /// </remarks>
    public string RunnerDirectory => Path.Combine(HarnessDirectory, RunnerDirectoryName);

    /// <summary>Where a runner's action directories live. Tracked by git.</summary>
    public string RunnerActionsDirectory => Path.Combine(RunnerDirectory, RunnerActionsDirectoryName);

    /// <summary>
    /// One action's own directory, holding its file and everything that file runs, relative to a
    /// tree root.
    /// </summary>
    /// <remarks>
    /// Relative on purpose. A step that runs in its action's directory is resolved against the leg's
    /// own tree, which is a worktree whenever the leg names one; an absolute path built here would
    /// be the tree the command was typed in, and every leg on a worktree would run the wrong copy.
    /// </remarks>
    /// <param name="name">The action's directory name.</param>
    public static string RunnerActionDirectoryRelative(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return Path.Combine(DirectoryName, RunnerDirectoryName, RunnerActionsDirectoryName, name);
    }

    /// <summary>The harness directory, relative to a tree root.</summary>
    public static string HarnessDirectoryRelative => DirectoryName;

    /// <summary>The runner directory, relative to a tree root, with forward separators.</summary>
    public const string RunnerDirectoryRelative = DirectoryName + "/" + RunnerDirectoryName;

    /// <summary>
    /// The actions directory, relative to a tree root, with forward separators: how a sync, an ignore
    /// rule and a message name it, whichever machine reads it.
    /// </summary>
    public const string RunnerActionsDirectoryRelative = RunnerDirectoryRelative + "/" + RunnerActionsDirectoryName;

    /// <summary>
    /// Where the values actions read live, resolved against the main checkout because they are
    /// gitignored and therefore absent from a worktree's checkout.
    /// </summary>
    public string RunnerEnvDirectory
        => Path.Combine(MainHarnessDirectory, RunnerDirectoryName, RunnerEnvDirectoryName);

    /// <summary>Where the secret values actions read live, resolved against the main checkout.</summary>
    public string RunnerSecretsDirectory
        => Path.Combine(MainHarnessDirectory, RunnerDirectoryName, RunnerSecretsDirectoryName);

    /// <summary>
    /// Where a run's logs live, resolved against the main checkout so that two runs started from
    /// different trees of one repository cannot write the same file without seeing each other.
    /// </summary>
    public string RunsDirectory => Path.Combine(MainHarnessDirectory, RunsDirectoryName);

    /// <summary>One run's directory, named by its id.</summary>
    /// <param name="runId">The run's id.</param>
    public string RunDirectory(string runId) => Path.Combine(RunsDirectory, runId);

    /// <summary>
    /// The run lock, resolved against the main checkout so that a run started from
    /// inside a worktree and one started from the root contend over the same file.
    /// </summary>
    public string LockFile => Path.Combine(MainHarnessDirectory, LockFileName);

    /// <summary>
    /// Directory of one named worktree under the default root. Callers validate the name first; the
    /// worktree service also checks containment before it deletes anything beneath this path.
    /// </summary>
    public string WorktreePath(string name) => Path.Combine(WorktreesDirectory, name);

    /// <summary>Directory of one named worktree under a configured root.</summary>
    /// <param name="root">The configured worktrees root, relative to the main checkout.</param>
    /// <param name="name">The worktree's name, already validated by the caller.</param>
    public string WorktreePathUnder(string root, string name)
        => Path.Combine(WorktreesDirectoryUnder(root), name);
}
