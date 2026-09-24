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

    /// <summary>
    /// The project whose tests <see cref="TestCount"/> counts, by name: recorded with the count by a
    /// test that reached its runner, and <see langword="null"/> on every other leg - a build, a run, a
    /// test stopped before its runner - and where the leg resolves no project. A count is compared only
    /// with the others of the same project and <see cref="TestSet"/>, and one with no project with nothing.
    /// </summary>
    /// <remarks>
    /// Recorded where the count is made, on the host that ran the leg, rather than looked up again
    /// wherever a report is built, as <see cref="Emulated"/> is for the duration comparison: a report
    /// composed from another host's <c>--json</c> has only the entries that arrived, and a host running
    /// what it has staged may have run another test set than this machine's configuration now names.
    /// </remarks>
    public string? Project { get; init; }

    /// <summary>
    /// Which of <see cref="Project"/>'s test sets <see cref="TestCount"/> belongs to, as the test
    /// invocation that produced it names it with <c>testSet</c>; <see langword="null"/> for the
    /// project's own shared set.
    /// </summary>
    public string? TestSet { get; init; }

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

    /// <summary>
    /// The last lines the leg's last phase that did not pass printed, on either stream in the order they
    /// came, secrets masked: what a reader who cannot open its log - another host's, which stays on that
    /// host - needs first. Empty for a leg that passed, and for one whose every phase passed, whatever else
    /// decided its verdict.
    /// </summary>
    public IReadOnlyList<string> LogTail { get; init; } = [];

    /// <summary>
    /// The steps of the runner's action this leg's operating system does not run, by name, in the order
    /// they are declared: left out on purpose, and said so, rather than simply absent.
    /// </summary>
    public IReadOnlyList<string> SkippedSteps { get; init; } = [];

    /// <summary>
    /// The steps this leg ran - its runner's action's, or the runner's own phases - by name, in the order
    /// they ran: each one whose work began, in this attempt or an earlier one of the same run, and none
    /// after the one that stopped it.
    /// </summary>
    public IReadOnlyList<string> RanSteps { get; init; } = [];

    /// <summary>
    /// The manual steps this leg ran, by name, in the order they are declared: work that runs only where a
    /// run names it, marked so, so a line that measured something says which of its steps did.
    /// </summary>
    public IReadOnlyList<string> ManualSteps { get; init; } = [];

    /// <summary>
    /// The steps of the runner's action this run did not select, by name, in the order they are declared:
    /// a manual step a plain run leaves out, or a step a run naming others does not reach. Listed, so a
    /// run that left a step out is never read as having run it.
    /// </summary>
    public IReadOnlyList<string> UnselectedSteps { get; init; } = [];

    /// <summary>
    /// The compilers CMake configured the leg's build with, as it reported them: named on its line
    /// with whatever verdict it reached, so every verdict says which compiler produced what it judged.
    /// </summary>
    public IReadOnlyList<Build.CompilerFact> Compilers { get; init; } = [];

    /// <summary>
    /// The developer environment the leg's processes started in, where its toolchain names one and it
    /// was set up: which Visual Studio instance and tools, for which processor. Named on its line with
    /// whatever verdict it reached, as the compilers are.
    /// </summary>
    public Hosts.DeveloperEnvironmentFact? DeveloperEnvironment { get; init; }

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

        var said = LedgerReport.Marked(entry.Detail, [], entry.Compilers, entry.DeveloperEnvironment, testCountNote: null);

        Transition(entry.Leg, Verdicts.Display(entry.Verdict) + (said.Length > 0 ? $" ({said})" : string.Empty));
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
