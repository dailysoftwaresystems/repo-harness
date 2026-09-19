using NSubstitute;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;

namespace RepoHarness.Tests;

/// <summary>
/// An emulator counts only once its witness proves it runs programs for its processor. The launcher
/// here is this test assembly started as a child, standing in for qemu: it prints its argument, so
/// each test decides exactly what the witness prints.
/// </summary>
public sealed class EmulatorProbeTests
{
    [Fact]
    public async Task CheckAsync_Passes_WhenTheWitnessPrintsWhatOnlyTheEmulatedProcessorWould()
    {
        var emulator = Emulator(prints: "aarch64", pattern: @"^\[aarch64\]$");
        var check = await Probe().CheckAsync(emulator, Found(emulator), TestContext.Current.CancellationToken);

        Assert.True(check.Available, check.Reason);
        Assert.Equal("[aarch64]", check.Witnessed);
    }

    [Fact]
    public async Task CheckAsync_Refuses_AWitnessWhoseOutputDoesNotMatch()
    {
        // A launcher that quietly ran the program natively would print the host's own processor.
        var emulator = Emulator(prints: "x86_64", pattern: @"^\[aarch64\]$");
        var check = await Probe().CheckAsync(emulator, Found(emulator), TestContext.Current.CancellationToken);

        Assert.False(check.Available);
        Assert.Contains("does not match", check.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAsync_Refuses_AnEmulatorForAnotherKindOfHost_WithoutRunningAnything()
    {
        var runner = Substitute.For<IProcessRunner>();
        var probe = new EmulatorProbe(Platform("macos", "arm64"), runner, FileSystem());

        var emulator = Emulator(prints: "aarch64", pattern: "x");
        var check = await probe.CheckAsync(emulator, Found(emulator), TestContext.Current.CancellationToken);

        Assert.False(check.Available);
        Assert.Contains("it runs on linux x86_64 hosts, and this one is macos arm64", check.Reason, StringComparison.Ordinal);
        _ = runner.DidNotReceiveWithAnyArgs().RunAsync(null!, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("definitely-not-a-real-tool-xyzzy")]
    [InlineData("/definitely/not/a/sysroot")]
    public async Task CheckAsync_NamesARequirementThatIsMissing(string requirement)
    {
        var emulator = Emulator(prints: "aarch64", pattern: "x", requires: [requirement]);
        var check = await Probe().CheckAsync(emulator, Found(emulator), TestContext.Current.CancellationToken);

        Assert.False(check.Available);
        Assert.Equal($"{requirement} is missing", check.Reason);
    }

    [Fact]
    public async Task CheckAsync_ReportsAWitnessThatCannotStart_AsThisEmulatorBeingUnavailable()
    {
        // What a host without the emulation installed does with a program for another processor: the file
        // is there, and the system refuses to run it. That rules out the emulator, not the whole host.
        using var temp = new TempDirectory();
        var witness = temp.WriteFile(OperatingSystem.IsWindows() ? "foreign.exe" : "foreign", "not a program for this machine\n");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(witness, File.GetUnixFileMode(witness) | UnixFileMode.UserExecute);
        }

        var emulator = new EmulatorConfig
        {
            HostOs = "linux",
            HostProcessor = "x86_64",
            Processor = "arm64",
            Witness = new EmulatorWitness { Command = [witness], Pattern = "x" },
        };

        var check = await Probe().CheckAsync(emulator, Found(emulator), TestContext.Current.CancellationToken);

        Assert.False(check.Available);
        Assert.StartsWith("its witness could not start: ", check.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAsync_ReportsAWitnessThatFails()
    {
        var emulator = new EmulatorConfig
        {
            HostOs = "linux",
            HostProcessor = "x86_64",
            Processor = "arm64",
            Launcher = [TestHost.DotnetExecutable, "exec", TestHost.AssemblyPath],
            Env = { [TestHost.ChildModeVariable] = "exit" },
            Witness = new EmulatorWitness { Command = ["7"], Pattern = "x" },
        };

        var check = await Probe().CheckAsync(emulator, Found(emulator), TestContext.Current.CancellationToken);

        Assert.False(check.Available);
        Assert.Contains("exited 7", check.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARequirementInstalledOffThePath_IsPresent_WhereTheSearchFoundIt()
    {
        // Homebrew's qemu over ssh on macOS: in a searched directory, and on no PATH a command there sees.
        using var temp = new TempDirectory();
        temp.WriteProgram("searched", "qemu-probe");
        var searched = temp.Combine("searched");

        var emulator = Emulator(prints: "aarch64", pattern: @"^\[aarch64\]$", requires: ["qemu-probe"]);
        var found = Resolver().Resolve(EmulatorProbe.ProgramsOf(emulator), [searched]);

        var check = await Probe().CheckAsync(emulator, found, TestContext.Current.CancellationToken);

        Assert.True(check.Available, check.Reason);
        Assert.Equal(ProgramFound.OffPath, found.Found["qemu-probe"].Found);
    }

    [Fact]
    public async Task TheWitness_IsStartedWithTheDirectoriesALegsRunIsGiven()
    {
        var runner = Substitute.For<IProcessRunner>();
        ProcessRequest? started = null;

        runner.RunAsync(Arg.Do<ProcessRequest>(request => started = request), Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(0, "[aarch64]", string.Empty, TimeSpan.Zero, TimedOut: false));

        var emulator = Emulator(prints: "aarch64", pattern: @"^\[aarch64\]$");
        var found = new ProgramSearch(Found(emulator).Found, [Path.Combine(TestHost.TemporaryRoot, "tools")]);

        var check = await new EmulatorProbe(Platform("linux", "x86_64"), runner, FileSystem())
            .CheckAsync(emulator, found, TestContext.Current.CancellationToken);

        Assert.True(check.Available, check.Reason);
        Assert.Equal(found.Directories, started?.AppendToPath);
    }

    [Fact]
    public async Task ARequirementNobodyCouldLookFor_IsSaidToBeUnknown_NotMissing()
    {
        var emulator = Emulator(prints: "aarch64", pattern: "x", requires: ["qemu-x86_64"]);
        var found = new ProgramSearch(
            new Dictionary<string, ProgramLocation>(StringComparer.Ordinal)
            {
                ["qemu-x86_64"] = new("qemu-x86_64", ProgramFound.Unreadable, Reason: "the home directory is not known here"),
            },
            []);

        var check = await Probe().CheckAsync(emulator, found, TestContext.Current.CancellationToken);

        Assert.False(check.Available);
        Assert.Equal("whether 'qemu-x86_64' is there could not be established: the home directory is not known here", check.Reason);
    }

    /// <summary>
    /// The program a witness starts, named by name, is read from the search as a requirement is: one
    /// nobody could look for everywhere is unknown, and one looked for everywhere and not found is
    /// missing. Neither is started: started anyway, one nobody could look for everywhere would be
    /// reported as not found.
    /// </summary>
    [Theory]
    [InlineData(ProgramFound.Unreadable, "whether 'qemu-aarch64' is there could not be established: the home directory is not known here")]
    [InlineData(ProgramFound.Nowhere, "qemu-aarch64 is missing")]
    public async Task AWitnessProgramTheSearchDidNotFind_IsNeverStarted(ProgramFound found, string reason)
    {
        var runner = Substitute.For<IProcessRunner>();
        var emulator = new EmulatorConfig
        {
            HostOs = "linux",
            HostProcessor = "x86_64",
            Processor = "arm64",
            Witness = new EmulatorWitness { Command = ["qemu-aarch64", "./witness"], Pattern = "x" },
        };
        var search = new ProgramSearch(
            new Dictionary<string, ProgramLocation>(StringComparer.Ordinal)
            {
                ["qemu-aarch64"] = new("qemu-aarch64", found, Reason: found == ProgramFound.Unreadable ? "the home directory is not known here" : null),
            },
            []);

        var check = await new EmulatorProbe(Platform("linux", "x86_64"), runner, FileSystem())
            .CheckAsync(emulator, search, TestContext.Current.CancellationToken);

        Assert.False(check.Available);
        Assert.Equal(reason, check.Reason);
        await runner.DidNotReceiveWithAnyArgs().RunAsync(default!, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// An emulator whose environment sets PATH finds its programs on that PATH, which no search sees:
    /// a name the search missed is no missing one, and the witness is started to find out.
    /// </summary>
    [Fact]
    public async Task AnEmulatorWhoseEnvironmentSetsPath_IsNotTurnedAwayForWhatTheSearchMissed()
    {
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(Arg.Any<ProcessRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessResult(0, "aarch64", string.Empty, TimeSpan.Zero, TimedOut: false));

        var emulator = new EmulatorConfig
        {
            HostOs = "linux",
            HostProcessor = "x86_64",
            Processor = "arm64",
            Requires = ["qemu-aarch64"],
            Env = { ["PATH"] = "/opt/qemu-8/bin:/usr/bin:/bin" },
            Witness = new EmulatorWitness { Command = ["qemu-aarch64", "./witness"], Pattern = "aarch64" },
        };
        var search = new ProgramSearch(
            new Dictionary<string, ProgramLocation>(StringComparer.Ordinal) { ["qemu-aarch64"] = new("qemu-aarch64", ProgramFound.Nowhere) },
            []);

        var check = await new EmulatorProbe(Platform("linux", "x86_64"), runner, FileSystem())
            .CheckAsync(emulator, search, TestContext.Current.CancellationToken);

        Assert.True(check.Available, check.Reason);
        await runner.Received(1).RunAsync(Arg.Is<ProcessRequest>(request => request.FileName == "qemu-aarch64"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARequirementTheSearchWasNotGiven_IsADefect_NotAMissingProgram()
    {
        var emulator = Emulator(prints: "aarch64", pattern: "x", requires: ["qemu-x86_64"]);
        var nothing = new ProgramSearch(new Dictionary<string, ProgramLocation>(StringComparer.Ordinal), []);

        await Assert.ThrowsAsync<ArgumentException>(
            () => Probe().CheckAsync(emulator, nothing, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AnEmulatorsPrograms_AreItsNamedRequirementsAndWhatItsWitnessStarts_NeverAPath()
    {
        var emulator = new EmulatorConfig
        {
            HostOs = "linux",
            HostProcessor = "x86_64",
            Processor = "arm64",
            Launcher = ["qemu-aarch64", "-L", "/usr/aarch64-linux-gnu"],
            Requires = ["qemu-aarch64", "/usr/aarch64-linux-gnu", "binfmt-probe"],
            Witness = new EmulatorWitness { Command = ["./witness"], Pattern = "x" },
        };

        Assert.Equal(["qemu-aarch64", "qemu-aarch64", "binfmt-probe"], EmulatorProbe.ProgramsOf(emulator));
    }

    private static EmulatorConfig Emulator(string prints, string pattern, List<string>? requires = null) => new()
    {
        HostOs = "linux",
        HostProcessor = "x86_64",
        Processor = "arm64",
        Launcher = [TestHost.DotnetExecutable, "exec", TestHost.AssemblyPath],
        Env = { [TestHost.ChildModeVariable] = "echo-args" },
        Requires = requires ?? [],
        Witness = new EmulatorWitness { Command = [prints], Pattern = pattern },
    };

    /// <summary>What the search a leg on this machine uses finds of <paramref name="emulator"/>'s programs.</summary>
    private static ProgramSearch Found(EmulatorConfig emulator) => Resolver().Resolve(EmulatorProbe.ProgramsOf(emulator), []);

    /// <summary>The search a leg on this machine uses: its own PATH, then the directories given.</summary>
    private static LocalProgramResolver Resolver() => new(new HostPlatform(), FilePermissionsFactory.Create());

    private static EmulatorProbe Probe()
        => new(Platform("linux", "x86_64"), new ProcessRunner(new HostPlatform(), FilePermissionsFactory.Create()), FileSystem());

    private static IHostPlatform Platform(string os, string processor)
    {
        var platform = Substitute.For<IHostPlatform>();
        platform.PlatformKey.Returns(os);
        platform.Processor.Returns(processor);
        return platform;
    }

    private static PhysicalFileSystem FileSystem() => new(FilePermissionsFactory.Create());
}
