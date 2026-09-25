using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Execution;

/// <summary>One leg as the executor needs to see it, whatever command is running it.</summary>
public sealed record LegPlan
{
    /// <summary>The leg, as the configuration names it.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// The variant-keyed build directory this leg uses. Two selected legs resolving to one
    /// directory is refused before either starts: CMake refuses a compiler change on an existing
    /// cache, and a native build and a cross-build in one directory corrupt each other silently.
    /// </summary>
    public required string BuildDirectory { get; init; }

    /// <summary>
    /// What identifies the host's tree this leg works in, such as the host and the tree root.
    /// Legs sharing it share one sync. Empty where the leg needs no sync. A key, never said: its parts
    /// may be joined by a character no reader should see.
    /// </summary>
    public string TreeKey { get; init; } = string.Empty;

    /// <summary>The host's tree this leg works in, as a message names it, such as <c>'~/repo' on wsl Ubuntu</c>.</summary>
    public string Tree { get; init; } = string.Empty;

    /// <summary>
    /// How two tree keys compare, wherever legs are grouped by one: ignoring case, as the run lock
    /// compares the host and the tree it names.
    /// </summary>
    public static StringComparer TreeKeyComparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>Whether the leg runs under emulation, which decides what its timings are compared with.</summary>
    public bool Emulated { get; init; }

    /// <summary>
    /// The physical machine this leg runs on. Legs sharing it are capped against each other; legs on
    /// different machines proceed independently.
    /// </summary>
    public string MachineKey { get; init; } = string.Empty;

    /// <summary>The host as a message names it, such as <c>local</c> or <c>ssh vps</c>.</summary>
    public string Host { get; init; } = string.Empty;
}

/// <summary>What running the selected legs produced.</summary>
/// <param name="Entries">
/// One entry per leg that reached a verdict, in the order the legs were selected rather than the
/// order they finished, so two runs of the same gate print the same table.
/// </param>
/// <param name="Unfinished">Legs that were still running when the run was interrupted.</param>
/// <param name="Cancelled">
/// Whether the run was interrupted. A cancelled run reached no verdict and must not be read as a
/// red one, so the caller reports it as interrupted rather than as a failure.
/// </param>
public sealed record LegExecution(IReadOnlyList<LegEntry> Entries, IReadOnlyList<string> Unfinished, bool Cancelled);

/// <summary>What to run, and what a leg actually does.</summary>
public sealed record LegExecutionRequest
{
    /// <summary>The selected legs.</summary>
    public required IReadOnlyList<LegPlan> Legs { get; init; }

    /// <summary>
    /// What a leg does once its tree is synced: build, then test, or whatever the command in
    /// question means by the leg. Returning <see langword="null"/> means the leg reached no
    /// verdict, which is recorded as <c>poisoned</c> rather than dropped.
    /// </summary>
    public required Func<LegPlan, CancellationToken, Task<LegEntry?>> RunLeg { get; init; }

    /// <summary>
    /// Syncs one host tree, called once per <see cref="LegPlan.TreeKey"/> however many legs share
    /// it. Left <see langword="null"/> where nothing needs syncing.
    /// </summary>
    public Func<string, CancellationToken, Task>? SyncTree { get; init; }

    /// <summary>
    /// Most legs to run at once <em>on any one physical machine</em>, or <see langword="null"/> for
    /// no per-machine cap.
    /// </summary>
    /// <remarks>
    /// Per machine rather than across the run, because that is where the contention is: a Windows
    /// leg and a WSL leg are one machine's processors however differently they are named, while an
    /// ssh host on the other side of the room shares nothing with either. A single cap across every
    /// leg had to be set low enough for the busiest machine, which left every other host idle.
    /// Legs are isolated from one another, so a cap is never needed for correctness; it keeps a
    /// machine from being asked for more than it has, and nothing else.
    /// </remarks>
    public int? MaxParallelLegs { get; init; }

    /// <summary>
    /// Most legs to run at once across every machine together, or <see langword="null"/> for no
    /// overall ceiling.
    /// </summary>
    /// <remarks>
    /// Applied on top of <see cref="MaxParallelLegs"/>, for what a fleet shares even when its
    /// machines do not: a license server, a network share, the bandwidth a sync needs.
    /// </remarks>
    public int? MaxParallelLegsTotal { get; init; }
}

