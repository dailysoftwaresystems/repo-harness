using RepoHarness.Core.Anchors;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Git;
using RepoHarness.Core.Projects;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Commands;

/// <summary>Creates the harness directory, its configuration, its ignore rules, and the anchor registries.</summary>
public sealed class InitService(
    IFileSystem fileSystem,
    IRepositoryLocator repositoryLocator,
    IConfigStore configStore,
    IGitIgnoreManager gitIgnoreManager,
    IProjectDetector projectDetector,
    VerifyGitService verifyGitService,
    IAnchorRegistryLocator anchorRegistryLocator)
{
    /// <summary>
    /// Ignore rules the harness owns. Contents of the worktrees and ssh directories
    /// are excluded while their placeholders are kept, which is only possible by
    /// excluding the directories' <em>contents</em> rather than the directories: git
    /// cannot re-include a file whose parent directory is itself excluded.
    /// </summary>
    private static readonly string[] IgnoreRules = BuildIgnoreRules();

    /// <summary>
    /// Builds the rules from the layout constants rather than repeating the literals.
    /// A renamed directory would otherwise leave the rules silently pointing at the
    /// old path, and for the ssh directory that means committing private keys.
    /// </summary>
    private static string[] BuildIgnoreRules()
    {
        var root = HarnessLayout.DirectoryName;
        var keep = HarnessLayout.GitKeepFileName;

        return
        [
            $"/{root}/{HarnessLayout.LockFileName}",
            $"/{root}/{HarnessLayout.WorktreesDirectoryName}/*",
            $"!/{root}/{HarnessLayout.WorktreesDirectoryName}/{keep}",
            $"/{root}/{HarnessLayout.SshDirectoryName}/*",
            $"!/{root}/{HarnessLayout.SshDirectoryName}/{keep}",
        ];
    }

    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IRepositoryLocator _repositoryLocator = repositoryLocator;
    private readonly IConfigStore _configStore = configStore;
    private readonly IGitIgnoreManager _gitIgnoreManager = gitIgnoreManager;
    private readonly IProjectDetector _projectDetector = projectDetector;
    private readonly VerifyGitService _verifyGitService = verifyGitService;
    private readonly IAnchorRegistryLocator _anchorRegistryLocator = anchorRegistryLocator;

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

        EnsureDirectory(Path.Combine(root, HarnessLayout.DirectoryName), actions);
        EnsurePlaceholderDirectory(layout.WorktreesDirectory, actions);
        EnsurePlaceholderDirectory(layout.SshDirectory, actions);

        var configFile = Path.Combine(root, HarnessLayout.DirectoryName, HarnessLayout.ConfigFileName);
        if (_fileSystem.FileExists(configFile))
        {
            actions.Add($"kept    {Describe(root, configFile)} (already present)");
        }
        else
        {
            var detected = _projectDetector.Detect(root);
            _configStore.Save(configFile, DefaultConfigFactory.Create(detected));

            actions.Add(detected.Count == 0
                ? $"created {Describe(root, configFile)} (no project detected; declare one under \"projects\")"
                : $"created {Describe(root, configFile)} (detected {string.Join(", ", detected.Select(d => d.Type))})");
        }

        var gitIgnorePath = Path.Combine(root, ".gitignore");
        actions.Add(_gitIgnoreManager.Update(gitIgnorePath, IgnoreRules)
            ? $"updated {Describe(root, gitIgnorePath)}"
            : $"kept    {Describe(root, gitIgnorePath)} (rules already current)");

        // Read back rather than assumed, so a configuration that already existed names the
        // registries, not the defaults. Each registry is created where every anchor command looks
        // for it: one git tracks belongs to the branch, so it goes in the tree init runs in, and
        // one git ignores goes in the main checkout.
        var config = _configStore.Load(configFile);
        var registries = await _anchorRegistryLocator
            .LocateAsync(new HarnessContext(layout, config), cancellationToken)
            .ConfigureAwait(false);

        foreach (var registry in registries.All)
        {
            EnsureAnchorRegistry(root, registry, config.Anchors, actions);
        }

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

    private void EnsureDirectory(string path, List<string> actions)
    {
        if (_fileSystem.DirectoryExists(path))
        {
            return;
        }

        _fileSystem.CreateDirectory(path);
        actions.Add($"created {Path.GetFileName(path)}/");
    }

    /// <summary>
    /// Creates a directory whose contents are ignored, plus the placeholder that
    /// keeps the directory itself in the repository.
    /// </summary>
    private void EnsurePlaceholderDirectory(string path, List<string> actions)
    {
        _fileSystem.CreateDirectory(path);

        var placeholder = Path.Combine(path, HarnessLayout.GitKeepFileName);
        if (_fileSystem.FileExists(placeholder))
        {
            return;
        }

        _fileSystem.WriteAllTextAtomic(placeholder, string.Empty);
        actions.Add($"created {Path.GetFileName(path)}/{HarnessLayout.GitKeepFileName}");
    }

    private static string Describe(string root, string path)
        => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
}
