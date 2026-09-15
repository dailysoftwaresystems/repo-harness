namespace RepoHarness.Core.Configuration;

/// <summary>
/// The whole of <c>.harness-config/config.json</c>. Every behaviour the harness has
/// is declared here: nothing about a specific repository, language or toolchain is
/// compiled into the tool. A behaviour that cannot be expressed here is a defect.
/// </summary>
/// <remarks>
/// Sections serve commands that arrive over several releases. A section whose command
/// is not implemented yet is still parsed and validated, so a configuration written
/// today keeps working when that command lands, but it has no effect until then.
/// </remarks>
public sealed class HarnessConfig
{
    /// <summary>Schema version, so later changes can be migrated rather than guessed.</summary>
    public int Version { get; init; } = 1;

    /// <summary>Settings used wherever a more specific section does not override them.</summary>
    public HarnessDefaults Defaults { get; init; } = new();

    /// <summary>Compilers, and the environment or cache variables that select them.</summary>
    public Dictionary<string, ToolchainConfig> Toolchains { get; init; } = Map<ToolchainConfig>();

    /// <summary>Instrumentation overlays, composed onto a build like a toolchain.</summary>
    public Dictionary<string, VariantOverlay> Sanitizers { get; init; } = Map<VariantOverlay>();

    /// <summary>Named build configurations: debug, release, o1, o2 and so on.</summary>
    public Dictionary<string, BuildConfiguration> BuildConfigs { get; init; } = Map<BuildConfiguration>();

    /// <summary>Buildable projects in this repository.</summary>
    public List<ProjectConfig> Projects { get; init; } = [];

    /// <summary>Machines the harness can reach, keyed by name.</summary>
    public Dictionary<string, TargetConfig> Targets { get; init; } = Map<TargetConfig>();

    /// <summary>Units of work that each receive exactly one verdict, keyed by name.</summary>
    public Dictionary<string, LegConfig> Legs { get; init; } = Map<LegConfig>();

    /// <summary>Named groups of legs, so a whole gate can be invoked by one name.</summary>
    public Dictionary<string, List<string>> LegSets { get; init; } = Map<List<string>>();

    /// <summary>External tools this repository needs, verified and installed by name.</summary>
    public List<ToolConfig> Tools { get; init; } = [];

    /// <summary>Multi-phase procedures such as a corpus build-and-test or a benchmark.</summary>
    public Dictionary<string, RunnerConfig> Runners { get; init; } = Map<RunnerConfig>();

    /// <summary>Named commands invokable through <c>repo-harness exec</c>.</summary>
    public Dictionary<string, ExecConfig> Exec { get; init; } = Map<ExecConfig>();

    /// <summary>Commit message templating and policy.</summary>
    public CommitConfig Commit { get; init; } = new();

    /// <summary>What the tree mirror carries, and what it must never carry.</summary>
    public SyncConfig Sync { get; init; } = new();

    /// <summary>Processes outside the harness that can corrupt a leg's result while it runs.</summary>
    public ContentionConfig Contention { get; init; } = new();

    /// <summary>Worktree creation policy.</summary>
    public WorktreeSettings Worktrees { get; init; } = new();

    /// <summary>The anchor registries: where they live, and how a new anchor id is spelled.</summary>
    public AnchorSettings Anchors { get; init; } = new();

    private static Dictionary<string, TValue> Map<TValue>() => new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Settings used wherever a more specific section does not override them.</summary>
public sealed class HarnessDefaults
{
    /// <summary>Cores a build or a test run uses unless something more specific says otherwise.</summary>
    public const int DefaultCores = 6;

    /// <summary>
    /// Cores a build uses. Deliberately not "every core": a machine entirely claimed by
    /// a build cannot be used for anything else while it runs. A target with a different
    /// core count replaces this with its own <c>buildCores</c>.
    /// </summary>
    public int BuildCores { get; init; } = DefaultCores;

    /// <summary>
    /// Cores a test run uses, for the same reason. Replaced by a target's
    /// <c>testCores</c>, and by a test invocation's own <c>cores</c>.
    /// </summary>
    public int TestCores { get; init; } = DefaultCores;

    /// <summary>
    /// Most legs a command runs at once, or <see langword="null"/> to start every selected
    /// leg together. Legs are isolated from one another, so running them one at a time is
    /// never needed for correctness; this exists only to cap the load on a busy machine.
    /// </summary>
    public int? MaxParallelLegs { get; init; }

    /// <summary>Leg set used when a command is given no explicit selection.</summary>
    public string? LegSet { get; init; }

    /// <summary>Project used when a command names none.</summary>
    public string? Project { get; init; }

    /// <summary>
    /// Seconds without output after which a phase is treated as hung; zero disables
    /// the check. A stall bound is used rather than a wall clock budget because
    /// output cadence stays stable even when total duration does not.
    /// </summary>
    public int StallSeconds { get; init; }

    /// <summary>
    /// How many times longer than the same phase on a sibling leg, or than its own recent
    /// runs, a phase may take before the ledger marks its timings suspect; zero disables the
    /// mark. The mark never changes a verdict: a host that slept, or a clock that stepped,
    /// makes a duration meaningless, and whether the code passed is a separate fact.
    /// </summary>
    public double DurationWarningFactor { get; init; } = 3.0;

    /// <summary>
    /// Milliseconds wall-clock time may drift from monotonic time across one phase before the
    /// phase is recorded as spanning a clock step or a host sleep. Durations from such a phase
    /// are suspect, and so is every mtime it stamped, so an incremental build that would trust
    /// those stamps is rebuilt instead.
    /// </summary>
    public int ClockStepToleranceMilliseconds { get; init; } = 2000;

    /// <summary>
    /// Seconds between samples of the process table while a leg runs, in addition to the
    /// samples always taken as it starts and ends. Every sample is kept: a contender that
    /// started and finished between the first and the last would otherwise go unreported.
    /// </summary>
    public int ProcessSampleSeconds { get; init; } = 5;
}
