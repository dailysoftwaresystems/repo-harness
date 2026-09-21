namespace RepoHarness.Core.Configuration;

/// <summary>One unit of work that receives exactly one verdict.</summary>
/// <remarks>
/// A leg says what it needs, never where it happens to run: an operating system, a processor,
/// and the emulator allowed to stand in for that processor. Which host provides those is
/// measured before anything starts, so one configuration serves every machine it is run from.
/// </remarks>
public sealed class LegConfig
{
    /// <summary>Operating system the leg runs on: <c>windows</c>, <c>linux</c> or <c>macos</c>.</summary>
    public required string Os { get; init; }

    /// <summary>Processor the leg's programs are built for and run as, such as <c>x86_64</c> or <c>arm64</c>.</summary>
    public required string Processor { get; init; }

    /// <summary>
    /// The emulator, by its name under <c>emulators</c>, that runs this leg's programs on a host
    /// with a different processor. Without one the leg runs only natively: an emulated run is a
    /// different leg, because neither its timings nor its failures compare with a native one.
    /// </summary>
    public string? Emulator { get; init; }

    /// <summary>A WSL distribution, by its name under <c>hosts.wsl</c>, that this leg runs on and nowhere else.</summary>
    public string? Wsl { get; init; }

    /// <summary>An ssh host, by its name under <c>hosts.ssh</c>, that this leg runs on and nowhere else.</summary>
    public string? Ssh { get; init; }

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
    /// Worktree to act on instead of the host's main tree, named by the same rules
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
}

/// <summary>One concrete test invocation.</summary>
public sealed class TestInvocation
{
    /// <summary>Test runner to invoke, such as <c>ctest</c>.</summary>
    public string? Runner { get; init; }

    /// <summary>Arguments passed to the runner.</summary>
    /// <remarks>
    /// May name the directories a leg runs against — <c>{buildDir}</c>, <c>{treeDir}</c>,
    /// <c>{harnessDir}</c> — which is how a project that builds out of source points its runner at
    /// the tests: the build directory is derived per leg from the processor, the toolchain and the
    /// configuration, so no tracked file can spell it.
    /// </remarks>
    public List<string>? Args { get; init; }

    /// <summary>
    /// Where the runner starts, under the leg's tree root, or absent the tree root itself. May name
    /// the same directories <see cref="Args"/> may.
    /// </summary>
    /// <remarks>
    /// Beside <see cref="Args"/> rather than instead of it, because runners disagree about which
    /// they want. ctest takes <c>--test-dir</c> and can be started anywhere; a runner that only ever
    /// looks at the directory it was started in has no such argument, and one started in the build
    /// directory would resolve a relative path in its own arguments against the wrong root.
    /// </remarks>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Argument introducing a test filter, such as <c>-R</c> for ctest or
    /// <c>--filter</c> for dotnet, so one <c>--filter</c> option works everywhere.
    /// </summary>
    public string? FilterArg { get; init; }

    /// <summary>
    /// Argument introducing a test exclusion, such as <c>-LE</c> for ctest, so one <c>--exclude</c>
    /// option works everywhere. Declared per leg as well as per project, because a leg reached
    /// through a transport legitimately runs a narrower suite than one running here: a guard that
    /// checks this checkout has nothing to say about a host's copy of it.
    /// </summary>
    public string? ExcludeArg { get; init; }

    /// <summary>
    /// Cores this invocation uses, replacing both the host's <c>testCores</c> and
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
    /// tests - the same project, and the same <see cref="TestSet"/> - are compared, and one that ran
    /// a different number is marked on its line: a platform that quietly skips a group of tests
    /// passes on less evidence than its siblings.
    /// </summary>
    public string? CountPattern { get; init; }

    /// <summary>
    /// Which of the project's test sets this invocation runs, where its tests differ from the rest
    /// on purpose - a platform's own tests, a sanitizer leg's subset. A leg's test count is compared
    /// only with the other legs of its project naming the same set; left out, the leg runs the
    /// project's shared set.
    /// </summary>
    public string? TestSet { get; init; }
}
