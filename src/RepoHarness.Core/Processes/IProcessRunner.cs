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
    /// <remarks>
    /// An implementation must invoke <see cref="ProcessRequest.OnStarted"/> once the child is
    /// running, and before any output line. It is how a caller tells this tool's own time apart
    /// from the child's silence: until it is called, a phase is held to the bound on starting
    /// rather than the bound on silence, so one that never calls it has every child judged by how
    /// long it took to start rather than by how long it said nothing.
    /// </remarks>
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// The file <see cref="RunAsync"/> would start for <paramref name="command"/>, or <see langword="null"/>
    /// when there is none: a name is looked up in the PATH directories and nowhere else, and a path is
    /// used as given. Used by tool verification rather than running the tool to find out.
    /// </summary>
    string? FindExecutable(string command);
}
