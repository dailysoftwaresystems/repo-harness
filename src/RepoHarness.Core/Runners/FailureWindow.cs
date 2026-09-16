namespace RepoHarness.Core.Runners;

/// <summary>What one line of a run's output is.</summary>
public enum RunOutputKind
{
    /// <summary>Output a unit produced while it ran.</summary>
    Output = 0,

    /// <summary>The line marking a unit starting. The opening edge of that unit's window.</summary>
    Start,

    /// <summary>The line carrying a unit's verdict. The closing edge of that unit's window.</summary>
    Verdict,
}

/// <summary>
/// One line of a run's output, attributed to the unit that produced it and stamped when it was
/// recorded.
/// </summary>
/// <remarks>
/// The unit and the timestamp are what make a window possible at all. Output recorded as an
/// undifferentiated stream can only be sampled once per run, and a once-per-run sample is the
/// defect this type exists to prevent.
/// </remarks>
/// <param name="Timestamp">When the line was recorded.</param>
/// <param name="Unit">The unit that produced it, or <see langword="null"/> for the run's own lines.</param>
/// <param name="Kind">Whether the line opens a unit, closes it, or is its output.</param>
/// <param name="Text">The line itself.</param>
public sealed record RunOutputLine(DateTimeOffset Timestamp, string? Unit, RunOutputKind Kind, string Text);

/// <summary>
/// One step recorded while a run was under way: a measured interval, with when it happened.
/// </summary>
/// <param name="At">When the step was recorded.</param>
/// <param name="Duration">How long it took.</param>
/// <param name="Detail">What it was, for the ledger.</param>
public sealed record RunStep(DateTimeOffset At, TimeSpan Duration, string Detail);

/// <summary>
/// The failing unit's own execution window: from the line that starts it to the line carrying its
/// verdict.
/// </summary>
/// <remarks>
/// <para>
/// A run check counts steps inside this window and nowhere else. The alternative — one sample taken
/// per run — was measured doing the opposite of its job on both sides: on a loaded machine it
/// charged genuine-looking failures to the tool under test, and on a quiet one it excused them,
/// from the same configuration on the same day, because what the machine was doing when the sample
/// was taken says nothing about what it was doing while the unit that failed was running. Adopting
/// it here would reopen a defect that was closed deliberately.
/// </para>
/// <para>
/// A window that cannot be established — the unit never started, or never reached a verdict —
/// excuses nothing. An unfinished window is not evidence of a quiet machine; it is the absence of
/// evidence, and it is honoured as ABSENT.
/// </para>
/// </remarks>
/// <param name="Unit">The failing unit the window belongs to.</param>
/// <param name="Start">When the unit started.</param>
/// <param name="End">When it reached its verdict.</param>
/// <param name="Lines">The unit's own output, the start and verdict lines included.</param>
public sealed record FailureWindow(
    string Unit,
    DateTimeOffset Start,
    DateTimeOffset End,
    IReadOnlyList<RunOutputLine> Lines)
{
    /// <summary>
    /// The window of <paramref name="unit"/> in <paramref name="output"/>, or
    /// <see langword="null"/> when that unit did not both start and reach a verdict.
    /// </summary>
    /// <param name="output">The run's output, in the order it was recorded.</param>
    /// <param name="unit">The failing unit's identity, as the run attributed its lines.</param>
    public static FailureWindow? Extract(IReadOnlyList<RunOutputLine> output, string unit)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);

        var start = -1;
        var end = -1;

        for (var index = 0; index < output.Count; index++)
        {
            var line = output[index];

            if (!string.Equals(line.Unit, unit, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (start < 0 && line.Kind == RunOutputKind.Start)
            {
                start = index;
                continue;
            }

            if (start >= 0 && line.Kind == RunOutputKind.Verdict)
            {
                end = index;
                break;
            }
        }

        if (start < 0 || end < 0)
        {
            return null;
        }

        // The unit's own lines, not everything recorded between its edges: another leg running
        // beside it writes into the same stream, and its output is not evidence about this one.
        var lines = output
            .Skip(start)
            .Take(end - start + 1)
            .Where(line => string.Equals(line.Unit, unit, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return new FailureWindow(unit, output[start].Timestamp, output[end].Timestamp, lines);
    }

    /// <summary>Whether <paramref name="step"/> was recorded inside this window.</summary>
    /// <param name="step">A step recorded during the run.</param>
    public bool Contains(RunStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        return step.At >= Start && step.At <= End;
    }

    /// <summary>
    /// How many of <paramref name="steps"/> were recorded inside this window and lasted at least
    /// <paramref name="minStepSeconds"/>.
    /// </summary>
    /// <remarks>
    /// A step outside the window does not count, however long it lasted. That is the whole
    /// distinction between this and a once-per-run sample, and it is the one the gate depends on.
    /// </remarks>
    /// <param name="steps">Every step recorded during the run.</param>
    /// <param name="minStepSeconds">The shortest step that counts, in seconds.</param>
    public int CountSteps(IEnumerable<RunStep> steps, double minStepSeconds)
    {
        ArgumentNullException.ThrowIfNull(steps);

        return steps.Count(step => Contains(step) && step.Duration.TotalSeconds >= minStepSeconds);
    }
}
