using RepoHarness.Core.Processes;

namespace RepoHarness.Core.Results;

/// <summary>
/// The exceptions whose cause this build already has a name for, and what each means.
/// </summary>
/// <remarks>
/// One table for the two places that turn an exception into an outcome: the command runner, which
/// ends a whole command, and the leg executor, which records one leg and lets the others go on.
/// Each used to decide for itself, and they disagreed about the same exception: a program that would
/// not start was a missing tool to the one and a defect in the harness to the other. So cmake missing
/// from a host read as exit 14 when a command was typed there, and as a poisoned leg — exit 70, "the
/// harness broke" — when a run placed a leg there. A missing program on a host is that host's
/// problem, and a defect is this tool's; sending a reader after the wrong one costs the whole of
/// the time they spend looking.
/// </remarks>
public static class KnownCauses
{
    /// <summary>
    /// The exit code <paramref name="exception"/> means, or <see langword="null"/> when its cause is
    /// not one this build can name.
    /// </summary>
    /// <param name="exception">What was thrown.</param>
    public static int? ExitCodeFor(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            // Missing, or there and unable to start: either way the instrument never ran.
            ProgramStartException => HarnessExit.ToolMissing,
            _ => null,
        };
    }
}