/// <summary>
/// Runs the selected legs: all of them at once, in a fixed order within each, sharing one sync per
/// host tree, and reporting what was left when a run is interrupted.
/// </summary>
/// <remarks>
/// Legs are isolated from one another, so running them one at a time would only make a gate slower.
/// The work a leg does is supplied by the caller, which is what lets <c>build</c>, <c>test</c> and
/// <c>run</c> share this trunk instead of each growing its own parallelism, its own sync sharing
/// and its own idea of what an interrupted run reports.
/// </remarks>
public sealed class LegExecutor(IHostPlatform platform, IHarnessOutput output)
{
    private readonly IHostPlatform _platform = platform;
    private readonly IHarnessOutput _output = output;

    /// <summary>Runs every selected leg and waits for all of them.</summary>
    /// <param name="request">The legs, and what a leg does.</param>
    /// <param name="ledger">Collects each leg's line as it finishes, and its progress while it runs.</param>
    /// <param name="cancellationToken">
    /// Stops the legs. The children go with them, and the report says what was left rather than
    /// raising: a run interrupted halfway still has to say which legs got a verdict.
    /// </param>
    /// <exception cref="HarnessException">
    /// Nothing was selected, a leg was selected twice, or two legs resolve to the same build
    /// directory. Raised before any leg starts, because the point of the check is that neither ran.
    /// </exception>
    public async Task<LegExecution> RunAsync(
        LegExecutionRequest request,
        LegLedger ledger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(ledger);

        Refuse(request.Legs);

        // One semaphore per machine, and one across all of them. A leg takes the overall slot first
        // and its machine's second, in that order everywhere: two legs taking them in opposite
        // orders would each hold what the other waits for, and the run would stop having reported
        // nothing. Released in the reverse order by the same rule.
        using var overall = Slots(request.MaxParallelLegsTotal);
        var machines = request.Legs
            .Select(leg => MachineOf(leg))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(machine => machine, _ => Slots(request.MaxParallelLegs), StringComparer.OrdinalIgnoreCase);

        Announce(request, ledger, machines.Count);

        var syncs = new Dictionary<string, Task>(LegPlan.TreeKeyComparer);
        var syncGate = new Lock();

        // Started together and awaited together. A leg that finishes early reports at once through
        // the ledger; nothing waits for the slowest leg before saying anything.
        var running = request.Legs
            .Select(leg => RunOneAsync(
                leg,
                request,
                ledger,
                overall,
                machines[MachineOf(leg)],
                syncs,
                syncGate,
                cancellationToken))
            .ToList();

        var entries = await Task.WhenAll(running).ConfigureAwait(false);

        var finished = entries.Where(entry => entry is not null).Select(entry => entry!).ToList();
        var unfinished = request.Legs
            .Where((_, index) => entries[index] is null)
            .Select(leg => leg.Name)
            .ToList();

        if (unfinished.Count > 0)
        {
            _output.Warn(
                ledger.CommandName,
                $"The run was interrupted; {unfinished.Count} leg(s) reached no verdict: {string.Join(", ", unfinished)}");
        }

        foreach (var machine in machines.Values)
        {
            machine?.Dispose();
        }

        return new LegExecution(finished, unfinished, cancellationToken.IsCancellationRequested);
    }

    /// <summary>A semaphore for <paramref name="limit"/>, or none when nothing is being limited.</summary>
    private static SemaphoreSlim? Slots(int? limit)
        => limit is > 0 ? new SemaphoreSlim(limit.Value, limit.Value) : null;

    /// <summary>The machine a leg runs on, or one shared machine where nothing said.</summary>
    /// <remarks>
    /// Legs that do not say where they run are counted as sharing one machine, not as owning one
    /// each. Both answers are guesses, and they fail in opposite directions: treating each as its
    /// own machine removes the cap entirely, so an unset field would quietly start every leg at
    /// once on a machine sized for two. Sharing one key only ever runs fewer at a time than the
    /// truth would allow, which costs time and nothing else.
    /// </remarks>
    private static string MachineOf(LegPlan leg)
        => leg.MachineKey.Length > 0 ? leg.MachineKey : "machine:unspecified";

