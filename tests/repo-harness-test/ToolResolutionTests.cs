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

        Assert.Equal(["cmake", "ninja", "gcc", "g++", "ctest"], LegPrograms.For(config, config.Legs["lin"], LegWorkload.BuildAndTest));

        // A declared tool is asked about everywhere, so a leg starting it from a step finds it, but
        // it makes no leg unrunnable.
        Assert.Contains("tclsh", LegPrograms.Wanted(config, LegWorkload.BuildAndTest));
        Assert.DoesNotContain("tclsh", LegPrograms.For(config, config.Legs["lin"], LegWorkload.BuildAndTest));
    }

    /// <summary>Another generator starts another build tool, which is cmake's to find and to report.</summary>
    [Fact]
    public void ANonNinjaGenerator_AddsNoNinja()
    {
        var config = Configured(generator: "Unix Makefiles");

        Assert.DoesNotContain("ninja", LegPrograms.For(config, config.Legs["lin"], LegWorkload.BuildAndTest));
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

        Assert.Equal(["cmake", "ninja", "gcc"], LegPrograms.For(config, leg, LegWorkload.BuildOnly));
        Assert.Equal(["ctest"], LegPrograms.For(config, leg, new LegWorkload(Build: false, Test: true, [])));
        Assert.Empty(LegPrograms.For(config, leg, LegWorkload.Copy));
        Assert.Equal(["python3"], LegPrograms.For(config, leg, new LegWorkload(Build: false, Test: false, ["python3"])));
        Assert.Equal(["cmake", "ninja", "gcc", "python3"], LegPrograms.For(config, leg, new LegWorkload(Build: true, Test: false, ["python3"])));
    }

    /// <summary>
    /// Only a program named by name is looked for beforehand. A relative path is the tree's own, which
    /// a host's copy may not hold until the run's sync; a placeholder is filled in only once the leg
    /// runs; and a compiler variable naming a path is read whole by CMake, never cut at its first
    /// space into a "C:\Program" nobody has.
    /// </summary>
    [Fact]
    public void OnlyAProgramNamedByName_IsLookedForBeforehand()
    {
        var relativeRunner = Configured(generator: "Ninja", cc: @"C:\Program Files\LLVM\bin\clang-cl.exe", cxx: "ccache g++", runner: "scripts/test.sh");

        Assert.Equal(["cmake", "ninja", "ccache"], LegPrograms.For(relativeRunner, relativeRunner.Legs["lin"], LegWorkload.BuildAndTest));

        var builtRunner = Configured(generator: null, runner: "{buildDir}/tests");

        Assert.Equal(["cmake"], LegPrograms.For(builtRunner, builtRunner.Legs["lin"], LegWorkload.BuildAndTest));

        Assert.Equal(
            ["python3"],
            LegPrograms.For(builtRunner, builtRunner.Legs["lin"], new LegWorkload(Build: false, Test: false, ["python3", "./bench.sh", "{tool}", " "])));
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

            Assert.All(LegPrograms.For(config, config.Legs["lin"], workload), program => Assert.Contains(program, wanted));
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

        var reason = LegPlacement.Obstacle(config, config.Legs["lin"], LegWorkload.BuildAndTest, host);

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
    /// A leg every candidate turned away reports the gravest reason among them: a defect in this tool
    /// over anything, then a program a right host lacks - which somebody can install - over a host
    /// that could not take the leg at all, whichever candidate said which.
    /// </summary>
    [Fact]
    public void ALegEveryHostTurnedAway_ReportsTheGravestReason()
    {
        var config = Configured(generator: "Ninja");
        config.Hosts.Ssh["mac"] = new SshHostConfig { RepositoryPath = "/srv/repo" };

        var wrongMachine = Host(HostId.Local, config) with { Os = "windows" };
        var lacksCmake = Host(HostId.Ssh("mac"), config, ("cmake", ProgramFound.Nowhere));
        var neverAsked = new HostReport { Host = HostId.Ssh("mac"), Os = "linux", Processor = "x86_64" };
        var lacksCmakeHere = Host(HostId.Local, config, ("cmake", ProgramFound.Nowhere));
        var unreachable = new HostReport { Host = HostId.Ssh("mac"), Reason = "ssh could not connect" };

        Assert.Equal(LegVerdict.SkippedToolMissing, Place(config, LegWorkload.BuildAndTest, wrongMachine, lacksCmake).Verdict);
        Assert.Equal(LegVerdict.SkippedToolMissing, Place(config, LegWorkload.BuildAndTest, lacksCmakeHere, unreachable).Verdict);
        Assert.Equal(LegVerdict.Poisoned, Place(config, LegWorkload.BuildAndTest, lacksCmakeHere, neverAsked).Verdict);
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

        Assert.Null(LegPlacement.Obstacle(config, config.Legs["lin"], LegWorkload.BuildAndTest, host));
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

        Executable(onPath, "cmake");
        Executable(searched, "ninja");
        Executable(searched, "qemu-probe");

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
            ["cmake", "ninja", "absent"],
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase) { ["linux"] = [searched] },
            TestContext.Current.CancellationToken);

        Assert.Equal(ProgramFound.OnPath, info.Programs["cmake"].Found);
        Assert.Equal(ProgramFound.OffPath, info.Programs["ninja"].Found);
        Assert.Equal(ProgramFound.Nowhere, info.Programs["absent"].Found);
        Assert.Equal(ProgramFound.OffPath, info.Programs["qemu-probe"].Found);
        Assert.Equal([onPath, searched], info.ProgramDirectories);
    }

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
