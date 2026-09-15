using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Git;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Worktrees;

/// <summary>Creating, listing and removing development worktrees.</summary>
public interface IWorktreeService
{
    /// <summary>Creates a worktree, generating its name when <paramref name="useRandomName"/> is set.</summary>
    /// <exception cref="HarnessException">
    /// The repository is not initialised, or git could not be reached.
    /// </exception>
    Task<WorktreeOutcome> CreateAsync(
        string startDirectory,
        string? name,
        bool useRandomName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a worktree and everything under it. Unless <paramref name="force"/> is set, a
    /// worktree with uncommitted changes is refused and left untouched, and so is one whose
    /// status git cannot report.
    /// </summary>
    /// <exception cref="HarnessException">
    /// As for <see cref="CreateAsync"/>, or the path resolved outside the worktrees directory.
    /// </exception>
    Task<WorktreeOutcome> DeleteAsync(
        string startDirectory,
        string name,
        bool force,
        CancellationToken cancellationToken = default);

    /// <summary>Lists existing worktree names.</summary>
    /// <exception cref="HarnessException">The repository is not initialised.</exception>
    Task<IReadOnlyList<string>> ListAsync(
        string startDirectory,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// What a worktree command did. The name is returned as its own value rather than
/// packed into a message, so a caller never has to parse it back out.
/// </summary>
/// <param name="Outcome">Exit code, and the message to report.</param>
/// <param name="Name">The worktree acted on. Empty when the command failed.</param>
/// <param name="Path">Its full path. Empty when the command failed.</param>
public sealed record WorktreeOutcome(CommandOutcome Outcome, string Name, string Path)
{
    /// <summary>Whether the command succeeded.</summary>
    public bool Succeeded => Outcome.Succeeded;

    /// <summary>A failure carrying no worktree.</summary>
    public static WorktreeOutcome Failed(CommandOutcome outcome)
        => new(outcome, string.Empty, string.Empty);
}

/// <inheritdoc cref="IWorktreeService"/>
public sealed class WorktreeService(
    IHarnessContextLoader contextLoader,
    IGitClient gitClient,
    IFileSystem fileSystem,
    IPathBudget pathBudget,
    IHostPlatform platform) : IWorktreeService
{
    private const int GenerateAttempts = 10;

    private const int NamedChangeLimit = 3;

    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly IGitClient _gitClient = gitClient;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IPathBudget _pathBudget = pathBudget;
    private readonly IHostPlatform _platform = platform;

    public async Task<WorktreeOutcome> CreateAsync(
        string startDirectory,
        string? name,
        bool useRandomName,
        CancellationToken cancellationToken = default)
    {
        // Argument shape is checked before the repository is inspected. The reverse
        // order makes these checks unreachable in an uninitialised repository, reporting
        // a usage mistake as a missing prerequisite. Only the length waits for the
        // configuration, because only the length is configured.
        if (useRandomName && !string.IsNullOrWhiteSpace(name))
        {
            return Usage("Give a name or --random, not both.");
        }

        if (!useRandomName)
        {
            if (!WorktreeName.ValidateFormat(name).TryGetName(out _, out var formatError))
            {
                return Usage(formatError);
            }
        }

        var context = await _contextLoader.LoadAsync(startDirectory, cancellationToken).ConfigureAwait(false);
        var layout = context.Layout;
        var settings = context.Config.Worktrees;

        string worktreeName;

        if (useRandomName)
        {
            var generated = GenerateUnusedName(layout, Math.Min(WorktreeName.RandomLength, settings.MaxNameLength));
            if (generated is null)
            {
                // The caller's arguments were fine; the harness could not satisfy them,
                // which is a refusal rather than a usage error.
                return WorktreeOutcome.Failed(CommandOutcome.Refused(
                    $"Could not find an unused random name in {GenerateAttempts} attempts."));
            }

            worktreeName = generated;
        }
        else
        {
            if (!WorktreeName.Validate(name, settings.MaxNameLength).TryGetName(out var accepted, out var lengthError))
            {
                return Usage(lengthError);
            }

            worktreeName = accepted;
        }

        var path = layout.WorktreePath(worktreeName);

        if (_fileSystem.DirectoryExists(path))
        {
            return WorktreeOutcome.Failed(
                CommandOutcome.Refused($"A worktree named '{worktreeName}' already exists."));
        }

        var budget = _pathBudget.Check(
            path,
            settings.PathBudgetReserve,
            settings.PathBudgetMargin,
            settings.PathLimit);

        if (!budget.IsWithinBudget)
        {
            return WorktreeOutcome.Failed(CommandOutcome.Refused(budget.Describe(path)));
        }

        _fileSystem.CreateDirectory(layout.WorktreesDirectory);

        // Worktrees are always created from the main checkout, so running this from
        // inside a worktree adds a sibling rather than nesting one.
        string[] arguments = settings.Detach
            ? ["worktree", "add", "--detach", path]
            : ["worktree", "add", path];

        var result = await _gitClient
            .RunAsync(layout.MainCheckoutRoot, arguments, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return WorktreeOutcome.Failed(CommandOutcome.Failed(
                HarnessExit.CommandFailed,
                $"git refused to create the worktree: {result.FailureMessage}"));
        }

        // git's exit code is not taken as proof the tree exists. A hook, a filter
        // driver or a scanner can remove it between git's success and this line, and
        // reporting a path that is not there sends the user somewhere unrelated.
        if (!_fileSystem.DirectoryExists(path))
        {
            return WorktreeOutcome.Failed(CommandOutcome.Failed(
                HarnessExit.CommandFailed,
                $"git reported success but '{path}' does not exist."));
        }

        return new WorktreeOutcome(
            CommandOutcome.Ok($"created worktree '{worktreeName}'", [path]),
            worktreeName,
            path);
    }

    public async Task<WorktreeOutcome> DeleteAsync(
        string startDirectory,
        string name,
        bool force,
        CancellationToken cancellationToken = default)
    {
        // Only the shape is checked, not the length: a worktree created under a longer
        // limit must stay deletable after the limit is lowered.
        if (!WorktreeName.ValidateFormat(name).TryGetName(out var worktreeName, out var error))
        {
            return Usage(error);
        }

        var context = await _contextLoader.LoadAsync(startDirectory, cancellationToken).ConfigureAwait(false);
        var layout = context.Layout;
        var path = layout.WorktreePath(worktreeName);

        if (!_fileSystem.DirectoryExists(path))
        {
            return WorktreeOutcome.Failed(CommandOutcome.Refused($"No worktree named '{worktreeName}'."));
        }

        if (!force)
        {
            var refusal = await RefuseIfWorkWouldBeLostAsync(worktreeName, path, cancellationToken).ConfigureAwait(false);

            if (refusal is not null)
            {
                return WorktreeOutcome.Failed(refusal);
            }
        }

        // git's own check is always overridden, because git also refuses a clean worktree
        // that holds a submodule. Whether work would be lost is settled above, from the
        // worktree's status, never from the wording of a refusal git printed.
        var removal = await _gitClient
            .RunAsync(
                layout.MainCheckoutRoot,
                ["worktree", "remove", "--force", path],
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // git can report success while leaving the directory behind, so removal is
        // verified rather than assumed, and only then forced. The containment check
        // guards the one recursive delete this command performs.
        if (_fileSystem.DirectoryExists(path))
        {
            if (!PathContainment.IsStrictlyInside(layout.WorktreesDirectory, path, _platform.PathComparison))
            {
                throw new HarnessException(
                    HarnessExit.Refused,
                    $"Refusing to delete '{path}': it is not inside '{layout.WorktreesDirectory}'.");
            }

            _fileSystem.DeleteDirectory(path);
        }

        if (_fileSystem.DirectoryExists(path))
        {
            return WorktreeOutcome.Failed(CommandOutcome.Failed(
                HarnessExit.CommandFailed,
                $"'{path}' still exists after removal: {removal.FailureMessage}"));
        }

        // Pruning only clears administrative entries whose directory is already gone,
        // so it must run after the directory is removed. Run before, it is a no-op, and
        // the entry is then orphaned: git keeps reporting the worktree as registered and
        // refuses to ever create that name again.
        var prune = await _gitClient
            .RunAsync(layout.MainCheckoutRoot, ["worktree", "prune"], cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!prune.Succeeded)
        {
            return WorktreeOutcome.Failed(CommandOutcome.Failed(
                HarnessExit.CommandFailed,
                $"'{worktreeName}' was deleted but git still has it registered: {prune.FailureMessage}"));
        }

        // Ask git, rather than the file system, whether the worktree is really gone. A
        // surviving registration is what makes the name unusable afterwards.
        var remaining = await _gitClient
            .ListWorktreesAsync(layout.MainCheckoutRoot, cancellationToken)
            .ConfigureAwait(false);

        if (remaining.Any(worktree => PathsEqual(worktree.Path, path)))
        {
            return WorktreeOutcome.Failed(CommandOutcome.Failed(
                HarnessExit.CommandFailed,
                $"git still has '{worktreeName}' registered; run 'git worktree prune' to clear it."));
        }

        return new WorktreeOutcome(
            CommandOutcome.Ok($"removed worktree '{worktreeName}'", [path]),
            worktreeName,
            path);
    }

    public async Task<IReadOnlyList<string>> ListAsync(
        string startDirectory,
        CancellationToken cancellationToken = default)
    {
        var context = await _contextLoader.LoadAsync(startDirectory, cancellationToken).ConfigureAwait(false);
        var worktreesDirectory = context.Layout.WorktreesDirectory;

        if (!_fileSystem.DirectoryExists(worktreesDirectory))
        {
            return [];
        }

        return [.. _fileSystem
            .EnumerateDirectories(worktreesDirectory)
            .Select(directory => Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)))
            .Where(directoryName => !string.IsNullOrEmpty(directoryName))
            .OrderBy(directoryName => directoryName, StringComparer.Ordinal)];
    }

    private static WorktreeOutcome Usage(string message)
        => WorktreeOutcome.Failed(CommandOutcome.Failed(HarnessExit.UsageError, message));

    /// <summary>
    /// The refusal to report when deleting the worktree at <paramref name="path"/> would lose
    /// uncommitted changes, or <see langword="null"/> when it would lose none. Ignored files do
    /// not count: they are what a build leaves behind, and a build makes them again.
    /// </summary>
    private async Task<CommandOutcome?> RefuseIfWorkWouldBeLostAsync(
        string name,
        string path,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> changes;

        try
        {
            // In a directory that is not the root of a worktree of its own, such as one whose
            // .git file is gone, git answers for the main checkout around it, which ignores
            // the worktrees directory, so nothing in the directory would ever be reported.
            var root = await _gitClient.GetRepositoryRootAsync(path, cancellationToken).ConfigureAwait(false);

            if (root is null || !PathsEqual(root, path))
            {
                return CannotTellIfWorkWouldBeLost(name, $"git does not see '{path}' as a worktree of its own.");
            }

            changes = await _gitClient.GetStatusAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException ex) when (ex.ExitCode == HarnessExit.CommandFailed)
        {
            // A question git could not answer is not a clean worktree.
            return CannotTellIfWorkWouldBeLost(name, ex.Message);
        }

        if (changes.Count == 0)
        {
            return null;
        }

        // A few paths are named and the rest only counted, so the refusal stays one line.
        var named = string.Join(", ", changes.Take(NamedChangeLimit).Select(entry => entry[3..]));
        var listed = changes.Count > NamedChangeLimit
            ? $"{named} and {changes.Count - NamedChangeLimit} more"
            : named;

        return CommandOutcome.Refused(
            $"Worktree '{name}' has {changes.Count} uncommitted change(s) that would be lost: {listed}. Commit or stash them, or pass --force to delete it anyway.");
    }

    private static CommandOutcome CannotTellIfWorkWouldBeLost(string name, string reason)
        => CommandOutcome.Refused(
            $"Could not tell whether worktree '{name}' has uncommitted changes, so it was not deleted; pass --force to delete it anyway. {reason}");

    /// <summary>
    /// Generates a name no existing worktree already uses, or <see langword="null"/>
    /// when repeated attempts all collided.
    /// </summary>
    private string? GenerateUnusedName(HarnessLayout layout, int length)
    {
        for (var attempt = 0; attempt < GenerateAttempts; attempt++)
        {
            var candidate = WorktreeName.Generate(length);

            if (!_fileSystem.DirectoryExists(layout.WorktreePath(candidate)))
            {
                return candidate;
            }
        }

        return null;
    }

    private bool PathsEqual(string left, string right)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            _platform.PathComparison);
}
