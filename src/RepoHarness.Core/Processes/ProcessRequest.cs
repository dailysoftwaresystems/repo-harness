namespace RepoHarness.Core.Processes;

/// <summary>
/// A child process to run. Arguments are passed as a list and never as a single
/// command line: the harness must behave identically regardless of what quoting
/// or escaping a host shell would have applied.
/// </summary>
public sealed record ProcessRequest
{
    /// <summary>Executable to run. Resolved against PATH by the operating system.</summary>
    public required string FileName { get; init; }

    /// <summary>Arguments, one element per argument. Never pre-quoted.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Working directory, or <see langword="null"/> to inherit the current one.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Environment overrides applied on top of the inherited environment.
    /// A <see langword="null"/> value removes the variable.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; }
        = new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>
    /// Invoked for each stdout line as it arrives, in addition to capture.
    /// Raised on background threads, so implementations must be thread safe.
    /// </summary>
    public Action<string>? OnOutputLine { get; init; }

    /// <summary>Invoked for each stderr line as it arrives, in addition to capture.</summary>
    public Action<string>? OnErrorLine { get; init; }

    /// <summary>Wall clock budget, or <see langword="null"/> for no limit.</summary>
    public TimeSpan? Timeout { get; init; }
}
