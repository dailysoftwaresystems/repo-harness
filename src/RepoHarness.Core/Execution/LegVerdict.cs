using RepoHarness.Core.Results;

namespace RepoHarness.Core.Execution;

/// <summary>
/// The closed vocabulary a leg's result is reported in. Every declared leg reaches exactly one of
/// these, and nothing else: a report whose words are invented per command cannot be compared across
/// commands, and a leg with no word for what happened to it silently vanishes from the table.
/// </summary>
public enum LegVerdict
{
    /// <summary>Ran to completion, succeeded, and its success pattern matched.</summary>
    Passed,

    /// <summary>Ran to completion and reported failure. The code is broken.</summary>
    Failed,

    /// <summary>
    /// Exited zero, but its success pattern never matched its own output. Measured three ways: a
    /// suite that printed <c>failed=0</c> while exiting 2, an exit code read after a pipe, and a
    /// test command that exited zero having run no tests at all.
    /// </summary>
    Unwitnessed,

    /// <summary>
    /// Files the tests read changed while they ran, so some tests saw the old files and some the
    /// new. Measured: eight failures, all passing seconds later on the unchanged tree.
    /// </summary>
    InputsMoved,

    /// <summary>
    /// Whether those files held still could not be established. Never reported as passed: an
    /// unreadable snapshot is not evidence that nothing moved.
    /// </summary>
    Unmeasured,

    /// <summary>Another process used the leg's build directory while it ran.</summary>
    Contended,

    /// <summary>Filtered out by <c>--legs</c>. Not a failure: nobody asked for it.</summary>
    SkippedNotSelected,

    /// <summary>No host can run the leg. A warning: a switched-off machine is normal.</summary>
    SkippedUnavailable,

    /// <summary>
    /// A required tool is not installed. A warning, and named: executables are resolved before a
    /// leg starts so this is never a failure halfway through a build.
    /// </summary>
    SkippedToolMissing,

    /// <summary>Another run holds the lock for this leg, so nothing ran.</summary>
    RefusedLocked,

    /// <summary>
    /// Another live run owns this run's log path. Distinct from <see cref="RefusedLocked"/> because
    /// the remedies differ: one waits for the other run, the other points this run elsewhere.
    /// </summary>
    LogHeld,

    /// <summary>
    /// The harness could not produce a verdict. Deliberately distinct from <see cref="Failed"/>:
    /// "your code is broken" and "the harness broke" call for different responses.
    /// </summary>
    Poisoned,
}

/// <summary>Everything the report and the exit code need to know about one verdict.</summary>
/// <param name="Verdict">The verdict.</param>
/// <param name="Display">Its name as the ledger and docs/architecture.md spell it.</param>
/// <param name="IsFailure">Whether it makes the run red.</param>
/// <param name="Rank">
/// How fundamental it is; the smaller number wins when legs disagree. A leg whose inputs moved is
/// not reported as failed even if its tests failed, because what failed was a tree that never existed.
/// </param>
/// <param name="ExitCode">The process exit code a run reports when this verdict decides it.</param>
public sealed record VerdictInfo(LegVerdict Verdict, string Display, bool IsFailure, int Rank, int ExitCode);

/// <summary>
/// A verdict together with the sentence that explains it, produced so that a leg which reached no
/// verdict cannot be dropped: there is no way to build one of these without saying what happened.
/// </summary>
/// <param name="Verdict">The verdict the leg reached.</param>
/// <param name="Detail">What produced it, as the ledger's DETAIL column shows it.</param>
public sealed record ReachedVerdict(LegVerdict Verdict, string Detail)
{
    /// <summary>A leg that reached <paramref name="verdict"/>.</summary>
    /// <param name="verdict">The verdict.</param>
    /// <param name="detail">What produced it.</param>
    public static ReachedVerdict Of(LegVerdict verdict, string detail = "") => new(verdict, detail);

    /// <summary>
    /// <paramref name="verdict"/> when the leg reached one, and <see cref="LegVerdict.Poisoned"/>
    /// naming the leg when it did not. A leg that finishes with no verdict is a defect in the
    /// harness itself, and it fails the run rather than silently vanishing from the report.
    /// </summary>
    /// <param name="verdict">The verdict the leg reached, if it reached one.</param>
    /// <param name="leg">The leg's name, so the poisoned entry says which one it was.</param>
    /// <param name="detail">What produced the verdict, when there is one.</param>
    public static ReachedVerdict OrPoisoned(LegVerdict? verdict, string leg, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leg);

