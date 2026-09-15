namespace RepoHarness.Core.Configuration;

/// <summary>
/// A machine the harness can reach. Reaching a machine and choosing which tree to
/// act on are separate concerns: a worktree is a tree, not a target.
/// </summary>
public sealed class TargetConfig
{
    /// <summary>How to reach it: <c>local</c>, <c>wsl</c> or <c>ssh</c>.</summary>
    public required string Transport { get; init; }

    /// <summary>Path of the repository on the target, for non-local transports.</summary>
    public string? RepositoryPath { get; init; }

    /// <summary>WSL distribution name, for the <c>wsl</c> transport.</summary>
    public string? Distro { get; init; }

    /// <summary>
    /// Comma separated host names this target is reachable from, or <c>ALL</c>.
    /// Omitting it means <c>ALL</c>, which suits a target reachable from anywhere.
    /// </summary>
    public string AvailableOn { get; init; } = "ALL";

    /// <summary>
    /// Cores a build uses on this machine, replacing <c>defaults.buildCores</c>. A remote
    /// host rarely has the same core count as the machine the configuration was written on.
    /// </summary>
    public int? BuildCores { get; init; }

    /// <summary>Cores a test run uses on this machine, replacing <c>defaults.testCores</c>.</summary>
    public int? TestCores { get; init; }

    /// <summary>
    /// Command that keeps this machine awake while a leg runs on it, with <c>{pid}</c> replaced
    /// by the process it should outlive, such as <c>["caffeinate", "-dimsu", "-w", "{pid}"]</c> on
    /// macOS. A host that sleeps mid-leg charges the sleep to whatever was running, which once
    /// reported a 4 ms test at 729 s. Without it, the leg's timings are marked suspect.
    /// </summary>
    public List<string>? KeepAwake { get; init; }

    /// <summary>
    /// Seconds an ssh connection may take to open. With <see cref="KeepAliveSeconds"/>, this is
    /// what stops a dead link from hanging a leg with no output and no end.
    /// </summary>
    public int ConnectTimeoutSeconds { get; init; } = 25;

    /// <summary>Seconds between ssh keep-alive probes; an unanswered probe ends the connection.</summary>
    public int KeepAliveSeconds { get; init; } = 30;

    /// <summary>
    /// Compiler cache directory on the target, set explicitly so that two targets
    /// never share one store. A shared store lets one host's objects satisfy
    /// another host's build, which is a contamination this prevents by construction.
    /// </summary>
    public string? CompilerCacheDirectory { get; init; }

    /// <summary>
    /// Environment applied to every phase run on this target. Names compare ignoring
    /// case on every platform, as they do on Windows.
    /// </summary>
    public Dictionary<string, string> Env { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>One unit of work that receives exactly one verdict.</summary>
public sealed class LegConfig
{
    /// <summary>Target this leg runs on.</summary>
    public required string Target { get; init; }

    /// <summary>Project this leg builds. Falls back to the default project.</summary>
    public string? Project { get; init; }

    /// <summary>Toolchain to build with. Falls back to the project's platform default.</summary>
    public string? Toolchain { get; init; }

    /// <summary>Build configuration to use.</summary>
    public required string Config { get; init; }

    /// <summary>
    /// The instrumentation overlay to apply, by its name under <c>sanitizers</c>, if any.
    /// Singular on purpose: a leg applies one overlay, and instruments are combined by
    /// declaring one overlay that sets them together.
    /// </summary>
    public string? Sanitizer { get; init; }

    /// <summary>
    /// Worktree to act on instead of the target's main tree, named by the same rules
    /// <c>create-worktree</c> applies.
    /// </summary>
    public string? Worktree { get; init; }

    /// <summary>Test settings for this leg, overriding the project's.</summary>
    public TestConfig? Test { get; init; }

    /// <summary>What this leg covers, shown in the ledger.</summary>
    public string? Description { get; init; }
}

/// <summary>
/// How to run tests, optionally specialised per platform. At least one invocation
/// section must be present, and on every platform an invocation applies to, the platform
/// section merged over <see cref="All"/> must name a runner and a success pattern: a
/// configuration that does not is rejected when it is read.
/// </summary>
public sealed class TestConfig
{
    /// <summary>
    /// Paths or globs, relative to the tree, that the tests read while they run. Their content
    /// is fingerprinted before the tests start and after they end; a difference makes the
    /// verdict <c>inputs-moved</c>, because some tests saw the old files and some the new, and
    /// the result describes a tree that never existed. Empty means every file git tracks.
    /// </summary>
    public List<string> Inputs { get; init; } = [];

    /// <summary>Settings applying on every platform.</summary>
    public TestInvocation? All { get; init; }

    /// <summary>Windows overrides, merged field by field over <see cref="All"/>.</summary>
    public TestInvocation? Windows { get; init; }

    /// <summary>Linux overrides, merged field by field over <see cref="All"/>.</summary>
    public TestInvocation? Linux { get; init; }

    /// <summary>macOS overrides, merged field by field over <see cref="All"/>.</summary>
    public TestInvocation? Macos { get; init; }

    /// <summary>Build configurations to test against; each is built before it is run.</summary>
    public List<string> Configs { get; init; } = [];

    /// <summary>
    /// SSH targets to additionally test against when reachable. An unreachable
    /// target warns and is recorded as skipped; it never fails the run, because a
    /// personal machine being switched off is the expected state, not a fault.
    /// </summary>
    public List<string> TestAgainstSshIfAvailable { get; init; } = [];
}

/// <summary>One concrete test invocation.</summary>
public sealed class TestInvocation
{
    /// <summary>Test runner to invoke, such as <c>ctest</c>.</summary>
    public string? Runner { get; init; }

    /// <summary>Arguments passed to the runner.</summary>
    public List<string>? Args { get; init; }

    /// <summary>
    /// Argument introducing a test filter, such as <c>-R</c> for ctest or
    /// <c>--filter</c> for dotnet, so one <c>--filter</c> option works everywhere.
    /// </summary>
    public string? FilterArg { get; init; }

    /// <summary>
    /// Cores this invocation uses, replacing both the target's <c>testCores</c> and
    /// <c>defaults.testCores</c>.
    /// </summary>
    public int? Cores { get; init; }

    /// <summary>
    /// Arguments that hand the runner its core count, with <c>{cores}</c> replaced, such as
    /// <c>["-j", "{cores}"]</c>. A runner left to its own default runs serially on one host and
    /// on every core on another, and legs stop being comparable.
    /// </summary>
    public List<string>? CoresArgs { get; init; }

    /// <summary>
    /// Environment variables set to the core count, such as <c>CTEST_PARALLEL_LEVEL</c>.
    /// Preferred wherever the runner reads one: an explicit option in <see cref="Args"/> still
    /// wins, decided by the runner itself, where spliced-in arguments would contradict it.
    /// </summary>
    public List<string>? CoresEnv { get; init; }

    /// <summary>Environment variables for this invocation.</summary>
    public Dictionary<string, string>? Env { get; init; }

    /// <summary>
    /// Pattern proving the runner actually ran, matched against the runner's own output and
    /// never against anything the harness wrote. Required: a zero exit code with no match is
    /// reported as <c>unwitnessed</c>, because a result with no evidence behind it is
    /// indistinguishable from never having run.
    /// </summary>
    public string? SuccessPattern { get; init; }

    /// <summary>
    /// Pattern whose named group <c>total</c> captures how many tests ran. Legs running the same
    /// tests are compared, and one that ran a different number is flagged: a platform that
    /// quietly skips a group of tests passes on less evidence than its siblings.
    /// </summary>
    public string? CountPattern { get; init; }
}
