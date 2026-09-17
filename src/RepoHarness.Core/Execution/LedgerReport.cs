using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Execution;

/// <summary>One row of the per-leg table.</summary>
/// <param name="Leg">The leg, as the configuration names it.</param>
/// <param name="Verdict">The one verdict it reached.</param>
/// <param name="Duration">Its wall time, or <see cref="TimeSpan.Zero"/> when it never ran.</param>
/// <param name="Detail">What produced the verdict, and any timing mark, in the words the table shows.</param>
/// <param name="TimingsSuspect">
/// Whether its durations mean anything. Never part of the verdict: a host that slept, or a clock
/// that stepped, makes a duration meaningless, and whether the code passed is a separate fact.
/// </param>
/// <param name="TimingNotes">Why the timings are suspect, one reason per note.</param>
/// <param name="CommandTime">The time the leg's own commands took.</param>
/// <param name="Overhead">What the harness spent on syncing, fingerprinting and sampling.</param>
/// <param name="TestCount">How many tests it reported running, where a count pattern extracted one.</param>
/// <param name="Timings">What its phases reported about their own timing under <c>--time</c>.</param>
public sealed record LedgerLine(
    string Leg,
    LegVerdict Verdict,
    TimeSpan Duration,
    string Detail,
    bool TimingsSuspect,
    IReadOnlyList<string> TimingNotes,
    TimeSpan CommandTime,
    TimeSpan Overhead,
    int? TestCount,
    IReadOnlyList<TimingMark> Timings);

/// <summary>
/// The per-leg ledger a run ends with: the table a reader sees, and the same facts as data.
/// </summary>
/// <remarks>
/// One table, whatever command produced it, because a gate is read by whoever is on call and not
/// by the person who wrote the command. <c>--json</c> emits the same ledger, so a leg that
/// silently skipped its emulated arm is as visible to a script as to a reader.
/// </remarks>
public sealed class LedgerReport
{
    /// <summary>The table's column headings, in order.</summary>
    public static IReadOnlyList<string> Headings { get; } = ["LEG", "VERDICT", "DURATION", "DETAIL"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private LedgerReport(IReadOnlyList<LedgerLine> lines)
    {
        Lines = lines;
        Verdict = Verdicts.Worst(lines.Select(line => line.Verdict));
    }

    /// <summary>One row per leg, in the order the legs were recorded.</summary>
    public IReadOnlyList<LedgerLine> Lines { get; }

    /// <summary>
    /// The verdict that decides the run: the most fundamental one any leg reached. A leg whose
    /// inputs moved is not reported as failed even if its tests failed, because what failed was a
    /// tree that never existed.
    /// </summary>
    public LegVerdict Verdict { get; }

    /// <summary>The exit code the run reports.</summary>
    /// <remarks>
    /// Computed here rather than at each caller so that the table, the JSON and the process agree.
    /// A run where nothing failed but a leg never reported is <see cref="HarnessExit.Incomplete"/>:
    /// the worst verdict such a run reached is a skip, which maps to success on its own, and
    /// reading that alone is how a leg nobody could reach came to print as a pass.
    /// </remarks>
    public int ExitCode => ExitCodeGiven(cancelled: false, unfinished: []);

    /// <summary>The exit code this run reports, given what only its caller knows.</summary>
    /// <remarks>
    /// The rows cannot show a run that stopped early or left a leg running, so the caller that can
    /// see those supplies them here rather than deciding a code of its own. One derivation, read by
    /// the summary and by the JSON alike, so the two cannot drift apart.
    /// </remarks>
    /// <param name="cancelled">Whether the run was interrupted before it finished.</param>
    /// <param name="unfinished">The legs still running when it stopped.</param>
    public int ExitCodeGiven(bool cancelled, IReadOnlyList<string> unfinished)
    {
        ArgumentNullException.ThrowIfNull(unfinished);

        return cancelled ? HarnessExit.Cancelled
            : !Passed ? Verdicts.ExitCodeFor(Verdict)
            : Complete && unfinished.Count == 0 ? HarnessExit.Success
            : HarnessExit.Incomplete;
    }

    /// <summary>Whether every leg reached a verdict that is not a failure.</summary>
    public bool Passed => !Lines.Any(line => Verdicts.IsFailure(line.Verdict));

    /// <summary>The legs that did no work, each with why, in the order they were recorded.</summary>
    /// <remarks>
    /// A skip is not a failure — a switched-off machine is normal — but it is not a pass either,
    /// and the difference is the whole of what a gate reads. Kept apart from
    /// <see cref="Passed"/> rather than folded into it: a run where one leg passed and another was
    /// never reached is neither wholly green nor red, and saying so is the only honest summary.
    /// </remarks>
    public IReadOnlyList<LedgerLine> WithoutVerdict
        => [.. Lines.Where(line => Verdicts.IsSkip(line.Verdict))];

    /// <summary>How many legs actually reached a verdict of their own.</summary>
    public int Reported => Lines.Count - WithoutVerdict.Count;

    /// <summary>Whether every leg with a row reached a verdict of its own.</summary>
    /// <remarks>
    /// Orthogonal to <see cref="Passed"/> on purpose: "did everything report" and "did anything
    /// fail" are separate questions, and folding them into one boolean made a run where all eight
    /// legs ran and two failed report as incomplete — which reads as a run that did not finish,
    /// when it finished and found bugs. Interruption is not visible from the rows at all, so the
    /// caller that can see it supplies it to <see cref="ExitCodeGiven"/> and <see cref="ToJson"/>.
    /// </remarks>
    public bool Complete => WithoutVerdict.Count == 0;

    /// <summary>
    /// Builds the report, marking a phase that took more than
    /// <paramref name="durationWarningFactor"/> times what its siblings took.
    /// </summary>
    /// <param name="entries">Each leg's line.</param>
    /// <param name="durationWarningFactor">The factor; zero or less disables the mark.</param>
    public static LedgerReport From(IReadOnlyList<LegEntry> entries, double durationWarningFactor)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var suspect = Compare(entries, durationWarningFactor);
        var counts = CompareCounts(entries);

        return new LedgerReport(
        [
            .. entries.Select(entry =>
            {
                var notes = entry.TimingNotes
                    .Concat(entry.Phases.Where(phase => phase.ClockStepped).Select(phase => $"the clock stepped during {phase.Phase}"))
                    .Concat(suspect.TryGetValue(entry.Leg, out var slow) ? slow : [])
                    .Concat(counts.TryGetValue(entry.Leg, out var counted) ? [counted] : Array.Empty<string>())
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                return new LedgerLine(
                    entry.Leg,
                    entry.Verdict,
                    entry.Duration,
                    Detail(entry, notes),
                    notes.Count > 0,
                    notes,
                    entry.CommandTime,
                    entry.Overhead,
                    entry.TestCount,
                    entry.Timings);
            }),
        ]);
    }

