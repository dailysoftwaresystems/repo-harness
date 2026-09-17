using RepoHarness.Core.Results;

namespace RepoHarness.Core.Execution;

/// <summary>
/// What a span of work wants watched while it runs.
/// </summary>
/// <remarks>
/// Each guard is off unless something asks for it, and each says so by supplying what it needs
/// rather than by a flag beside it: a contention request names the directory to watch, an input set
/// names the files to fingerprint. A flag and a value can disagree; a value on its own cannot.
/// </remarks>
public sealed record LegGuardRequest
{
    /// <summary>The leg, so a report says whose work this was.</summary>
    public required string Leg { get; init; }

    /// <summary>The tree the inputs are resolved against.</summary>
    public required string TreeRoot { get; init; }

    /// <summary>
    /// The files to fingerprint before, during and after, or <see langword="null"/> to fingerprint
    /// nothing.
    /// </summary>
    public IReadOnlyList<string>? Inputs { get; init; }

    /// <summary>
    /// Why the input set could not be established, when it could not. Carried rather than left as an
    /// empty list: an empty list fingerprints cleanly, and "nothing moved" is exactly the answer
    /// work nobody watched must not give.
    /// </summary>
    public string? UnmeasurableInputs { get; init; }

    /// <summary>What to watch the process table for, or <see langword="null"/> to watch nothing.</summary>
    public ContentionRequest? Contention { get; init; }
}

/// <summary>What the guards saw over a span of work.</summary>
/// <param name="Inputs">
/// Whether the inputs held still, or <see langword="null"/> when none were watched.
/// </param>
/// <param name="Contention">
/// What else used the build directory, or <see langword="null"/> when nothing sampled.
/// </param>
public sealed record LegGuardReport(InputComparison? Inputs, ContentionReport? Contention)
{
    /// <summary>
    /// The verdict this work reached, taking the worst of what the guards saw and what the phases
    /// did.
    /// </summary>
    /// <param name="leg">The leg, which a poisoned verdict names.</param>
    /// <param name="reached">
    /// What the work itself reached, one verdict per thing that decided: each phase, and whatever
    /// else a verb holds its work to. A build's witness is such a thing — exiting zero having
    /// produced nothing is not a phase failing.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="reached"/> is empty.</exception>
    /// <remarks>
    /// One decision for every verb, because the alternative is three: a phase that failed, a tree
    /// that moved and a directory somebody else was building in are three different facts about one
    /// span of work, and which of them a reader is told about must not depend on which verb they
    /// happened to run.
    /// <para>
    /// A guard that was not asked for contributes nothing, which is not the same as contributing a
    /// pass. Nothing here can turn "nobody looked" into "nothing found"; that distinction is made
    /// where the guards are chosen, by the caller that knows what it asked for.
    /// </para>
    /// </remarks>
    public ReachedVerdict Decide(string leg, IReadOnlyList<ReachedVerdict> reached)
    {
        ArgumentNullException.ThrowIfNull(reached);

        if (reached.Count == 0)
        {
            throw new ArgumentException("A span of work that decided nothing has no verdict to reach.", nameof(reached));
        }

        var candidates = new List<ReachedVerdict>(reached);

        if (Inputs?.Verdict() is { } moved)
        {
            candidates.Add(moved);
        }

        if (Contention?.Verdict() is { } contended)
        {
            candidates.Add(contended);
        }

        var worst = Verdicts.Worst(candidates.Select(candidate => candidate.Verdict));

        return ReachedVerdict.OrPoisoned(
            worst,
            leg,
            candidates.First(candidate => candidate.Verdict == worst).Detail);
    }
}

