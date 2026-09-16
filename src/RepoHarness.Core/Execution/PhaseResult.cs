namespace RepoHarness.Core.Execution;

/// <summary>One value a timing pattern pulled out of a phase's output.</summary>
/// <param name="Pattern">The pattern that matched, so a reader knows which measurement this is.</param>
/// <param name="Text">The whole match, exactly as the command printed it.</param>
/// <param name="Value">
/// The first capturing group when the pattern has one, and the whole match otherwise, so a pattern
/// can name the number without the words around it.
/// </param>
public sealed record PhaseTiming(string Pattern, string Text, string Value);

/// <summary>
/// What running one phase established. Every field here is evidence: the exit code came from the
/// process itself, the witness from the command's own output, and the duration from the monotonic
/// clock rather than from two readings of a wall clock that may have stepped between them.
/// </summary>
/// <param name="Leg">The leg this phase belongs to.</param>
/// <param name="Phase">The phase's name, which also names its log file.</param>
/// <param name="ExitCode">The code the process reported, read from the process and not through a pipe or a wrapper.</param>
/// <param name="Stalled">
/// Whether the phase went <see cref="StallSeconds"/> without a line on either stream and was stopped
/// as hung. A stall bound rather than a time budget: output cadence stays stable even when total
/// duration does not, so an honest run that simply takes longer than someone guessed is never killed.
/// </param>
/// <param name="StallSeconds">The bound that was in force; zero means none was.</param>
/// <param name="Witnessed">
/// Whether the declared success pattern matched the command's own output, or <see langword="null"/>
/// when the phase declared no pattern. A pattern is never matched against anything the harness wrote.
/// </param>
/// <param name="Duration">The command's own time, from the monotonic clock.</param>
/// <param name="ClockDrift">
/// How far wall-clock time moved from monotonic time across this phase. Reported even when it is
/// within tolerance, because it is the measurement <see cref="ClockStepped"/> is decided from.
/// </param>
/// <param name="ClockStepped">
/// Whether the drift exceeded <c>defaults.clockStepToleranceMilliseconds</c>, meaning the phase
/// spanned a clock step or a host sleep. Its durations are suspect, and so is every file
/// modification time it stamped, which is what an incremental build would otherwise trust.
/// </param>
/// <param name="Timings">Every match of the phase's timing patterns, in the order they appeared.</param>
/// <param name="LogFile">Where the whole of the child's output was written, for a reader or a regex.</param>
/// <param name="Output">
/// The child's own output, both streams, exactly as it wrote them. Held apart from the log file,
/// which also carries the header the harness wrote: a header that echoes the command line contains
/// the success pattern whenever the command does.
/// </param>
public sealed record PhaseResult(
    string Leg,
    string Phase,
    int ExitCode,
    bool Stalled,
    int StallSeconds,
    bool? Witnessed,
    TimeSpan Duration,
    TimeSpan ClockDrift,
    bool ClockStepped,
    IReadOnlyList<PhaseTiming> Timings,
    string LogFile,
    string Output)
{
    /// <summary>
    /// Whether the phase passed: it ran to completion, reported success, and where a pattern was
    /// declared that pattern matched its own output. A zero exit code alone is not proof a command
    /// ran, which was measured three separate ways.
    /// </summary>
    public bool Passed => !Stalled && ExitCode == 0 && Witnessed != false;

    /// <summary>
    /// The verdict this phase gives its leg, with the sentence the ledger shows for it. A phase that
    /// exited zero with no match is <c>unwitnessed</c> rather than passed, and a hung phase is
    /// reported as failed and says what it was doing: neither is a defect in the harness.
    /// </summary>
    public ReachedVerdict Verdict()
    {
        if (Stalled)
        {
            return ReachedVerdict.Of(
                LegVerdict.Failed,
                $"{Phase} hung: no output for {StallSeconds}s");
        }

        if (ExitCode != 0)
        {
            return ReachedVerdict.Of(LegVerdict.Failed, $"{Phase} exited {ExitCode}");
        }

        return Witnessed == false
            ? ReachedVerdict.Of(LegVerdict.Unwitnessed, $"{Phase} exited 0 and its success pattern never matched its output")
            : ReachedVerdict.Of(LegVerdict.Passed, string.Empty);
    }
}
