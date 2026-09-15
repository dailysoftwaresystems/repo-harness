namespace RepoHarness.Core.Results;

/// <summary>
/// What a command decided. Commands return this rather than calling into the
/// console or Environment.Exit, so their behaviour is assertable in tests.
/// </summary>
/// <param name="ExitCode">Process exit code to report.</param>
/// <param name="Message">Single line summary, already phrased for a human.</param>
/// <param name="Details">Optional extra lines shown beneath the summary.</param>
public sealed record CommandOutcome(int ExitCode, string Message, IReadOnlyList<string>? Details = null)
{
    /// <summary>True when the command reported success.</summary>
    public bool Succeeded => ExitCode == HarnessExit.Success;

    /// <summary>
    /// The command's result, written to standard output unprefixed and before anything else: a
    /// listing, a detail block, a JSON document. Kept apart from <see cref="Details"/>, which are
    /// prefixed with the command's name, so another program can read a result without stripping it.
    /// </summary>
    public IReadOnlyList<string> Data { get; init; } = [];

    /// <summary>
    /// Whether a success leaves out its status line, because <see cref="Data"/> is the whole answer
    /// and one more line would be one more thing for a reader to strip. A failure is always reported.
    /// </summary>
    public bool Quiet { get; init; }

    /// <summary>A successful outcome.</summary>
    public static CommandOutcome Ok(string message, IReadOnlyList<string>? details = null)
        => new(HarnessExit.Success, message, details);

    /// <summary>A refusal: a precondition was not met, nothing was changed.</summary>
    public static CommandOutcome Refused(string message, IReadOnlyList<string>? details = null)
        => new(HarnessExit.Refused, message, details);

    /// <summary>A failure with an explicit exit code.</summary>
    public static CommandOutcome Failed(int exitCode, string message, IReadOnlyList<string>? details = null)
        => new(exitCode, message, details);
}
