using RepoHarness.Core.Output;

namespace RepoHarness.Core.Execution;

/// <summary>One phase of a leg, as the ledger records it.</summary>
/// <param name="Phase">The phase's name, which is what sibling legs are compared by.</param>
/// <param name="Duration">The command's own time, from the monotonic clock.</param>
/// <param name="ClockStepped">
/// Whether wall-clock time and monotonic time disagreed across the phase by more than the
/// tolerance, meaning it spanned a clock step or a host sleep. A host that slept once reported a
/// 4 millisecond test at 729 seconds.
/// </param>
public sealed record PhaseRecord(string Phase, TimeSpan Duration, bool ClockStepped);

/// <summary>
/// One timing a phase reported about itself, pulled from its output under <c>--time</c>.
/// </summary>
/// <remarks>
/// Carries the phase as well as the value because a leg runs several, and a list of bare numbers
/// with nothing saying which phase produced them is not a measurement anyone can act on. The
/// matched text is kept beside the captured value so a reader can see what the pattern actually
/// found, rather than trusting a number whose units the pattern alone decided.
/// </remarks>
/// <param name="Phase">The phase whose output carried the mark.</param>
/// <param name="Text">The whole match, as the phase printed it.</param>
/// <param name="Value">The first captured group, or the whole match when the pattern captures none.</param>
public sealed record TimingMark(string Phase, string Text, string Value);

/// <summary>One leg's line in the ledger.</summary>
public sealed record LegEntry
{
    /// <summary>The leg, as the configuration names it.</summary>
    public required string Leg { get; init; }

    /// <summary>The one verdict it reached.</summary>
    public required LegVerdict Verdict { get; init; }

    /// <summary>What produced the verdict, in the words the DETAIL column shows.</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>
    /// The whole leg's time, from the monotonic clock. Zero for a leg that never ran, which the
    /// table then leaves blank rather than reporting as an instant run.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// The time the leg's own commands took. Reported apart from the whole, because the difference
    /// is what the harness itself spent on syncing, fingerprinting and sampling, and a leg that
    /// looks slow for that reason is a different problem from one whose build is slow.
    /// </summary>
    public TimeSpan CommandTime { get; init; }

    /// <summary>
    /// Whether the leg ran under emulation. An emulated leg is never compared with a native one:
    /// the two have no common scale, and comparing them marks every emulated leg suspect forever.
    /// </summary>
    public bool Emulated { get; init; }

    /// <summary>How many tests the leg reported running, where a count pattern extracted one.</summary>
    public int? TestCount { get; init; }

    /// <summary>Each phase, for the comparison that marks a slow one suspect.</summary>
    public IReadOnlyList<PhaseRecord> Phases { get; init; } = [];

    /// <summary>
    /// What the leg's phases reported about their own timing under <c>--time</c>. Empty without it,
    /// and empty with it where no timing pattern is configured or none matched.
    /// </summary>
    public IReadOnlyList<TimingMark> Timings { get; init; } = [];

    /// <summary>
    /// Anything already known to make this leg's timings meaningless, such as a phase that spanned a
    /// clock step or a host sleep.
    /// </summary>
    public IReadOnlyList<string> TimingNotes { get; init; } = [];

    /// <summary>
    /// Where this leg's records are when another host ran it: that host's own run directory, since a
    /// host runs a leg under a run of its own. Null for a leg this machine ran, whose records are in
    /// this run's directory.
    /// </summary>
    public string? RunDirectory { get; init; }

    /// <summary>What the harness spent outside the leg's own commands.</summary>
    public TimeSpan Overhead => Duration > CommandTime ? Duration - CommandTime : TimeSpan.Zero;
}

/// <summary>
/// Collects what each leg did while the run is going on, and hands the whole set to the report at
/// the end.
/// </summary>
/// <remarks>
/// Progress is one line per leg transition, not a stream of child process output: with several legs
/// running at once, interleaved build output says nothing about which leg is where. Legs run in
/// parallel, so every method here is safe to call from any of them.
/// </remarks>
public sealed class LegLedger(IHarnessOutput output, string commandName)
{
    private readonly Lock _gate = new();
    private readonly List<LegEntry> _entries = [];
    private readonly IHarnessOutput _output = output;
    private readonly string _commandName = commandName;

    /// <summary>The command every line is reported under, so interleaved output stays attributable.</summary>
    public string CommandName => _commandName;

    /// <summary>
    /// Every leg recorded so far, in the order they finished. The table is rendered from the
    /// selection's order instead, so two runs of one gate print their rows the same way round.
    /// </summary>
    public IReadOnlyList<LegEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>
    /// Reports that a leg moved on: started, synced, built, testing, finished. One line, so the
    /// progress of a run with eight legs still fits on a screen.
    /// </summary>
    /// <param name="leg">The leg.</param>
    /// <param name="message">What it is now doing.</param>
    public void Transition(string leg, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leg);

        _output.Info(_commandName, $"{leg}: {message}");
    }

    /// <summary>
    /// Records a leg's verdict. A leg is recorded exactly once; recording it twice is a defect,
    /// because the second entry would give one leg two verdicts in a table that promises one.
    /// </summary>
    /// <param name="entry">The leg's line.</param>
    /// <exception cref="InvalidOperationException">The leg was already recorded.</exception>
    public void Record(LegEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_gate)
        {
            if (_entries.Any(existing => string.Equals(existing.Leg, entry.Leg, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Leg '{entry.Leg}' already has a verdict in this run's ledger.");
            }

            _entries.Add(entry);
        }

        Transition(entry.Leg, Verdicts.Display(entry.Verdict) + (entry.Detail.Length > 0 ? $" ({entry.Detail})" : string.Empty));
    }

    /// <summary>
    /// The report for everything recorded, with a phase slower than its siblings marked suspect.
    /// </summary>
    /// <param name="durationWarningFactor">
    /// How many times longer than the same phase on a sibling leg a phase may take before its
    /// timings are marked suspect; zero or less disables the mark.
    /// </param>
    public LedgerReport Build(double durationWarningFactor) => LedgerReport.From(Entries, durationWarningFactor);
}