    /// <summary>
    /// Says what is about to run, and where, before any of it starts.
    /// </summary>
    /// <remarks>
    /// A parallel run's output is several legs' lines woven together, so the one thing a reader
    /// cannot reconstruct afterwards is what was started and what it was waiting for. Said once, up
    /// front, naming every leg and the machines they are spread across.
    /// </remarks>
    private void Announce(LegExecutionRequest request, LegLedger ledger, int machineCount)
    {
        var legs = string.Join(", ", request.Legs.Select(leg => leg.Name));
        var caps = (request.MaxParallelLegs, request.MaxParallelLegsTotal) switch
        {
            (null, null) => "all at once",
            ({ } perMachine, null) => $"up to {perMachine} at once per machine",
            (null, { } total) => $"up to {total} at once",
            var (perMachine, total) => $"up to {perMachine.Value} at once per machine, {total.Value} in all",
        };

        _output.Info(
            ledger.CommandName,
            $"starting {request.Legs.Count} leg(s) across {machineCount} machine(s), {caps}: {legs}");
    }

    /// <summary>
    /// Refuses a selection that cannot be run as a set, before anything starts.
    /// </summary>
    private void Refuse(IReadOnlyList<LegPlan> legs)
    {
        if (legs.Count == 0)
        {
            throw new HarnessException(
                HarnessExit.UsageError,
                "No leg was selected, so there is nothing to run and nothing to report a verdict on.");
        }

        var repeated = legs
            .GroupBy(leg => leg.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        if (repeated.Count > 0)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"Leg(s) {string.Join(", ", repeated)} were selected more than once; each leg reaches exactly one verdict.");
        }

        var comparer = _platform.PathComparison == StringComparison.OrdinalIgnoreCase
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        // Keyed by the tree as well as the directory. Two hosts commonly keep their copy at the same
        // path — `~/work/repo` is an ordinary convention — and two legs of the same variant on two
        // different machines are exactly the "run this suite on both" case the tool exists for.
        // Comparing paths alone would refuse it as a collision that does not exist.
        var shared = legs
            .GroupBy(
                leg => CompositeKey.Of(leg.TreeKey, Path.TrimEndingDirectorySeparator(Path.GetFullPath(leg.BuildDirectory))),
                comparer)
            .Where(group => group.Count() > 1)
            .Select(group => $"{string.Join(", ", group.Select(leg => leg.Name))} all build in "
                + $"'{Path.TrimEndingDirectorySeparator(Path.GetFullPath(group.First().BuildDirectory))}'")
            .ToList();

