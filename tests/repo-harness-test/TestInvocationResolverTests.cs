using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Results;
using RepoHarness.Core.Testing;

namespace RepoHarness.Tests;

/// <summary>
/// What a leg runs is decided in three layers, and the two that matter most are the ones a
/// configuration can leave out: a run with no runner does nothing, and a run with no success
/// pattern reports a zero exit code as a pass. Both are refused when the file is read, and the
/// core count is handed over by one implementation rather than two.
/// </summary>
public sealed class TestInvocationResolverTests
{
    [Fact]
    public void AMergedInvocationWithNoSuccessPattern_IsRefused()
    {
        var settings = new TestConfig { All = new TestInvocation { Runner = "ctest" } };

        var refusal = Assert.Throws<HarnessException>(
            () => TestInvocationResolver.Resolve(settings, PlatformNames.Linux));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Contains("successPattern", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APlatformSectionThatRemovesNothing_StillNeedsTheSuccessPatternFromAll()
    {
        // The platform section names a runner and nothing else, so the pattern has to come from
        // 'all'. A merge that took the platform section whole would silently drop it.
        var settings = new TestConfig
        {
            All = new TestInvocation { Runner = "ctest", SuccessPattern = "tests passed" },
            Windows = new TestInvocation { Runner = "ctest.exe" },
        };

        var invocation = TestInvocationResolver.Resolve(settings, PlatformNames.Windows);

        Assert.Equal("ctest.exe", invocation.Runner);
        Assert.Equal("tests passed", invocation.SuccessPattern);
    }

    [Fact]
    public void AMergedInvocationWithNoRunner_IsRefused()
    {
        var settings = new TestConfig { All = new TestInvocation { SuccessPattern = "tests passed" } };

        var refusal = Assert.Throws<HarnessException>(
            () => TestInvocationResolver.Resolve(settings, PlatformNames.MacOs));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Contains("no runner", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALegsOwnTestSettings_ReplaceTheProjects()
    {
        var project = new ProjectConfig
        {
            Name = "core",
            Type = "cmake",
            Test = new TestConfig { All = new TestInvocation { Runner = "ctest", SuccessPattern = "tests passed" } },
        };

        var leg = new LegConfig
        {
            Os = PlatformNames.Linux,
            Processor = PlatformNames.Arm64,
            Config = "release",

            // A leg reached through a transport legitimately runs a narrower suite than one running
            // here, so its section replaces the project's rather than merging with it: a half
            // inherited runner is how a leg ends up running the project's suite with its own
            // arguments.
            Test = new TestConfig
            {
                All = new TestInvocation { Runner = "ctest", Args = ["-L", "smoke"], SuccessPattern = "tests passed" },
            },
        };

        var settings = TestInvocationResolver.SettingsFor(new HarnessConfig(), leg, project);

        Assert.NotNull(settings);
        Assert.Equal(["-L", "smoke"], TestInvocationResolver.Resolve(settings, PlatformNames.Linux).Args);
    }

    /// <summary>
    /// A test set is merged per platform like every other setting: a platform's own names its set,
    /// and one naming none runs the set <c>all</c> names, or the project's shared one.
    /// </summary>
    [Fact]
    public void ATestSet_IsMergedPerPlatform_LikeEveryOtherSetting()
    {
        var settings = new TestConfig
        {
            All = new TestInvocation { Runner = "ctest", SuccessPattern = "tests passed" },
            Windows = new TestInvocation { TestSet = "windows" },
        };

        var named = new TestConfig
        {
            All = new TestInvocation { Runner = "ctest", SuccessPattern = "tests passed", TestSet = "sanitized" },
            Windows = new TestInvocation { TestSet = "windows" },
        };

        Assert.Equal("windows", TestInvocationResolver.Resolve(settings, PlatformNames.Windows).TestSet);
        Assert.Null(TestInvocationResolver.Resolve(settings, PlatformNames.Linux).TestSet);
        Assert.Equal("sanitized", TestInvocationResolver.Resolve(named, PlatformNames.Linux).TestSet);
        Assert.Equal("windows", TestInvocationResolver.Resolve(named, PlatformNames.Windows).TestSet);
    }

    [Fact]
    public void AProjectsTestSettings_ApplyWhenTheLegDeclaresNone()
    {
        var project = new ProjectConfig
        {
            Name = "core",
            Type = "cmake",
            Test = new TestConfig { All = new TestInvocation { Runner = "ctest", SuccessPattern = "tests passed" } },
        };

        var leg = new LegConfig { Os = PlatformNames.Linux, Processor = PlatformNames.X64, Config = "release" };

        var settings = TestInvocationResolver.SettingsFor(new HarnessConfig(), leg, project);

        Assert.Same(project.Test, settings);
    }

    [Theory]
    [InlineData("-j", "8")]
    [InlineData("-j8", null)]
    [InlineData("-j=8", null)]
    public void AnExplicitCoreOptionInArgs_BeatsCoresArgs(string option, string? value)
    {
        string[] args = value is null ? [option] : [option, value];

        var command = TestInvocationResolver.CommandFor(
            Invocation(args: args, coresArgs: ["-j", "{cores}"]),
            cores: 6,
            filter: null,
            excludes: null);

        // Nothing is spliced in. Two '-j' options would leave the runner to pick one, and the
        // report would say which count was asked for rather than which one ran.
        Assert.Equal(args, command.Arguments);
    }

    [Fact]
    public void CoresArgs_CarryTheCount_WhenTheArgumentsDoNotSetIt()
    {
        var command = TestInvocationResolver.CommandFor(
            Invocation(args: ["--output-on-failure"], coresArgs: ["-j", "{cores}"]),
            cores: 4,
            filter: null,
            excludes: null);

        Assert.Equal(["--output-on-failure", "-j", "4"], command.Arguments);
    }

    [Fact]
    public void CoresEnv_CarriesTheCount_AlongsideTheInvocationsOwnEnvironment()
    {
        var command = TestInvocationResolver.CommandFor(
            Invocation(
                env: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["CTEST_OUTPUT_ON_FAILURE"] = "1" },
                coresEnv: ["CTEST_PARALLEL_LEVEL"]),
            cores: 3,
            filter: null,
            excludes: null);

        Assert.Equal("3", command.Environment["CTEST_PARALLEL_LEVEL"]);
        Assert.Equal("1", command.Environment["CTEST_OUTPUT_ON_FAILURE"]);
    }

    [Fact]
    public void AFilterAndAnExclusion_AreSplicedInWithTheInvocationsOwnArguments()
    {
        var command = TestInvocationResolver.CommandFor(
            Invocation(filterArg: "-R", excludeArg: "-LE"),
            cores: 6,
            filter: "parser",
            excludes: ["slow", "flaky"]);

        Assert.Equal(["-R", "parser", "-LE", "slow", "-LE", "flaky"], command.Arguments);
    }

    [Fact]
    public void AFilterWithNoFilterArg_IsRefused()
    {
        var refusal = Assert.Throws<HarnessException>(
            () => TestInvocationResolver.CommandFor(Invocation(), cores: 6, filter: "parser", excludes: null));

        Assert.Equal(HarnessExit.UsageError, refusal.ExitCode);
        Assert.Contains("filterArg", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExclusionWithNoExcludeArg_IsRefused()
    {
        var refusal = Assert.Throws<HarnessException>(
            () => TestInvocationResolver.CommandFor(Invocation(), cores: 6, filter: null, excludes: ["slow"]));

        Assert.Equal(HarnessExit.UsageError, refusal.ExitCode);
        Assert.Contains("excludeArg", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A project that builds out of source keeps its tests in a directory this tool chooses:
    /// build/&lt;processor&gt;-&lt;toolchain&gt;-&lt;config&gt;, which differs per leg and so cannot be written
    /// down. Measured on a consumer's tree: ctest started at the tree root reported "No tests were
    /// found!!!" and exited 8 in under a fifth of a second, in a tree holding 2204 tests.
    /// </summary>
    [Fact]
    public void AnArgumentNamingTheBuildDirectory_IsHandedTheOneThisLegBuildsIn()
    {
        var command = TestInvocationResolver.CommandFor(
            Invocation(args: ["--test-dir", "{buildDir}", "--no-tests=error"]),
            cores: 6,
            filter: null,
            excludes: null,
            new LegPaths("/tree", "/tree/build/x86_64-gcc-debug"));

        Assert.Equal(["--test-dir", "/tree/build/x86_64-gcc-debug", "--no-tests=error"], command.Arguments);
    }

    /// <summary>
    /// The other half of the same need: a runner with no such argument, which only ever looks at
    /// the directory it was started in.
    /// </summary>
    [Fact]
    public void AnInvocationNamingAWorkingDirectory_StartsThere_AndOneNamingNoneKeepsTheTreeRoot()
    {
        var paths = new LegPaths("/tree", "/tree/build/x86_64-gcc-debug");

        var declared = TestInvocationResolver.CommandFor(
            Invocation(workingDirectory: "{buildDir}"), cores: 6, filter: null, excludes: null, paths);

        Assert.Equal("/tree/build/x86_64-gcc-debug", declared.WorkingDirectory);

        // Null rather than the tree root, so the caller keeps the one it already had: a test phase
        // written before this key existed ran at the tree root and still does.
        var silent = TestInvocationResolver.CommandFor(
            Invocation(), cores: 6, filter: null, excludes: null, paths);

        Assert.Null(silent.WorkingDirectory);
    }

    /// <summary>
    /// A relative working directory means what every other path in the configuration means, which
    /// is somewhere under the tree.
    /// </summary>
    [Fact]
    public void ARelativeWorkingDirectory_IsUnderTheTree()
    {
        var command = TestInvocationResolver.CommandFor(
            Invocation(workingDirectory: "tests/integration"),
            cores: 6,
            filter: null,
            excludes: null,
            new LegPaths(Path.Combine("/tree"), "/tree/build/x86_64-gcc-debug"));

        Assert.Equal(Path.Combine("/tree", "tests/integration"), command.WorkingDirectory);
    }

    /// <summary>
    /// Expanded after the filter and the exclusions are spliced in, so every argument the runner
    /// sees has been through one rule. Expanded before them, an argument that arrived from --filter
    /// could name a directory an argument from the configuration could not.
    /// </summary>
    [Fact]
    public void EveryArgumentGoesThroughOneRule_WhereverItCameFrom()
    {
        var command = TestInvocationResolver.CommandFor(
            Invocation(args: ["{buildDir}"], filterArg: "-R", coresArgs: ["-j", "{cores}"]),
            cores: 6,
            filter: "{treeDir}",
            excludes: null,
            new LegPaths("/tree", "/tree/build/x86_64-gcc-debug"));

        // The core count is still the core count: two vocabularies, neither eating the other.
        Assert.Equal(["/tree/build/x86_64-gcc-debug", "-R", "/tree", "-j", "6"], command.Arguments);
    }

    /// <summary>
    /// With no leg in hand nothing is expanded, so a caller that only wants to see the command line
    /// gets it as written rather than a half-resolved one.
    /// </summary>
    [Fact]
    public void WithNoLegInHand_APlaceholderIsLeftAsWritten()
    {
        var command = TestInvocationResolver.CommandFor(
            Invocation(args: ["--test-dir", "{buildDir}"]), cores: 6, filter: null, excludes: null);

        Assert.Equal(["--test-dir", "{buildDir}"], command.Arguments);
    }

    private static ResolvedTestInvocation Invocation(
        IReadOnlyList<string>? args = null,
        string? filterArg = null,
        string? excludeArg = null,
        IReadOnlyList<string>? coresArgs = null,
        IReadOnlyList<string>? coresEnv = null,
        IReadOnlyDictionary<string, string>? env = null,
        string? workingDirectory = null)
        => new(
            "ctest",
            args ?? [],
            filterArg,
            excludeArg,
            Cores: null,
            coresArgs ?? [],
            coresEnv ?? [],
            env ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            "tests passed",
            CountPattern: null,
            workingDirectory);
}
