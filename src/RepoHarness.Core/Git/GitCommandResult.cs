namespace RepoHarness.Core.Git;

/// <summary>Outcome of a git invocation.</summary>
/// <param name="ExitCode">git's exit code, read directly and never through a pipeline.</param>
/// <param name="StandardOutput">Captured stdout.</param>
/// <param name="StandardError">Captured stderr.</param>
/// <param name="TimedOut">Whether git was killed for exceeding its budget.</param>
public sealed record GitCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut = false)
{
    /// <summary>Whether git reported success.</summary>
    public bool Succeeded => !TimedOut && ExitCode == 0;

    /// <summary>Non-empty output lines, trimmed.</summary>
    public IReadOnlyList<string> OutputLines => StandardOutput
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.TrimEnd('\r'))
        .Where(line => line.Length > 0)
        .ToList();

    /// <summary>The best available description of a failure.</summary>
    public string FailureMessage
    {
        get
        {
            if (TimedOut)
            {
                return "git did not finish within its time budget.";
            }

            var error = StandardError.Trim();
            if (error.Length > 0)
            {
                return error;
            }

            var output = StandardOutput.Trim();

            // Never report a failure with nothing after the colon: a caller that
            // interpolates this must always have something to show.
            return output.Length > 0 ? output : $"git exited with code {ExitCode}.";
        }
    }
}
