namespace RepoHarness.Core.Processes;

/// <summary>
/// Raised when a requested executable is not installed or not on PATH.
/// Distinct from a non-zero exit code: the tool never ran, so callers can
/// report "the instrument could not run" rather than "the command failed".
/// </summary>
public sealed class ExecutableNotFoundException(string fileName, Exception? innerException = null)
    : ProgramStartException(fileName, Describe(fileName), innerException)
{
    /// <summary>A name is looked up on PATH; a path is not, so for a path the file itself is what is missing.</summary>
    private static string Describe(string fileName)
        => ProcessRunner.IsPath(fileName)
            ? $"Executable '{fileName}' does not exist."
            : $"Executable '{fileName}' was not found on PATH.";
}
