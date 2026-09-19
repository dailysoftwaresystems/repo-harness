using RepoHarness.Core.Anchors;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Git;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Projects;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Tools;

namespace RepoHarness.Core.Commands;

/// <summary>Creates the harness directory, its configuration, its ignore rules, and the anchor registries.</summary>
public sealed class InitService(
    IFileSystem fileSystem,
    IRepositoryLocator repositoryLocator,
    IConfigStore configStore,
    IGitIgnoreManager gitIgnoreManager,
    IProjectDetector projectDetector,
    VerifyGitService verifyGitService,
    IAnchorRegistryLocator anchorRegistryLocator,
    IToolProvisionService toolProvisionService,
    ManagedIgnoreCheck managedIgnoreCheck,
    IHostPlatform platform)
{
    /// <summary>
    /// Builds the ignore rules the harness owns, from the layout constants rather than by repeating
    /// the literals. A renamed directory would otherwise leave the rules pointing at the old path,
    /// and for the directories holding connection data that means committing a private key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two shapes, for two kinds of directory.
    /// </para>
    /// <para>
    /// The four slots — <c>sshItems</c>, <c>wslDistros</c>, <c>runner/.env</c> and
    /// <c>runner/.secrets</c> — are small directories a person fills by hand, and keeping each one
    /// in the repository is what tells that person where the file goes. So their <em>contents</em>
    /// are excluded and a placeholder is kept, which is the only way to do both: git cannot
    /// re-include a file whose parent directory is itself excluded.
    /// </para>
    /// <para>
    /// The worktrees root is excluded whole, with no placeholder, because it is a different kind of
    /// directory: it holds entire working trees, nobody fills it by hand, and the worktree commands
    /// create it themselves the first time they need it. Excluding only its contents has a cost the
    /// slots can afford and it cannot. Measured, in a throwaway repository:
    /// </para>
    /// <code>
    /// query                          /wt/          /wt/* + !/wt/.gitkeep
    /// check-ignore 'wt/'  absent     IGNORED       IGNORED
    /// check-ignore 'wt'   absent     NOT-IGNORED   NOT-IGNORED
    /// check-ignore 'wt/'  present    IGNORED       IGNORED, and NOT-IGNORED once .gitkeep is committed
    /// check-ignore 'wt'   present    IGNORED       NOT-IGNORED
    /// </code>
    /// <para>
    /// Excluding the contents makes the directory's own answer depend on a trailing slash, and it
    /// fails toward not ignored. Worse, a directory holding any tracked file never reads as ignored
    /// under either spelling, so a committed placeholder turns even the careful query wrong. For a
    /// slot holding an address and a key that answer costs nothing; for a root holding checkouts it
    /// is the difference between a clean sync and shipping every worktree to a remote host, asked
    /// for in the most natural spelling and answered wrongly without a word. It also shows the
    /// placeholder as untracked until it is committed, which is exactly what sync refuses on.
    /// </para>
    /// <para>
    /// The root is taken from the configuration, because a configured root that nothing ignores
    /// puts whole checkouts into <c>git status</c>.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<ManagedIgnoreRule> BuildIgnoreRules(WorktreeSettings worktrees)
    {
        var root = HarnessLayout.DirectoryName;
        var keep = HarnessLayout.GitKeepFileName;
        var runner = HarnessLayout.RunnerDirectoryRelative;
        var worktreesRoot = worktrees.Root.Replace('\\', '/').Trim('/');
        var any = ManagedIgnoreRule.AnyName;

        return
        [
            new($"/{root}/{HarnessLayout.LockFileName}", $"{root}/{HarnessLayout.LockFileName}", Ignores: true),

            // Run logs, which exist to be read after a run and never to be committed.
            new($"/{root}/{HarnessLayout.RunsDirectoryName}/", $"{root}/{HarnessLayout.RunsDirectoryName}/{any}", Ignores: true),

            // The directory itself, never only its contents: see the remarks above.
            new($"/{worktreesRoot}/", $"{worktreesRoot}/{any}", Ignores: true),

            // One directory per host, each holding an address, a user and a key. Nothing under
            // either may ever be tracked.
            .. Slot($"{root}/{HarnessLayout.SshItemsDirectoryName}", keep),
            .. Slot($"{root}/{HarnessLayout.WslDistrosDirectoryName}", keep),

            // Action files are tracked; the values they read are not.
            .. Slot($"{runner}/{HarnessLayout.RunnerEnvDirectoryName}", keep),
            .. Slot($"{runner}/{HarnessLayout.RunnerSecretsDirectoryName}", keep),

            // What an action's steps write, and what they asked to keep. Both sit beside the
            // action's own tracked files, at whatever depth the author grouped it to, so these are
            // matched at any depth rather than rooted. Neither is ever tracked: one is this run's
            // working space and the other is output, and output committed beside the thing that
            // produced it is how a repository comes to hold a measurement nobody can reproduce.
            new(
                HarnessLayout.ActionScratchIgnoreRule(HarnessLayout.ActionBuildDirectoryName),
                $"{HarnessLayout.RunnerActionsDirectoryRelative}/{any}/{HarnessLayout.ActionBuildDirectoryName}/{any}",
                Ignores: true),
            new(
                HarnessLayout.ActionScratchIgnoreRule(HarnessLayout.ActionArtifactsDirectoryName),
                $"{HarnessLayout.RunnerActionsDirectoryRelative}/{any}/{HarnessLayout.ActionArtifactsDirectoryName}/{any}",
                Ignores: true),
        ];

        // A slot's contents ignored, and its placeholder kept.
        static ManagedIgnoreRule[] Slot(string directory, string keep) =>
        [
            new($"/{directory}/*", $"{directory}/{ManagedIgnoreRule.AnyName}", Ignores: true),
            new($"!/{directory}/{keep}", $"{directory}/{keep}", Ignores: false),
        ];
    }

    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IRepositoryLocator _repositoryLocator = repositoryLocator;
    private readonly IConfigStore _configStore = configStore;
    private readonly IGitIgnoreManager _gitIgnoreManager = gitIgnoreManager;
    private readonly IProjectDetector _projectDetector = projectDetector;
    private readonly VerifyGitService _verifyGitService = verifyGitService;
    private readonly IAnchorRegistryLocator _anchorRegistryLocator = anchorRegistryLocator;
    private readonly IToolProvisionService _toolProvisionService = toolProvisionService;
    private readonly ManagedIgnoreCheck _managedIgnoreCheck = managedIgnoreCheck;
    private readonly IHostPlatform _platform = platform;

    /// <summary>Initialises the tree containing <paramref name="startDirectory"/>, installing nothing.</summary>
    /// <param name="startDirectory">A directory in the tree.</param>
    /// <param name="cancellationToken">Stops the run; what was already created stays.</param>
    public Task<CommandOutcome> InitializeAsync(string startDirectory, CancellationToken cancellationToken = default)
        => InitializeAsync(startDirectory, installTools: false, cancellationToken);

    /// <summary>
    /// Initialises the tree containing <paramref name="startDirectory"/> - the worktree it is in, or
    /// the main checkout. Safe to re-run: missing pieces are created and existing ones are left alone,
    /// so this also repairs a partially initialised tree.
    /// </summary>
    /// <param name="startDirectory">A directory in the tree.</param>
    /// <param name="installTools">
    /// Whether to install what each declared leg's host is missing as well. Off unless asked for:
    /// adopting the harness's files is a change to one tree, and an install is a change to machines.
    /// </param>
    /// <param name="cancellationToken">Stops the run; what was already created stays.</param>
    /// <remarks>
    /// Everything git tracks is written in the tree it runs in, a worktree's included: its
    /// configuration, its <c>.gitignore</c> and the placeholders that keep each directory in git. A
    /// lane adopting the harness adopts it on its own branch; written into the main checkout, the
    /// lane's own <c>.gitignore</c> never changed and main's did. What git ignores - connection data,
    /// runner values and secrets, the lock - is read from the main checkout whichever tree asks, and
    /// init in a worktree says so rather than creating any of it there.
    /// </remarks>
    public async Task<CommandOutcome> InitializeAsync(
        string startDirectory,
        bool installTools,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);

        var gitStatus = await _verifyGitService
            .VerifyAsync(startDirectory, cancellationToken)
            .ConfigureAwait(false);

        if (gitStatus != VerifyGitStatus.Success)
        {
            return gitStatus == VerifyGitStatus.GitNotInstalled
                ? CommandOutcome.Failed(HarnessExit.ToolMissing, "git is not installed or not on PATH.")
                : CommandOutcome.Failed(
                    HarnessExit.Refused,
                    $"'{startDirectory}' is not inside a git repository. Run 'git init' first.");
        }

        var layout = await _repositoryLocator
            .LocateAsync(startDirectory, cancellationToken)
            .ConfigureAwait(false);

        if (layout is null)
        {
            return CommandOutcome.Failed(HarnessExit.Refused, "Could not resolve the repository root.");
        }

        var root = layout.RepositoryRoot;
        var worktree = layout.IsWorktree(_platform);
        var actions = new List<string>();

        EnsureDirectory(root, layout.HarnessDirectory, actions);
        EnsureConfiguration(layout, worktree, actions);

        // Read back rather than assumed, so a configuration that already existed names the
        // registries, the worktrees root and the hosts it declares, not the defaults. Read before
        // the ignore rules are written, because the rules depend on the configured worktrees root.
        var config = _configStore.Load(layout.ConfigFile);

        // Each carries a placeholder, because git tracks no empty directory: without one a fresh
        // clone of this branch would arrive without the directory at all. Whether the rest of each
        // is ignored is the block's to say, below. No worktrees root here: the worktree commands
        // create it the first time they need it, and a placeholder in it is what would make it read
        // as not ignored (see BuildIgnoreRules).
        foreach (var directory in HarnessLayout.PlaceholderDirectories)
        {
            EnsurePlaceholderDirectory(root, Path.Combine(layout.HarnessDirectory, directory), actions);
        }

        var gitIgnorePath = Path.Combine(root, ".gitignore");
        var ignoreRules = BuildIgnoreRules(config.Worktrees);

        actions.Add(_gitIgnoreManager.Update(gitIgnorePath, [.. ignoreRules.Select(rule => rule.Rule)])
            ? $"updated {Describe(root, gitIgnorePath)}"
            : $"kept    {Describe(root, gitIgnorePath)} (rules already current)");

        await ReportConflictsAsync(root, gitIgnorePath, ignoreRules, actions, cancellationToken).ConfigureAwait(false);

        var registries = await _anchorRegistryLocator
            .LocateAsync(new HarnessContext(layout, config), cancellationToken)
            .ConfigureAwait(false);

        foreach (var registry in registries.All)
        {
            EnsureAnchorRegistry(root, registry, config.Anchors, actions);
        }

        if (worktree)
        {
            actions.Add(
                $"note    this is a worktree: what git ignores - connection data, runner values and secrets, "
                + $"the lock - is read from the main checkout's '{layout.MainHarnessDirectory}', which init "
                + "leaves as it is");
        }

        var interrupted = installTools
            ? await ProvisionAsync(root, config, actions, cancellationToken).ConfigureAwait(false)
            : Unprovisioned(config, actions);

        // The tree is initialised either way, and the list below says everything that was done. Only
        // the exit code reports that the last step was stopped rather than that it finished.
        return interrupted
            ? CommandOutcome.Failed(
                HarnessExit.Cancelled,
                $"initialised {root}, and the tool check was interrupted",
                actions)
            : CommandOutcome.Ok($"initialised {root}", actions);
    }

    /// <summary>
    /// Creates the tree's configuration when it has none: a copy of the main checkout's, for a
    /// worktree running on it, and one seeded from the projects detected here otherwise.
    /// </summary>
    /// <remarks>
    /// A worktree of a branch that predates the harness runs with the main checkout's configuration
    /// until it has its own, and is warned on every command that it does. Adopting the harness there
    /// keeps what it was running with, now tracked on its branch: a default in its place would drop
    /// every leg, host and runner main declares, and say nothing.
    /// </remarks>
    private void EnsureConfiguration(HarnessLayout layout, bool worktree, List<string> actions)
    {
        var root = layout.RepositoryRoot;
        var configFile = layout.ConfigFile;
        var mainConfig = Path.Combine(layout.MainHarnessDirectory, HarnessLayout.ConfigFileName);

        if (_fileSystem.FileExists(configFile))
        {
            actions.Add($"kept    {Describe(root, configFile)} (already present)");
            return;
        }

        if (worktree && _fileSystem.FileExists(mainConfig))
        {
            _fileSystem.WriteAllTextAtomic(configFile, _fileSystem.ReadAllText(mainConfig));
            actions.Add($"created {Describe(root, configFile)} (copied from the main checkout's, which this worktree was running with)");
            return;
        }

        var detected = _projectDetector.Detect(root);
        _configStore.Save(configFile, DefaultConfigFactory.Create(detected, _platform.PlatformKey, _platform.Processor));

        actions.Add(detected.Count == 0
            ? $"created {Describe(root, configFile)} (no project detected; declare one under \"projects\")"
            : $"created {Describe(root, configFile)} (detected {string.Join(", ", detected.Select(d => d.Type))}; legs for {_platform.PlatformKey} {_platform.Processor})");
    }

    /// <summary>
    /// Creates a missing anchor registry from the skeleton. An existing registry is never touched,
    /// whatever it holds: it is somebody's record of deferred work.
    /// </summary>
    private void EnsureAnchorRegistry(
        string root,
        AnchorRegistry registry,
        AnchorSettings settings,
        List<string> actions)
    {
        var shown = Describe(root, registry.FullPath);

        if (_fileSystem.FileExists(registry.FullPath))
        {
            actions.Add($"kept    {shown} (already present)");
            return;
        }

        _fileSystem.WriteAllTextAtomic(registry.FullPath, AnchorRegistrySkeleton.Render(registry.Kind, settings));
        actions.Add($"created {shown}");
    }

    /// <summary>
    /// Names each rule that turns a path the managed block rules on the other way, as git itself
    /// decides those paths.
    /// </summary>
    /// <remarks>
    /// Reported and never removed: hand-written rules are the repository's own. A rule that git
    /// follows undoes the block there - a re-include putting a secret back in reach of <c>git add</c>,
    /// or a whole-directory rule no placeholder can be re-included from. One the block overrules does
    /// nothing there, which is worth knowing when somebody wrote it meaning something. A rule that
    /// agrees with the block changes nothing and is not named. Where git could not be asked, that is
    /// said instead: the tree is initialised either way.
    /// </remarks>
    private async Task ReportConflictsAsync(
        string root,
        string gitIgnorePath,
        IReadOnlyList<ManagedIgnoreRule> rules,
        List<string> actions,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ManagedIgnoreConflict> conflicts;

        try
        {
            conflicts = await _managedIgnoreCheck
                .FindAsync(root, _fileSystem.ReadAllText(gitIgnorePath), rules, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HarnessException ex)
        {
            actions.Add($"note    could not ask git which rules decide the paths the managed block keeps: {ex.Message}");
            return;
        }

        foreach (var conflict in conflicts)
        {
            var paths = string.Join(", ", conflict.Paths.Select(path => $"'{path}'"));
            var managed = conflict.Ignores ? "keeps in git" : "ignores";

            if (conflict.Source is null)
            {
                actions.Add($"note    no rule ignores {paths}, which the managed block ignores; git reads another .gitignore than this one");
                continue;
            }

            var where = $"note    {conflict.Source} line {conflict.Line} ('{conflict.Pattern}')";
            var what = conflict.Ignores ? "ignores" : "re-includes";

            actions.Add(conflict.Wins
                ? $"{where} {what} {paths}, which the managed block {managed}; git follows that rule"
                : $"{where} {what} {paths}, which the managed block {managed}; a later rule decides them, so this one does nothing there");
        }
    }

    private void EnsureDirectory(string root, string path, List<string> actions)
    {
        if (_fileSystem.DirectoryExists(path))
        {
            return;
        }

        _fileSystem.CreateDirectory(path);
        actions.Add($"created {Describe(root, path)}/");
    }

    /// <summary>
    /// Creates a directory plus the placeholder that keeps the directory itself in the repository,
    /// whether its contents are ignored or tracked: git records no empty directory either way.
    /// </summary>
    private void EnsurePlaceholderDirectory(string root, string path, List<string> actions)
    {
        _fileSystem.CreateDirectory(path);

        var placeholder = Path.Combine(path, HarnessLayout.GitKeepFileName);
        if (_fileSystem.FileExists(placeholder))
        {
            return;
        }

        _fileSystem.WriteAllTextAtomic(placeholder, string.Empty);
        actions.Add($"created {Describe(root, placeholder)}");
    }

    /// <summary>
    /// Installs or updates what each declared leg's host is missing, so a fresh clone is ready to run
    /// rather than ready to be told what is missing.
    /// </summary>
    /// <remarks>
    /// Only with <c>--install-tools</c>, and nothing happens where no leg is declared, which is every
    /// first <c>init</c>: the configuration it has just written names no host. Where hosts are
    /// declared, a failure here is reported and never fatal — the harness directory exists either way, and a host that is switched off is a
    /// normal state that must not leave a repository half-initialised. Being interrupted is not a
    /// failure of this step and is reported apart from one, because a privileged install can stop here
    /// to ask for a password and Ctrl+C is how somebody who has not got one answers.
    /// </remarks>
    /// <returns>Whether the run was interrupted rather than finishing, however it ended otherwise.</returns>
    private async Task<bool> ProvisionAsync(
        string root,
        HarnessConfig config,
        List<string> actions,
        CancellationToken cancellationToken)
    {
        if (config.Legs.Count == 0)
        {
            return false;
        }

        try
        {
            var report = await _toolProvisionService
                .ProvisionAsync(root, legNames: null, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            foreach (var leg in report.Legs)
            {
                actions.Add(leg.Unreachable is { } reason
                    ? $"tools   {leg.Leg} on {leg.Host}: not checked, {reason}"
                    : $"tools   {leg.Leg} on {leg.Host}: "
                        + string.Join(", ", leg.Tools.Select(tool => $"{tool.Tool} {tool.StateName}")));
            }

            if (!report.Passed)
            {
                actions.Add(
                    "tools   some legs are missing tools; run 'DssHarness install-missing-tools' for the detail");
            }

            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Told apart from the failures below: nothing went wrong, somebody stopped it. Reported
            // rather than thrown so the list of what was created survives, and reported as an
            // interruption rather than as success so a script that reads the exit code is not told
            // the tools were checked when they were not. Guarded on the run's own token, because a
            // budget inside provisioning cancels its own work and is a failure like any other.
            actions.Add("tools   could not be checked: interrupted before finishing");
            return true;
        }
        catch (Exception ex) when (ex is HarnessException or ConfigException or Processes.ProgramStartException)
        {
            actions.Add($"tools   could not be checked: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Says how to install what each declared leg's host is missing, where init was not asked to.
    /// </summary>
    /// <returns>Never interrupted: nothing ran.</returns>
    private static bool Unprovisioned(HarnessConfig config, List<string> actions)
    {
        if (config.Legs.Count > 0)
        {
            actions.Add(
                $"tools   not checked; 'DssHarness {ToolProvisionService.CommandName} --dry-run' lists what each leg's "
                + $"host is missing, and 'DssHarness {ToolProvisionService.CommandName}' or 'DssHarness init "
                + "--install-tools' installs it");
        }

        return false;
    }

    /// <summary>
    /// A path as the list of what was done shows it: from the tree's root, or whole where it lies
    /// outside the tree, as an ignored registry kept in the main checkout does.
    /// </summary>
    private static string Describe(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);

        return relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative)
            ? path
            : relative.Replace(Path.DirectorySeparatorChar, '/');
    }
}
