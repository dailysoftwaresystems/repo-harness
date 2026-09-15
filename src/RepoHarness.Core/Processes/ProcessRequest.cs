namespace RepoHarness.Core.Processes;

/// <summary>
/// A child process to run. Arguments are passed as a list and never as a single
/// command line: the harness must behave identically regardless of what quoting
/// or escaping a host shell would have applied.
/// </summary>
public sealed record ProcessRequest
{
    /// <summary>
    /// The program: a name, looked up in the PATH directories and nowhere else, or a path, used as given.
    /// </summary>
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
    /// Text written to the child's standard input, which is then closed so the child sees its
    /// end; <see langword="null"/> gives it an input that ends at once. A child is never
    /// connected to this process's own input. Written as UTF-8 on every platform, the encoding
    /// output is read with.
    /// </summary>
    public string? StandardInput { get; init; }

    /// <summary>
    /// Keeps standard input open after <see cref="StandardInput"/> is written, until the child exits,
    /// instead of closing it at once. A child that acts on what it read and then watches for the end of
    /// its input learns from that end that this process has gone. A child that reads to the end before
    /// acting would wait forever, so only a child that watches this way is started with it.
    /// </summary>
    public bool HoldStandardInputOpen { get; init; }

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
