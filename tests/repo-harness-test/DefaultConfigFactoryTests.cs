using System.Globalization;
using System.Text.RegularExpressions;
using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Projects;

namespace RepoHarness.Tests;

/// <summary>What <c>init</c> seeds, which is the starting point every user edits from.</summary>
public sealed class DefaultConfigFactoryTests
{
    private const string Os = "linux";

    private const string Processor = "x86_64";

    [Fact]
    public void ACmakeProject_GetsToolchains_ASanitizer_AndLegsForBothConfigurations()
    {
        var config = Create(new DetectedProject("cmake", ".", "CMakeLists.txt"));

        Assert.Equal(["clang", "gcc", "msvc"], config.Toolchains.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["windows"], config.Toolchains["msvc"].Platforms);

        // Each names its compiler, as every toolchain must: msvc's is cl, for both languages.
        Assert.Equal("cl", config.Toolchains["msvc"].Env["CC"]);
        Assert.Equal("cl", config.Toolchains["msvc"].Env["CXX"]);
        Assert.All(config.Toolchains.Values, toolchain => Assert.True(CompilerValue.Named(toolchain.CacheVars, toolchain.Env)));

        // cl is reached through the environment Visual Studio sets up where the leg runs, so a clone
        // builds from a plain shell; no other toolchain needs one.
        Assert.Equal("visualStudio", config.Toolchains["msvc"].DeveloperEnvironment);
        Assert.Equal(DeveloperEnvironmentKinds.VisualStudio, Assert.Single(config.DeveloperEnvironments, pair => pair.Key == "visualStudio").Value.Kind);
        Assert.Null(config.Toolchains["gcc"].DeveloperEnvironment);
        Assert.Null(config.Toolchains["clang"].DeveloperEnvironment);
        Assert.Contains("asan", config.Sanitizers.Keys);

        var project = Assert.Single(config.Projects);
        Assert.Equal("cmake", project.Type);
        Assert.Equal(project.Name, config.Defaults.Project);
        Assert.Equal("msvc", project.DefaultToolchain["windows"]);
        Assert.Equal("gcc", project.DefaultToolchain["linux"]);
        Assert.Equal("clang", project.DefaultToolchain["macos"]);
        Assert.Equal("ctest", project.Test?.All?.Runner);

        // A test is chosen by name with -R, and by the label ctest groups it under with -L; a label is
        // left out with -LE, as the help for all three says, and several joined into one, which ctest
        // otherwise reads as leaving out only a test that carries every one.
        Assert.Equal("-R", project.Test?.All?.FilterArg);
        Assert.Equal("-L", project.Test?.All?.LabelArg);
        Assert.Equal("-LE", project.Test?.All?.ExcludeArg);
        Assert.Equal("|", project.Test?.All?.ExcludeJoin);

        Assert.Equal(["linux-x86_64-debug", "linux-x86_64-release"], config.Legs.Keys.Order(StringComparer.Ordinal));
        Assert.All(config.Legs.Values, leg =>
        {
            Assert.Equal(project.Name, leg.Project);
            Assert.Equal(Os, leg.Os);
            Assert.Equal(Processor, leg.Processor);
            Assert.Null(leg.Emulator);
            Assert.Null(leg.Wsl);
            Assert.Null(leg.Ssh);
        });
        Assert.Equal(["linux-x86_64-debug", "linux-x86_64-release"], config.LegSets["gate"]);
    }

    [Fact]
    public void SeededLegs_AreForTheMachineInitRanOn()
    {
        var config = DefaultConfigFactory.Create([new DetectedProject("dotnet", "App.slnx", "App.slnx")], "windows", "arm64");

        Assert.Equal(["windows-arm64-debug", "windows-arm64-release"], config.Legs.Keys.Order(StringComparer.Ordinal));
        Assert.All(config.Legs.Values, leg =>
        {
            Assert.Equal("windows", leg.Os);
            Assert.Equal("arm64", leg.Processor);
        });
    }

