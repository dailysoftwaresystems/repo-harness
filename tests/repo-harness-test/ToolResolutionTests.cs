using NSubstitute;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// Where a program is found, and that the survey, the run and install-missing-tools all find it in
/// the same place. Measured on a consumer's gate: cmake installed in /opt/homebrew/bin on a macOS
/// host, which the PATH of a command run over ssh does not name. The survey called both of that
/// host's legs runnable; each then poisoned the run, exit 70, because the run looked on its PATH and
/// nowhere else.
/// </summary>
public sealed class ToolResolutionTests
{
    /// <summary>
    /// A platform's own list replaces the built-in one, and only when it has something in it.
    /// Declared but empty would otherwise mean "search nowhere", which turns every program off the
    /// PATH into one that is missing and is never what somebody writing the key meant.
    /// </summary>
    [Fact]
    public void ADeclaredList_ReplacesTheBuiltInOne_OnlyWhenItHasSomethingInIt()
    {
        var declared = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["macos"] = ["/opt/local/bin"],
            ["linux"] = [],
        };

        Assert.Equal(["/opt/local/bin"], ToolSearchDirectories.For(declared, "macos"));
        Assert.Equal(ToolSearchDirectories.Posix, ToolSearchDirectories.For(declared, "linux"));
        Assert.Equal(ToolSearchDirectories.Posix, ToolSearchDirectories.For(null, "macos"));

