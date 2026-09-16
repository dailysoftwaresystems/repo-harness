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
    private static string[] BuildIgnoreRules(WorktreeSettings worktrees)
    {
        var root = HarnessLayout.DirectoryName;
        var keep = HarnessLayout.GitKeepFileName;
        var runner = $"{root}/{HarnessLayout.RunnerDirectoryName}";
        var worktreesRoot = worktrees.Root.Replace('\\', '/').Trim('/');

        return
        [
            $"/{root}/{HarnessLayout.LockFileName}",

            // Run logs, which exist to be read after a run and never to be committed.
            $"/{root}/{HarnessLayout.RunsDirectoryName}/",

            // The directory itself, never only its contents: see the remarks above.
            $"/{worktreesRoot}/",

            // One directory per host, each holding an address, a user and a key. Nothing under
            // either may ever be tracked.
            $"/{root}/{HarnessLayout.SshItemsDirectoryName}/*",
            $"!/{root}/{HarnessLayout.SshItemsDirectoryName}/{keep}",
            $"/{root}/{HarnessLayout.WslDistrosDirectoryName}/*",
            $"!/{root}/{HarnessLayout.WslDistrosDirectoryName}/{keep}",

            // Action files are tracked; the values they read are not.
            $"/{runner}/{HarnessLayout.RunnerEnvDirectoryName}/*",
            $"!/{runner}/{HarnessLayout.RunnerEnvDirectoryName}/{keep}",
            $"/{runner}/{HarnessLayout.RunnerSecretsDirectoryName}/*",
            $"!/{runner}/{HarnessLayout.RunnerSecretsDirectoryName}/{keep}",
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
    private readonly IHostPlatform _platform = platform;

    /// <summary>
    /// Initialises the repository containing <paramref name="startDirectory"/>.
    /// Safe to re-run: missing pieces are created and existing ones are left alone,
    /// so this also repairs a partially initialised repository.
    /// </summary>
    public async Task<CommandOutcome> InitializeAsync(
        string startDirectory,
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

        // Initialise the main checkout even when invoked from a worktree: the state
        // created here is shared, and a second copy inside a worktree would be a
        // second source of truth. The anchor registries are the exception, below.
        var root = layout.MainCheckoutRoot;
        var actions = new List<string>();

        EnsureDirectory(root, Path.Combine(root, HarnessLayout.DirectoryName), actions);

        var configFile = Path.Combine(root, HarnessLayout.DirectoryName, HarnessLayout.ConfigFileName);
        if (_fileSystem.FileExists(configFile))
        {
            actions.Add($"kept    {Describe(root, configFile)} (already present)");
        }
        else
        {
            var detected = _projectDetector.Detect(root);
            _configStore.Save(configFile, DefaultConfigFactory.Create(detected, _platform.PlatformKey, _platform.Processor));

            actions.Add(detected.Count == 0
                ? $"created {Describe(root, configFile)} (no project detected; declare one under \"projects\")"
                : $"created {Describe(root, configFile)} (detected {string.Join(", ", detected.Select(d => d.Type))}; legs for {_platform.PlatformKey} {_platform.Processor})");
        }

        // Read back rather than assumed, so a configuration that already existed names the
        // registries, the worktrees root and the hosts it declares, not the defaults. Read before
        // the ignore rules are written, because the rules depend on the configured worktrees root.
        var config = _configStore.Load(configFile);

        // No worktrees root here. The worktree commands create it the first time they need it, and a
        // placeholder in it is what would make it read as not ignored (see BuildIgnoreRules).
        EnsurePlaceholderDirectory(root, layout.SshItemsDirectory, actions);
        EnsurePlaceholderDirectory(root, layout.WslDistrosDirectory, actions);
        // Described against the tree it is created in, not the main checkout. This one is tracked,
        // so it belongs to whichever tree init was run from; described against the main checkout it
        // prints as a path climbing out of one directory and back into another.
        EnsureDirectory(layout.RepositoryRoot, layout.RunnerActionsDirectory, actions);
        EnsurePlaceholderDirectory(root, layout.RunnerEnvDirectory, actions);
        EnsurePlaceholderDirectory(root, layout.RunnerSecretsDirectory, actions);

        var gitIgnorePath = Path.Combine(root, ".gitignore");
        var ignoreRules = BuildIgnoreRules(config.Worktrees);

        actions.Add(_gitIgnoreManager.Update(gitIgnorePath, ignoreRules)
            ? $"updated {Describe(root, gitIgnorePath)}"
            : $"kept    {Describe(root, gitIgnorePath)} (rules already current)");

        ReportOverlaps(root, gitIgnorePath, ignoreRules, actions);

        var registries = await _anchorRegistryLocator
            .LocateAsync(new HarnessContext(layout, config), cancellationToken)
            .ConfigureAwait(false);

        foreach (var registry in registries.All)
        {
            EnsureAnchorRegistry(root, registry, config.Anchors, actions);
        }

        await ProvisionAsync(root, config, actions, cancellationToken).ConfigureAwait(false);

        return CommandOutcome.Ok($"initialised {root}", actions);
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
    /// Names each hand-written rule that states again, perhaps in another shape, a path the managed
    /// block already rules on.
    /// </summary>
    /// <remarks>
    /// Reported and never removed. The managed block leaves hand-written rules alone on purpose, so a
    /// repository that already ignored one of these paths by hand keeps its own rule and gains a
    /// second one — and two differently-shaped rules for one path is a state nothing else points
    /// out. A whole-directory rule written by hand also silently cancels the managed block's
    /// placeholder, since git cannot re-include a file inside an excluded directory. Read from the
    /// file as it now is, so the line numbers are the ones a reader will find.
    /// </remarks>
    private void ReportOverlaps(string root, string gitIgnorePath, IReadOnlyList<string> rules, List<string> actions)
    {
        var shown = Describe(root, gitIgnorePath);

        foreach (var overlap in _gitIgnoreManager.FindOverlaps(_fileSystem.ReadAllText(gitIgnorePath), rules))
        {
            var where = $"note    {shown} line {overlap.LineNumber} ('{overlap.Rule}')";

            actions.Add(overlap.Contradicts
                ? $"{where} {(overlap.ReIncludes ? "re-includes" : "ignores")} '{overlap.Path}', which the managed "
                    + $"block {(overlap.ReIncludes ? "ignores" : "re-includes")}; whichever of the two comes later in "
                    + "the file wins"
                : $"{where} repeats the managed block's rule for '{overlap.Path}'; keep one of them so there is one "
                    + "statement of what is ignored");
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
    /// Creates a directory whose contents are ignored, plus the placeholder that
    /// keeps the directory itself in the repository.
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
    /// Nothing happens where no leg is declared, which is every first <c>init</c>: the configuration
    /// it has just written names no host. Where hosts are declared, a failure here is reported and
    /// never fatal — the harness directory exists either way, and a host that is switched off is a
    /// normal state that must not leave a repository half-initialised.
    /// </remarks>
    private async Task ProvisionAsync(
        string root,
        HarnessConfig config,
        List<string> actions,
        CancellationToken cancellationToken)
    {
        if (config.Legs.Count == 0)
        {
            return;
        }

        try
        {
            var report = await _toolProvisionService
                .ProvisionAsync(root, legNames: null, cancellationToken)
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
        }
        catch (Exception ex) when (ex is HarnessException or ConfigException or Processes.ProgramStartException)
        {
            actions.Add($"tools   could not be checked: {ex.Message}");
        }
    }

    private static string Describe(string root, string path)
        => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
}
