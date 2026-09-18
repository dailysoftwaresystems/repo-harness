using System.Collections.Concurrent;
using System.Diagnostics;
using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Sync;

namespace RepoHarness.Core.Runs;

/// <summary>What a leg-running command was asked to do.</summary>
/// <param name="Directory">The directory the command was invoked in.</param>
/// <param name="LegNames">The legs named with <c>--legs</c>, or null when it was left out.</param>
/// <param name="ForceLock">Whether to take a lock a run on another host holds.</param>
/// <param name="Json">Whether the ledger is wanted as data rather than as a table.</param>
/// <param name="UseStaged">Whether to act on what is already staged on a host, without syncing again.</param>
/// <param name="Time">Whether to report the profile timing.</param>
/// <param name="Here">
/// Whether every selected leg runs on this machine rather than on the host it was placed on. What a
/// host is asked when the machine that reached it dispatches a leg there: without it the host would
/// be free to dispatch the leg onward, putting the verdict one further hop from the reader.
/// </param>
/// <param name="RemoteArguments">
/// The command's own options, passed on to a host running a leg for this run so that it runs the
/// same command. Never <c>--legs</c> or <c>--json</c>, which the dispatch supplies itself.
/// </param>
public sealed record LegRunRequest(
    string Directory,
    IReadOnlyList<string>? LegNames,
    bool ForceLock = false,
    bool Json = false,
    bool UseStaged = false,
    bool Time = false,
    bool Here = false,
    IReadOnlyList<string>? RemoteArguments = null)
{
    /// <summary>
    /// What the command has each leg do, which decides the programs a host must have to be given
    /// one. Said by every command, because what one needs is not what another does.
    /// </summary>
    public required LegWorkload Workload { get; init; }
}

/// <summary>What one leg is asked to do once its tree is ready.</summary>
/// <param name="Leg">The placed leg.</param>
/// <param name="Context">The repository and its configuration.</param>
/// <param name="RunId">This run's id, which every log is scoped to.</param>
/// <param name="RunDirectory">Where this run's logs go.</param>
/// <param name="Time">Whether to report the profile timing.</param>
public sealed record LegWork(
    PlacedLeg Leg,
    HarnessContext Context,
    RunId RunId,
    string RunDirectory,
    bool Time);

