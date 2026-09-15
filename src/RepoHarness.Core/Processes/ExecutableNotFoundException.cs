namespace RepoHarness.Core.Processes;

/// <summary>
/// Raised when a requested executable is not installed or not on PATH.
/// Distinct from a non-zero exit code: the tool never ran, so callers can
/// report "the instrument could not run" rather than "the command failed".
/// </summary>
public sealed class ExecutableNotFoundException(string fileName, Exception? innerException = null)
    : Exception($"Executable '{fileName}' was not found on PATH.", innerException)
{
    /// <summary>The executable that could not be found.</summary>
    public string FileName { get; } = fileName;
}
