using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;

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

        var result = await (await TrackedAsync(temp, TestContext.Current.CancellationToken)).BuildAsync(
            Config(),
            Request(temp, outputs: []),
            TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.Unwitnessed, result.Verdict.Verdict);
        Assert.Equal(LegExit.Unwitnessed, Verdicts.ExitCodeFor(result.Verdict.Verdict));
        Assert.Contains("declares no buildOutputs", result.Verdict.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// worktrees.pathBudgetReserve is a number measured once, against whatever the build produced
    /// then. Every build measures what it actually left below its build directory and says so, with
    /// both numbers, when that went deeper - and says nothing when it did not, at the boundary too.
    /// </summary>
    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    public async Task ABuildDeeperThanTheReserve_SaysSo_WithBothNumbers(int reserveAgainstDeepest, bool warned)
    {
        using var temp = new TempDirectory();
        var request = Request(temp, outputs: ["bin/app.dll"]);
        var directory = request.Variant.DirectoryUnder(temp.Path);
        var deepest = Path.Combine("obj", "nested", "deeper", "still", "file.obj");

        foreach (var file in new[] { Path.Combine("bin", "app.dll"), deepest })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(directory, file))!);
            await File.WriteAllTextAsync(Path.Combine(directory, file), "built", TestContext.Current.CancellationToken);
        }

        var (service, factory) = await TrackedWithFactoryAsync(temp, TestContext.Current.CancellationToken);
        var reserve = deepest.Length + reserveAgainstDeepest;

        await service.BuildAsync(
            new HarnessConfig
            {
                Defaults = new HarnessDefaults { StallSeconds = 0 },
                Worktrees = new WorktreeSettings { PathBudgetReserve = reserve },
            },
            request,
            TestContext.Current.CancellationToken);

        var said = factory.StandardError.ToString();

        Assert.Equal(warned, said.Contains("worktrees.pathBudgetReserve declares", StringComparison.Ordinal));

        if (warned)
        {
            Assert.Contains($"{deepest.Length} characters long", said, StringComparison.Ordinal);
            Assert.Contains($"declares {reserve}", said, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ABuildThatExitedZeroAndProducedNothingItDeclared_IsUnwitnessed()
    {
        // The result that hands the tests a stale binary: the build system had nothing to do, said
        // so with a zero exit code, and the objects on disk are the previous build's.
        using var temp = new TempDirectory();

        var result = await (await TrackedAsync(temp, TestContext.Current.CancellationToken)).BuildAsync(
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

        var result = await (await TrackedAsync(temp, TestContext.Current.CancellationToken)).BuildAsync(
            Config(),
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.Passed, result.Verdict.Verdict);
    }

    [Fact]
    public async Task ABuildThatFailed_KeepsItsOwnVerdict_AndIsNeverAskedForOutputs()
    {
        using var temp = new TempDirectory();

        var result = await (await TrackedAsync(temp, TestContext.Current.CancellationToken, exitCode: 2)).BuildAsync(
            Config(),
            Request(temp, outputs: []),
            TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.Failed, result.Verdict.Verdict);
    }

    /// <summary>
    /// The measurement that opened this: from a consumer's worktree, one markdown edit made two
    /// legs rebuild from clean, discarding a warm build directory that had cost eleven minutes.
    /// Neither named file is read by the build.
    /// </summary>
    [Fact]
    public async Task ADocumentationEdit_KeepsTheWarmBuildDirectory()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (factory, request) = await TrackedTreeAsync(temp, token);

        Assert.Equal(LegVerdict.Passed, (await BuildOnceAsync(factory, request, temp, token)).Verdict.Verdict);

        // Edited after the build and dated after what it produced, so only the content comparison
        // can have anything to say about it.
        await TouchAsync(temp, "docs/guide.md", "rewritten\n", token);

        var again = await BuildOnceAsync(factory, request, temp, token);

        Assert.Null(again.RebuiltFromClean);
    }

    /// <summary>
    /// The other half, which is the one that must never be lost: a file whose content differs from
    /// what this directory was built from is stale however its timestamp reads.
    /// </summary>
    [Theory]
    [InlineData("src/app.cpp")]
    [InlineData("VERSION")]
    public async Task AnEditToSomethingTheBuildReads_DiscardsIt_AndSaysWhichConditionFired(string edited)
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (factory, request) = await TrackedTreeAsync(temp, token);

        Assert.Equal(LegVerdict.Passed, (await BuildOnceAsync(factory, request, temp, token)).Verdict.Verdict);

        await TouchAsync(temp, edited, "changed\n", token);

        var again = await BuildOnceAsync(factory, request, temp, token);

        Assert.NotNull(again.RebuiltFromClean);
        Assert.Contains(edited, again.RebuiltFromClean, StringComparison.Ordinal);

        // Which of the three conditions fired, not only which file differed. A consumer measured
        // their file against the timestamp rule, found it did not hold, and could not tell from the
        // line whether the clock-step condition had fired instead.
        Assert.Contains("changed content:", again.RebuiltFromClean, StringComparison.Ordinal);
    }

    /// <summary>
    /// Declared and non-empty, the project's own list replaces the one its type would use. Empty is
    /// "say nothing", not "match nothing": a list matching nothing compares equal every time, which
    /// is exactly the answer that keeps a stale binary.
    /// </summary>
    [Theory]
    [InlineData(new string[0], false)]
    [InlineData(new[] { ".md" }, true)]
    public async Task RebuildableFormats_ReplaceTheLanguageSet_OnlyWhenTheySaySomething(
        string[] formats,
        bool rebuilds)
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (factory, request) = await TrackedTreeAsync(temp, token, formats);

        Assert.Equal(LegVerdict.Passed, (await BuildOnceAsync(factory, request, temp, token)).Verdict.Verdict);

        await TouchAsync(temp, "docs/guide.md", "rewritten\n", token);

        var again = await BuildOnceAsync(factory, request, temp, token);

        Assert.Equal(rebuilds, again.RebuiltFromClean is not null);
    }

    /// <summary>
    /// A tree git tracks, holding one source, one document and one extensionless file a build
    /// reads, with a cmake project built out of source.
    /// </summary>
    private static async Task<(HarnessFactory Factory, BuildRequest Request)> TrackedTreeAsync(
        TempDirectory temp,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? formats = null)
    {
        var factory = new HarnessFactory();

        Directory.CreateDirectory(temp.Combine("src"));
        Directory.CreateDirectory(temp.Combine("docs"));
        await File.WriteAllTextAsync(temp.Combine("src", "app.cpp"), "int main(){}\n", cancellationToken);
        await File.WriteAllTextAsync(temp.Combine("docs", "guide.md"), "how to\n", cancellationToken);
        await File.WriteAllTextAsync(temp.Combine("VERSION"), "1.0.0\n", cancellationToken);

        await factory.InitializeHarnessAsync(temp.Path, cancellationToken, new HarnessConfig());
        await factory.CommitAllAsync(temp.Path, "initial", cancellationToken);

        var request = new BuildRequest(
            Leg,
            temp.Path,
            new ProjectConfig
            {
                Name = "app",
                Type = "cmake",
                Path = ".",
                BuildOutputs = [(BuildOutput)"bin/app"],
                RebuildableFormats = [.. formats ?? []],
            },
            new VariantKey("x86_64", "gcc", "debug", null),
            PlatformNames.Linux,
            Cores: 2,
            RunDirectory: temp.Combine(".harness-config", "runs", "20260916-100000-0a1b2c3d"));

        return (factory, request);
    }

    /// <summary>
    /// Builds once with the output present, so the build is witnessed and records what it was built
    /// from, then dates every tracked file well before that output.
    /// </summary>
    /// <remarks>
    /// The dates are the subject of a rule of their own: a changed input that is not newer than the
    /// newest output is read as a stepped clock. Left at the times a test writes them, every file
    /// in the tree sits inside that window and the timestamp condition fires before the one under
    /// test can.
    /// </remarks>
    private static async Task<BuildResult> BuildOnceAsync(
        HarnessFactory factory,
        BuildRequest request,
        TempDirectory temp,
        CancellationToken cancellationToken)
    {
        var buildDirectory = request.Variant.DirectoryUnder(temp.Path);
        var produced = Path.Combine(buildDirectory, "bin", "app");

        Directory.CreateDirectory(Path.GetDirectoryName(produced)!);
        await File.WriteAllTextAsync(produced, "built", cancellationToken);

        var result = await Service(factory, exitCode: 0).BuildAsync(Config(), request, cancellationToken);
        var old = DateTime.UtcNow.AddHours(-1);

        foreach (var file in Directory.EnumerateFiles(temp.Path, "*", SearchOption.AllDirectories))
        {
            if (!file.StartsWith(buildDirectory, StringComparison.Ordinal))
            {
                File.SetLastWriteTimeUtc(file, old);
            }
        }

        return result;
    }

    /// <summary>Rewrites one tracked file and dates it after the build, so only content can matter.</summary>
    private static async Task TouchAsync(
        TempDirectory temp,
        string relativePath,
        string content,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(temp.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));

        await File.WriteAllTextAsync(path, content, cancellationToken);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(1));
    }

    /// <summary>
    /// A service whose tree git can be asked about. A build reads the tree while it runs, so a tree
    /// nothing can list is a build nobody watched, and the leg is unmeasured rather than passed —
    /// the same rule the test verb has always applied. Every build here needs a repository for that
    /// reason and not because these tests are about git.
    /// </summary>
    private static async Task<BuildService> TrackedAsync(TempDirectory temp, CancellationToken cancellationToken, int exitCode = 0)
        => (await TrackedWithFactoryAsync(temp, cancellationToken, exitCode)).Service;

    /// <summary>The same, with the factory, for a test that reads what the build said.</summary>
    private static async Task<(BuildService Service, HarnessFactory Factory)> TrackedWithFactoryAsync(
        TempDirectory temp,
        CancellationToken cancellationToken,
        int exitCode = 0)
    {
        var factory = new HarnessFactory();

        await factory.InitializeGitRepositoryAsync(temp.Path, cancellationToken);
        // A source the project's own type reads, so the guards are actually on in these tests: a
        // tree tracking nothing this build reads has nothing to watch and would exercise none of it.
        await File.WriteAllTextAsync(temp.Combine("src.cs"), "class App;" + Environment.NewLine, cancellationToken);
        await factory.CommitAllAsync(temp.Path, "initial", cancellationToken);

        return (Service(factory, exitCode), factory);
    }

    /// <summary>
    /// A compiler a survey found off the PATH is the one the build starts, from the directory
    /// appended to it, so that is the file the directory is held to: one configured with a gcc
    /// elsewhere is refused, though the names match.
    /// </summary>
    [Fact]
    public async Task ACompilerFoundInAProgramDirectory_IsTheOneTheDirectoryIsHeldTo()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var (factory, tracked) = await TrackedTreeAsync(temp, cancellationToken);

        temp.WriteProgram(ToolchainDirectory, "gcc");
        var elsewhere = temp.WriteProgram("elsewhere-bin", "gcc");
        Directory.CreateDirectory(temp.Combine("empty-path"));

        var request = tracked with
        {
            HostEnvironment = new Dictionary<string, string> { ["CC"] = "gcc", ["PATH"] = temp.Combine("empty-path") },
            ProgramDirectories = [temp.Combine(ToolchainDirectory)],
        };

        var buildDirectory = request.Variant.DirectoryUnder(temp.Path);
        Directory.CreateDirectory(buildDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(buildDirectory, BuildDirectoryGuard.CMakeCacheFileName),
            $"CMAKE_HOME_DIRECTORY:INTERNAL={temp.Path.Replace('\\', '/')}\nCMAKE_C_COMPILER:FILEPATH={elsewhere.Replace('\\', '/')}\n",
            cancellationToken);

        var refusal = await Assert.ThrowsAsync<HarnessException>(
            () => Service(factory, exitCode: 0).BuildAsync(Config(), request, cancellationToken));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Contains($"which starts '{temp.Combine(ToolchainDirectory, OperatingSystem.IsWindows() ? "gcc.exe" : "gcc")}' now", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>Where a test puts the compilers its build's PATH names, outside anything the build reads.</summary>
    private const string ToolchainDirectory = "toolchain-bin";

    private static BuildService Service(HarnessFactory factory, int exitCode, IProcessRunner? dependencies = null, IProcessRunner? phases = null)
        => new(
            new PhaseRunner(phases ?? new QuietRunner(exitCode), factory.FileSystem, factory.Output),
            new BuildDirectoryGuard(factory.FileSystem, factory.Platform, factory.FilePermissions),
            new NinjaDependencyCheck(dependencies ?? new QuietRunner(exitCode), factory.FileSystem),
            new InputFingerprint(factory.FileSystem, factory.Platform),
            new ProcessSampler(factory.ProcessTable, factory.Platform, factory.Output),
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
    [InlineData("linux", "bin/posix-app")]
    [InlineData("macos", "bin/posix-app")]
    public async Task AKeyedOutput_IsLookedForUnderThePlatformTheBuildRanOn(string platformKey, string expected)
    {
        using var temp = new TempDirectory();

        var result = await (await TrackedAsync(temp, TestContext.Current.CancellationToken)).BuildAsync(
            Config(),
            Request(
                temp,
                [Keyed(("windows", "bin/app.exe"), ("all", "bin/posix-app"))],
                platformKey),
            TestContext.Current.CancellationToken);

        // Nothing was produced, so the refusal names the path it looked for — which is the point:
        // the reader has to be able to see which platform's spelling was checked.
        Assert.Equal(LegVerdict.Unwitnessed, result.Verdict.Verdict);
        Assert.Contains(expected, result.Verdict.Detail, StringComparison.Ordinal);

        // And only that platform's spelling: naming both would leave the reader to work out which
        // of them this leg was actually missing. The two stems differ so that this discriminates on
        // every row — 'bin/app' is a prefix of 'bin/app.exe', so it never could.
        Assert.DoesNotContain(
            platformKey == "windows" ? "bin/posix-app" : "bin/app.exe",
            result.Verdict.Detail,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// An output that resolves to nothing here is dropped from the list this build is held to. All
    /// of them resolving to nothing passes having looked for no file at all; some of them doing so
    /// passes having checked part of the evidence the file declares, with nothing saying which part
    /// went unchecked. Two things stop a configuration reaching either, and this refuses both
    /// anyway: a green build nobody witnessed is what the whole mechanism exists to prevent.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnOutputNamingNoPathForThisPlatform_IsUnwitnessed_NotSilentlyDropped(bool alsoDeclaresOneThatResolves)
    {
        using var temp = new TempDirectory();

        // The second entry resolves and is found, so only the unresolved one can decide this.
        var build = temp.Combine("build", "x86_64-msvc-release");
        Directory.CreateDirectory(build);
        File.WriteAllText(Path.Combine(build, "compile_commands.json"), "[]");

        BuildOutput[] outputs = alsoDeclaresOneThatResolves
            ? [Keyed(("windows", "bin/app.exe")), (BuildOutput)"compile_commands.json"]
            : [Keyed(("windows", "bin/app.exe"))];

        var result = await (await TrackedAsync(temp, TestContext.Current.CancellationToken)).BuildAsync(
            Config(),
            Request(temp, outputs, "linux"),
            TestContext.Current.CancellationToken);

        Assert.Equal(LegVerdict.Unwitnessed, result.Verdict.Verdict);
        Assert.Contains("naming no path for 'linux'", result.Verdict.Detail, StringComparison.Ordinal);

        // The entry that went unchecked is named, because which one it was is the first thing to ask.
        Assert.Contains("windows: bin/app.exe", result.Verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APlainOutput_StillAppliesOnEveryPlatform()
    {
        using var temp = new TempDirectory();
        var build = temp.Combine("build", "x86_64-msvc-release");
        Directory.CreateDirectory(build);
        File.WriteAllText(Path.Combine(build, "compile_commands.json"), "[]");

        var result = await (await TrackedAsync(temp, TestContext.Current.CancellationToken)).BuildAsync(
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

        var result = await (await TrackedAsync(temp, TestContext.Current.CancellationToken)).BuildAsync(
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
    private static BuildOutput Keyed(params (string Platform, string Path)[] paths)
        => BuildOutput.Keyed(paths.Select(entry => new KeyValuePair<string, string>(entry.Platform, entry.Path)));

    /// <summary>A runner that starts nothing, prints nothing, and exits as it was told to.</summary>
    /// <summary>
    /// The dependency records are read by the ninja the build ran, as its configuration recorded it:
    /// one only the build's own environment could find is found all the same, and reads the records
    /// the way it wrote them. Looked up by name instead, it was not there, and a green build failed.
    /// </summary>
    [Fact]
    public async Task TheDependencyRecords_AreReadByTheNinjaTheBuildRan()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var (factory, request) = await TrackedTreeAsync(temp, cancellationToken);
        var buildDirectory = request.Variant.DirectoryUnder(temp.Path);

        // Whole on the machine that ran the build, written the way CMake writes it.
        var recorded = temp.Combine("toolchain", "bin", "ninja").Replace('\\', '/');

        Directory.CreateDirectory(Path.Combine(buildDirectory, "bin"));
        await File.WriteAllTextAsync(Path.Combine(buildDirectory, "bin", "app"), "built", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(buildDirectory, NinjaDependencyCheck.ManifestFileName), string.Empty, cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(buildDirectory, BuildDirectoryGuard.CMakeCacheFileName),
            $"CMAKE_HOME_DIRECTORY:INTERNAL={temp.Path}\nCMAKE_MAKE_PROGRAM:FILEPATH={recorded}\n",
            cancellationToken);

        var dependencies = new RecordingRunner("app.o: #deps 1, deps mtime 1 (VALID)\n    app.h\n");

        _ = await Service(factory, exitCode: 0, dependencies).BuildAsync(Config(), request, cancellationToken);

        Assert.Equal(recorded, Assert.Single(dependencies.Started).FileName);
    }

    /// <summary>
    /// A host's own environment reaches every phase of the build and the ninja that reads its
    /// records, beneath the variant's: a name only the host sets is the host's, one the toolchain
    /// sets too is the toolchain's, and a compiler cache the host declares is keyed against the leg's
    /// own tree.
    /// </summary>
    [Fact]
    public async Task TheHostsEnvironment_ReachesEveryPhaseAndTheCheck_BeneathTheVariants()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var (factory, tracked) = await TrackedTreeAsync(temp, cancellationToken);
        var request = tracked with
        {
            HostEnvironment = new Dictionary<string, string>
            {
                ["RH_HOST"] = "host",
                ["RH_BOTH"] = "host",
                ["CCACHE_DIR"] = temp.Combine("cache"),
            },
        };
        var buildDirectory = request.Variant.DirectoryUnder(temp.Path);

        Directory.CreateDirectory(Path.Combine(buildDirectory, "bin"));
        await File.WriteAllTextAsync(Path.Combine(buildDirectory, "bin", "app"), "built", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(buildDirectory, NinjaDependencyCheck.ManifestFileName), string.Empty, cancellationToken);

        var config = Config();
        config.Toolchains["gcc"] = new ToolchainConfig { Platforms = [PlatformNames.Linux], Env = { ["RH_BOTH"] = "variant" } };

        var phases = new RecordingRunner(string.Empty);
        var dependencies = new RecordingRunner("app.o: #deps 1, deps mtime 1 (VALID)\n    app.h\n");

        _ = await Service(factory, exitCode: 0, dependencies, phases).BuildAsync(config, request, cancellationToken);

        Assert.NotEmpty(phases.Started);
        Assert.Single(dependencies.Started);

        foreach (var started in phases.Started.Concat(dependencies.Started))
        {
            Assert.Equal("host", started.Environment["RH_HOST"]);
            Assert.Equal("variant", started.Environment["RH_BOTH"]);
            Assert.Equal(temp.Path, started.Environment["CCACHE_BASEDIR"]);
        }
    }

    /// <summary>
    /// A compiler the host's env names is the one the build uses wherever the variant names none, so
    /// a directory CMake configured with another is refused, as it is for a variant's compiler -
    /// never reused with the compiler CMake cached, the leg passing on a compiler nobody chose.
    /// </summary>
    [Fact]
    public async Task ADirectoryConfiguredWithAnotherCompilerThanTheHostNames_IsRefused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var (factory, tracked) = await TrackedTreeAsync(temp, cancellationToken);
        var request = tracked with { HostEnvironment = new Dictionary<string, string> { ["CC"] = "clang-17" } };
        var buildDirectory = request.Variant.DirectoryUnder(temp.Path);

        Directory.CreateDirectory(buildDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(buildDirectory, BuildDirectoryGuard.CMakeCacheFileName),
            $"CMAKE_HOME_DIRECTORY:INTERNAL={temp.Path.Replace('\\', '/')}\nCMAKE_C_COMPILER:FILEPATH=/usr/bin/gcc\n",
            cancellationToken);

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => Service(factory, exitCode: 0).BuildAsync(Config(), request, cancellationToken));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Contains("clang-17", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A compiler the host names with words after it rebuilds the directory it configured: CMake
    /// cached ccache as the compiler and clang as its argument, and the guard reads the value the
    /// same way. Compared whole, 'ccache clang' against '/usr/bin/ccache' refused every rebuild, and
    /// the refusal ended the whole run.
    /// </summary>
    [Fact]
    public async Task ACompilerTheHostNamesWithWordsAfterIt_RebuildsTheDirectoryItConfigured()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var (factory, tracked) = await TrackedTreeAsync(temp, cancellationToken);

        // The ccache the build's PATH starts is the one the directory was configured with, so the
        // question is only what was recorded after it.
        var ccache = temp.WriteProgram(ToolchainDirectory, "ccache");
        var request = tracked with
        {
            HostEnvironment = new Dictionary<string, string> { ["CC"] = "ccache clang", ["PATH"] = temp.Combine(ToolchainDirectory) },
        };
        var buildDirectory = request.Variant.DirectoryUnder(temp.Path);

        Directory.CreateDirectory(buildDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(buildDirectory, BuildDirectoryGuard.CMakeCacheFileName),
            $"CMAKE_HOME_DIRECTORY:INTERNAL={temp.Path.Replace('\\', '/')}\nCMAKE_C_COMPILER:FILEPATH={ccache.Replace('\\', '/')}\nCMAKE_C_COMPILER_ARG1:STRING= clang\n",
            cancellationToken);

        var result = await Service(factory, exitCode: 0).BuildAsync(Config(), request, cancellationToken);

        Assert.NotEqual(LegVerdict.Poisoned, result.Verdict.Verdict);
    }

    /// <summary>
    /// A toolchain that gives CMake its compiler as a cache variable builds with that one, whatever
    /// the host's env names, so its directory rebuilds: compared with the host's CC, which CMake never
    /// used, every rebuild was refused.
    /// </summary>
    [Fact]
    public async Task ACompilerGivenAsACacheVariable_RebuildsTheDirectoryItConfigured_WhateverTheHostNames()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var (factory, tracked) = await TrackedTreeAsync(temp, cancellationToken);

        // The gcc the build starts is the one the directory was configured with, found where a survey
        // found it: off a PATH that holds none, in the directory appended to it.
        var gcc = temp.WriteProgram(ToolchainDirectory, "gcc");
        Directory.CreateDirectory(temp.Combine("empty-path"));
        var request = tracked with
        {
            HostEnvironment = new Dictionary<string, string> { ["CC"] = "clang", ["PATH"] = temp.Combine("empty-path") },
            ProgramDirectories = [temp.Combine(ToolchainDirectory)],
        };
        var buildDirectory = request.Variant.DirectoryUnder(temp.Path);

        Directory.CreateDirectory(buildDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(buildDirectory, BuildDirectoryGuard.CMakeCacheFileName),
            $"CMAKE_HOME_DIRECTORY:INTERNAL={temp.Path.Replace('\\', '/')}\nCMAKE_C_COMPILER:STRING={gcc.Replace('\\', '/')}\n",
            cancellationToken);

        var config = Config();
        config.Toolchains["gcc"] = new ToolchainConfig { Platforms = [PlatformNames.Linux], CacheVars = { ["CMAKE_C_COMPILER"] = "gcc" } };

        var result = await Service(factory, exitCode: 0).BuildAsync(config, request, cancellationToken);

        Assert.NotEqual(LegVerdict.Poisoned, result.Verdict.Verdict);
    }

    /// <summary>
    /// A relative program the build recorded is read from the build directory, where the check starts,
    /// never from wherever this process began.
    /// </summary>
    [Fact]
    public async Task ARelativeRecordedNinja_IsReadFromTheBuildDirectory()
    {
        using var temp = new TempDirectory();
        var buildDirectory = temp.Combine("build", "x86_64-gcc-debug");
        Directory.CreateDirectory(buildDirectory);
        await File.WriteAllTextAsync(Path.Combine(buildDirectory, NinjaDependencyCheck.ManifestFileName), string.Empty, TestContext.Current.CancellationToken);

        var runner = new RecordingRunner("app.o: #deps 1, deps mtime 1 (VALID)\n    app.h\n");

        _ = await new NinjaDependencyCheck(runner, new HarnessFactory().FileSystem)
            .CheckAsync(buildDirectory, [], "tools/ninja", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(buildDirectory, "tools", "ninja"), Assert.Single(runner.Started).FileName);
    }

    /// <summary>
    /// A check that could not start says so as a check that did not run - never as the build failing,
    /// and never as a pass.
    /// </summary>
    [Fact]
    public async Task ADependencyCheckThatCouldNotStart_IsACheckThatDidNotRun()
    {
        using var temp = new TempDirectory();
        var buildDirectory = temp.Combine("build", "x86_64-gcc-debug");
        Directory.CreateDirectory(buildDirectory);
        await File.WriteAllTextAsync(Path.Combine(buildDirectory, NinjaDependencyCheck.ManifestFileName), string.Empty, TestContext.Current.CancellationToken);

        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(Arg.Any<ProcessRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ProgramStartException("/opt/arm/bin/ninja", "'/opt/arm/bin/ninja' could not be started: Text file busy"));

        var failure = await Assert.ThrowsAsync<HarnessException>(() => new NinjaDependencyCheck(runner, new HarnessFactory().FileSystem)
            .CheckAsync(buildDirectory, [], "/opt/arm/bin/ninja", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.CommandFailed, failure.ExitCode);
        Assert.Contains("could not be started", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Answers every program with <paramref name="output"/>, recording what it was asked to start.</summary>
    private sealed class RecordingRunner(string output) : IProcessRunner
    {
        public List<ProcessRequest> Started { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Started.Add(request);
            return Task.FromResult(new ProcessResult(0, output, string.Empty, TimeSpan.Zero, TimedOut: false));
        }

        public string? FindExecutable(string command) => command;
    }

    private sealed class QuietRunner(int exitCode) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ProcessResult(exitCode, string.Empty, string.Empty, TimeSpan.Zero, TimedOut: false));

        public string? FindExecutable(string command) => command;
    }
}
