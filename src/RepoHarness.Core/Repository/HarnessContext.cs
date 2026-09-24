using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Git;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Repository;

/// <summary>Everything a command needs to know about where it is running.</summary>
/// <param name="Layout">Resolved paths for this repository.</param>
/// <param name="Config">Parsed configuration.</param>
public sealed record HarnessContext(HarnessLayout Layout, HarnessConfig Config)
{
    /// <summary>
    /// The file <see cref="Config"/> was read from: the tree's own, or the main checkout's for a
    /// worktree that has none of its own.
    /// </summary>
    /// <remarks>
    /// Recorded because something else has to send the same file: a sync places the configuration
    /// a leg on a host will run with, and placing any file but the one this command read is how a
    /// worktree's remote legs came to run with the main checkout's configuration.
    /// </remarks>
    public string ConfigFile { get; init; } = Layout.ConfigFile;
}

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
    IFileSystem fileSystem,
    Platform.IHostPlatform platform,
    Output.IHarnessOutput output) : IHarnessContextLoader
{
    private readonly IRepositoryLocator _repositoryLocator = repositoryLocator;
    private readonly IConfigStore _configStore = configStore;
    private readonly IGitClient _gitClient = gitClient;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly Platform.IHostPlatform _platform = platform;
    private readonly Output.IHarnessOutput _output = output;

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

        // Read from the tree being acted on, which for a worktree is that worktree. config.json
        // is tracked, so a worktree has its own and a branch may legitimately change it: a leg it
        // adds, a runner it declares, a project it renames. Read from the main checkout instead —
        // which is what this did — a worktree could not run anything it had just written, and nothing
        // said so: the tree's own file was read by nobody and no message named the file that was.
        //
        // State stays where it was. Locks and connection data are gitignored and shared, so they
        // resolve against MainCheckoutRoot through the layout and are unaffected by this. The rule is
        // the one the runner directory already follows: what git tracks belongs to the tree, what git
        // ignores belongs to the checkout that owns the repository - with one exception, a run's
        // records, which belong to the tree that ran it (HarnessLayout.RunsDirectory).
        var configFile = layout.ConfigFile;

        if (!_fileSystem.FileExists(configFile))
        {
            // A worktree of a branch that predates the harness has no tracked copy to read. The
            // main checkout's is then the only configuration there is, and using it silently would
            // be the same silence this rule exists to end.
            var fallback = Path.Combine(layout.MainHarnessDirectory, HarnessLayout.ConfigFileName);

            if (layout.IsWorktree(_platform) && _fileSystem.FileExists(fallback))
            {
                _output.Warn(
                    "config",
                    $"this worktree has no '{HarnessLayout.DirectoryName}/{HarnessLayout.ConfigFileName}', "
                    + $"so the main checkout's is being used: '{fallback}'. Anything this tree changes "
                    + "about its configuration is not what is running.");

                return new HarnessContext(layout, _configStore.Load(fallback)) { ConfigFile = fallback };
            }

            throw new HarnessException(
                HarnessExit.NotInitialized,
                $"No harness configuration at '{configFile}'. Run '{ToolPackage.Command} init' first.");
        }

        return new HarnessContext(layout, _configStore.Load(configFile));
    }
}
