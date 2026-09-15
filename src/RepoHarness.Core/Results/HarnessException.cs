namespace RepoHarness.Core.Results;

/// <summary>
/// A refusal that carries the exit code it should be reported with. Lets a service
/// deep in a call chain refuse precisely without every caller re-deciding what the
/// failure meant.
/// </summary>
public sealed class HarnessException(int exitCode, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>Exit code this failure should produce.</summary>
    public int ExitCode { get; } = exitCode;
}
