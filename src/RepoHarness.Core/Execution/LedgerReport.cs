using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

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
public sealed record LedgerLine(
    string Leg,
    LegVerdict Verdict,
    TimeSpan Duration,
    string Detail,
    bool TimingsSuspect,
    IReadOnlyList<string> TimingNotes,
    TimeSpan CommandTime,
    TimeSpan Overhead,
    int? TestCount);

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
    public int ExitCode => Verdicts.ExitCodeFor(Verdict);

    /// <summary>Whether every leg reached a verdict that is not a failure.</summary>
    public bool Passed => !Lines.Any(line => Verdicts.IsFailure(line.Verdict));

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
                    entry.TestCount);
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

        return rows;
    }

    /// <summary>The same ledger as data, for <c>--json</c>.</summary>
    public string ToJson() => JsonSerializer.Serialize(
        new
        {
            Verdict = Verdicts.Display(Verdict),
            ExitCode,
            Passed,
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
