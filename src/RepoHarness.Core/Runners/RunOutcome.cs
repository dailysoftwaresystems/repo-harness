using RepoHarness.Core.Configuration;

namespace RepoHarness.Core.Runners;

/// <summary>
/// What a run reported: the four things an expected exception can declare, and the four a run check
/// compares against.
/// </summary>
/// <remarks>
/// The same shape on both sides on purpose. <c>sameException: true</c> means "the runner this check
/// names produced what the entry says", and that comparison is only trustworthy while the two are
/// the same four fields; a check comparing a subset would pass on a runner that failed differently.
/// </remarks>
/// <param name="Success">Whether the run is reported as a success.</param>
/// <param name="Warning">Whether the outcome carries a warning, so an excused failure stays visible.</param>
/// <param name="ResultCode">The code reported. Never negative: no process returns one.</param>
/// <param name="Message">What is reported, already phrased for a reader.</param>
/// <param name="Output">
/// What the run's steps printed, when it was captured. Carried beside <paramref name="Message"/>
/// for the reason <see cref="RunFailure"/> carries both: a run's own summary sentence and what its
/// steps said are different evidence, and a check looking for a line a step prints — a count, a
/// marker, a measured figure — would otherwise be matching against a sentence that never contains
/// it. Not part of <see cref="SameExceptionAs"/>: an excusal is defined by the four fields an
/// entry declares, and output is not one of them.
/// </param>
public sealed record RunOutcome(bool Success, bool Warning, int ResultCode, string Message, string? Output = null)
{
    /// <summary>Everything a check's expected message is matched against.</summary>
    /// <remarks>
    /// Mirrors <see cref="RunFailure.Texts"/>, so a check reads the same way whether it is
    /// confirming a failure or witnessing a run that passed.
    /// </remarks>
    public IEnumerable<string> Texts
    {
        get
        {
            yield return Message;

            if (!string.IsNullOrEmpty(Output))
            {
                yield return Output;
            }
        }
    }

    /// <summary>A plain success.</summary>
    /// <param name="message">What to report.</param>
    /// <param name="output">What the run's steps printed, when it was captured.</param>
    public static RunOutcome Ok(string message, string? output = null) => new(true, false, 0, message, output);

    /// <summary>A plain failure.</summary>
    /// <param name="resultCode">The code the run reported.</param>
    /// <param name="message">What to report.</param>
    public static RunOutcome Failed(int resultCode, string message)
        => new(false, false, resultCode, message);

    /// <summary>The outcome <paramref name="entry"/> declares in place of an unexplained failure.</summary>
    /// <param name="entry">The expected exception that matched.</param>
    public static RunOutcome From(ExpectedException entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new RunOutcome(entry.Success, entry.Warning, entry.ResultCode, entry.Message);
    }

    /// <summary>
    /// Whether <paramref name="other"/> is the same exception: the same success, warning, result
    /// code and message.
    /// </summary>
    /// <param name="other">The outcome to compare with.</param>
    public bool SameExceptionAs(RunOutcome other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return Success == other.Success
            && Warning == other.Warning
            && ResultCode == other.ResultCode
            && string.Equals(Message, other.Message, StringComparison.Ordinal);
    }
}

/// <summary>
/// What a failed run reported, as much of it as was captured.
/// </summary>
/// <remarks>
/// Both a message and the output are carried because the two sources are not interchangeable: a
/// leg that surfaces a typed exception has a message, and a leg whose child process merely printed
/// and exited has only output. An entry matches when either carries what it recognises, so an
/// excusal earned against a printed failure is not lost the day the same failure arrives typed.
/// </remarks>
/// <param name="ExceptionType">The exception's type name, as the run reported it.</param>
/// <param name="Message">The exception's message, when one was reported.</param>
/// <param name="Output">The failing unit's output, when it was captured.</param>
public sealed record RunFailure(string ExceptionType, string? Message = null, string? Output = null)
{
    /// <summary>Everything an entry's messages are matched against.</summary>
    public IEnumerable<string> Texts
    {
        get
        {
            if (Message is not null)
            {
                yield return Message;
            }

            if (Output is not null)
            {
                yield return Output;
            }
        }
    }
}
