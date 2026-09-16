using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Processes;

namespace RepoHarness.Tests;

/// <summary>
/// A build passes only on evidence that it produced something. Each test here answers a way a build
/// was measured reporting success having produced nothing, after which the tests run against
/// whatever the previous build left behind.
/// </summary>
public sealed class BuildServiceTests
{
    private const string Leg = "win-msvc-release";

    [Fact]
    public async Task AProjectDeclaringNoBuildOutputs_IsUnwitnessed_RatherThanPassedOnItsExitCode()
    {
        // An empty list makes the witness vacuously true rather than absent, which reads as a check
        // that passed when it is a check nobody performed.
        using var temp = new TempDirectory();

        var result = await Service(new HarnessFactory(), exitCode: 0).BuildAsync(
            Config(),
            Request(temp, outputs: []),
            TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.Unwitnessed, result.Verdict.Verdict);
        Assert.Equal(LegExit.Unwitnessed, Verdicts.ExitCodeFor(result.Verdict.Verdict));
        Assert.Contains("declares no buildOutputs", result.Verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABuildThatExitedZeroAndProducedNothingItDeclared_IsUnwitnessed()
    {
        // The result that hands the tests a stale binary: the build system had nothing to do, said
        // so with a zero exit code, and the objects on disk are the previous build's.
        using var temp = new TempDirectory();

        var result = await Service(new HarnessFactory(), exitCode: 0).BuildAsync(
            Config(),
            Request(temp, outputs: ["bin/app.dll"]),
            TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.Unwitnessed, result.Verdict.Verdict);
        Assert.Contains("bin/app.dll", result.Verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABuildThatProducedWhatItDeclared_Passes()
    {
        using var temp = new TempDirectory();
        var request = Request(temp, outputs: ["bin/app.dll"]);

        // Written where the build would have put it, so the witness has something to find.
        var produced = Path.Combine(request.Variant.DirectoryUnder(temp.Path), "bin", "app.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(produced)!);
        await File.WriteAllTextAsync(produced, "built", TestContext.Current.CancellationToken);

        var result = await Service(new HarnessFactory(), exitCode: 0).BuildAsync(
            Config(),
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.Passed, result.Verdict.Verdict);
    }

    [Fact]
    public async Task ABuildThatFailed_KeepsItsOwnVerdict_AndIsNeverAskedForOutputs()
    {
        using var temp = new TempDirectory();

        var result = await Service(new HarnessFactory(), exitCode: 2).BuildAsync(
            Config(),
            Request(temp, outputs: []),
            TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.Failed, result.Verdict.Verdict);
    }

    private static BuildService Service(HarnessFactory factory, int exitCode)
        => new(
            new PhaseRunner(new QuietRunner(exitCode), factory.FileSystem, factory.Output),
            new BuildDirectoryGuard(factory.FileSystem, factory.Platform),
            new NinjaDependencyCheck(new QuietRunner(exitCode), factory.FileSystem),
            new InputFingerprint(factory.FileSystem, factory.Platform),
            factory.GitClient,
            factory.FileSystem,
            factory.Output);

    private static HarnessConfig Config() => new()
    {
        Defaults = new HarnessDefaults { StallSeconds = 0 },
    };

    /// <summary>
    /// The same target is not the same file everywhere, and a witness had to be able to say so:
    /// with one flat path per entry, a leg set spanning Windows and POSIX could not name a program
    /// that existed on both, so no build on any of those legs could be witnessed at all.
    /// </summary>
    [Theory]
    [InlineData("windows", "bin/app.exe")]
    [InlineData("linux", "bin/app")]
    [InlineData("macos", "bin/app")]
    public async Task AKeyedOutput_IsLookedForUnderThePlatformTheBuildRanOn(string platformKey, string expected)
    {
        using var temp = new TempDirectory();

        var result = await Service(new HarnessFactory(), exitCode: 0).BuildAsync(
            Config(),
            Request(
                temp,
                [Keyed(("windows", "bin/app.exe"), ("all", "bin/app"))],
                platformKey),
            TestContext.Current.CancellationToken);

        // Nothing was produced, so the refusal names the path it looked for — which is the point:
        // the reader has to be able to see which platform's spelling was checked.
        Assert.Equal(LegVerdict.Unwitnessed, result.Verdict.Verdict);
        Assert.Contains(expected, result.Verdict.Detail, StringComparison.Ordinal);

        // And only that platform's spelling: naming both would leave the reader to work out which
        // of them this leg was actually missing.
        Assert.DoesNotContain(
            platformKey == "windows" ? "bin/app\"" : "bin/app.exe",
            result.Verdict.Detail,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task APlainOutput_StillAppliesOnEveryPlatform()
    {
        using var temp = new TempDirectory();
        var build = temp.Combine("build", "x86_64-msvc-release");
        Directory.CreateDirectory(build);
        File.WriteAllText(Path.Combine(build, "compile_commands.json"), "[]");

        var result = await Service(new HarnessFactory(), exitCode: 0).BuildAsync(
            Config(),
            Request(temp, [(BuildOutput)"compile_commands.json"], "macos"),
            TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.Passed, result.Verdict.Verdict);
    }

    [Fact]
    public async Task AKeyedOutput_IsWitnessed_WhenThePlatformsOwnFileIsThere()
    {
        using var temp = new TempDirectory();
        var build = temp.Combine("build", "x86_64-msvc-release", "bin");
        Directory.CreateDirectory(build);
        File.WriteAllText(Path.Combine(build, "app.exe"), "program");

        var result = await Service(new HarnessFactory(), exitCode: 0).BuildAsync(
            Config(),
            Request(temp, [Keyed(("windows", "bin/app.exe"), ("all", "bin/app"))], "windows"),
            TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.Passed, result.Verdict.Verdict);
    }

    private static BuildRequest Request(TempDirectory temp, IReadOnlyList<string> outputs)
        => Request(temp, [.. outputs.Select(output => (BuildOutput)output)], "windows");

    private static BuildRequest Request(TempDirectory temp, IReadOnlyList<BuildOutput> outputs, string platformKey)
        => new(
            Leg,
            temp.Path,
            new ProjectConfig
            {
                Name = "app",
                Type = "dotnet",
                Path = "src/app",
                BuildOutputs = [.. outputs],
            },
            new VariantKey("x86_64", "msvc", "release", null),
            platformKey,
            Cores: 2,
            RunDirectory: temp.Combine(".harness-config", "runs", "20260916-100000-0a1b2c3d"));

    /// <summary>An entry naming one path per platform.</summary>
    private static BuildOutput Keyed(params (string Platform, string Path)[] paths) => new()
    {
        Paths = paths.ToDictionary(entry => entry.Platform, entry => entry.Path, StringComparer.OrdinalIgnoreCase),
    };

    /// <summary>A runner that starts nothing, prints nothing, and exits as it was told to.</summary>
    private sealed class QuietRunner(int exitCode) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ProcessResult(exitCode, string.Empty, string.Empty, TimeSpan.Zero, TimedOut: false));

        public string? FindExecutable(string command) => command;
    }
}
