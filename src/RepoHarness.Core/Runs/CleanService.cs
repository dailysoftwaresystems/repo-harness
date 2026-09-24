using System.Diagnostics;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Sync;

namespace RepoHarness.Core.Runs;

/// <summary>What <c>clean</c> was asked to do.</summary>
/// <param name="Directory">The directory the command was typed in.</param>
/// <param name="LegNames">The legs named with <c>--legs</c>, or null when it was left out.</param>
/// <param name="DryRun">Whether to say what each build directory holds, and remove nothing.</param>
/// <param name="Json">Whether the ledger is wanted as data rather than as a table.</param>
/// <param name="Here">
/// The host this machine is to the machine that sent the legs here, as <see cref="LegRunRequest.Here"/>
/// says; <see langword="null"/> where this machine places them itself.
/// </param>
public sealed record CleanRequest(
    string Directory,
    IReadOnlyList<string>? LegNames,
    bool DryRun = false,
    bool Json = false,
    HostId? Here = null);

/// <summary>
/// Removes each selected leg's build directory wherever the leg runs - this machine, a WSL distribution's
/// copy, an ssh host's copy - or, asked for a dry run, says what each holds and the room left beside it.
/// </summary>
/// <remarks>
/// For a machine whose disk a build filled. Nothing else removes a build directory from a host's copy: a
/// build that starts from clean fills the disk again as it goes, and deleting the worktree takes its local
/// tree too. So it writes nothing on the machine it removes from before it has removed: no sync, no lock
/// entry, no run records - a leg another run holds is kept from it by the lock file being read, under the
/// mutex a run needs to write one, and never by an entry of its own. The directory is renamed aside while
/// no run can take the lock, then removed; one an earlier removal left aside is removed by the next. A host
/// whose DssHarness is behind this machine's is still brought to this build first, as it is by every
/// command that asks it anything, and that write needs room.
/// </remarks>
public sealed class CleanService(
    IHarnessContextLoader contextLoader,
    LegsService legsService,
    RunLock runLock,
    ISyncTransportFactory transports,
    RemoteLegRunner remoteLegs,
    IFileSystem fileSystem,
    IHostPlatform platform,
    IHarnessOutput output)
{
    /// <summary>The command, as it is typed and as it reports.</summary>
    public const string CommandName = "clean";

    /// <summary>The option that measures and removes nothing, passed on to a host as it was given here.</summary>
    public const string DryRunOption = "--dry-run";

    /// <summary>
    /// What a build directory is renamed to while it is removed, beside it and after a dot: hidden, and never
    /// the name of another variant's, whose names start with their processor.
    /// </summary>
    private const string AsideSuffix = ".removing";

    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly LegsService _legsService = legsService;
    private readonly RunLock _runLock = runLock;
    private readonly ISyncTransportFactory _transports = transports;
    private readonly RemoteLegRunner _remoteLegs = remoteLegs;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHostPlatform _platform = platform;
    private readonly IHarnessOutput _output = output;

    /// <summary>Removes, or measures, every selected leg's build directory, and reports the ledger.</summary>
    /// <param name="request">What the command was asked to do.</param>
    /// <param name="cancellationToken">Stops the command, here and on every host.</param>
    public async Task<CommandOutcome> RunAsync(CleanRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var context = await _contextLoader.LoadAsync(request.Directory, cancellationToken).ConfigureAwait(false);

        // What a copy of the tree needs of a host: no program starts there, so a host without the build's tools
        // still has its build directories removed.
        var report = await _legsService
            .CheckAsync(request.Directory, request.LegNames, LegWorkload.Copy, request.Here, cancellationToken)
            .ConfigureAwait(false);

        var placed = LegRunPlan.From(context, report, _platform, out var skipped);
        var factor = context.Config.Defaults.DurationWarningFactor;

        if (placed.Count == 0)
        {
            var nothing = LegRunPlan.NothingRuns(skipped);
            return Stopped(request, nothing.ExitCode, nothing.Message, skipped, factor, nothing.Details ?? []);
        }

        var ledger = new LegLedger(_output, CommandName);

        foreach (var entry in skipped)
        {
            ledger.Record(entry);
        }

        try
        {
            await Task
                .WhenAll(placed.Select(async leg => ledger.Record(await CleanAsync(context, leg, request, cancellationToken).ConfigureAwait(false))))
                .ConfigureAwait(false);
        }
        catch (HarnessException ex)
        {
            // The legs already dealt with are said first, then the refusal, as a run that builds says them: a
            // directory already removed is gone whatever stopped the rest.
            return Stopped(request, ex.ExitCode, ex.Message, ledger.Entries, factor, ledger.Build(factor).Render());
        }

        var built = ledger.Build(factor);
        var exitCode = built.ExitCodeGiven(cancelled: false, unfinished: []);
        var message = built.Summarize(cancelled: false, unfinished: []);

        return request.Json
            ? new CommandOutcome(exitCode, message) { Data = [built.ToJson(cancelled: false, unfinished: [])], Quiet = true }
            : new CommandOutcome(exitCode, message, built.Render());
    }

    /// <summary>Removes, or measures, one leg's build directory where the leg runs.</summary>
    private async Task<LegEntry> CleanAsync(HarnessContext context, PlacedLeg leg, CleanRequest request, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();

        // The lock a build of this leg takes, keyed as the build keys it - by the host, the tree there and the
        // variant - so a build holding it is seen, and a build about to take it is kept out.
        var building = new LockRequest
        {
            Host = leg.Host.Host.ToString(),
            Tree = leg.HostTreeRoot,
            Variant = leg.Variant.DirectoryName,
            Scope = LockScope.TreeShared,
            RunId = RunId.New(),
            Command = CommandName,
        };

        if (leg.Host.Host.Kind == HostKind.Local)
        {
            return CleanHere(context.Layout, leg, building, request.DryRun) with { Duration = Stopwatch.GetElapsedTime(started) };
        }

        // A run started on this machine that is building the leg there holds this machine's lock too, for as
        // long as it runs: asked here first, the host is never sent a command a run of this machine's holds off.
        if (!request.DryRun && _runLock.HeldBy(context.Layout, building) is { } held)
        {
            return Entry(leg, LegVerdict.RefusedLocked, held) with { Duration = Stopwatch.GetElapsedTime(started) };
        }

        // Asked before the host is sent the command: DssHarness there runs a command in the tree's copy, and
        // refuses one for a copy that is not there as a host it could not run it on.
        if (!await _transports.For(leg.Host).RootExistsAsync(leg.HostTreeRoot, cancellationToken).ConfigureAwait(false))
        {
            return Entry(leg, LegVerdict.Passed, $"nothing to remove: {leg.Named} holds no copy of this tree at '{leg.HostTreeRoot}'")
                with { Duration = Stopwatch.GetElapsedTime(started) };
        }

        return await _remoteLegs
            .RunAsync(CommandName, leg, request.DryRun ? [DryRunOption] : [], cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Removes, or measures, the build directory of a leg that runs on this machine.</summary>
    private LegEntry CleanHere(HarnessLayout layout, PlacedLeg leg, LockRequest building, bool dryRun)
    {
        var directory = leg.BuildDirectory;
        var aside = Path.Combine(Path.GetDirectoryName(directory)!, "." + Path.GetFileName(directory) + AsideSuffix);

        try
        {
            // Where it points is somebody's decision - a variant built on another disk - and removing the link
            // alone would free nothing and have the next build fill this disk instead.
            if (_fileSystem.IsLink(directory))
            {
                return Entry(leg, LegVerdict.Failed, $"'{directory}' is a link, so nothing was removed: what it holds is wherever it points, and yours to remove");
            }

            if (dryRun)
            {
                var holds = _fileSystem.DirectorySize(directory);
                var left = _fileSystem.DirectorySize(aside);
                var room = Room(directory, out var unmeasured);

                var said = _fileSystem.DirectoryExists(directory) ? $"{DiskSpace.Size(holds)} in '{directory}'" : $"nothing at '{directory}'";

                if (left > 0)
                {
                    said += $", and {DiskSpace.Size(left)} a removal that did not finish left at '{aside}'";
                }

                return Entry(leg, LegVerdict.Passed, $"{said}; {room?.Describe() ?? unmeasured}") with
                {
                    Space = new BuildSpace(directory, holds + left, Removed: false, room),
                };
            }

            // Removed first, and outside the lock: nothing builds in a directory an earlier removal moved aside.
            var removed = _fileSystem.DirectorySize(aside);
            _fileSystem.DeleteDirectory(aside);

            var moved = false;
            var holder = _runLock.HeldBy(layout, building, () =>
            {
                if (_fileSystem.DirectoryExists(directory))
                {
                    _fileSystem.MoveDirectory(directory, aside);
                    moved = true;
                }
            });

            if (holder is not null)
            {
                return Entry(
                    leg,
                    LegVerdict.RefusedLocked,
                    removed > 0 ? $"{holder} What an earlier removal had left at '{aside}' was removed: {DiskSpace.Size(removed)}." : holder);
            }

            if (moved)
            {
                removed += _fileSystem.DirectorySize(aside);
                _fileSystem.DeleteDirectory(aside);
            }

            var after = Room(directory, out var why);
            var done = moved || removed > 0 ? $"removed {DiskSpace.Size(removed)} from '{directory}'" : $"nothing to remove at '{directory}'";

            return Entry(leg, LegVerdict.Passed, $"{done}; {after?.Describe() ?? why}") with
            {
                Space = new BuildSpace(directory, removed, Removed: moved || removed > 0, after),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var remains = _fileSystem.DirectoryExists(aside)
                ? $"; what is left of it is at '{aside}', and the next clean of this leg removes it"
                : string.Empty;

            return Entry(leg, LegVerdict.Failed, $"'{directory}' could not be removed: {ex.Message.TrimEnd('.')}{remains}");
        }
    }

    /// <summary>The room on the filesystem <paramref name="directory"/> is on, or why it could not be measured.</summary>
    private DiskSpace? Room(string directory, out string unmeasured)
    {
        unmeasured = string.Empty;

        try
        {
            return _fileSystem.SpaceAt(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            unmeasured = $"the room on its filesystem could not be measured: {ex.Message.TrimEnd('.')}";
            return null;
        }
    }

    private static LegEntry Entry(PlacedLeg leg, LegVerdict verdict, string detail)
        => new() { Leg = leg.Name, Verdict = verdict, Detail = detail, Emulated = leg.Emulated };

    /// <summary>
    /// What the command ends with when something other than its legs ended it, with the legs that had a line
    /// by then; asked for data, the ledger is still the whole of standard output.
    /// </summary>
    private static CommandOutcome Stopped(
        CleanRequest request,
        int exitCode,
        string message,
        IReadOnlyList<LegEntry> entries,
        double factor,
        IReadOnlyList<string> details)
        => request.Json
            ? new CommandOutcome(exitCode, message) { Data = [LedgerReport.From(entries, factor).ToJson(exitCode, message)] }
            : CommandOutcome.Failed(exitCode, message, details);
}
