using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Git;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Repository;

/// <summary>Everything a command needs to know about where it is running.</summary>
/// <param name="Layout">Resolved paths for this repository.</param>
/// <param name="Config">Parsed configuration.</param>
public sealed record HarnessContext(HarnessLayout Layout, HarnessConfig Config);

/// <summary>
/// Resolves the repository and its configuration. Every command begins this way,
/// so the checks and their error messages exist once rather than per command.
/// </summary>
public interface IHarnessContextLoader
{
    /// <summary>
    /// Loads the context for <paramref name="startDirectory"/>.
    /// </summary>
    /// <exception cref="HarnessException">
    /// git is missing, the directory is not a repository, or the repository has not
    /// been initialised.
    /// </exception>
    Task<HarnessContext> LoadAsync(string startDirectory, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IHarnessContextLoader"/>
public sealed class HarnessContextLoader(
    IRepositoryLocator repositoryLocator,
    IConfigStore configStore,
    IGitClient gitClient,
    IFileSystem fileSystem) : IHarnessContextLoader
{
    private readonly IRepositoryLocator _repositoryLocator = repositoryLocator;
    private readonly IConfigStore _configStore = configStore;
    private readonly IGitClient _gitClient = gitClient;
    private readonly IFileSystem _fileSystem = fileSystem;

    public async Task<HarnessContext> LoadAsync(
        string startDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);

        if (!_gitClient.IsInstalled())
        {
            throw new HarnessException(HarnessExit.ToolMissing, "git is not installed or not on PATH.");
        }

        var layout = await _repositoryLocator
            .LocateAsync(startDirectory, cancellationToken)
            .ConfigureAwait(false);

        if (layout is null)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{startDirectory}' is not inside a git repository.");
        }

        // Configuration is read from the main checkout. A worktree carries its own
        // copy through git, but the main checkout's is the one the harness maintains.
        var configFile = Path.Combine(
            layout.MainHarnessDirectory,
            HarnessLayout.ConfigFileName);

        if (!_fileSystem.FileExists(configFile))
        {
            throw new HarnessException(
                HarnessExit.NotInitialized,
                $"No harness configuration in '{layout.MainCheckoutRoot}'. Run 'repo-harness init' first.");
        }

        return new HarnessContext(layout, _configStore.Load(configFile));
    }
}