    /// <summary>
    /// A duration as the table writes it: <c>2m14s</c>, <c>12m40s</c>, <c>1h04m</c>. Whole seconds,
    /// because a column that shows a build's milliseconds invites a reader to compare two numbers
    /// that a clock step can move by 25 seconds.
    /// </summary>
    /// <param name="duration">The duration.</param>
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return string.Empty;
        }

        var rounded = TimeSpan.FromSeconds(Math.Round(duration.TotalSeconds));

        if (rounded.TotalHours >= 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)rounded.TotalHours}h{rounded.Minutes:00}m");
        }

        return rounded.TotalMinutes >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)rounded.TotalMinutes}m{rounded.Seconds:00}s")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)rounded.TotalSeconds}s");
    }

    /// <summary>
    /// The table, heading first: <c>LEG / VERDICT / DURATION / DETAIL</c>.
    /// </summary>
    /// <remarks>
    /// Each column is as wide as what it holds, so a long verdict such as
    /// <c>skipped-unavailable</c> never pushes a detail into the duration column and nothing is
    /// truncated. The duration is right-aligned, so two legs' times can be compared down the column.
    /// </remarks>
    public IReadOnlyList<string> Render()
    {
        var leg = Width(Headings[0], Lines.Select(line => line.Leg));
        var verdict = Width(Headings[1], Lines.Select(line => Verdicts.Display(line.Verdict)));
        var duration = Width(Headings[2], Lines.Select(line => FormatDuration(line.Duration)));

        var rows = new List<string>
        {
            Row(Headings[0], Headings[1], Headings[2], Headings[3], leg, verdict, duration),
        };

        rows.AddRange(Lines.Select(line => Row(
            line.Leg,
            Verdicts.Display(line.Verdict),
            FormatDuration(line.Duration),
            line.Detail,
            leg,
            verdict,
            duration)));

        rows.AddRange(RenderTimings());

        return rows;
    }

    /// <summary>
    /// What each leg's phases reported about their own timing, as a block below the table.
    /// </summary>
    /// <remarks>
    /// Below rather than in the table: a leg can report many marks, and a column wide enough for all
    /// of them would push the verdict a reader came for off the edge. Empty without <c>--time</c>,
    /// and empty with it where no pattern is configured or none matched, so the block appears only
    /// when there is something in it. Each row names the leg and the phase that printed it, because
    /// legs run at the same time and a number with neither is a measurement of nothing.
    /// </remarks>
    private IReadOnlyList<string> RenderTimings()
    {
        var marks = Lines
            .SelectMany(line => line.Timings.Select(timing => (line.Leg, timing.Phase, timing.Text, timing.Value)))
            .ToList();

        if (marks.Count == 0)
        {
            return [];
        }

        var leg = Width("LEG", marks.Select(mark => mark.Leg));
        var phase = Width("PHASE", marks.Select(mark => mark.Phase));
        var text = Width("REPORTED", marks.Select(mark => mark.Text));

        return
        [
            string.Empty,
            "TIMINGS",
            $"  {"LEG".PadRight(leg)}  {"PHASE".PadRight(phase)}  {"REPORTED".PadRight(text)}  VALUE",
            .. marks.Select(mark =>
                $"  {mark.Leg.PadRight(leg)}  {mark.Phase.PadRight(phase)}  {mark.Text.PadRight(text)}  {mark.Value}"),
        ];
    }

    /// <summary>The same ledger as data, for <c>--json</c>.</summary>
    /// <remarks>
    /// An interrupted run is told, not inferred. The report is built from the legs that finished,
    /// so on its own it would describe a run stopped after its first leg as a clean pass of one
    /// leg, with the others simply absent — and the process would meanwhile exit 130. A script
    /// reading this instead of the shell's status would take that for a green run, which is the
    /// failure the whole ledger exists to prevent.
    /// </remarks>
    /// <param name="cancelled">Whether the run was interrupted before it finished.</param>
    /// <param name="unfinished">The legs that were still running when it stopped.</param>
    public string ToJson(bool cancelled, IReadOnlyList<string> unfinished) => JsonSerializer.Serialize(
        new
        {
            Verdict = Verdicts.Display(Verdict),
            ExitCode = ExitCodeGiven(cancelled, unfinished),
            Passed = !cancelled && Passed,
            Cancelled = cancelled,
            Unfinished = unfinished,

            // Beside Passed rather than folded into it: a script comparing two runs has to be able
            // to tell "nothing failed" from "nothing failed and everything reported", and those are
            // the same boolean only when every leg ran. A run that was interrupted, or that left a
            // leg running, reported on fewer legs than it was asked about whatever its rows say.
            Complete = !cancelled && Complete && unfinished.Count == 0,
            Legs = Lines.Select(line => new
            {
                line.Leg,
                Verdict = Verdicts.Display(line.Verdict),
                Failure = Verdicts.IsFailure(line.Verdict),
                DurationSeconds = Seconds(line.Duration),
                CommandSeconds = Seconds(line.CommandTime),
                OverheadSeconds = Seconds(line.Overhead),
                line.Detail,
                line.TimingsSuspect,
                TimingNotes = line.TimingNotes,
                line.TestCount,
                Timings = line.Timings.Select(timing => new
                {
                    timing.Phase,
                    timing.Text,
                    timing.Value,
                }),
            }),
        },
        JsonOptions);

    /// <summary>
    /// Which legs ran a different number of tests from the rest, and what the rest ran.
    /// </summary>
    /// <remarks>
    /// Legs running the same suite are meant to run the same tests. A leg that reports three where
    /// its siblings report four hundred is green on both the exit code and the success pattern, and
    /// neither of those can see the difference — a filter that matched almost nothing, a discovery
    /// step that failed quietly, a test project excluded by a stale glob. The count is the only
    /// thing in the ledger that can, so a leg disagreeing with the others is marked.
    /// Compared only where at least three legs reported a count: with two, "which one is wrong" has
    /// no answer, and marking both says nothing a reader can act on. Emulated legs are compared with
    /// the rest here, because a count does not depend on how fast the machine is.
    /// </remarks>
    private static Dictionary<string, string> CompareCounts(IReadOnlyList<LegEntry> entries)
    {
        var marks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var counted = entries
            .Where(entry => entry.TestCount is not null && !Verdicts.IsFailure(entry.Verdict))
            .ToList();

        if (counted.Count < 3)
        {
            return marks;
        }

        var agreed = counted
            .GroupBy(entry => entry.TestCount!.Value)
            .OrderByDescending(group => group.Count())
            .First();

        if (agreed.Count() == counted.Count)
        {
            return marks;
        }

        foreach (var entry in counted.Where(entry => entry.TestCount != agreed.Key))
        {
            marks[entry.Leg] = $"it ran {entry.TestCount} test(s), where {agreed.Count()} other leg(s) ran {agreed.Key}";
        }

        return marks;
    }

    /// <summary>
    /// Which legs have a phase slower than its siblings, and by how much. Phases are compared only
    /// with the same phase on legs of the same kind: an emulated leg is never compared with a native
    /// one, because the two have no common scale and every emulated leg would be marked forever.
    /// </summary>
    private static Dictionary<string, List<string>> Compare(IReadOnlyList<LegEntry> entries, double factor)
    {
        var marks = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        if (factor <= 0)
        {
            return marks;
        }

        var groups = entries
            .SelectMany(entry => entry.Phases.Select(phase => (entry.Leg, entry.Emulated, Phase: phase)))
            .GroupBy(item => (item.Phase.Phase, item.Emulated), StringTuple);

        foreach (var group in groups)
        {
            var members = group.ToList();

            if (members.Count < 2)
            {
                continue;
            }

            for (var index = 0; index < members.Count; index++)
            {
                var member = members[index];

                // Measured against the middle of its siblings rather than the fastest: with three
                // legs where one is slow, the fastest alone would mark the two honest ones too.
                // Excluded by position, since two legs can legitimately record identical phases.
                var baseline = Median(members.Where((_, other) => other != index).Select(other => other.Phase.Duration));

                if (baseline <= TimeSpan.Zero || member.Phase.Duration <= baseline * factor)
                {
                    continue;
                }

                if (!marks.TryGetValue(member.Leg, out var notes))
                {
                    notes = [];
                    marks[member.Leg] = notes;
                }

                notes.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{member.Phase.Phase} took {FormatDuration(member.Phase.Duration)} against {FormatDuration(baseline)} on sibling legs"));
            }
        }

        return marks;
    }

    private static TimeSpan Median(IEnumerable<TimeSpan> durations)
    {
        var ordered = durations.Order().ToList();

        return ordered.Count switch
        {
            0 => TimeSpan.Zero,
            _ when ordered.Count % 2 == 1 => ordered[ordered.Count / 2],
            _ => (ordered[(ordered.Count / 2) - 1] + ordered[ordered.Count / 2]) / 2,
        };
    }

    private static string Detail(LegEntry entry, IReadOnlyList<string> notes)
    {
        // The count where the leg has one and nothing more urgent to say. A passing leg's detail is
        // the evidence it passed on, and two legs running the same tests that report different
        // counts are only comparable if the table shows both: one of them quietly skipped a group,
        // and a blank column beside a green verdict hides exactly that.
        var detail = entry.Detail.Length == 0 && entry.TestCount is { } count
            ? $"{count} test{(count == 1 ? string.Empty : "s")}"
            : entry.Detail;

        if (notes.Count == 0)
        {
            return detail;
        }

        var mark = "timings suspect: " + string.Join("; ", notes);
        return detail.Length == 0 ? mark : detail + "; " + mark;
    }

    private static int Width(string heading, IEnumerable<string> values)
        => Math.Max(heading.Length, values.Select(value => value.Length).DefaultIfEmpty(0).Max());

    private static string Row(string leg, string verdict, string duration, string detail, int legWidth, int verdictWidth, int durationWidth)
        => (leg.PadRight(legWidth) + "  " + verdict.PadRight(verdictWidth) + "  " + duration.PadLeft(durationWidth) + "  " + detail).TrimEnd();

    private static double Seconds(TimeSpan duration) => Math.Round(duration.TotalSeconds, 3);

    /// <summary>Groups phases by name and kind, comparing the name as configuration keys compare.</summary>
    private static IEqualityComparer<(string Phase, bool Emulated)> StringTuple { get; } = new PhaseKindComparer();

    private sealed class PhaseKindComparer : IEqualityComparer<(string Phase, bool Emulated)>
    {
        public bool Equals((string Phase, bool Emulated) first, (string Phase, bool Emulated) second)
            => first.Emulated == second.Emulated
                && string.Equals(first.Phase, second.Phase, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Phase, bool Emulated) value)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.Phase), value.Emulated);
    }
}