/// <summary>
/// What every leg-running command shares: selecting legs, measuring the hosts, refusing what cannot
/// run, taking the locks, syncing each tree once, running the legs together, and reporting a ledger.
/// </summary>
/// <remarks>
/// One implementation for <c>build</c>, <c>test</c> and <c>run</c>, so the isolation rules cannot
/// hold for one command and not another. Each command supplies only what a leg actually does.
/// </remarks>
public sealed class LegRunService(
    IHarnessContextLoader contextLoader,
    LegsService legsService,
    LegExecutor legExecutor,
    RunLock runLock,
    LogOwnership logOwnership,
    ISyncService syncService,
    ISyncTransportFactory transportFactory,
    RemoteLegRunner remoteLegs,
    IHostPlatform platform,
    IHarnessOutput output)
{
    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly LegsService _legsService = legsService;
    private readonly LegExecutor _legExecutor = legExecutor;
    private readonly RunLock _runLock = runLock;
    private readonly LogOwnership _logOwnership = logOwnership;
    private readonly ISyncService _syncService = syncService;
    private readonly ISyncTransportFactory _transportFactory = transportFactory;
    private readonly RemoteLegRunner _remoteLegs = remoteLegs;
    private readonly IHostPlatform _platform = platform;
    private readonly IHarnessOutput _output = output;

    /// <summary>
    /// Runs <paramref name="work"/> on every selected leg and reports the ledger.
    /// </summary>
    /// <param name="commandName">The command reporting, which prefixes every line it writes.</param>
    /// <param name="request">What the command was asked to do.</param>
    /// <param name="work">What one leg does once its tree is ready.</param>
    /// <param name="cancellationToken">Stops the run.</param>
    public async Task<CommandOutcome> RunAsync(
        string commandName,
        LegRunRequest request,
        Func<LegWork, CancellationToken, Task<LegEntry>> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(work);

        var context = await _contextLoader.LoadAsync(request.Directory, cancellationToken).ConfigureAwait(false);

        // Hosts are measured before anything runs, and DssHarness on each is brought to this
        // machine's build there, so a leg never starts on a host that turns out not to answer. A leg
        // goes where a sync puts its tree whatever this command starts, so a run on what is already
        // staged finds it there.
        var report = await _legsService
            .CheckAsync(request.Directory, request.LegNames, request.Workload, request.Here, cancellationToken)
            .ConfigureAwait(false);

        var placed = LegRunPlan.From(context, report, _platform, out var skipped);
        var factor = context.Config.Defaults.DurationWarningFactor;

        if (placed.Count == 0)
        {
            var nothing = LegRunPlan.NothingRuns(skipped);

            return Stopped(request, nothing.ExitCode, nothing.Message, skipped, factor, nothing.Details ?? []);
        }

        var runId = RunId.New();
        var runDirectory = context.Layout.RunDirectory(runId.Value);
        var ledger = new LegLedger(_output, commandName);

        foreach (var entry in skipped)
        {
            ledger.Record(entry);
        }

        var claim = await _logOwnership.ClaimAsync(runDirectory, runId, request.ForceLock, cancellationToken).ConfigureAwait(false);

        if (!claim.Taken)
        {
            // Two runs writing one set of logs would each read the other's output as its own, which
            // is why this is its own verdict and its own exit code rather than a lock refusal - and
            // the verdict of every leg this run would have started.
            var held = $"another run owns '{runDirectory}': {claim.Holder?.Describe()}";

            return Stopped(
                request,
                LegExit.LogHeld,
                held,
                [.. skipped, .. placed.Select(leg => new LegEntry { Leg = leg.Name, Verdict = LegVerdict.LogHeld, Detail = held, Emulated = leg.Emulated })],
                factor,
                []);
        }

        // Trees another run holds, by tree: a verdict for the legs that need one, as a variant
        // another run holds is, and no end to the legs that do not.
        var lockedTrees = new ConcurrentDictionary<string, string>(LegPlan.TreeKeyComparer);

        try
        {
            _output.Info(commandName, $"run {runId.Value}, {placed.Count} leg(s)");

            LegExecution execution;

            try
            {
                execution = await _legExecutor
                    .RunAsync(
                        new LegExecutionRequest
                        {
                            Legs = [.. placed.Select(leg => leg.ToPlan())],
                            MaxParallelLegs = context.Config.Defaults.MaxParallelLegs,
                            MaxParallelLegsTotal = context.Config.Defaults.MaxParallelLegsTotal,

                            // Left out entirely where nothing is remote, rather than supplied and
                            // made to do nothing: the executor reports a sync transition per tree,
                            // and a run that never leaves this machine should not announce a
                            // transfer it did not make.
                            SyncTree = request.UseStaged || !placed.Any(leg => leg.Host.Host.Kind != HostKind.Local)
                                ? null
                                : (treeKey, token) => SyncTreeAsync(context, placed, treeKey, runId, request.ForceLock, lockedTrees, token),
                            RunLeg = (plan, token) => RunLegAsync(
                                context, placed, plan, runId, runDirectory, request, work, commandName, ledger, lockedTrees, token),
                        },
                        ledger,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HarnessException ex)
            {
                // The ledger is reported first, then the refusal. A leg that finished before another
                // leg's configuration turned out to be unsatisfiable still reached a verdict, and
                // throwing it away would make the reader run everything again to learn what they
                // already knew. The exit code is still the refusal's own.
                var reached = ledger.Build(factor);

                return Stopped(request, ex.ExitCode, ex.Message, ledger.Entries, factor, [.. reached.Render(), $"logs: {runDirectory}"]);
            }

            return Report(commandName, context, ledger, execution, runDirectory, request.Json);
        }
        finally
        {
            await _logOwnership.ReleaseAsync(runDirectory, runId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Syncs one host's tree, once, however many legs share it.
    /// </summary>
    /// <remarks>
    /// Legs on one host share one tree. If each synced it they would race over the same files, and
    /// the leg that lost would build sources another leg was halfway through replacing. The tree is
    /// taken exclusively while it is rewritten, and shared afterwards while the variants build.
    /// </remarks>
    private async Task SyncTreeAsync(
        HarnessContext context,
        IReadOnlyList<PlacedLeg> placed,
        string treeKey,
        RunId runId,
        bool force,
        ConcurrentDictionary<string, string> lockedTrees,
        CancellationToken cancellationToken)
    {
        var leg = placed.First(candidate => LegPlan.TreeKeyComparer.Equals(candidate.TreeKey, treeKey));

        if (leg.Host.Host.Kind == HostKind.Local)
        {
            return;
        }

        var attempt = await _runLock
            .TryAcquireAsync(
                context.Layout,
                new LockRequest
                {
                    Host = leg.Host.Host.ToString(),

                    // The tree on the host, which is what the legs sharing this sync also lock.
                    // Locked by the source instead, the exclusive hold taken while the copy is
                    // replaced and the shared holds taken while its variants build would sit in
                    // two different key spaces and never exclude each other.
                    Tree = leg.HostTreeRoot,
                    Scope = LockScope.TreeExclusive,
                    RunId = runId,
                    Command = SyncService.CommandName,
                    Force = force,
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (attempt.Handle is not { } handle)
        {
            // About this tree and this moment, as a variant another run holds is: each leg that needs
            // the tree records it as refused-locked, and the legs on other trees still report. Raised
            // from here it ended the whole run, as though it were a configuration every leg shares.
            // A lock file nobody can use is not this, and is raised as the refusal of the run it is.
            lockedTrees[treeKey] = attempt.HeldBy!;
            return;
        }

        await using (handle)
        {
            // The tree this leg declares, not whatever tree the command was typed in. A leg naming a
            // worktree measures that worktree; sending the main checkout instead would report the
            // worktree's name over the main checkout's sources. A transport that will not start is
            // reported by the runner that starts it, as that host being unavailable.
            await _syncService
                .SyncAsync(leg.TreeRoot, _transportFactory.For(leg.Host), leg.HostTreeRoot, new SyncOptions(), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// What a run ends with when something other than its legs ended it, with the legs that had a
    /// line by then.
    /// </summary>
    /// <param name="request">What the command was asked to do.</param>
    /// <param name="exitCode">What the process exits with.</param>
    /// <param name="message">The line it ends on.</param>
    /// <param name="entries">The legs' lines so far.</param>
    /// <param name="factor">The duration warning factor the ledger is built with.</param>
    /// <param name="details">What the table form says beneath the line.</param>
    /// <remarks>
    /// Asked for data, the ledger is the whole of standard output whatever ended the run: the document,
    /// with the code and the line the process ends on. Written as text instead - a table, a list of
    /// reasons - it reached the machine that dispatched the leg as a host whose answer could not be
    /// read, where the host had said exactly what happened.
    /// </remarks>
    private static CommandOutcome Stopped(
        LegRunRequest request,
        int exitCode,
        string message,
        IReadOnlyList<LegEntry> entries,
        double factor,
        IReadOnlyList<string> details)
        => request.Json
            ? new CommandOutcome(exitCode, message) { Data = [LedgerReport.From(entries, factor).ToJson(exitCode, message)] }
            : CommandOutcome.Failed(exitCode, message, details);

    private async Task<LegEntry?> RunLegAsync(
        HarnessContext context,
        IReadOnlyList<PlacedLeg> placed,
        LegPlan plan,
        RunId runId,
        string runDirectory,
        LegRunRequest request,
        Func<LegWork, CancellationToken, Task<LegEntry>> work,
        string commandName,
        LegLedger ledger,
        ConcurrentDictionary<string, string> lockedTrees,
        CancellationToken cancellationToken)
    {
        var leg = placed.First(candidate => candidate.Name == plan.Name);
        var started = Stopwatch.GetTimestamp();

        if (lockedTrees.TryGetValue(leg.TreeKey, out var treeHeld))
        {
            return new LegEntry
            {
                Leg = leg.Name,
                Verdict = LegVerdict.RefusedLocked,
                Detail = treeHeld,
                Duration = Stopwatch.GetElapsedTime(started),
                Emulated = leg.Emulated,
            };
        }

        // The tree shared and this variant exclusive: variants build side by side, but never while
        // their sources are being replaced.
        var attempt = await _runLock
            .TryAcquireAsync(
                context.Layout,
                new LockRequest
                {
                    Host = leg.Host.Host.ToString(),
                    Tree = leg.HostTreeRoot,
                    Variant = leg.Variant.DirectoryName,
                    Scope = LockScope.TreeShared,
                    RunId = runId,
                    Command = ledger.CommandName,
                    Force = request.ForceLock,
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (attempt.Handle is not { } handle)
        {
            // The one refusal that is a verdict rather than an end to the run: it is about this leg
            // and this moment, so the other legs still report, and one locked leg never hides them.
            // A lock file nobody can use is raised instead, as the refusal of the run it is.
            return new LegEntry
            {
                Leg = leg.Name,
                Verdict = LegVerdict.RefusedLocked,
                Detail = attempt.HeldBy!,
                Duration = Stopwatch.GetElapsedTime(started),
                Emulated = leg.Emulated,
            };
        }

        // Every other refusal is left to propagate. A configuration a leg cannot satisfy — an
        // undeclared program in an action file, a project that names no toolchain — is the same
        // fact for every leg, and turning it into a per-leg verdict would report it as many times
        // as there are legs, under a verdict that named the wrong cause and an exit code that said
        // a lock was held.
        await using (handle)
        {
            // A leg placed on another machine runs on that machine. Doing the work here instead
            // would produce a verdict about the machine that typed the command, under the name of
            // the leg that was supposed to check a different one — which is the whole failure a
            // harness exists to prevent, wearing a green colour.
            if (leg.Host.Host.Kind != HostKind.Local && !request.Here)
            {
                return await _remoteLegs
                    .RunAsync(
                        commandName,
                        leg,
                        leg.HostTreeRoot,
                        request.RemoteArguments ?? [],
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return await work(
                    new LegWork(leg, context, runId, runDirectory, request.Time),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private CommandOutcome Report(
        string commandName,
        HarnessContext context,
        LegLedger ledger,
        LegExecution execution,
        string runDirectory,
        bool json)
    {
        var report = ledger.Build(context.Config.Defaults.DurationWarningFactor);

        // One code and one line, whichever form the ledger is shown in. Deciding them per branch
        // is how the JSON branch came to report a failing run with an empty line: a host is always
        // asked for JSON, so every failure on another machine read as `FAIL - ` and nothing else.
        var exitCode = report.ExitCodeGiven(execution.Cancelled, execution.Unfinished);
        var message = report.Summarize(execution.Cancelled, execution.Unfinished);

        if (json)
        {
            return new CommandOutcome(exitCode, message)
            {
                Data = [report.ToJson(execution.Cancelled, execution.Unfinished)],
                Quiet = true,
            };
        }

        var details = new List<string>(report.Render()) { $"logs: {runDirectory}" };

        if (execution.Unfinished.Count > 0)
        {
            details.Add($"left unfinished: {string.Join(", ", execution.Unfinished)}");
        }

        return new CommandOutcome(exitCode, message, details);
    }
}
