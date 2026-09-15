using System.Globalization;
using System.Text;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Git;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Output;
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
    /// Removes a worktree, everything under it and git's record of it. Unless
    /// <paramref name="force"/> is set, it is refused and left untouched when deleting it would lose
    /// uncommitted changes, edits status cannot see, commits on no branch, tag, remote-tracking ref,
    /// newest stash or other worktree's HEAD, or a submodule repository's unpushed commits or stash;
    /// when it is locked; and when git does not see it as a worktree of this repository. Ignored
    /// files, and ignored directories with everything in them, are deleted unchecked.
    /// <paramref name="force"/> skips every check and overrides a lock.
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
    IHostPlatform platform,
    IHarnessOutput output) : IWorktreeService
{
    private const int GenerateAttempts = 10;

    private const int NamedChangeLimit = 3;

    private const string DeleteCommand = "delete-worktree";

    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly IGitClient _gitClient = gitClient;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IPathBudget _pathBudget = pathBudget;
    private readonly IHostPlatform _platform = platform;
    private readonly IHarnessOutput _output = output;

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

        // Guards the one recursive delete this command performs, and comes before anything is
        // touched, so this refusal can never follow a deletion.
        if (!PathContainment.IsStrictlyInside(layout.WorktreesDirectory, path, _platform.PathComparison))
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"Refusing to delete '{path}': it is not inside '{layout.WorktreesDirectory}'.");
        }

        var inspector = new WorktreeInspector(_gitClient, _fileSystem, _platform);

        if (!_fileSystem.DirectoryExists(path))
        {
            return await ClearRecordAsync(layout, worktreeName, path, force, inspector, cancellationToken)
                .ConfigureAwait(false);
        }

        WorktreeIdentity identity;

        try
        {
            identity = await inspector.IdentifyAsync(layout.MainCheckoutRoot, path, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException ex) when (ex.ExitCode == HarnessExit.CommandFailed && force)
        {
            // Forced, the deletion does not wait on git's answer; only finding git's record does.
            identity = new WorktreeIdentity(WorktreeMembership.NotAWorktree, null);
        }
        catch (HarnessException ex) when (ex.ExitCode == HarnessExit.CommandFailed)
        {
            return Unchecked(worktreeName, ex.Message);
        }

        var holdsSubmodules = false;

        if (!force)
        {
            switch (identity.Membership)
            {
                case WorktreeMembership.NotAWorktree:
                    return Refused(
                        $"'{Printable(path)}' is not a worktree git can find, so what it holds cannot be checked. "
                        + $"Run 'git worktree repair {Printable(path)}', which helps only while git still has the worktree's record: "
                        + "when it was moved or its .git file was lost; or pass --force to delete it anyway.");

                case WorktreeMembership.OfAnotherRepository:
                    return Refused(
                        $"'{worktreeName}' is not a worktree of this repository: '{Printable(path)}' belongs to another repository, whose history would be deleted with it. "
                        + "Move it elsewhere, or pass --force to delete it anyway.");
            }

            WorktreeFindings findings;

            try
            {
                findings = await inspector
                    .FindAsync(layout.MainCheckoutRoot, path, identity.AdministrativeDirectory!, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HarnessException ex) when (ex.ExitCode == HarnessExit.CommandFailed)
            {
                return Unchecked(worktreeName, ex.Message);
            }

            if (findings.StopsDeletion)
            {
                return Refused(Describe(worktreeName, path, findings));
            }

            holdsSubmodules = findings.HoldsSubmodules;
        }

        // The last moment an interruption can stop this cleanly. From here the deletion runs to
        // the end, or reports how far it got.
        cancellationToken.ThrowIfCancellationRequested();
        using var interruption = WarnIfInterrupted(worktreeName, cancellationToken);

        return force
            ? await RemoveForcedAsync(layout, worktreeName, path, identity.AdministrativeDirectory, inspector).ConfigureAwait(false)
            : await RemoveCheckedAsync(layout, worktreeName, path, identity.AdministrativeDirectory!, holdsSubmodules).ConfigureAwait(false);
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

    private static WorktreeOutcome Refused(string message)
        => WorktreeOutcome.Failed(CommandOutcome.Refused(message));

    private static WorktreeOutcome Failed(string message)
        => WorktreeOutcome.Failed(CommandOutcome.Failed(HarnessExit.CommandFailed, message));

    private static WorktreeOutcome Removed(string name, string path)
        => new(CommandOutcome.Ok($"removed worktree '{name}'", [path]), name, path);

    /// <summary>
    /// The failure for a worktree git could not answer for, without --force. Nothing was deleted,
    /// and --force is not offered as the way forward, because it would destroy exactly what could
    /// not be seen.
    /// </summary>
    private static WorktreeOutcome Unchecked(string name, string reason)
        => Failed(
            $"{reason} Nothing was deleted: without git's answer, what worktree '{name}' holds cannot be checked. "
            + "Fix what git reports and try again; --force would delete it unchecked.");

    /// <summary>
    /// Says, the moment an interruption arrives past the point of no return, that the deletion is
    /// under way and how to finish it. The deletion itself ignores the interruption, but the command
    /// line stops waiting for it after a bounded grace, and on Linux and macOS the interruption
    /// reaches git too, so it can still be left half done.
    /// </summary>
    private CancellationTokenRegistration WarnIfInterrupted(string name, CancellationToken cancellationToken)
        => cancellationToken.Register(() => _output.Warn(
            DeleteCommand,
            $"Deleting worktree '{name}' is under way and may be left half done; "
            + $"if it is, run '{ToolPackage.Command} {DeleteCommand} {name} --force' to finish it."));

    /// <summary>
    /// Removes a worktree whose checks passed. Plain removal runs git's own check as well, which still
    /// catches a file changed or added since ours, or a lock. git refuses every worktree that holds
    /// submodules, so for those alone it is told to go ahead, skipping that check: their work, and the
    /// lock, were checked already.
    /// </summary>
    private async Task<WorktreeOutcome> RemoveCheckedAsync(
        HarnessLayout layout,
        string name,
        string path,
        string administrativeDirectory,
        bool holdsSubmodules)
    {
        string[] arguments = holdsSubmodules
            ? ["worktree", "remove", "--force", path]
            : ["worktree", "remove", path];

        var removal = await _gitClient
            .RunAsync(layout.MainCheckoutRoot, arguments, cancellationToken: CancellationToken.None)
            .ConfigureAwait(false);

        // Nothing more is deleted here when git fails: git's refusal is the second check, and
        // deleting past it would undo the point of running it.
        if (!removal.Succeeded)
        {
            return Failed(
                $"git could not remove worktree '{name}': {removal.FailureMessage.TrimEnd('.')}. "
                + DescribeWhatRemains(name, path, administrativeDirectory));
        }

        if (_fileSystem.DirectoryExists(path))
        {
            return Failed($"git reported worktree '{name}' removed, but '{path}' still exists.");
        }

        return Verified(name, path, administrativeDirectory, gitFailure: null);
    }

    /// <summary>
    /// What a removal git gave up on left behind. git deletes a worktree's .git file and its record
    /// even when it fails to delete some file under it, and after that no check can run again.
    /// Finishing with --force is safe, because every check passed before removal began.
    /// </summary>
    private string DescribeWhatRemains(string name, string path, string administrativeDirectory)
    {
        var gone = new List<string>();

        if (!_fileSystem.DirectoryExists(path))
        {
            gone.Add("its directory");
        }
        else if (!_fileSystem.FileExists(Path.Combine(path, ".git")))
        {
            gone.Add("its .git file");
        }

        if (!_fileSystem.DirectoryExists(administrativeDirectory))
        {
            gone.Add("git's record of it");
        }

        return gone.Count == 0
            ? "Nothing was deleted."
            : $"It stopped part way: {string.Join(" and ", gone)} {(gone.Count == 1 ? "is" : "are")} already gone. "
                + $"Every check passed before removal began, so run '{ToolPackage.Command} {DeleteCommand} {name} --force' to finish deleting it.";
    }

    /// <summary>
    /// Removes a worktree without checking it: its directory, and git's record of it, even when it
    /// is locked, its .git file is gone, or it belongs to another repository.
    /// </summary>
    private async Task<WorktreeOutcome> RemoveForcedAsync(
        HarnessLayout layout,
        string name,
        string path,
        string? administrativeDirectory,
        WorktreeInspector inspector)
    {
        // Forced twice, git removes a locked worktree as well.
        var removal = await _gitClient
            .RunAsync(layout.MainCheckoutRoot, ["worktree", "remove", "--force", "--force", path], cancellationToken: CancellationToken.None)
            .ConfigureAwait(false);

        // git leaves the directory when it cannot treat it as a worktree at all, and can report
        // success while leaving part of it behind, so what remains is deleted here.
        if (_fileSystem.DirectoryExists(path))
        {
            try
            {
                _fileSystem.DeleteDirectory(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Failed(
                    $"Could not finish deleting '{path}'; part of it may already be gone: {ex.Message.TrimEnd('.')}. "
                    + $"Close whatever is using it, then run '{ToolPackage.Command} {DeleteCommand} {name} --force'.");
            }
        }

        if (_fileSystem.DirectoryExists(path))
        {
            return Failed(removal.Succeeded
                ? $"'{path}' still exists after it was deleted."
                : $"'{path}' still exists after removal: {removal.FailureMessage}");
        }

        string? clearingFailure = null;

        if (!removal.Succeeded)
        {
            // With the directory gone, git clears the record it could not remove before, locked
            // or not. Its answer is not taken on trust: the record is looked for below.
            var clearing = await _gitClient
                .RunAsync(layout.MainCheckoutRoot, ["worktree", "remove", "--force", "--force", path], cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);

            clearingFailure = clearing.Succeeded ? null : clearing.FailureMessage;
        }

        return administrativeDirectory is null
            ? await VerifiedByListAsync(layout, name, path, clearingFailure, inspector).ConfigureAwait(false)
            : Verified(name, path, administrativeDirectory, clearingFailure);
    }

    /// <summary>
    /// Clears git's record of a worktree whose directory is already gone, as an interrupted delete
    /// or a directory removed by hand leaves it. The record alone keeps the name from ever being
    /// created again, and its git directory can still hold the only copy of some work.
    /// </summary>
    private async Task<WorktreeOutcome> ClearRecordAsync(
        HarnessLayout layout,
        string name,
        string path,
        bool force,
        WorktreeInspector inspector,
        CancellationToken cancellationToken)
    {
        GitWorktree? record;
        WorktreeFindings? findings = null;

        try
        {
            // With no directory to ask in, git's list is the only place the record can be found. git
            // lists it by the path it resolved, so the path is resolved the same way to match it.
            var resolved = inspector.ResolveLinks(path);
            var worktrees = await _gitClient.ListWorktreesAsync(layout.MainCheckoutRoot, cancellationToken).ConfigureAwait(false);
            record = worktrees.FirstOrDefault(worktree => !worktree.IsMain && PathsEqual(worktree.Path, resolved));

            if (record is not null && !force)
            {
                findings = await inspector.FindForRecordAsync(layout.MainCheckoutRoot, record, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (HarnessException ex) when (ex.ExitCode == HarnessExit.CommandFailed)
        {
            return force
                ? Failed($"{ex.Message} Nothing was deleted: git's record of worktree '{name}' could not be looked up.")
                : Unchecked(name, ex.Message);
        }

        if (record is null)
        {
            return Refused($"No worktree named '{name}'.");
        }

        if (findings is { StopsDeletion: true })
        {
            return Refused(Describe(name, path, findings));
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var interruption = WarnIfInterrupted(name, cancellationToken);

        string[] arguments = force
            ? ["worktree", "remove", "--force", "--force", path]
            : ["worktree", "remove", path];

        var removal = await _gitClient
            .RunAsync(layout.MainCheckoutRoot, arguments, cancellationToken: CancellationToken.None)
            .ConfigureAwait(false);

        if (!removal.Succeeded)
        {
            return Failed($"git could not clear its record of worktree '{name}', whose directory is gone: {removal.FailureMessage}");
        }

        return await VerifiedByListAsync(layout, name, path, gitFailure: null, inspector).ConfigureAwait(false);
    }

    /// <summary>Confirms git's record is gone, by the administrative directory the record lives in.</summary>
    private WorktreeOutcome Verified(string name, string path, string administrativeDirectory, string? gitFailure)
        => _fileSystem.DirectoryExists(administrativeDirectory)
            ? StillRegistered(name, path, gitFailure)
            : Removed(name, path);

    /// <summary>
    /// Confirms git's record is gone by looking for it in git's list. Used only where git could not
    /// name the record's directory beforehand.
    /// </summary>
    private async Task<WorktreeOutcome> VerifiedByListAsync(
        HarnessLayout layout,
        string name,
        string path,
        string? gitFailure,
        WorktreeInspector inspector)
    {
        IReadOnlyList<GitWorktree> remaining;
        string resolved;

        try
        {
            // git lists a worktree by the path it resolved, links followed, so the path is resolved
            // the same way before the two are compared.
            resolved = inspector.ResolveLinks(path);
            remaining = await _gitClient.ListWorktreesAsync(layout.MainCheckoutRoot, CancellationToken.None).ConfigureAwait(false);
        }
        catch (HarnessException ex) when (ex.ExitCode == HarnessExit.CommandFailed)
        {
            return Failed($"Worktree '{name}' is deleted, but whether git still has it registered could not be confirmed: {ex.Message}");
        }

        return remaining.Any(worktree => !worktree.IsMain && PathsEqual(worktree.Path, resolved))
            ? StillRegistered(name, path, gitFailure)
            : Removed(name, path);
    }

    private static WorktreeOutcome StillRegistered(string name, string path, string? gitFailure)
        => Failed(gitFailure is null
            ? $"Worktree '{name}' is deleted, but git still has it registered, which keeps the name from being used again; "
                + $"run 'git worktree remove --force --force {Printable(path)}' to clear it."
            : $"Worktree '{name}' is deleted, but git still has it registered, which keeps the name from being used again, "
                + $"and could not clear it: {gitFailure}");

    /// <summary>The one-line refusal naming everything that stopped the deletion, each with its remedy.</summary>
    private static string Describe(string name, string path, WorktreeFindings findings)
    {
        var reasons = new List<string>();

        if (findings.Changes.Count > 0)
        {
            var named = string.Join(", ", findings.Changes.Take(NamedChangeLimit).Select(Printable));
            var listed = findings.Changes.Count > NamedChangeLimit
                ? $"{named} and {findings.Changes.Count - NamedChangeLimit} more"
                : named;

            reasons.Add(
                $"it has {findings.Changes.Count} uncommitted change(s) that would be lost: {listed} "
                + "(commit them to a branch, or run 'git stash -u')");
        }

        if (findings.Commits > 0 && findings.Head is { } head)
        {
            var shortHead = head[..Math.Min(12, head.Length)];

            reasons.Add(
                $"{findings.Commits} commit(s) up to {shortHead} are on no branch, tag, remote-tracking ref, newest stash or other worktree's HEAD "
                + $"(run 'git branch <name> {shortHead}', or push them)");
        }

        foreach (var submodule in findings.Submodules)
        {
            var held = (submodule.Commits > 0, submodule.HasStash) switch
            {
                (true, true) => $"{submodule.Commits} commit(s) no remote-tracking ref or tag contains, and a stash",
                (true, false) => $"{submodule.Commits} commit(s) no remote-tracking ref or tag contains",
                _ => "a stash",
            };

            reasons.Add(
                $"submodule '{Printable(submodule.Path)}' holds {held}, in a repository deleted with the worktree "
                + "(push them, or keep them elsewhere)");
        }

        if (findings.LockReason is { } reason)
        {
            var because = reason.Length == 0 ? string.Empty : $": {Printable(reason)}";
            reasons.Add($"it is locked{because} (run 'git worktree unlock {Printable(path)}')");
        }

        return $"Worktree '{name}' was not deleted, because {string.Join("; ", reasons)}; fix that, or pass --force to delete it anyway.";
    }

    /// <summary>
    /// Text as it can appear in a one-line message. A control character in a file name, such as
    /// a newline, would split the line or reach the terminal raw, so such text is quoted and escaped.
    /// </summary>
    private static string Printable(string text)
    {
        if (!text.Any(char.IsControl))
        {
            return text;
        }

        var builder = new StringBuilder("\"");

        foreach (var character in text)
        {
            switch (character)
            {
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                default:
                    if (char.IsControl(character))
                    {
                        builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        return builder.Append('"').ToString();
    }

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
