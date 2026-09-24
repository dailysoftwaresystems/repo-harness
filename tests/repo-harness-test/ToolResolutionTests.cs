using RepoHarness.Core.Output;
using NSubstitute;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// Where a program is found, and that the survey and the run find it in the same place. Measured on a
/// consumer's gate: cmake installed in /opt/homebrew/bin on a macOS host, which the PATH of a command
/// run over ssh does not name. The survey called both of that host's legs runnable; each then
/// poisoned the run, exit 70, because the run looked on its PATH and nowhere else.
/// </summary>
public sealed class ToolResolutionTests
{
    /// <summary>What a host with no section of its own declares: nothing.</summary>
    private static readonly HostSettings NoSettings = new LocalHostConfig();

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
        Assert.Equal([searched], search.Directories);
    }

    /// <summary>
    /// Every directory a program was found in is handed on, the PATH's own as well: a phase whose
    /// environment sets a PATH of its own still finds, in what is appended to it, every program the
    /// survey found. They are handed on in the order the search looked, the PATH first, never in the
    /// order the programs were asked for, so the PATH a leg is given ranks them as the search did.
    /// </summary>
    [Fact]
    public void EveryDirectoryAProgramWasFoundIn_IsHandedOn_InTheOrderTheSearchLooked()
    {
        using var temp = new TempDirectory();
        var onPath = Directory.CreateDirectory(temp.Combine("path")).FullName;
        var first = Directory.CreateDirectory(temp.Combine("first")).FullName;
        var second = Directory.CreateDirectory(temp.Combine("second")).FullName;

        Executable(onPath, "ctest");
        Executable(first, "ninja");
        Executable(second, "cmake");

        // Asked for the program in the directory searched last first, and the one on the PATH last.
        var search = Resolver(onPath).Resolve(["cmake", "ninja", "ctest"], [first, second]);

        Assert.Equal([onPath, first, second], search.Directories);
    }

    /// <summary>A program named by its path is started by that path, and adds nothing to anybody's PATH.</summary>
    [Fact]
    public void AProgramNamedByItsPath_IsFoundThere_AndAddsNoDirectory()
    {
        using var temp = new TempDirectory();
        var tool = Executable(Directory.CreateDirectory(temp.Combine("tools")).FullName, "bench");

        var search = Resolver(temp.Combine("empty")).Resolve([tool], []);

        Assert.Equal(new ProgramLocation(tool, ProgramFound.OnPath, tool), search.Found[tool]);
        Assert.Empty(search.Directories);
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
    /// With no home directory to expand it against, a <c>~/</c> entry names a directory this machine
    /// has and nobody could look in. A program not found anywhere else is then not known either way,
    /// and never "missing": the directory skipped is the one it may well be in.
    /// </summary>
    [Fact]
    public void ATildeDirectoryWithNoHomeToExpandItAgainst_LeavesAProgramUnknown_NotMissing()
    {
        using var temp = new TempDirectory();

        var found = Resolver(temp.Combine("empty"), home: string.Empty).Find("tclsh", ["~/.local/bin"]);

        Assert.Equal(ProgramFound.Unreadable, found.Found);
        Assert.Equal("the home directory is not known here, so '~/.local/bin' could not be searched", found.Reason);
    }

    /// <summary>
    /// An entry that is not a whole path on this machine names no directory here, and is never read
    /// against wherever the search happened to start: that is where a program somebody left in the
    /// current directory would be found in place of the real one.
    /// </summary>
    [Fact]
    public void AnEntryThatIsNotAWholePathHere_IsNeverReadAgainstTheCurrentDirectory()
    {
        using var temp = new TempDirectory();
        var tools = Directory.CreateDirectory(temp.Combine("tools")).FullName;
        Executable(tools, "tclsh");

        // A relative path that, read against the current directory, leads exactly there.
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, tools);
        Assert.SkipWhen(Path.IsPathFullyQualified(relative), "the temporary directory is on another drive, so no relative path reaches it");

        var found = Resolver(temp.Combine("empty")).Find("tclsh", [relative]);

        Assert.Equal(ProgramFound.Nowhere, found.Found);
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

        TestHost.StartableProgram(appended, name);

        var result = await new ProcessRunner(new HostPlatform(), FilePermissionsFactory.Create()).RunAsync(
            new ProcessRequest { FileName = name, AppendToPath = [appended], Timeout = TimeSpan.FromSeconds(30) },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
    }

    /// <summary>
    /// A phase whose environment sets a PATH of its own - MinGW's runtime directory for a test that
    /// needs its DLLs - still starts a program the survey found, because what the survey found is
    /// appended after whatever PATH the environment sets. Looked for only where that environment
    /// said, a runner the survey had just found would not start.
    /// </summary>
    [Fact]
    public async Task AProgramTheSurveyFound_StillStarts_WhenThePhasesEnvironmentSetsAPathOfItsOwn()
    {
        using var temp = new TempDirectory();
        var found = Directory.CreateDirectory(temp.Combine("found")).FullName;
        var elsewhere = Directory.CreateDirectory(temp.Combine("elsewhere")).FullName;
        var name = "rh-probe-" + Guid.NewGuid().ToString("N")[..8];

        TestHost.StartableProgram(found, name);

        var result = await new ProcessRunner(new HostPlatform(), FilePermissionsFactory.Create()).RunAsync(
            new ProcessRequest
            {
                FileName = name,
                Environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["PATH"] = elsewhere },
                AppendToPath = [found],
                Timeout = TimeSpan.FromSeconds(30),
            },
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

        Assert.Equal(["cmake", "ninja", "gcc", "g++", "ctest"], LegPrograms.For(config, config.Legs["lin"], LegWorkload.BuildAndTest, NoSettings));

        // A declared tool is asked about everywhere, so a leg starting it from a step finds it, but
        // it makes no leg unrunnable.
        Assert.Contains("tclsh", LegPrograms.Wanted(config, LegWorkload.BuildAndTest));
        Assert.DoesNotContain("tclsh", LegPrograms.For(config, config.Legs["lin"], LegWorkload.BuildAndTest, NoSettings));
    }

    /// <summary>Another generator starts another build tool, which is cmake's to find and to report.</summary>
    [Fact]
    public void ANonNinjaGenerator_AddsNoNinja()
    {
        var config = Configured(generator: "Unix Makefiles");

        Assert.DoesNotContain("ninja", LegPrograms.For(config, config.Legs["lin"], LegWorkload.BuildAndTest, NoSettings));
    }

    /// <summary>
    /// Each command asks for what it will start and nothing more: a build never needs the test
    /// runner, a test given --no-build needs nothing but the runner, a run what its steps start and
    /// the build's only when the runner requires a build, and a copy nothing at all.
    /// </summary>
    [Fact]
    public void EachCommand_AsksForWhatItWillStart_AndNothingMore()
    {
        var config = Configured(generator: "Ninja", cc: "gcc");
        var leg = config.Legs["lin"];

        Assert.Equal(["cmake", "ninja", "gcc"], LegPrograms.For(config, leg, LegWorkload.BuildOnly, NoSettings));
        Assert.Equal(["ctest"], LegPrograms.For(config, leg, new LegWorkload(Build: false, Test: true, []), NoSettings));
        Assert.Empty(LegPrograms.For(config, leg, LegWorkload.Copy, NoSettings));
        Assert.Equal(["python3"], LegPrograms.For(config, leg, new LegWorkload(Build: false, Test: false, ["python3"]), NoSettings));
        Assert.Equal(["cmake", "ninja", "gcc", "python3"], LegPrograms.For(config, leg, new LegWorkload(Build: true, Test: false, ["python3"]), NoSettings));
    }

    /// <summary>
    /// A relative path and a placeholder are left to the run. A relative path is read from the
    /// directory its phase starts in, which a host's copy may not hold until the run's sync; a
    /// placeholder is filled in only once the leg runs; and a compiler variable naming a path with a
    /// space in it is read whole by CMake, never cut at its first space into a "C:\Program" nobody has.
    /// </summary>
    [Fact]
    public void ARelativePathOrAPlaceholder_IsLeftToTheRun()
    {
        var relativeRunner = Configured(generator: "Ninja", cc: @"C:\Program Files\LLVM\bin\clang-cl.exe", cxx: "ccache g++", runner: "scripts/test.sh");

        Assert.Equal(["cmake", "ninja", "ccache"], LegPrograms.For(relativeRunner, relativeRunner.Legs["lin"], LegWorkload.BuildAndTest, NoSettings));

        var builtRunner = Configured(generator: null, runner: "{buildDir}/tests");

        Assert.Equal(["cmake"], LegPrograms.For(builtRunner, builtRunner.Legs["lin"], LegWorkload.BuildAndTest, NoSettings));

        Assert.Equal(
            ["python3"],
            LegPrograms.For(builtRunner, builtRunner.Legs["lin"], new LegWorkload(Build: false, Test: false, ["python3", "./bench.sh", "{tool}", " "]), NoSettings));
    }

    /// <summary>
    /// A program named by a path absolute on the leg's platform is an installed file - a compiler kept
    /// out of every PATH, as Homebrew keeps its llvm - and is looked for as surely as a name is. A
    /// path absolute only on another platform names nothing there.
    /// </summary>
    [Fact]
    public void AnAbsolutePath_IsLookedForBeforehand_OnItsOwnPlatform()
    {
        var absolute = Configured(generator: null, cc: "/usr/bin/gcc-13", runner: "/opt/tools/ctest");

        Assert.Equal(["cmake", "/usr/bin/gcc-13", "/opt/tools/ctest"], LegPrograms.For(absolute, absolute.Legs["lin"], LegWorkload.BuildAndTest, NoSettings));

        var windowsPath = Configured(generator: null, runner: @"C:\tools\ctest.exe");

        Assert.Equal(["cmake"], LegPrograms.For(windowsPath, windowsPath.Legs["lin"], LegWorkload.BuildAndTest, NoSettings));
    }

    /// <summary>
    /// A compiler variable is read as CMake reads it where the value alone can say: the whole of it
    /// when it holds no space, and its first word when that is a name. A value with a space whose
    /// first word is a path is left to CMake, which alone can ask whether the whole of it is a file.
    /// </summary>
    [Theory]
    [InlineData("gcc -m32", "gcc")]
    [InlineData("ccache gcc", "ccache")]
    [InlineData("/usr/bin/gcc-13", "/usr/bin/gcc-13")]
    [InlineData(@"C:\Program Files\LLVM\bin\clang-cl.exe", null)]
    [InlineData("/opt/llvm/bin/clang --target=x86_64-linux-gnu", null)]
    public void ACompilerVariable_IsReadAsCMakeReadsIt(string value, string? program)
    {
        var config = Configured(generator: null, cc: value);

        var programs = LegPrograms.For(config, config.Legs["lin"], LegWorkload.BuildOnly, NoSettings);

        Assert.Equal(program is null ? ["cmake"] : ["cmake", program], programs);
    }

    /// <summary>
    /// A program started under an environment the configuration declares that sets PATH is found on
    /// that PATH, which no survey can see. Demanded anyway, a compiler only that PATH holds would turn
    /// away a leg that builds - so it is never required of a host. It is still asked about: the
    /// directory the survey finds it in is appended to that PATH as any other's is.
    /// </summary>
    [Fact]
    public void AProgramStartedUnderAnEnvironmentThatSetsPath_IsAskedAbout_ButNeverRequired()
    {
        var build = Configured(generator: "Ninja", cc: "arm-none-eabi-gcc");
        build.Toolchains["gcc"].Env["Path"] = "/opt/arm/bin:/usr/bin:/bin";

        Assert.Empty(LegPrograms.For(build, build.Legs["lin"], LegWorkload.BuildOnly, NoSettings));
        Assert.Contains("arm-none-eabi-gcc", LegPrograms.Wanted(build, LegWorkload.BuildOnly));
        Assert.Contains("ninja", LegPrograms.Wanted(build, LegWorkload.BuildOnly));

        var test = Configured(generator: null);
        test.Projects[0] = new ProjectConfig
        {
            Name = "app",
            Type = "cmake",
            Path = ".",
            Test = new TestConfig
            {
                All = new TestInvocation
                {
                    Runner = "ctest",
                    SuccessPattern = "tests passed",
                    Env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PATH"] = "/opt/cmake/bin" },
                },
            },
        };

        Assert.Empty(LegPrograms.For(test, test.Legs["lin"], new LegWorkload(Build: false, Test: true, []), NoSettings));
        Assert.Contains("ctest", LegPrograms.Wanted(test, LegWorkload.BuildAndTest));

        var phases = new RunnerConfig
        {
            Phases =
            [
                new RunnerPhase { Name = "plain", Command = ["python3", "bench.py"] },
                new RunnerPhase
                {
                    Name = "cross",
                    Command = ["arm-none-eabi-size", "out.elf"],
                    Env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PATH"] = "/opt/arm/bin" },
                },
            ],
        };

        Assert.Equal(["python3"], LegWorkload.ForRunner(phases, action: null).Programs);
        Assert.Equal(["arm-none-eabi-size"], LegWorkload.ForRunner(phases, action: null).UnderOwnPath);

        var whole = new RunnerConfig
        {
            Env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PATH"] = "/opt/arm/bin" },
            Phases = phases.Phases,
        };

        Assert.Empty(LegWorkload.ForRunner(whole, action: null).Programs);
        Assert.Equal(["python3", "arm-none-eabi-size"], LegWorkload.ForRunner(whole, action: null).UnderOwnPath);

        var action = new RepoHarness.Core.Runners.ActionFile(
            "actions/probe/probe.yml",
            "probe",
            null,
            [],
            [
                new RepoHarness.Core.Runners.ActionStep
                {
                    Name = "plain",
                    Commands = [new RepoHarness.Core.Runners.ActionCommand("tclsh probe.tcl", 4, ["tclsh", "probe.tcl"])],
                    Env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                },
                new RepoHarness.Core.Runners.ActionStep
                {
                    Name = "cross",
                    Commands = [new RepoHarness.Core.Runners.ActionCommand("arm-none-eabi-size out.elf", 8, ["arm-none-eabi-size", "out.elf"])],
                    Env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PATH"] = "/opt/arm/bin" },
                },
            ]);

        var steps = LegWorkload.ForRunner(new RunnerConfig { Action = "probe/probe.yml" }, action);

        Assert.Equal(["tclsh"], steps.Programs);
        Assert.Equal(["arm-none-eabi-size"], steps.UnderOwnPath);
        Assert.Contains("arm-none-eabi-size", LegPrograms.Wanted(build, steps));
        Assert.DoesNotContain("arm-none-eabi-size", LegPrograms.For(build, build.Legs["lin"], steps, NoSettings));
    }

    /// <summary>
    /// A step that names runOn starts its program only on a leg of a system it names: that leg's host
    /// must have it, and a leg of any other system is never turned away for want of it. Every host is
    /// still asked about it, so the leg that does start it finds the directory it is in - under an
    /// environment that sets PATH as well as without one.
    /// </summary>
    [Fact]
    public void AStepForSomeSystems_IsRequiredOnlyOfTheirLegs_AndAskedOfEveryHost()
    {
        var config = new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Legs =
            {
                ["lin"] = new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug" },
                ["win"] = new LegConfig { Os = "windows", Processor = "x86_64", Config = "debug" },
                ["mac"] = new LegConfig { Os = "macos", Processor = "arm64", Config = "debug" },
            },
        };

        var action = new RepoHarness.Core.Runners.ActionFile(
            "actions/probe/probe.yml",
            "probe",
            null,
            [],
            [
                new RepoHarness.Core.Runners.ActionStep
                {
                    Name = "everywhere",
                    Commands = [new RepoHarness.Core.Runners.ActionCommand("tclsh probe.tcl", 4, ["tclsh", "probe.tcl"])],
                },
                new RepoHarness.Core.Runners.ActionStep
                {
                    Name = "msvc",
                    Commands = [new RepoHarness.Core.Runners.ActionCommand("cl /nologo", 6, ["cl", "/nologo"])],
                    RunOn = ["windows"],
                },
                new RepoHarness.Core.Runners.ActionStep
                {
                    Name = "cross",
                    Commands = [new RepoHarness.Core.Runners.ActionCommand("arm-none-eabi-size out.elf", 8, ["arm-none-eabi-size", "out.elf"])],
                    Env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PATH"] = "/opt/arm/bin" },
                    RunOn = ["linux", "macos"],
                },
            ]);

        var steps = LegWorkload.ForRunner(new RunnerConfig { Action = "probe/probe.yml" }, action);

        Assert.Equal(["tclsh"], LegPrograms.For(config, config.Legs["lin"], steps, NoSettings));
        Assert.Equal(["tclsh", "cl"], LegPrograms.For(config, config.Legs["win"], steps, NoSettings));
        Assert.Equal(["tclsh"], LegPrograms.For(config, config.Legs["mac"], steps, NoSettings));

        Assert.Equal(["tclsh"], steps.On("windows").Programs.Take(1));
        Assert.Equal(["arm-none-eabi-size"], steps.On("linux").UnderOwnPath);
        Assert.Empty(steps.On("windows").UnderOwnPath);

        // A leg's os is read ignoring case, as everywhere else.
        Assert.Equal(["arm-none-eabi-size"], steps.On("Linux").UnderOwnPath);
        Assert.Contains("cl", steps.On("WINDOWS").Programs);

        var wanted = LegPrograms.Wanted(config, steps);

        Assert.Contains("cl", wanted);
        Assert.Contains("arm-none-eabi-size", wanted);
    }

    /// <summary>
    /// A program named by its path is looked for at that path and nowhere else, so the reason it is
    /// missing says that, and sends nobody to toolSearchDirectories, which has nothing to do with it.
    /// </summary>
    [Fact]
    public void AProgramNamedByAnAbsolutePath_IsMissingFromThatPath_NotFromTheSearchedDirectories()
    {
        var config = Configured(generator: null, cc: "/usr/bin/gcc-13");
        var host = Host(HostId.Local, config, ("/usr/bin/gcc-13", ProgramFound.Nowhere));

        var placement = Place(config, LegWorkload.BuildOnly, host);

        Assert.Equal(LegVerdict.SkippedToolMissing, placement.Verdict);
        Assert.Equal("local: nothing is at '/usr/bin/gcc-13' there; install it at that path, or name the program where it is", placement.Reason);
    }

    /// <summary>
    /// A list under 'all' naming only another platform's directories names nothing on this one, so
    /// it replaces nothing: read as a replacement, it would have searched no directory at all, and
    /// every program off the PATH - cmake in /opt/homebrew/bin - would read as missing.
    /// </summary>
    [Fact]
    public void AnAllListNamingOnlyAnotherPlatformsDirectories_KeepsTheBuiltInOne()
    {
        var onlyWindows = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase) { ["all"] = ["C:/Tools"] };
        var both = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase) { ["all"] = ["C:/Tools", "/opt/tools"] };

        Assert.Equal(ToolSearchDirectories.Posix, ToolSearchDirectories.For(onlyWindows, "macos"));
        Assert.Equal(["C:/Tools"], ToolSearchDirectories.For(onlyWindows, "windows"));
        Assert.Equal(["/opt/tools"], ToolSearchDirectories.For(both, "linux"));
    }

    /// <summary>
    /// A host whose own environment sets PATH starts every program of a leg under that PATH, which no
    /// survey can see: it is required to have none of them - each is the run's to find - and each is
    /// still asked about, so the directory it is found in reaches that PATH like any other.
    /// </summary>
    [Fact]
    public void AHostWhoseEnvironmentSetsPath_IsRequiredToHaveNothing_AndStillAskedAboutEverything()
    {
        var config = Configured(generator: "Ninja");
        config.Hosts.Local.Env["PATH"] = "/opt/tools/bin";

        Assert.Empty(LegPrograms.For(config, config.Legs["lin"], LegWorkload.BuildAndTest, config.Hosts.Local));
        Assert.Contains("cmake", LegPrograms.For(config, config.Legs["lin"], LegWorkload.BuildAndTest, NoSettings));
        Assert.Contains("cmake", LegPrograms.Wanted(config, LegWorkload.BuildAndTest));

        var placement = LegPlacement.Place(
            config,
            new SelectedLeg("lin", config.Legs["lin"]),
            LegWorkload.BuildAndTest,
            new Dictionary<HostId, HostReport> { [HostId.Local] = Host(HostId.Local, config, ("cmake", ProgramFound.Nowhere)) });

        Assert.True(placement.Runnable);
    }

    /// <summary>
    /// A leg whose toolchain names a developer environment starts every program under the PATH that
    /// environment sets up, which no survey can see: it is required to have none of them, each still
    /// asked about, and the environment is what the host must provide - for every command that starts
    /// anything there, and for a copy, nothing.
    /// </summary>
    [Fact]
    public void ALegInADeveloperEnvironment_IsRequiredToHaveNoProgram_ButTheEnvironment()
    {
        var config = new HarnessConfig
        {
            DeveloperEnvironments = { ["vs"] = new DeveloperEnvironmentConfig { Kind = DeveloperEnvironmentKinds.VisualStudio } },
            Toolchains =
            {
                ["msvc"] = new ToolchainConfig { Platforms = ["windows"], Generator = "Ninja", Env = { ["CC"] = "cl" }, DeveloperEnvironment = "vs" },
                ["mingw"] = new ToolchainConfig { Platforms = ["windows"], Generator = "Ninja", Env = { ["CC"] = "gcc" } },
            },
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
            Legs =
            {
                ["msvc"] = new LegConfig { Os = "windows", Processor = "x86_64", Config = "debug", Toolchain = "msvc" },
                ["mingw"] = new LegConfig { Os = "windows", Processor = "x86_64", Config = "debug", Toolchain = "mingw" },
            },
        };
        var msvc = config.Legs["msvc"];

        Assert.Empty(LegPrograms.For(config, msvc, LegWorkload.BuildAndTest, NoSettings));
        Assert.Equal(["cmake", "ninja", "gcc", "ctest"], LegPrograms.For(config, config.Legs["mingw"], LegWorkload.BuildAndTest, NoSettings));
        Assert.Contains("cl", LegPrograms.Wanted(config, LegWorkload.BuildAndTest));

        Assert.Equal("vs", LegPrograms.DeveloperEnvironmentOf(config, msvc, LegWorkload.BuildOnly));
        Assert.Equal("vs", LegPrograms.DeveloperEnvironmentOf(config, msvc, new LegWorkload(Build: false, Test: true, [])));
        Assert.Equal("vs", LegPrograms.DeveloperEnvironmentOf(config, msvc, new LegWorkload(Build: false, Test: false, ["python3"])));
        Assert.Equal("vs", LegPrograms.DeveloperEnvironmentOf(config, msvc, LegWorkload.Copy with { UnderOwnPath = ["python3"] }));
        Assert.Equal("vs", LegPrograms.DeveloperEnvironmentOf(config, msvc, LegWorkload.Copy with { OnlyOn = [new OsScopedStart("python3", false, ["windows"])] }));
        Assert.Null(LegPrograms.DeveloperEnvironmentOf(config, msvc, LegWorkload.Copy));
        Assert.Null(LegPrograms.DeveloperEnvironmentOf(config, config.Legs["mingw"], LegWorkload.BuildAndTest));
    }

    /// <summary>
    /// A compiler the host names in its own environment is the one the build starts wherever the
    /// variant names none, so it is required of that host and asked about everywhere - and one the
    /// variant names is still the variant's.
    /// </summary>
    [Fact]
    public void ACompilerTheHostNames_IsTheOneRequired_WhereTheVariantNamesNone()
    {
        var hostOnly = Configured(generator: "Ninja");
        hostOnly.Hosts.Local.Env["CC"] = "clang-17";

        Assert.Contains("clang-17", LegPrograms.For(hostOnly, hostOnly.Legs["lin"], LegWorkload.BuildOnly, hostOnly.Hosts.Local));
        Assert.DoesNotContain("clang-17", LegPrograms.For(hostOnly, hostOnly.Legs["lin"], LegWorkload.BuildOnly, NoSettings));
        Assert.Contains("clang-17", LegPrograms.Wanted(hostOnly, LegWorkload.BuildOnly));

        var both = Configured(generator: "Ninja", cc: "gcc-13");
        both.Hosts.Local.Env["CC"] = "clang-17";

        var programs = LegPrograms.For(both, both.Legs["lin"], LegWorkload.BuildOnly, both.Hosts.Local);

        Assert.Contains("gcc-13", programs);
        Assert.DoesNotContain("clang-17", programs);
    }

    /// <summary>
    /// The command a host's keepAwake starts is asked about, so a directory the survey finds it in
    /// reaches that host's PATH, and it is required of no host: one it cannot hold awake still runs
    /// its legs, and a sleep there still marks their timings suspect.
    /// </summary>
    [Fact]
    public void AKeepAwakeCommand_IsAskedAbout_AndRequiredOfNoHost()
    {
        var config = Configured(generator: "Ninja");
        config.Hosts.Ssh["pi"] = new SshHostConfig { RepositoryPath = "~/repo", KeepAwake = ["rh-awake", "-w", "{pid}"] };

        Assert.Contains("rh-awake", LegPrograms.Wanted(config, LegWorkload.BuildAndTest));
        Assert.DoesNotContain("rh-awake", LegPrograms.For(config, config.Legs["lin"], LegWorkload.BuildAndTest, config.Hosts.Ssh["pi"]));
    }

    /// <summary>
    /// A compiler the variant gives CMake as a cache variable is the one the survey requires, whatever
    /// the host's env names: it is the one the build starts.
    /// </summary>
    [Fact]
    public void ACompilerGivenAsACacheVariable_IsTheOneRequired()
    {
        var config = Configured(generator: "Ninja");
        config.Toolchains["gcc"].CacheVars["CMAKE_C_COMPILER"] = "gcc-13";
        config.Hosts.Local.Env["CC"] = "clang-17";

        var programs = LegPrograms.For(config, config.Legs["lin"], LegWorkload.BuildOnly, config.Hosts.Local);

        Assert.Contains("gcc-13", programs);
        Assert.DoesNotContain("clang-17", programs);
    }

    /// <summary>
    /// A host running a leg another machine dispatched to it judges the leg by the section that
    /// machine's configuration gives it, not by 'local' - which, in the configuration the two share,
    /// is the machine that dispatched it.
    /// </summary>
    [Fact]
    public void AHostRunningALegItWasSent_IsJudgedByItsOwnSection_NotByLocal()
    {
        var config = Configured(generator: "Ninja");
        config.Hosts.Ssh["pi"] = new SshHostConfig { RepositoryPath = "~/repo", Env = { ["PATH"] = "/opt/pi/bin" } };

        var reports = new Dictionary<HostId, HostReport> { [HostId.Local] = Host(HostId.Local, config, ("cmake", ProgramFound.Nowhere)) };
        var selected = new SelectedLeg("lin", config.Legs["lin"]);

        Assert.True(LegPlacement.Place(config, selected, LegWorkload.BuildAndTest, reports, here: HostId.Ssh("pi")).Runnable);
        Assert.False(LegPlacement.Place(config, selected, LegWorkload.BuildAndTest, reports, here: HostId.Wsl("other")).Runnable);
    }

    /// <summary>
    /// A host running a leg for the machine that dispatched it names no candidate in why it cannot:
    /// it is the only one, and its name for itself - "local" - would send the reader to the machine
    /// that asked.
    /// </summary>
    [Fact]
    public void AHostRunningALegItWasSent_NamesNoCandidate_InWhyItCannot()
    {
        var config = Configured(generator: "Ninja");
        var here = Host(HostId.Local, config, ("cmake", ProgramFound.Nowhere));

        var placement = LegPlacement.Place(
            config,
            new SelectedLeg("lin", config.Legs["lin"]),
            LegWorkload.BuildAndTest,
            new Dictionary<HostId, HostReport> { [HostId.Local] = here },
            here: HostId.Ssh("pi"));

        Assert.StartsWith("'cmake' is not installed there", placement.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A leg the survey turned away through a defect of its own fails 'legs', with the defect's
    /// code, even when another leg can run and none was named: whether it could run was never
    /// established, which is no switched-off machine.
    /// </summary>
    [Fact]
    public void ALegTurnedAwayThroughADefect_FailsTheSurvey_WithTheDefectsCode()
    {
        var config = Configured(generator: "Ninja");
        var fine = Host(HostId.Local, config);
        var never = new HostReport { Host = HostId.Local, Os = "linux", Processor = "x86_64" };

        var report = new LegsReport(
            [
                Place(config, LegWorkload.BuildAndTest, fine),
                Place(config, LegWorkload.BuildAndTest, never),
            ],
            [fine],
            Named: false);

        var outcome = LegsReports.Render(report, json: false);

        Assert.False(report.Passed);
        Assert.Equal(HarnessExit.InternalError, outcome.ExitCode);
        Assert.Contains("was never established, through a defect in this tool", outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// What a host is asked about covers what any leg needs of it, for every command: a program a leg
    /// needs that its host was never asked about is a defect, so it must not be reachable at all.
    /// </summary>
    [Fact]
    public void WhatAHostIsAskedAbout_CoversWhatEveryLegNeeds_ForEveryCommand()
    {
        var config = Configured(generator: "Ninja", cc: "gcc");

        LegWorkload[] workloads =
        [
            LegWorkload.BuildAndTest,
            LegWorkload.BuildOnly,
            LegWorkload.Copy,
            new(Build: false, Test: true, []),
            new(Build: true, Test: false, ["python3"]),
        ];

        foreach (var workload in workloads)
        {
            var wanted = LegPrograms.Wanted(config, workload);

            Assert.All(LegPrograms.For(config, config.Legs["lin"], workload, NoSettings), program => Assert.Contains(program, wanted));
        }
    }

    /// <summary>
    /// A host missing a program a leg's build starts turns that leg away, naming the program and
    /// where to fix it, and the placement records that a missing tool was the whole of the reason.
    /// </summary>
    [Fact]
    public void AHostWithoutALegsBuildTool_TurnsTheLegAway_AsAMissingTool()
    {
        var config = Configured(generator: "Ninja");
        var mac = Host(HostId.Local, config, ("cmake", ProgramFound.Nowhere));

        var placement = Place(config, LegWorkload.BuildAndTest, mac);

        Assert.False(placement.Runnable);
        Assert.Equal(LegVerdict.SkippedToolMissing, placement.Verdict);
        Assert.Contains("'cmake' is not installed there", placement.Reason, StringComparison.Ordinal);
        Assert.Contains("toolSearchDirectories", placement.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A build is never turned away for want of the test runner it does not start, and a copy for
    /// want of anything: a copy starts no program, so a host without cmake still gets the tree a
    /// runner that builds nothing runs in.
    /// </summary>
    [Fact]
    public void ACommand_IsNeverTurnedAwayForAProgramItDoesNotStart()
    {
        var config = Configured(generator: "Ninja");
        var noTestRunner = Host(HostId.Local, config, ("ctest", ProgramFound.Nowhere));
        var noBuildTool = Host(HostId.Local, config, ("cmake", ProgramFound.Nowhere));

        Assert.True(Place(config, LegWorkload.BuildOnly, noTestRunner).Runnable);
        Assert.False(Place(config, LegWorkload.BuildAndTest, noTestRunner).Runnable);
        Assert.True(Place(config, LegWorkload.Copy, noBuildTool).Runnable);
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

        var placement = Place(config, LegWorkload.BuildAndTest, windows);

        Assert.Equal(LegVerdict.SkippedUnavailable, placement.Verdict);
        Assert.DoesNotContain("cmake", placement.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A program the host was never asked about is not reported as missing, and not as present: a
    /// survey that did not look must not read as one that looked. And since a host is asked about
    /// every program any leg starts, it is a defect in this tool - poisoned, never a skip.
    /// </summary>
    [Fact]
    public void AProgramTheHostWasNeverAskedAbout_IsADefect_NotAMissingTool()
    {
        var config = Configured(generator: "Ninja");
        var host = new HostReport { Host = HostId.Local, Os = "linux", Processor = "x86_64" };

        var reason = Place(config, LegWorkload.BuildAndTest, host).Reason;

        Assert.NotNull(reason);
        Assert.Contains("was never asked, which is a defect in this tool", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("not installed", reason, StringComparison.Ordinal);
        Assert.Equal(LegVerdict.Poisoned, Place(config, LegWorkload.BuildAndTest, host).Verdict);
    }

    /// <summary>
    /// A program the host could not look for everywhere is not known either way. The leg does not
    /// go there, and nothing calls the program missing: the host is unavailable for it, with the
    /// search's own reason.
    /// </summary>
    [Fact]
    public void AProgramTheHostCouldNotLookFor_LeavesTheHostUnavailable_NotTheToolMissing()
    {
        var config = Configured(generator: "Ninja");
        var host = Host(HostId.Local, config) with
        {
            Programs = new Dictionary<string, ProgramLocation>(Host(HostId.Local, config).Programs, StringComparer.Ordinal)
            {
                ["cmake"] = new("cmake", ProgramFound.Unreadable, Reason: "its home directory could not be read"),
            },
        };

        var placement = Place(config, LegWorkload.BuildAndTest, host);

        Assert.Equal(LegVerdict.SkippedUnavailable, placement.Verdict);
        Assert.Contains("whether 'cmake' is there could not be established: its home directory could not be read", placement.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("not installed", placement.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A program never chooses the host. A leg goes to the first candidate that can take it - the
    /// right machine, reached - and a program that host lacks turns it away there. Moved to a host
    /// that has the program, it measured a machine nobody chose, and each command could send it
    /// somewhere else: a build to one host, the run on what that build staged to another. Only a
    /// host that cannot take the leg at all is passed over.
    /// </summary>
    [Fact]
    public void AProgramNeverChoosesTheHost_ALegIsTurnedAwayWhereItIsPlaced()
    {
        var config = Configured(generator: "Ninja");
        config.Hosts.Ssh["mac"] = new SshHostConfig { RepositoryPath = "/srv/repo" };

        var wrongMachine = Host(HostId.Local, config) with { Os = "windows" };
        var lacksCmakeHere = Host(HostId.Local, config, ("cmake", ProgramFound.Nowhere));
        var hasEverything = Host(HostId.Ssh("mac"), config);
        var lacksCmake = Host(HostId.Ssh("mac"), config, ("cmake", ProgramFound.Nowhere));
        var neverAsked = new HostReport { Host = HostId.Ssh("mac"), Os = "linux", Processor = "x86_64" };
        var unreachable = new HostReport { Host = HostId.Ssh("mac"), Reason = "ssh could not connect" };

        // This machine lacks cmake and the mac has it: turned away here, never moved there.
        var here = Place(config, LegWorkload.BuildAndTest, lacksCmakeHere, hasEverything);

        Assert.False(here.Runnable);
        Assert.Equal(LegVerdict.SkippedToolMissing, here.Verdict);
        Assert.StartsWith("local: 'cmake' is not installed there", here.Reason, StringComparison.Ordinal);

        // A copy starts nothing and goes to the same host, where a run on what it staged looks.
        Assert.Equal(HostId.Local, Place(config, LegWorkload.Copy, lacksCmakeHere, hasEverything).Host?.Host);

        // Passed over only for a host that cannot take the leg, and judged on the one it reaches.
        Assert.Equal(HostId.Ssh("mac"), Place(config, LegWorkload.BuildAndTest, wrongMachine, hasEverything).Host?.Host);
        Assert.Equal(LegVerdict.SkippedToolMissing, Place(config, LegWorkload.BuildAndTest, wrongMachine, lacksCmake).Verdict);
        Assert.Equal(LegVerdict.Poisoned, Place(config, LegWorkload.BuildAndTest, wrongMachine, neverAsked).Verdict);
        Assert.Equal(LegVerdict.SkippedUnavailable, Place(config, LegWorkload.BuildAndTest, wrongMachine, unreachable).Verdict);
    }

    /// <summary>
    /// When no selected leg can run, the command says why for each, under the verdict the run would
    /// have recorded - and a leg turned away by a defect in this tool keeps the defect's exit code,
    /// where "no selected leg can run" alone would send the reader to the hosts.
    /// </summary>
    [Fact]
    public void WhenNothingCanRun_EachLegIsNamedWithItsVerdict_AndADefectKeepsItsCode()
    {
        var missing = new LegEntry { Leg = "mac", Verdict = LegVerdict.SkippedToolMissing, Detail = "ssh mac: 'cmake' is not installed there" };
        var off = new LegEntry { Leg = "pi", Verdict = LegVerdict.SkippedUnavailable, Detail = "ssh pi: ssh could not connect" };
        var defect = new LegEntry { Leg = "lin", Verdict = LegVerdict.Poisoned, Detail = "local: whether 'cmake' is there was never asked" };

        var unavailable = RepoHarness.Core.Runs.LegRunPlan.NothingRuns([missing, off]);

        Assert.Equal(LegsExit.Unavailable, unavailable.ExitCode);
        Assert.Equal("no selected leg can run", unavailable.Message);
        Assert.Equal(
            ["mac: skipped-tool-missing: ssh mac: 'cmake' is not installed there", "pi: skipped-unavailable: ssh pi: ssh could not connect"],
            unavailable.Details);

        Assert.Equal(HarnessExit.InternalError, RepoHarness.Core.Runs.LegRunPlan.NothingRuns([missing, defect]).ExitCode);
    }

    /// <summary>Every program there, found on the PATH or off it, and the leg runs.</summary>
    [Fact]
    public void AHostWithEveryProgram_RunsTheLeg_WhereverItFoundThem()
    {
        var config = Configured(generator: "Ninja");
        var host = Host(HostId.Local, config, ("cmake", ProgramFound.OffPath));

        Assert.Null(Place(config, LegWorkload.BuildAndTest, host).Reason);
    }

    /// <summary>
    /// A program that will not start is a cause this build can name, in one table both the command
    /// runner and the leg executor read. Anything else is not, and stays a defect.
    /// </summary>
    [Fact]
    public void AProgramThatWouldNotStart_IsANamedCause_InTheOneTableBothReaders()
    {
        Assert.Equal(HarnessExit.ToolMissing, KnownCauses.ExitCodeFor(new ExecutableNotFoundException("cmake")));
        Assert.True(KnownCauses.Names(new ProgramStartException("./bench", "'./bench' could not be started: Exec format error")));
        Assert.Null(KnownCauses.ExitCodeFor(new InvalidOperationException("a defect")));
        Assert.False(KnownCauses.Names(new InvalidOperationException("a defect")));
    }

    /// <summary>
    /// A search directory that is not a whole path on the platform it is declared for would be looked
    /// in wherever a command happened to start, so one leg would find a tool and the next would not.
    /// Refused when the file is read, with a platform nobody has and a blank entry. Under 'all', an
    /// entry one kind of machine can name is kept, and searched where it can be.
    /// </summary>
    [Fact]
    public void ASearchDirectory_ThatNamesNoDirectoryWhereItIsDeclared_IsRefused()
    {
        var problems = HarnessConfigValidator.Validate(new HarnessConfig
        {
            ToolSearchDirectories =
            {
                ["macos"] = ["/opt/homebrew/bin", "~/.local/bin", "tools/bin", " ", @"C:\tools"],
                ["windows"] = [@"C:\tools", @"\\server\share\bin", "/opt/tools", "C:tools"],
                ["all"] = ["/opt/tools", @"D:\tools", "~/bin", "bin"],
                ["beos"] = ["/boot/bin"],
            },
        });

        Assert.Contains(problems, problem => problem.Contains("toolSearchDirectories.macos lists 'tools/bin', which names no directory there", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains(@"toolSearchDirectories.macos lists 'C:\tools'", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("toolSearchDirectories.macos has a blank entry", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("toolSearchDirectories.windows lists '/opt/tools'", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("toolSearchDirectories.windows lists 'C:tools'", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("toolSearchDirectories.all lists 'bin', which names no directory on any platform", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("platform 'beos'", StringComparison.Ordinal));

        Assert.DoesNotContain(problems, problem => problem.Contains("'/opt/homebrew/bin'", StringComparison.Ordinal));
        Assert.DoesNotContain(problems, problem => problem.Contains("'~/.local/bin'", StringComparison.Ordinal));
        Assert.DoesNotContain(problems, problem => problem.Contains(@"windows lists 'C:\tools'", StringComparison.Ordinal));
        Assert.DoesNotContain(problems, problem => problem.Contains(@"'\\server\share\bin'", StringComparison.Ordinal));
        Assert.DoesNotContain(problems, problem => problem.Contains("all lists '/opt/tools'", StringComparison.Ordinal));
        Assert.DoesNotContain(problems, problem => problem.Contains(@"all lists 'D:\tools'", StringComparison.Ordinal));
        Assert.DoesNotContain(problems, problem => problem.Contains("'~/bin'", StringComparison.Ordinal));
        Assert.DoesNotContain(problems, problem => problem.Contains("'/boot/bin'", StringComparison.Ordinal));
    }

    /// <summary>
    /// A host answers a survey about its programs with the function its own leg will use, and says
    /// which directories that leg will append to its PATH - the emulators' own programs among what it
    /// found, in the same pass, so a launcher off the PATH is handed on like a build tool.
    /// </summary>
    [Fact]
    public async Task AHostAnswersWhereItsProgramsAre_WithTheSearchItsOwnLegWillUse()
    {
        using var temp = new TempDirectory();
        var onPath = Directory.CreateDirectory(temp.Combine("path")).FullName;
        var searched = Directory.CreateDirectory(temp.Combine("searched")).FullName;

        // As this machine is, because the searched directories are read for the host's own platform.
        var windows = OperatingSystem.IsWindows();
        var exe = windows ? ".exe" : string.Empty;

        Executable(onPath, "cmake" + exe);
        Executable(searched, "ninja" + exe);
        Executable(searched, "qemu-probe" + exe);

        var platform = HostDoubles.Platform(windows ? PlatformId.Windows : PlatformId.Linux, "arm64", temp.Path);
        var fileSystem = new RepoHarness.Core.FileSystem.PhysicalFileSystem(FilePermissionsFactory.Create());
        var identity = Substitute.For<IToolIdentityProvider>();
        identity.Current.Returns(new ToolIdentity("1.2.3", "abc123"));

        var processRunner = new ProcessRunner(new HostPlatform(), FilePermissionsFactory.Create());
        var agent = new HostAgentService(
            platform,
            identity,
            new EmulatorProbe(platform, processRunner, fileSystem),
            new DeveloperEnvironmentProbe(platform, processRunner),
            fileSystem,
            new LocalProgramResolver(platform, Permissions(), () => onPath),
            new KeepAwake(processRunner, new ConsoleHarnessOutput(new StringWriter(), new StringWriter(), verbose: false)));

        // An emulator for another kind of host, so the witness never runs: only the search is measured.
        var emulators = new Dictionary<string, EmulatorConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["qemu"] = new()
            {
                HostOs = "macos",
                HostProcessor = "arm64",
                Processor = "x86_64",
                Requires = ["qemu-probe"],
                Witness = new EmulatorWitness { Command = ["/bin/true"], Pattern = "x" },
            },
        };

        var info = await agent.DescribeAsync(
            emulators,
            new Dictionary<string, DeveloperEnvironmentConfig>(StringComparer.OrdinalIgnoreCase),
            ["cmake", "ninja", "absent"],
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase) { [platform.PlatformKey] = [searched] },
            RoomQuestions.None,
            TestContext.Current.CancellationToken);

        Assert.Equal(ProgramFound.OnPath, Answer(info, "cmake").Found);
        Assert.Equal(ProgramFound.OffPath, Answer(info, "ninja").Found);
        Assert.Equal(ProgramFound.Nowhere, Answer(info, "absent").Found);
        Assert.Equal(ProgramFound.OffPath, Answer(info, "qemu-probe").Found);
        Assert.Equal([onPath, searched], info.ProgramDirectories);
    }

    /// <summary>
    /// Two programs whose names differ only in case are two files on Linux, and a host asked about
    /// both answers about both. Keyed by name in a map read back ignoring case, that answer was
    /// refused whole, and every leg on the host read as unavailable.
    /// </summary>
    [Fact]
    public void AnAnswerAboutProgramsDifferingOnlyInCase_ReadsBack_WithBoth()
    {
        var info = new HostAgentInfo
        {
            Version = "1.2.3",
            AssemblySha256 = "abc123",
            Os = "linux",
            Processor = "x86_64",
            Programs =
            [
                new("cmake", ProgramFound.OnPath, "/usr/bin/cmake"),
                new("CMake", ProgramFound.Nowhere),
            ],
        };

        var read = System.Text.Json.JsonSerializer.Deserialize<HostAgentInfo>(
            System.Text.Json.JsonSerializer.Serialize(info, HostAgentProtocol.JsonOptions),
            HostAgentProtocol.JsonOptions)!;

        Assert.Equal(ProgramFound.OnPath, Answer(read, "cmake").Found);
        Assert.Equal(ProgramFound.Nowhere, Answer(read, "CMake").Found);
    }

    /// <summary>What <paramref name="info"/> says of <paramref name="program"/>.</summary>
    private static ProgramLocation Answer(HostAgentInfo info, string program)
        => info.Programs.Single(location => string.Equals(location.Program, program, StringComparison.Ordinal));

    /// <summary>A configuration with one Linux leg building a cmake project and testing it.</summary>
    private static HarnessConfig Configured(string? generator, string? cc = null, string? cxx = null, string runner = "ctest")
    {
        var toolchain = new ToolchainConfig { Platforms = ["linux"], Generator = generator };

        if (cc is not null)
        {
            toolchain.Env["CC"] = cc;
        }

        if (cxx is not null)
        {
            toolchain.Env["CXX"] = cxx;
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
                    Test = new TestConfig { All = new TestInvocation { Runner = runner, SuccessPattern = "tests passed" } },
                },
            },
            Legs = { ["lin"] = new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug", Toolchain = "gcc" } },
            Tools = { new ToolConfig { Name = "tclsh" } },
        };
    }

    /// <summary>Places the configuration's one leg on the first of <paramref name="measured"/> that takes it.</summary>
    private static LegPlacement Place(HarnessConfig config, LegWorkload workload, params HostReport[] measured)
        => LegPlacement.Place(config, new SelectedLeg("lin", config.Legs["lin"]), workload, measured.ToDictionary(report => report.Host));

    /// <summary>A Linux host that found every program the configuration wants, except as <paramref name="overrides"/> say.</summary>
    private static HostReport Host(HostId host, HarnessConfig config, params (string Program, ProgramFound Found)[] overrides)
    {
        var programs = LegPrograms.Wanted(config, LegWorkload.BuildAndTest).ToDictionary(
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

}