        // And Windows searches nothing beyond its PATH unless told to: its installers put a program
        // on the machine PATH, and none of the POSIX directories exist there.
        Assert.Empty(ToolSearchDirectories.For(null, "windows"));
        Assert.Contains("/opt/homebrew/bin", ToolSearchDirectories.Posix);
    }

    /// <summary>
    /// The PATH is asked first and wins. A searched directory only ever adds a program the PATH does
    /// not know, so no name a command already resolves changes what it resolves to.
    /// </summary>
    [Fact]
    public void ThePath_WinsOverASearchedDirectory()
    {
        using var temp = new TempDirectory();
        var onPath = Directory.CreateDirectory(temp.Combine("path")).FullName;
        var searched = Directory.CreateDirectory(temp.Combine("searched")).FullName;

        var expected = Executable(onPath, "cmake");
        Executable(searched, "cmake");

        var found = Resolver(onPath).Find("cmake", [searched]);

        Assert.Equal(ProgramFound.OnPath, found.Found);
        Assert.Equal(expected, found.Path);
    }

    /// <summary>
    /// The case the consumer measured: not on the PATH, installed in a directory that is searched.
    /// Found there, and its directory is what a leg appends to the PATH of what it starts.
    /// </summary>
    [Fact]
    public void AProgramOffThePath_IsFoundInASearchedDirectory_AndItsDirectoryIsHandedOn()
    {
        using var temp = new TempDirectory();
        var onPath = Directory.CreateDirectory(temp.Combine("path")).FullName;
        var searched = Directory.CreateDirectory(temp.Combine("homebrew")).FullName;
        var cmake = Executable(searched, "cmake");

        var search = Resolver(onPath).Resolve(["cmake", "absent"], [searched]);

        Assert.Equal(new ProgramLocation("cmake", ProgramFound.OffPath, cmake), search.Found["cmake"]);
        Assert.Equal(ProgramFound.Nowhere, search.Found["absent"].Found);
        Assert.Equal([searched], search.OffPathDirectories);
    }

    /// <summary>
    /// Directories are handed on in the order the search prefers them, not the order programs were
    /// asked for, so the PATH a leg is given ranks two directories the way the search did.
    /// </summary>
    [Fact]
    public void DirectoriesFoundOffThePath_KeepTheSearchsOwnOrder()
    {
        using var temp = new TempDirectory();
        var first = Directory.CreateDirectory(temp.Combine("first")).FullName;
        var second = Directory.CreateDirectory(temp.Combine("second")).FullName;

        Executable(first, "ninja");
        Executable(second, "cmake");

        // Asked for cmake first, whose directory the search prefers second.
        var search = Resolver(temp.Combine("empty")).Resolve(["cmake", "ninja"], [first, second]);

        Assert.Equal([first, second], search.OffPathDirectories);
    }

    /// <summary><c>~/</c> is the home directory of whoever searches, on each machine its own.</summary>
    [Fact]
    public void ATildeDirectory_IsThisMachinesHome()
    {
        using var temp = new TempDirectory();
        var tools = Directory.CreateDirectory(temp.Combine("home", ".local", "bin")).FullName;
        var tool = Executable(tools, "tclsh");

        var found = Resolver(temp.Combine("empty"), home: temp.Combine("home")).Find("tclsh", ["~/.local/bin"]);

        Assert.Equal(tool, found.Path);
    }

    /// <summary>
    /// A program started by name is looked up on the PATH the child is given, not on this process's
    /// own. They differ exactly when directories are appended, and a program found only there would
    /// otherwise be reported missing by the very call that was meant to start it.
    /// </summary>
    [Fact]
    public async Task AProgramOnlyInAnAppendedDirectory_IsStarted_ByTheSamePathTheChildGets()
    {
        using var temp = new TempDirectory();
        var appended = Directory.CreateDirectory(temp.Combine("appended")).FullName;
        var name = "rh-probe-" + Guid.NewGuid().ToString("N")[..8];

        StartableProgram(appended, name);

        var result = await new ProcessRunner(new HostPlatform(), FilePermissionsFactory.Create()).RunAsync(
            new ProcessRequest { FileName = name, AppendToPath = [appended], Timeout = TimeSpan.FromSeconds(30) },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
    }

    /// <summary>
    /// Appended, never prepended: every name the PATH already answers for keeps that answer, and the
    /// directories come last in the order given.
    /// </summary>
    [Fact]
    public async Task AppendedDirectories_ComeAfterThePathTheChildInherits()
    {
        using var temp = new TempDirectory();
        var first = Directory.CreateDirectory(temp.Combine("first")).FullName;
        var second = Directory.CreateDirectory(temp.Combine("second")).FullName;

        var request = TestHost.ChildRequest("print-env", "PATH") with { AppendToPath = [first, second] };

        var result = await new ProcessRunner(new HostPlatform(), FilePermissionsFactory.Create())
            .RunAsync(request, TestContext.Current.CancellationToken);

        var entries = result.StandardOutput.Trim().Split(Path.PathSeparator);

        Assert.Equal([first, second], entries[^2..]);
        Assert.True(entries.Length > 2, "the PATH the child inherited should come first");
    }

    /// <summary>
    /// What a leg's build and test start, read from the configuration they read: the adapter's
    /// program, ninja for the Ninja generator, the compilers the variant names, and the test runner.
    /// A declared tool is not among them, because a tool one runner needs is not every leg's need.
    /// </summary>
    [Fact]
    public void ALegsPrograms_AreWhatItsBuildAndTestStart_AndNoMore()
    {
        var config = Configured(generator: "Ninja", cc: "gcc -m32", cxx: "g++");

        Assert.Equal(["cmake", "ninja", "gcc", "g++", "ctest"], LegPrograms.For(config, config.Legs["lin"]));

        // A declared tool is asked about everywhere, so a leg starting it from a step finds it, but
        // it makes no leg unrunnable.
        Assert.Contains("tclsh", LegPrograms.Wanted(config));
        Assert.DoesNotContain("tclsh", LegPrograms.For(config, config.Legs["lin"]));
    }

    /// <summary>Another generator starts another build tool, which is cmake's to find and to report.</summary>
    [Fact]
    public void ANonNinjaGenerator_AddsNoNinja()
        => Assert.DoesNotContain(
            "ninja",
            LegPrograms.For(Configured(generator: "Unix Makefiles"), Configured(generator: "Unix Makefiles").Legs["lin"]));

    /// <summary>
    /// A host missing a program a leg's build starts turns that leg away, naming the program and
    /// where to fix it, and the placement records that a missing tool was the whole of the reason.
    /// </summary>
    [Fact]
    public void AHostWithoutALegsBuildTool_TurnsTheLegAway_AsAMissingTool()
    {
        var config = Configured(generator: "Ninja");
        var mac = Host(HostId.Local, config, ("cmake", ProgramFound.Nowhere));

        var placement = LegPlacement.Place(
            config,
            new SelectedLeg("lin", config.Legs["lin"]),
            new Dictionary<HostId, HostReport> { [HostId.Local] = mac });

        Assert.False(placement.Runnable);
        Assert.True(placement.ToolMissing);
        Assert.Contains("'cmake' is not installed there", placement.Reason, StringComparison.Ordinal);
        Assert.Contains("toolSearchDirectories", placement.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// The wrong machine says so before anything about programs. Told cmake is missing on a host
    /// being considered for a leg of another operating system, a reader would install cmake on a
    /// machine that leg will never run on.
    /// </summary>
    [Fact]
    public void AWrongMachine_IsReportedAsThat_NotAsAMissingTool()
    {
        var config = Configured(generator: "Ninja");
        var windows = Host(HostId.Local, config, ("cmake", ProgramFound.Nowhere)) with { Os = "windows" };

        var placement = LegPlacement.Place(
            config,
            new SelectedLeg("lin", config.Legs["lin"]),
            new Dictionary<HostId, HostReport> { [HostId.Local] = windows });

        Assert.False(placement.ToolMissing);
        Assert.DoesNotContain("cmake", placement.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A program the host was never asked about is not reported as missing, and not as present. A
    /// survey that did not look must not read as one that looked.
    /// </summary>
    [Fact]
    public void AProgramTheHostWasNotAskedAbout_IsSaidToBeUnasked()
    {
        var config = Configured(generator: "Ninja");
        var host = new HostReport { Host = HostId.Local, Os = "linux", Processor = "x86_64" };

        var reason = LegPlacement.Obstacle(config, config.Legs["lin"], host);

        Assert.NotNull(reason);
        Assert.Contains("was not asked", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("not installed", reason, StringComparison.Ordinal);
    }

    /// <summary>Every program there, found on the PATH or off it, and the leg runs.</summary>
    [Fact]
    public void AHostWithEveryProgram_RunsTheLeg_WhereverItFoundThem()
    {
        var config = Configured(generator: "Ninja");
        var host = Host(HostId.Local, config, ("cmake", ProgramFound.OffPath));

        Assert.Null(LegPlacement.Obstacle(config, config.Legs["lin"], host));
    }

    /// <summary>
    /// A program that will not start is a cause this build can name, in one table both the command
    /// runner and the leg executor read. Anything else is not, and stays a defect.
    /// </summary>
    [Fact]
    public void AProgramThatWouldNotStart_IsAMissingTool_InTheOneTableBothReaders()
    {
        Assert.Equal(HarnessExit.ToolMissing, KnownCauses.ExitCodeFor(new ExecutableNotFoundException("cmake")));
        Assert.Null(KnownCauses.ExitCodeFor(new InvalidOperationException("a defect")));
    }

    /// <summary>
    /// A search directory that is relative would be looked in wherever a command happened to start,
    /// so one leg would find a tool and the next would not. Refused when the file is read, with a
    /// platform nobody has and a blank entry.
    /// </summary>
    [Fact]
    public void ASearchDirectory_ThatIsRelativeBlankOrForNoPlatform_IsRefused()
    {
        var problems = HarnessConfigValidator.Validate(new HarnessConfig
        {
            ToolSearchDirectories =
            {
                ["macos"] = ["/opt/homebrew/bin", "~/.local/bin", "tools/bin", " "],
                ["beos"] = ["/boot/bin"],
            },
        });

        Assert.Contains(problems, problem => problem.Contains("'tools/bin', which is relative", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("toolSearchDirectories.macos has a blank entry", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("platform 'beos'", StringComparison.Ordinal));
        Assert.DoesNotContain(problems, problem => problem.Contains("/opt/homebrew/bin", StringComparison.Ordinal));
        Assert.DoesNotContain(problems, problem => problem.Contains("~/.local/bin", StringComparison.Ordinal));
    }

    /// <summary>
    /// A host answers a survey about its programs with the function its own leg will use, and says
    /// which directories that leg will append to its PATH.
    /// </summary>
    [Fact]
    public async Task AHostAnswersWhereItsProgramsAre_WithTheSearchItsOwnLegWillUse()
    {
        using var temp = new TempDirectory();
        var onPath = Directory.CreateDirectory(temp.Combine("path")).FullName;
        var searched = Directory.CreateDirectory(temp.Combine("searched")).FullName;

        Executable(onPath, "cmake");
        Executable(searched, "ninja");

        var platform = HostDoubles.Platform(PlatformId.Linux, "arm64", temp.Path);
        var fileSystem = new RepoHarness.Core.FileSystem.PhysicalFileSystem(FilePermissionsFactory.Create());
        var identity = Substitute.For<IToolIdentityProvider>();
        identity.Current.Returns(new ToolIdentity("1.2.3", "abc123"));

        var agent = new HostAgentService(
            platform,
            identity,
            new EmulatorProbe(platform, new ProcessRunner(new HostPlatform(), FilePermissionsFactory.Create()), fileSystem),
            fileSystem,
            new LocalProgramResolver(platform, Permissions(), () => onPath));

        var info = await agent.DescribeAsync(
            new Dictionary<string, EmulatorConfig>(StringComparer.OrdinalIgnoreCase),
            ["cmake", "ninja", "absent"],
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase) { ["linux"] = [searched] },
            TestContext.Current.CancellationToken);

        Assert.Equal(ProgramFound.OnPath, info.Programs["cmake"].Found);
        Assert.Equal(ProgramFound.OffPath, info.Programs["ninja"].Found);
        Assert.Equal(ProgramFound.Nowhere, info.Programs["absent"].Found);
        Assert.Equal([searched], info.ProgramDirectories);
    }

    /// <summary>A configuration with one Linux leg building a cmake project and testing it with ctest.</summary>
    private static HarnessConfig Configured(string? generator, string? cc = null, string? cxx = null)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (cc is not null)
        {
            env["CC"] = cc;
        }

        if (cxx is not null)
        {
            env["CXX"] = cxx;
        }

        var toolchain = new ToolchainConfig { Platforms = ["linux"], Generator = generator };

        foreach (var (name, value) in env)
        {
            toolchain.Env[name] = value;
        }

        return new HarnessConfig
        {
            Toolchains = { ["gcc"] = toolchain },
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Projects =
            {
                new ProjectConfig
                {
                    Name = "app",
                    Type = "cmake",
                    Path = ".",
                    Test = new TestConfig { All = new TestInvocation { Runner = "ctest", SuccessPattern = "tests passed" } },
                },
            },
            Legs = { ["lin"] = new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug", Toolchain = "gcc" } },
            Tools = { new ToolConfig { Name = "tclsh" } },
        };
    }

    /// <summary>A Linux host that found every program the configuration wants, except as <paramref name="overrides"/> say.</summary>
    private static HostReport Host(HostId host, HarnessConfig config, params (string Program, ProgramFound Found)[] overrides)
    {
        var programs = LegPrograms.Wanted(config).ToDictionary(
            program => program,
            program => new ProgramLocation(program, ProgramFound.OnPath, "/usr/bin/" + program),
            StringComparer.Ordinal);

        foreach (var (program, found) in overrides)
        {
            programs[program] = new ProgramLocation(program, found, found == ProgramFound.Nowhere ? null : "/opt/homebrew/bin/" + program);
        }

        return new HostReport { Host = host, Os = "linux", Processor = "x86_64", Programs = programs };
    }

    /// <summary>A resolver over <paramref name="path"/> as the PATH, answering from the files that are really there.</summary>
    private static LocalProgramResolver Resolver(string path, string? home = null)
        => new(HostDoubles.Platform(PlatformId.Linux, "x86_64", home), Permissions(), () => path);

    /// <summary>A file starts when it is there: what Windows answers, made to hold on every platform the tests run on.</summary>
    private static IFilePermissions Permissions()
    {
        var permissions = Substitute.For<IFilePermissions>();
        permissions.IsExecutable(Arg.Any<string>()).Returns(call => File.Exists(call.Arg<string>()));
        return permissions;
    }

    /// <summary>Creates a file named <paramref name="name"/> in <paramref name="directory"/>, and returns its path.</summary>
    private static string Executable(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    /// <summary>A program that really starts and exits zero, under a name nothing else has.</summary>
    private static void StartableProgram(string directory, string name)
    {
        if (OperatingSystem.IsWindows())
        {
            // A standalone system program, copied under a name no PATH can know.
            File.Copy(Path.Combine(Environment.SystemDirectory, "hostname.exe"), Path.Combine(directory, name + ".exe"));
            return;
        }

        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