/// <summary>
/// Watches a span of work: whether the tree held still while it ran, and what else was using its
/// build directory.
/// </summary>
/// <remarks>
/// Opened around a SPAN rather than around each phase, which is the whole reason this is a scope
/// and not two fields on a phase request. A build is two phases, configure and then build; guards
/// attached to each separately would fingerprint around one, fingerprint around the other, and miss
/// a file changed in the gap between them — the same moving tree, reported as a pass. An action of
/// five steps has four such gaps, and a sampler restarted per step loses a contender that spanned
/// the join.
/// <para>
/// The shape is the one <see cref="ProcessSamplingSession"/> already established: open, do the
/// work, close once and read what it saw. This widens that to carry the fingerprint too, so one
/// scope answers both questions and every verb asks them the same way.
/// </para>
/// </remarks>
public sealed class LegGuards : IAsyncDisposable
{
    private readonly InputFingerprint _fingerprints;
    private readonly LegGuardRequest _request;
    private readonly InputSnapshot? _before;
    private readonly InputWatch? _watch;
    private readonly ProcessSamplingSession? _sampling;
    private LegGuardReport? _report;

    private LegGuards(
        InputFingerprint fingerprints,
        LegGuardRequest request,
        InputSnapshot? before,
        InputWatch? watch,
        ProcessSamplingSession? sampling)
    {
        _fingerprints = fingerprints;
        _request = request;
        _before = before;
        _watch = watch;
        _sampling = sampling;
    }

    /// <summary>Opens the guards this work asked for, and none it did not.</summary>
    /// <param name="fingerprints">Takes and compares the fingerprints.</param>
    /// <param name="sampler">Samples the process table.</param>
    /// <param name="request">What to watch.</param>
    /// <param name="cancellationToken">Stops the opening reading.</param>
    public static async Task<LegGuards> OpenAsync(
        InputFingerprint fingerprints,
        ProcessSampler sampler,
        LegGuardRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fingerprints);
        ArgumentNullException.ThrowIfNull(sampler);
        ArgumentNullException.ThrowIfNull(request);

        // Watched only where the set is known. Where it is not, the reason is carried through to
        // the report, which is what keeps an unmeasured span from reading as a clean one.
        var watching = request.Inputs is { Count: > 0 } && request.UnmeasurableInputs is null;

        var before = watching
            ? await fingerprints.TakeAsync(request.TreeRoot, request.Inputs!, cancellationToken).ConfigureAwait(false)
            : null;

        // Before, during and after. Two snapshots alone cannot see an edit that was undone before
        // the work ended, which is the shape the measured failure took: a configuration file
        // rewritten while a suite ran and restored before it finished.
        var watch = watching ? fingerprints.Watch(request.TreeRoot, request.Inputs!) : null;

        var sampling = request.Contention is null
            ? null
            : await sampler.StartAsync(request.Contention, cancellationToken).ConfigureAwait(false);

        return new LegGuards(fingerprints, request, before, watch, sampling);
    }

    /// <summary>
    /// Closes the guards and says what they saw. Asked again, it answers the same rather than
    /// reading a tree the work has already left.
    /// </summary>
    /// <param name="cancellationToken">Stops the closing reading.</param>
    public async Task<LegGuardReport> CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_report is { } existing)
        {
            return existing;
        }

        var contention = _sampling is null
            ? null
            : await _sampling.StopAsync(cancellationToken).ConfigureAwait(false);

        InputComparison? inputs = null;

        if (_request.UnmeasurableInputs is { } why)
        {
            // Asked for and not answerable. Unmeasured rather than clean, because a set that could
            // not be established says nothing about whether the tree held still.
            inputs = new InputComparison(InputChange.Unmeasured, [], why);
        }
        else if (_before is not null)
        {
            var after = await _fingerprints
                .TakeAsync(_request.TreeRoot, _request.Inputs!, cancellationToken)
                .ConfigureAwait(false);

            inputs = InputFingerprint.Compare(_before, after, _watch);
        }

        // An empty set with no reason is neither: nothing was asked to be watched, so this guard
        // has nothing to say and contributes no verdict. A project whose kinds match none of its
        // tracked files is exactly that, and reading it as unmeasured would fail every build in a
        // repository that happens not to track the language it builds.

        _report = new LegGuardReport(inputs, contention);
        return _report;
    }

    /// <summary>Stops watching. A report that was never asked for is simply not produced.</summary>
    public async ValueTask DisposeAsync()
    {
        _watch?.Dispose();

        if (_sampling is not null)
        {
            await _sampling.DisposeAsync().ConfigureAwait(false);
        }
    }
}