        return verdict is { } reached
            ? new ReachedVerdict(reached, detail ?? string.Empty)
            : new ReachedVerdict(
                LegVerdict.Poisoned,
                $"leg '{leg}' finished without reaching a verdict; this is a defect in the harness");
    }
}

/// <summary>
/// The facts about each verdict, in one place: what it is called, whether it is a failure, how it
/// ranks against the others, and what a run exits with when it decides the run.
/// </summary>
/// <remarks>
/// Kept here rather than spread over the commands that report verdicts, because three commands
/// reporting the same verdict under three spellings, or with three exit codes, is exactly what a
/// closed vocabulary exists to prevent.
/// </remarks>
public static class Verdicts
{
    private static readonly IReadOnlyDictionary<LegVerdict, VerdictInfo> Table = new Dictionary<LegVerdict, VerdictInfo>
    {
        // Ranks 0-7 are the order docs/architecture.md gives for disagreeing legs. The rest exist
        // only so that Worst is total, and none of them decides a non-zero exit code: a warning
        // outranks a pass so that a run with an unavailable leg summarises as that warning rather
        // than as an unqualified success, and a leg nobody asked for outranks nothing at all.
        [LegVerdict.Poisoned] = new(LegVerdict.Poisoned, "poisoned", true, 0, HarnessExit.InternalError),
        [LegVerdict.Unmeasured] = new(LegVerdict.Unmeasured, "unmeasured", true, 1, LegExit.InputsMoved),
        [LegVerdict.InputsMoved] = new(LegVerdict.InputsMoved, "inputs-moved", true, 2, LegExit.InputsMoved),
        [LegVerdict.Contended] = new(LegVerdict.Contended, "contended", true, 3, LegExit.Contended),
        [LegVerdict.LogHeld] = new(LegVerdict.LogHeld, "log-held", true, 4, LegExit.LogHeld),
        [LegVerdict.RefusedLocked] = new(LegVerdict.RefusedLocked, "refused-locked", true, 5, HarnessExit.Refused),
        [LegVerdict.Failed] = new(LegVerdict.Failed, "failed", true, 6, HarnessExit.CommandFailed),
        [LegVerdict.Unwitnessed] = new(LegVerdict.Unwitnessed, "unwitnessed", true, 7, LegExit.Unwitnessed),
        [LegVerdict.SkippedUnavailable] = new(LegVerdict.SkippedUnavailable, "skipped-unavailable", false, 8, HarnessExit.Success),
        [LegVerdict.SkippedToolMissing] = new(LegVerdict.SkippedToolMissing, "skipped-tool-missing", false, 9, HarnessExit.Success),
        [LegVerdict.Passed] = new(LegVerdict.Passed, "passed", false, 10, HarnessExit.Success),
        [LegVerdict.SkippedNotSelected] = new(LegVerdict.SkippedNotSelected, "skipped-not-selected", false, 11, HarnessExit.Success),
    };

    /// <summary>Every verdict, most fundamental first.</summary>
    public static IReadOnlyList<VerdictInfo> All { get; } = [.. Table.Values.OrderBy(info => info.Rank)];

    /// <summary>Everything known about <paramref name="verdict"/>.</summary>
    /// <param name="verdict">The verdict.</param>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a declared verdict.</exception>
    public static VerdictInfo Describe(LegVerdict verdict)
        => Table.TryGetValue(verdict, out var info)
            ? info
            : throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Not a declared verdict.");

    /// <summary>The verdict's name as the ledger prints it and docs/architecture.md spells it.</summary>
    /// <param name="verdict">The verdict.</param>
    public static string Display(LegVerdict verdict) => Describe(verdict).Display;

    /// <summary>
    /// Whether the verdict makes the run red. <c>skipped-unavailable</c> and
    /// <c>skipped-tool-missing</c> are warnings rather than failures; a command asked for such a leg
    /// by name decides for itself what that means, as <c>legs</c> does.
    /// </summary>
    /// <param name="verdict">The verdict.</param>
    public static bool IsFailure(LegVerdict verdict) => Describe(verdict).IsFailure;

