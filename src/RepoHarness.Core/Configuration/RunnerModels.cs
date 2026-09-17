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
    /// <remarks>
    /// A runner declares <see cref="Phases"/> or <see cref="Action"/>, never both. Two descriptions
    /// of what a runner does would eventually disagree, and nothing could say which one ran.
    /// </remarks>
    public List<RunnerPhase> Phases { get; init; } = [];

    /// <summary>
    /// The action file under <c>.harness-config/runner/actions</c> holding this runner's steps, in
    /// place of <see cref="Phases"/>. Every field a phase carries is a key on a step, so nothing the
    /// verdict contract depends on is lost by declaring one instead of the other.
    /// </summary>
    public string? Action { get; init; }

    /// <summary>
    /// Whether this runner needs the repository built before it runs. A runner that calls a program
    /// the build produces otherwise runs against whatever was left there.
    /// </summary>
    /// <remarks>
    /// This gates the build alone, never the sync. A leg on an ssh host or a WSL distribution runs
    /// from that host's own copy of the tree — the host reads <c>config.json</c> and this runner's
    /// action file from it — so the tree is put there whether or not anything is compiled. Use
    /// <c>--use-staged</c> to run against a copy already known to be current.
    /// </remarks>
    public bool RequireBuild { get; init; }

    /// <summary>
    /// Seconds without output after which a phase of this runner is treated as hung, replacing
    /// <c>defaults.stallSeconds</c>. A stall bound rather than a time budget: output cadence stays
    /// stable even when total duration does not.
    /// </summary>
    public int? StallSeconds { get; init; }

    /// <summary>
    /// Failures this runner is allowed to produce, each with the outcome to report instead of an
    /// unexplained failure, and each gated on the checks that confirm it.
    /// </summary>
    public List<ExpectedException> ExpectedExceptions { get; init; } = [];

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
/// <remarks>
/// A record so that a phase derived from another is written as a copy with the one field changed,
/// rather than rebuilt member by member. Rebuilt, every field added later has to be remembered at
/// every rebuild site, and the one that is forgotten is silently false: this shipped once, and what
/// it dropped was <see cref="WatchContention"/> and <see cref="RequireInputsUnmoved"/> — two
/// guards, off, in exactly the action files that declared inputs.
/// </remarks>
public sealed record RunnerPhase
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

    /// <summary>
    /// Whether the process table is watched while this runs, so another run building in the same
    /// directory is reported rather than silently shared with.
    /// </summary>
    /// <remarks>
    /// Needs a build directory to watch, which is the leg's. A run reaching no leg has none, and
    /// that is refused when the action runs rather than passed over: a guard that watched nothing
    /// would report a clean directory without having looked at one.
    /// </remarks>
    public bool WatchContention { get; init; }

    /// <summary>
    /// Whether the tracked files are fingerprinted before, during and after this, so a tree edited
    /// while it ran is reported rather than producing a result that describes no tree that existed.
    /// </summary>
    public bool RequireInputsUnmoved { get; init; }

    /// <summary>
    /// The step this phase belongs to, which names the directory its work goes in. Empty for a
    /// phase a runner declared directly, which owns no action directory.
    /// </summary>
    public string StepName { get; init; } = string.Empty;

    /// <summary>What this phase must have produced, relative to its step's own build directory.</summary>
    public IReadOnlyList<string> Outputs { get; init; } = [];

    /// <summary>Whether this step's outputs survive the run.</summary>
    public bool Persist { get; init; }
}

/// <summary>A named command invokable through <c>DssHarness exec</c>.</summary>
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
