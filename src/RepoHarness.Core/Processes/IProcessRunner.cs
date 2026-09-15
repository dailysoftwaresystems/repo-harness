namespace RepoHarness.Core.Processes;

/// <summary>
/// Runs child processes. The single seam through which the harness reaches
/// external tools, so every command is testable without invoking real binaries.
/// </summary>
public interface IProcessRunner
{
    /// <summary>Runs <paramref name="request"/> to completion.</summary>
    /// <exception cref="ExecutableNotFoundException">
    /// The executable named by <see cref="ProcessRequest.FileName"/> is not installed
    /// or not on PATH. Distinct from a non-zero exit code: the tool never ran.
    /// </exception>
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves an executable's full path, or <see langword="null"/> when it is not
    /// installed. Used by tool verification rather than running the tool to find out.
    /// </summary>
    string? FindExecutable(string command);
}