        if (shared.Count > 0)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"Selected legs share a build directory, so neither result would describe its own leg: {string.Join("; ", shared)}");
        }
    }

    private async Task<LegEntry?> RunOneAsync(
        LegPlan leg,
        LegExecutionRequest request,
        LegLedger ledger,
        SemaphoreSlim? overall,
        SemaphoreSlim? machine,
        Dictionary<string, Task> syncs,
        Lock syncGate,
        CancellationToken cancellationToken)
    {
        LegEntry entry;

        var held = 0;

        try
        {
            if (overall is not null)
            {
                await overall.WaitAsync(cancellationToken).ConfigureAwait(false);
                held = 1;
            }

            if (machine is not null)
            {
                await machine.WaitAsync(cancellationToken).ConfigureAwait(false);
                held = 2;
            }
        }
        catch (OperationCanceledException)
        {
            // Whatever was taken before the wait was cancelled is given back, or a run interrupted
            // while legs were queued would leave the slots they hold taken for the rest of it.
            if (held >= 1)
            {
                overall?.Release();
            }

            return null;
        }

        ledger.Transition(leg.Name, $"starting on {(leg.Host.Length > 0 ? leg.Host : "this machine")}");

        try
        {
            // The order within a leg is fixed: sync, then whatever the command does with the tree,
            // which is build on buildCores and then test on testCores. A leg that tested before its
            // tree was in place would be testing the previous run's sources.
            if (request.SyncTree is { } sync && leg.TreeKey.Length > 0)
            {
                // The tree as a reader names it, never its key: a key joins its parts with a NUL, which a
                // consumer found in the progress line of every leg on a host, where it made grep call the log binary.
                ledger.Transition(leg.Name, leg.Tree.Length > 0 ? $"sync of {leg.Tree}" : "sync of its host's copy");
                await Shared(syncs, syncGate, leg.TreeKey, sync, cancellationToken).ConfigureAwait(false);
            }

            entry = await request.RunLeg(leg, cancellationToken).ConfigureAwait(false)
                ?? Entry(leg, ReachedVerdict.OrPoisoned(null, leg.Name));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Left without a verdict on purpose: an interrupted run reached none, and a caller must
            // not read one. What was left is named in the report.
            return null;
        }
        catch (HarnessException ex) when (HarnessExit.RefusesTheRun(ex.ExitCode))
        {
            // Left to propagate, and it ends the run. A configuration or a policy a leg cannot
            // satisfy — an action file naming an undeclared program, a leg whose project declares no
            // toolchain — is the same fact for every leg. Turned into a verdict it would be reported
            // once per leg, under a name that said the wrong cause, when what the reader has to do
            // is edit one file.
            // The one refusal that IS about this leg alone, a lock another run holds - on its variant,
            // or on its host's tree while that is synced - never reaches here: the caller turns it
            // into a verdict itself, precisely so that the other legs still report. The slot is
            // released by the finally below, as on every other path out.
            throw;
        }
        catch (HarnessException ex)
        {
            // A refusal that is genuinely about this leg keeps its own verdict: a host that is
            // switched off, or a tree git cannot answer in, is skipped-unavailable, neither of them
            // a defect in the tool. The other legs still report.
            entry = Entry(leg, ReachedVerdict.Of(Verdicts.ForRefusal(ex.ExitCode), ex.Message));
        }
        catch (Exception ex) when (KnownCauses.Names(ex))
        {
            // A cause this build can name, read from the table the command runner reads too, is not
            // a defect in this tool: recorded as poisoned, a program that would not start read as
            // exit 70 and sent the reader looking for a bug that was not there. Nor is it a skip. The
            // survey turned away, before anything started, every leg whose host lacks a program it
            // requires there; a program that still will not start once the leg is running is one it
            // could not require - a file the build was to make, a script in the tree, one a phase's
            // own PATH finds, a binary for another processor - and a leg that cannot start its own
            // program has failed. Read
            // as a skip, a build that never produced what its next step runs would pass a gate
            // that accepts an incomplete run.
            entry = Entry(leg, ReachedVerdict.Of(LegVerdict.Failed, ex.Message));
        }
        catch (Exception ex)
        {
            // The harness could not produce a verdict, which is what poisoned means. Recorded
            // rather than rethrown, so one broken leg does not take the other legs' results with it.
            entry = Entry(leg, ReachedVerdict.Of(LegVerdict.Poisoned, $"{ex.GetType().Name}: {ex.Message}"));
        }
        finally
        {
            // Released in the reverse of the order they were taken, so a leg never holds the overall
            // slot while waiting on anything else.
            machine?.Release();
            overall?.Release();
        }

        ledger.Record(entry);
        return entry;
    }

    /// <summary>
    /// The one sync of <paramref name="treeKey"/>, started by whichever leg needs it first and
    /// awaited by every other leg on that tree. A tree is synced once, not once per leg: if each
    /// leg synced, their copies would race over the same files.
    /// </summary>
    private static Task Shared(
        Dictionary<string, Task> syncs,
        Lock gate,
        string treeKey,
        Func<string, CancellationToken, Task> sync,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!syncs.TryGetValue(treeKey, out var running))
            {
                running = sync(treeKey, cancellationToken);
                syncs[treeKey] = running;
            }

            return running;
        }
    }

    /// <summary>The line a leg gets when the executor, rather than the leg's own work, decided its verdict.</summary>
    private static LegEntry Entry(LegPlan leg, ReachedVerdict reached)
        => new()
        {
            Leg = leg.Name,
            Verdict = reached.Verdict,
            Detail = reached.Detail,
            Emulated = leg.Emulated,
        };
}