    [Fact]
    public void ADotnetProject_NeedsNoToolchain_AndPointsAtItsSolution()
    {
        // dotnet resolves its own compiler, so a toolchain axis would only be noise.
        var config = Create(new DetectedProject("dotnet", "App.slnx", "App.slnx"));

        Assert.Empty(config.Toolchains);
        Assert.Empty(config.Sanitizers);

        var project = Assert.Single(config.Projects);
        Assert.Equal("App.slnx", project.Path);
        Assert.Empty(project.DefaultToolchain);
        Assert.Equal("dotnet", project.Test?.All?.Runner);
        Assert.Equal("--filter", project.Test?.All?.FilterArg);
    }

    [Fact]
    public void ADartProject_RunsDartTest()
    {
        var config = Create(new DetectedProject("dart", ".", "pubspec.yaml"));

        Assert.Equal("dart", Assert.Single(config.Projects).Test?.All?.Runner);
    }

    [Theory]
    [InlineData("100% tests passed, 0 tests failed out of 12", "cmake", 12)]
    [InlineData("Test run summary: Passed!\n  total: 259\n  failed: 0\n  succeeded: 257", "dotnet", 259)]
    [InlineData("Passed!  - Failed:     0, Passed:    42, Skipped:     0, Total:    42, Duration: 1 s", "dotnet", 42)]
    [InlineData("00:02 +42 ~1: All tests passed!", "dart", 42)]
    public void SeededPatterns_MatchTheirRunnersSummaries(string summary, string projectType, int total)
    {
        // A seeded pattern that never matches would report every healthy run as unwitnessed.
        var invocation = Assert.Single(Create(new DetectedProject(projectType, ".", "marker")).Projects).Test?.All;

        Assert.NotNull(invocation);
        Assert.Matches(invocation.SuccessPattern!, summary);

        var count = Regex.Match(summary, invocation.CountPattern!);
        Assert.True(count.Success, $"'{invocation.CountPattern}' found no count in: {summary}");
        Assert.Equal(total.ToString(CultureInfo.InvariantCulture), count.Groups["total"].Value);
    }

    [Fact]
    public void SeededTestRunners_ReceiveTheirCoreCount()
    {
        var ctest = Create(new DetectedProject("cmake", ".", "CMakeLists.txt")).Projects[0].Test?.All;
        var dart = Create(new DetectedProject("dart", ".", "pubspec.yaml")).Projects[0].Test?.All;

        Assert.Equal(["CTEST_PARALLEL_LEVEL"], ctest?.CoresEnv);
        Assert.Equal(["--concurrency={cores}"], dart?.CoresArgs);
    }

    [Fact]
    public void OnlyTheFirstDetectedProject_IsDeclared()
    {
        // Detection order is the priority order; the rest are for the user to add.
        var config = DefaultConfigFactory.Create(
            [
                new DetectedProject("cmake", ".", "CMakeLists.txt"),
                new DetectedProject("dotnet", "App.sln", "App.sln"),
            ],
            Os,
            Processor);

        Assert.Equal("cmake", Assert.Single(config.Projects).Type);
    }

    [Fact]
    public void NothingDetected_StillYieldsConfigurations_ButNoLegs()
    {
        var config = DefaultConfigFactory.Create([], Os, Processor);

        Assert.Null(config.Defaults.Project);
        Assert.Empty(config.Projects);
        Assert.Empty(config.Legs);
        Assert.Empty(config.LegSets);
        Assert.Empty(config.Hosts.Wsl);
        Assert.Empty(config.Hosts.Ssh);
        Assert.Equal(["debug", "release"], config.BuildConfigs.Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("cmake")]
    [InlineData("dotnet")]
    [InlineData("dart")]
    [InlineData(null)]
    public void TheSeed_PassesValidation_AndSurvivesTheFileUnchanged(string? projectType)
    {
        // init must never write a configuration the tool then refuses to read.
        IReadOnlyList<DetectedProject> detected = projectType is null
            ? []
            : [new DetectedProject(projectType, ".", "marker")];

        var config = DefaultConfigFactory.Create(detected, Os, Processor);
        Assert.Empty(HarnessConfigValidator.Validate(config));

        using var temp = new TempDirectory();
        var store = new JsonConfigStore(new PhysicalFileSystem(FilePermissionsFactory.Create()));
        var path = temp.Combine("config.json");

        store.Save(path, config);
        var loaded = store.Load(path);

        Assert.Equal(store.Serialize(config), store.Serialize(loaded));
    }

    private static HarnessConfig Create(DetectedProject project) => DefaultConfigFactory.Create([project], Os, Processor);
}
