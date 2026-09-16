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
    IReadOnlyList<string>? RemoteArguments = null);

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

        // Asked for the ledger as data, the ledger is the whole of standard output. Progress still
        // appears, on standard error, where a reader parsing the document never sees it — and the
        // reader here is often this tool on another machine, collecting a dispatched leg's verdict.
        using var document = request.Json ? _output.DataOnly() : null;

        var context = await _contextLoader.LoadAsync(request.Directory, cancellationToken).ConfigureAwait(false);

        // Hosts are measured before anything runs, and DssHarness on each is brought to this
        // machine's build there, so a leg never starts on a host that turns out not to answer.
        var report = await _legsService
            .CheckAsync(request.Directory, request.LegNames, request.Here, cancellationToken)
            .ConfigureAwait(false);

        var placed = LegRunPlan.From(context, report, _platform, out var skipped);

        if (placed.Count == 0)
        {
            return CommandOutcome.Failed(
                LegsExit.Unavailable,
                "no selected leg can run",
                [.. skipped.Select(entry => $"{entry.Leg}: {entry.Detail}")]);
        }

        var runId = RunId.New();
        var runDirectory = context.Layout.RunDirectory(runId.Value);
        var ledger = new LegLedger(_output, commandName);

        foreach (var entry in skipped)
        {
            ledger.Record(entry);
        }

        var claim = await _logOwnership.ClaimAsync(runDirectory, runId, cancellationToken).ConfigureAwait(false);

        if (!claim.Taken)
        {
            // Two runs writing one set of logs would each read the other's output as its own, which
            // is why this is its own verdict and its own exit code rather than a lock refusal.
            return CommandOutcome.Failed(
                LegExit.LogHeld,
                $"another run owns '{runDirectory}': {claim.Holder?.Describe()}");
        }

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
                                : (treeKey, token) => SyncTreeAsync(context, placed, treeKey, runId, request.ForceLock, token),
                            RunLeg = (plan, token) => RunLegAsync(
                                context, placed, plan, runId, runDirectory, request, work, commandName, ledger, token),
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
                var reached = ledger.Build(context.Config.Defaults.DurationWarningFactor);

                return CommandOutcome.Failed(
                    ex.ExitCode,
                    ex.Message,
                    [.. reached.Render(), $"logs: {runDirectory}"]);
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
        CancellationToken cancellationToken)
    {
        var leg = placed.First(candidate => candidate.TreeKey == treeKey);

        if (leg.Host.Host.Kind == HostKind.Local)
        {
            return;
        }

        await using var handle = await _runLock
            .AcquireAsync(
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

        var transport = _transportFactory.For(leg.Host);

        // The tree this leg declares, not whatever tree the command was typed in. A leg naming a
        // worktree measures that worktree; sending the main checkout instead would report the
        // worktree's name over the main checkout's sources.
        await _syncService
            .SyncAsync(leg.TreeRoot, transport, leg.HostTreeRoot, new SyncOptions(), cancellationToken)
            .ConfigureAwait(false);
    }

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
        CancellationToken cancellationToken)
    {
        var leg = placed.First(candidate => candidate.Name == plan.Name);
        var started = Stopwatch.GetTimestamp();

        RunLockHandle handle;

        try
        {
            // The tree shared and this variant exclusive: variants build side by side, but never
            // while their sources are being replaced.
            handle = await _runLock
                .AcquireAsync(
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
        }
        catch (HarnessException ex) when (ex.ExitCode == HarnessExit.Refused)
        {
            // The one refusal that is a verdict rather than an end to the run: it is about this leg
            // and this moment, so the other legs still report, and one locked leg never hides them.
            return new LegEntry
            {
                Leg = leg.Name,
                Verdict = LegVerdict.RefusedLocked,
                Detail = ex.Message,
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

        if (json)
        {
            return CommandOutcome.Ok(string.Empty, null) with
            {
                Data = [report.ToJson(execution.Cancelled, execution.Unfinished)],
                Quiet = true,
                ExitCode = report.ExitCodeGiven(execution.Cancelled, execution.Unfinished),
            };
        }

        var details = new List<string>(report.Render()) { $"logs: {runDirectory}" };

        if (execution.Unfinished.Count > 0)
        {
            details.Add($"left unfinished: {string.Join(", ", execution.Unfinished)}");
        }

        if (execution.Cancelled)
        {
            // Not a red verdict. A caller reading a failure code for an interrupted run would
            // report the code as broken when nothing reached a verdict at all.
            return CommandOutcome.Failed(
                HarnessExit.Cancelled,
                $"interrupted after {report.Lines.Count} leg(s)",
                details);
        }

        if (!report.Passed)
        {
            return CommandOutcome.Failed(
                report.ExitCode,
                $"{Verdicts.Display(report.Verdict)}: {report.Lines.Count} leg(s) reported",
                details);
        }

        // A leg that did no work is not a leg that passed. Nothing failed here, so this is not a
        // red run; but reporting it as an unqualified success would put "OK - 8 leg(s) passed" in
        // front of a reader when none of those eight ran, which is the one thing a gate reads. The
        // legs are named, because which of them went unreported is the first thing to ask.
        // Asked about legs that did no work, or left running when the run stopped: neither is a
        // failure, and neither is a pass. Decided by the same derivation the JSON uses, so a reader
        // and a script are never told different things about one run.
        var code = report.ExitCodeGiven(execution.Cancelled, execution.Unfinished);

        if (code != HarnessExit.Success)
        {
            var withoutWork = report.WithoutVerdict.Select(line => line.Leg).Concat(execution.Unfinished).ToList();

            return CommandOutcome.Failed(
                code,
                $"{report.Reported} of {report.Lines.Count} leg(s) passed; "
                + $"{withoutWork.Count} did no work: {string.Join(", ", withoutWork)}",
                details);
        }

        return CommandOutcome.Ok($"{report.Lines.Count} leg(s) passed", details);
    }
}
