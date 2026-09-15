namespace RepoHarness.Core.Processes;

/// <summary>
/// Raised when a program could not be started: it is missing, or it exists and the operating system
/// refused to run it, because it is not a program for this machine, may not be run, or needs an
/// interpreter or loader that is missing. Distinct from a non-zero exit code: the program never ran,
/// so callers can report "the instrument could not run" rather than "the command failed".
/// </summary>
public class ProgramStartException(string fileName, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>The program that could not be started, as it was named.</summary>
    public string FileName { get; } = fileName;
}
