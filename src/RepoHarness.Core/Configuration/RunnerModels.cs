namespace RepoHarness.Core.Configuration;

/// <summary>
/// A multi-phase procedure, such as a corpus build-and-test or a benchmark.
/// Runners exist so that procedures specific to one repository live in
/// configuration rather than in the tool.
/// </summary>
public sealed class RunnerConfig
{
    /// <summary>What this runner is for, shown in help and in the ledger.</summary>
    public string? Description { get; init; }

    /// <summary>Legs this runner executes against. Empty means the default set.</summary>
    public List<string> Legs { get; init; } = [];

    /// <summary>Phases executed in order; a failing phase ends that leg.</summary>
    public List<RunnerPhase> Phases { get; init; } = [];

    /// <summary>
    /// Directories under the leg's work directory to wipe before every run. Use for
    /// run and scratch directories, whose contents must never carry across runs.
    /// Build directories are deliberately not listed: they stay incremental.
    /// </summary>
    public List<string> CleanDirectories { get; init; } = [];

    /// <summary>Environment applied to every phase.</summary>
    public Dictionary<string, string> Env { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One phase of a runner.</summary>
public sealed class RunnerPhase
{
    /// <summary>Name, used in progress output and to name this phase's log file.</summary>
    public required string Name { get; init; }

    /// <summary>Command and arguments, one element per argument; never a shell string.</summary>
    public required List<string> Command { get; init; }

    /// <summary>Working directory, relative to the leg's work directory.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Environment for this phase.</summary>
    public Dictionary<string, string> Env { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Pattern proving the phase ran, checked in addition to its exit code.</summary>
    public string? SuccessPattern { get; init; }

    /// <summary>Seconds without output after which this phase is treated as hung.</summary>
    public int? StallSeconds { get; init; }

    /// <summary>Whether a failure here ends the leg or is recorded and passed over.</summary>
    public bool ContinueOnError { get; init; }
}

/// <summary>A named command invokable through <c>repo-harness exec</c>.</summary>
public sealed class ExecConfig
{
    /// <summary>Executable to run.</summary>
    public required string Command { get; init; }

    /// <summary>Arguments; any given on the command line are appended to these.</summary>
    public List<string> Args { get; init; } = [];

    /// <summary>Working directory, relative to the tree root.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Environment for the command.</summary>
    public Dictionary<string, string> Env { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What the command does, shown in help.</summary>
    public string? Description { get; init; }
}