    /// <summary>
    /// Whether the leg did no work: it was not selected, no host could run it, or a tool it needs
    /// is missing.
    /// </summary>
    /// <remarks>
    /// Neither a failure nor a pass, and the run's summary needs all three: a leg that never ran
    /// proves nothing about the code, so counting it as passed reports evidence nobody gathered.
    /// Listed by verdict rather than by a flag on the table, because these three are exactly the
    /// verdicts a leg reaches without running, and a new verdict should have to say which it is.
    /// </remarks>
    /// <param name="verdict">The verdict.</param>
    public static bool IsSkip(LegVerdict verdict) => verdict
        is LegVerdict.SkippedNotSelected
        or LegVerdict.SkippedUnavailable
        or LegVerdict.SkippedToolMissing;

    /// <summary>How fundamental the verdict is; the smaller number decides when legs disagree.</summary>
    /// <param name="verdict">The verdict.</param>
    public static int Rank(LegVerdict verdict) => Describe(verdict).Rank;

    /// <summary>The exit code a run reports when <paramref name="verdict"/> decides it.</summary>
    /// <param name="verdict">The verdict.</param>
    public static int ExitCodeFor(LegVerdict verdict) => Describe(verdict).ExitCode;

    /// <summary>
    /// The verdict a display name belongs to, or <see langword="null"/> when nothing is spelled that
    /// way.
    /// </summary>
    /// <param name="display">The name as a ledger writes it, such as <c>inputs-moved</c>.</param>
    /// <remarks>
    /// Read back from the same table that writes it, so a ledger this build produced is one this
    /// build can read — which is what lets a host report its leg's verdict to the machine that asked
    /// for it. A name this build does not know is not guessed at: the caller decides what an
    /// unreadable answer means, and every caller here treats it as no verdict at all.
    /// </remarks>
    public static LegVerdict? Parse(string? display)
        => display is null
            ? null
            : All
                .Where(info => string.Equals(info.Display, display, StringComparison.Ordinal))
                .Select(info => (LegVerdict?)info.Verdict)
                .FirstOrDefault();

    /// <summary>
    /// The verdict that decides a run over <paramref name="verdicts"/>: the most fundamental one
    /// present. An empty sequence is <see cref="LegVerdict.Passed"/>, because no leg failed; a run
    /// that selected no leg at all is refused where the legs are selected, not scored here.
    /// </summary>
    /// <param name="verdicts">Every selected leg's verdict.</param>
    public static LegVerdict Worst(IEnumerable<LegVerdict> verdicts)
    {
        ArgumentNullException.ThrowIfNull(verdicts);

        LegVerdict? worst = null;
        var rank = int.MaxValue;

        foreach (var verdict in verdicts)
        {
            var candidate = Rank(verdict);

            if (candidate < rank)
            {
                worst = verdict;
                rank = candidate;
            }
        }

        return worst ?? LegVerdict.Passed;
    }

    /// <summary>The exit code a run over <paramref name="verdicts"/> reports.</summary>
    /// <param name="verdicts">Every selected leg's verdict.</param>
    public static int ExitCodeFor(IEnumerable<LegVerdict> verdicts) => ExitCodeFor(Worst(verdicts));

    /// <summary>
    /// The verdict a refusal carrying <paramref name="exitCode"/> gives the leg it stopped. A leg
    /// that refuses is still a leg with a verdict: reported as poisoned, a host that is merely
    /// switched off would read as a defect in the tool.
    /// </summary>
    /// <param name="exitCode">The code the refusal carried, as <see cref="HarnessException.ExitCode"/> reports it.</param>
    public static LegVerdict ForRefusal(int exitCode) => exitCode switch
    {
        HarnessExit.Refused => LegVerdict.RefusedLocked,
        HarnessExit.ToolMissing => LegVerdict.SkippedToolMissing,
        HarnessExit.HostUnavailable => LegVerdict.SkippedUnavailable,
        HarnessExit.CommandFailed => LegVerdict.Failed,
        LegExit.InputsMoved => LegVerdict.InputsMoved,
        LegExit.Contended => LegVerdict.Contended,
        LegExit.Unwitnessed => LegVerdict.Unwitnessed,
        LegExit.LogHeld => LegVerdict.LogHeld,
        _ => LegVerdict.Poisoned,
    };
}
