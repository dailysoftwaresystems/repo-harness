using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Output;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// Which hosts are measured for a selection, and what a measurement that fails does to the check. Reaching a
/// remote host costs a connection, one that is switched off costs its whole connect timeout, and measuring
/// one can install software there, so nothing is measured that no leg needs.
/// </summary>
public sealed class LegsServiceTests
{
    private static readonly string Root = TestHost.TemporaryRoot;

    private static readonly Dictionary<HostId, HostReport> Measurements = new()
    {
        [HostId.Local] = new() { Host = HostId.Local, Os = "linux", Processor = "x86_64" },
        [HostId.Wsl("Ubuntu")] = new() { Host = HostId.Wsl("Ubuntu"), Os = "linux", Processor = "x86_64" },
        [HostId.Ssh("pi")] = new() { Host = HostId.Ssh("pi"), Os = "linux", Processor = "arm64" },
    };

    [Fact]
    public async Task LegsThisMachineCanRun_MeasureNoOtherHost()
    {
        var fixture = Create(new() { ["a"] = HostDoubles.Leg("linux", "x86_64"), ["b"] = HostDoubles.Leg("linux", "x86_64") });

        var report = await fixture.Service.CheckAsync(Root, null, LegWorkload.BuildAndTest, here: null, TestContext.Current.CancellationToken);

        Assert.True(report.Passed);
        Assert.Equal([HostId.Local], fixture.Inspector.Inspected);
    }

    [Fact]
    public async Task LegsThisMachineCannotRun_HaveTheirCandidatesMeasured_EachOnce()
    {
        var fixture = Create(new() { ["arm-a"] = HostDoubles.Leg("linux", "arm64"), ["arm-b"] = HostDoubles.Leg("linux", "arm64") });

        var report = await fixture.Service.CheckAsync(Root, null, LegWorkload.BuildAndTest, here: null, TestContext.Current.CancellationToken);

        Assert.All(report.Placements, placement => Assert.Equal(HostId.Ssh("pi"), placement.Host?.Host));
        Assert.Equal(3, fixture.Inspector.Inspected.Count);
        Assert.Equal(fixture.Inspector.Inspected.Count, fixture.Inspector.Inspected.Distinct().Count());
    }

    [Fact]
    public async Task ALegNoHostCanRun_IsAWarning_ThatNamesItAndSaysWhy()
    {
        var fixture = Create(new() { ["mac"] = HostDoubles.Leg("macos", "arm64"), ["native"] = HostDoubles.Leg("linux", "x86_64") });

        var report = await fixture.Service.CheckAsync(Root, null, LegWorkload.BuildAndTest, here: null, TestContext.Current.CancellationToken);

        Assert.True(report.Passed);
        Assert.Contains(
            "legs: WARN - leg 'mac' cannot run: local: it runs linux, and the leg needs macos",
            fixture.Error.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALegThatIsNotLinux_NeverHasAWslDistributionMeasured()
    {
        // A distribution runs Linux only, and measuring one installs DssHarness there, for a leg it can never run.
        var fixture = Create(new() { ["mac"] = HostDoubles.Leg("macos", "arm64") });

        await fixture.Service.CheckAsync(Root, null, LegWorkload.BuildAndTest, here: null, TestContext.Current.CancellationToken);

        Assert.Equal([HostId.Local, HostId.Ssh("pi")], fixture.Inspector.Inspected);
    }

    [Fact]
    public async Task ALegThatNamesItsHost_MeasuresOnlyThatHost()
    {
        var fixture = Create(new()
        {
            ["pinned"] = new LegConfig { Os = "linux", Processor = "arm64", Config = "debug", Ssh = "pi" },
        });

        await fixture.Service.CheckAsync(Root, null, LegWorkload.BuildAndTest, here: null, TestContext.Current.CancellationToken);

        Assert.Equal([HostId.Ssh("pi")], fixture.Inspector.Inspected);
    }

    [Fact]
    public async Task AnUnknownName_IsRefused_BeforeAnyHostIsMeasured()
    {
        // Measuring can install software on a host, so a typo must never get that far.
        var fixture = Create(new() { ["a"] = HostDoubles.Leg("linux", "x86_64") });

        var exception = await Assert.ThrowsAsync<HarnessException>(
            () => fixture.Service.CheckAsync(Root, ["nope"], LegWorkload.BuildAndTest, here: null, TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.UsageError, exception.ExitCode);
        Assert.Empty(fixture.Inspector.Inspected);
    }

    /// <summary>
    /// Each host is asked only about the developer environments the selected legs start their work
    /// in - never one no selected leg names, and none at all for a copy, which starts nothing.
    /// </summary>
    [Fact]
    public async Task EachHost_IsAskedOnlyAboutTheDeveloperEnvironmentsTheSelectedLegsStartIn()
    {
        var fixture = Create(
            new()
            {
                ["msvc"] = new LegConfig { Os = "windows", Processor = "x86_64", Config = "debug", Toolchain = "msvc" },
                ["mingw"] = new LegConfig { Os = "windows", Processor = "x86_64", Config = "debug", Toolchain = "mingw" },
            },
            configure: config =>
            {
                config.DeveloperEnvironments["vs"] = new DeveloperEnvironmentConfig { Kind = DeveloperEnvironmentKinds.VisualStudio };
                config.DeveloperEnvironments["unused"] = new DeveloperEnvironmentConfig { Kind = DeveloperEnvironmentKinds.VisualStudio };
                config.Toolchains["msvc"] = new ToolchainConfig { Platforms = ["windows"], Env = { ["CC"] = "cl" }, DeveloperEnvironment = "vs" };
                config.Toolchains["mingw"] = new ToolchainConfig { Platforms = ["windows"], Env = { ["CC"] = "gcc" } };
            });

        await fixture.Service.CheckAsync(Root, null, LegWorkload.BuildAndTest, here: null, TestContext.Current.CancellationToken);

        Assert.NotEmpty(fixture.Inspector.DeveloperEnvironmentsAsked);
        Assert.All(fixture.Inspector.DeveloperEnvironmentsAsked, asked => Assert.Equal(["vs"], asked.Keys));

        var copying = Create(
            new() { ["msvc"] = new LegConfig { Os = "windows", Processor = "x86_64", Config = "debug", Toolchain = "msvc" } },
            configure: config =>
            {
                config.DeveloperEnvironments["vs"] = new DeveloperEnvironmentConfig { Kind = DeveloperEnvironmentKinds.VisualStudio };
                config.Toolchains["msvc"] = new ToolchainConfig { Platforms = ["windows"], Env = { ["CC"] = "cl" }, DeveloperEnvironment = "vs" };
            });

        await copying.Service.CheckAsync(Root, null, LegWorkload.Copy, here: null, TestContext.Current.CancellationToken);

        Assert.NotEmpty(copying.Inspector.DeveloperEnvironmentsAsked);
        Assert.All(copying.Inspector.DeveloperEnvironmentsAsked, Assert.Empty);
    }

    [Fact]
    public async Task EachHost_ChecksOnlyTheEmulatorsTheSelectedLegsUse()
    {
        var fixture = Create(
            new()
            {
                ["emulated"] = new LegConfig { Os = "linux", Processor = "arm64", Emulator = "qemu-arm64", Config = "debug" },
                ["native"] = HostDoubles.Leg("linux", "x86_64"),
            },
            configure: config =>
            {
                config.Emulators["qemu-arm64"] = Emulator();
                config.Emulators["unused"] = Emulator();
            });

        await fixture.Service.CheckAsync(Root, null, LegWorkload.BuildAndTest, here: null, TestContext.Current.CancellationToken);

        Assert.NotEmpty(fixture.Inspector.EmulatorsAsked);
        Assert.All(fixture.Inspector.EmulatorsAsked, asked => Assert.Equal(["qemu-arm64"], asked.Keys));
    }

    [Fact]
    public async Task AHostThatRefusesTheRun_StopsTheCheck_AfterWhatTheOtherHostsDidIsReported()
    {
        // Hosts are measured together, so one can be updated while another refuses; that update happened all the same.
        var refusal = new HarnessException(HarnessExit.Refused, "ssh pi has DssHarness 1.3.0, newer than this machine's 1.2.0");

        var fixture = Create(
            new() { ["arm"] = HostDoubles.Leg("linux", "arm64") },
            inspect: host => host.Kind switch
            {
                HostKind.Ssh => throw refusal,
                HostKind.Wsl => Measurements[host] with { Actions = ["updated DssHarness 1.1.9 to 1.2.0"] },
                _ => Measurements[host],
            });

        var exception = await Assert.ThrowsAsync<HarnessException>(
            () => fixture.Service.CheckAsync(Root, null, LegWorkload.BuildAndTest, here: null, TestContext.Current.CancellationToken));

        Assert.Same(refusal, exception);
        Assert.Contains("legs: wsl Ubuntu: updated DssHarness 1.1.9 to 1.2.0", fixture.Output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A leg no host MATCHES is a complete answer: the survey looked and says so. A host that could
    /// not be REACHED is not, and 'OK - 6 of 8' differs from 'OK - 8 of 8' only by a number
    /// somebody has to parse out of prose. Incomplete is the code the run verbs already use for
    /// exactly this shape.
    /// </summary>
    [Fact]
    public void AHostThatDidNotAnswer_MakesTheSurveyIncomplete_NotAnOk()
    {
        var report = new LegsReport(
            [new LegPlacement(new SelectedLeg("a", HostDoubles.Leg("linux", "x86_64")), Reachable, Reason: null)],
            [
                new HostReport { Host = HostId.Local, Os = "linux", Processor = "x86_64" },
                new HostReport { Host = HostId.Ssh("vps"), Reason = "the connection was refused" },
            ],
            Named: false);

        var outcome = LegsReports.Render(report, json: false);

        Assert.Equal(HarnessExit.Incomplete, outcome.ExitCode);
        Assert.Contains("ssh vps (the connection was refused)", outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A host that answered and turned out unable to run legs is named with what it answered, and
    /// never said to have been silent. One verb for every host with a reason is how a host busy
    /// with a run read as unreachable in the one line a log reader sees, while the reason that
    /// said otherwise sat on a line of its own further up.
    /// </summary>
    [Fact]
    public void AHostThatAnsweredButCannotRunLegs_IsNamedWithItsOwnReason_NotAsSilent()
    {
        var report = new LegsReport(
            [new LegPlacement(new SelectedLeg("a", HostDoubles.Leg("linux", "x86_64")), Reachable, Reason: null)],
            [
                new HostReport { Host = HostId.Local, Os = "linux", Processor = "x86_64" },
                new HostReport
                {
                    Host = HostId.Wsl("Ubuntu"),
                    Reason = "DssHarness needs the .NET 10 SDK there, and it has 8.0.100",
                },
            ],
            Named: false);

        var outcome = LegsReports.Render(report, json: false);

        Assert.Equal(HarnessExit.Incomplete, outcome.ExitCode);
        Assert.Contains(
            "wsl Ubuntu (DssHarness needs the .NET 10 SDK there, and it has 8.0.100)",
            outcome.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("did not answer", outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>Every host answering is an unqualified OK, which is the whole point of the other one.</summary>
    [Fact]
    public void EveryHostAnswering_IsStillSuccess()
    {
        var report = new LegsReport(
            [new LegPlacement(new SelectedLeg("a", HostDoubles.Leg("linux", "x86_64")), Reachable, Reason: null)],
            [new HostReport { Host = HostId.Local, Os = "linux", Processor = "x86_64" }],
            Named: false);

        Assert.Equal(HarnessExit.Success, LegsReports.Render(report, json: false).ExitCode);
    }

    /// <summary>A host a leg can be placed on, which is what makes a placement runnable.</summary>
    private static HostReport Reachable { get; } = new() { Host = HostId.Local, Os = "linux", Processor = "x86_64" };

    private static EmulatorConfig Emulator() => new()
    {
        HostOs = "linux",
        HostProcessor = "x86_64",
        Processor = "arm64",
        Witness = new EmulatorWitness { Command = ["/opt/arm64/uname"], Pattern = "aarch64" },
    };

    /// <summary>
    /// A leg whose host lacks the room its build needs is turned away before it starts, skipped-unavailable,
    /// saying what is free there and what it needs: started, it died with the disk full half way through.
    /// </summary>
    [Fact]
    public async Task ALegWhoseHostLacksTheRoomItsBuildNeeds_IsTurnedAway_SayingWhatIsFreeAndWhatItNeeds()
    {
        var fixture = Create(
            new() { ["arm"] = new LegConfig { Os = "linux", Processor = "arm64", Config = "debug", BuildSpaceGiB = 8 } },
            rooms: (_, path) => Room(path, exists: false, recorded: null, free: 3));

        var report = await fixture.Service.CheckAsync(Root, null, LegWorkload.BuildAndTest, here: null, TestContext.Current.CancellationToken);
        var placement = Assert.Single(report.Placements);

        Assert.False(placement.Runnable);
        Assert.Equal(LegVerdict.SkippedUnavailable, placement.Verdict);
        Assert.Equal("ssh pi: 3 GiB free on '/', and this leg needs ~8 GiB, as its buildSpaceGiB, 8, declares", placement.Reason);
    }

    /// <summary>
    /// Legs building on one filesystem of one host are counted together, in the order they were selected,
    /// because every build directory stays once built: a leg that does not fit beside those before it is turned
    /// away, naming them, and they are kept.
    /// </summary>
    [Fact]
    public async Task LegsSharingAHostsRoom_AreCountedTogether_AndTheOneThatNoLongerFitsIsTurnedAway()
    {
        var fixture = Create(
            new()
            {
                ["arm-debug"] = new LegConfig { Os = "linux", Processor = "arm64", Config = "debug", BuildSpaceGiB = 5 },
                ["arm-release"] = new LegConfig { Os = "linux", Processor = "arm64", Config = "release", BuildSpaceGiB = 5 },
            },
            configure: config => config.BuildConfigs["release"] = new BuildConfiguration(),
            rooms: (_, path) => Room(path, exists: false, recorded: null, free: 8));

        var report = await fixture.Service.CheckAsync(Root, null, LegWorkload.BuildAndTest, here: null, TestContext.Current.CancellationToken);

        Assert.True(report.Placements.Single(placement => placement.Leg.Name == "arm-debug").Runnable);

        var turned = report.Placements.Single(placement => placement.Leg.Name == "arm-release");
        Assert.False(turned.Runnable);
        Assert.EndsWith(", beside the ~5 GiB 'arm-debug' need there", turned.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A worktree's first build of a variant needs what the main checkout's copy of that variant came to on the
    /// same host - what a consumer's first builds of a new worktree's copy needed, and did not have.
    /// </summary>
    [Fact]
    public async Task AWorktreesFirstBuild_NeedsWhatTheMainCheckoutsCopyOfTheVariantCameTo()
    {
        var fixture = Create(
            new() { ["arm"] = new LegConfig { Os = "linux", Processor = "arm64", Config = "debug", Worktree = "feature" } },
            rooms: (_, path) => path.Contains(".worktree-", StringComparison.Ordinal)
                ? Room(path, exists: false, recorded: null, free: 6)
                : Room(path, exists: true, recorded: 7L << 30, free: 6));

        var report = await fixture.Service.CheckAsync(Root, null, LegWorkload.BuildAndTest, here: null, TestContext.Current.CancellationToken);
        var placement = Assert.Single(report.Placements);

        Assert.False(placement.Runnable);
        Assert.Equal(
            "ssh pi: 6 GiB free on '/', and this leg needs ~7 GiB, what the main checkout's copy of the same variant came to there",
            placement.Reason);
    }

    /// <summary>
    /// What a build directory already holds counts against its need: one its own last build recorded needs no
    /// more room than it has, and one no build of this version recorded holds an amount nothing measured, so
    /// both are placed as they always were, however little is free.
    /// </summary>
    [Theory]
    [InlineData(8L << 30, null)]
    [InlineData(null, 100.0)]
    public async Task ABuildDirectoryAlreadyThere_CountsAgainstItsNeed_OrLeavesItUnknown(long? recorded, double? declared)
    {
        var fixture = Create(
            new() { ["arm"] = new LegConfig { Os = "linux", Processor = "arm64", Config = "debug", BuildSpaceGiB = declared } },
            rooms: (_, path) => Room(path, exists: true, recorded: recorded, free: 1));

        var report = await fixture.Service.CheckAsync(Root, null, LegWorkload.BuildAndTest, here: null, TestContext.Current.CancellationToken);

        Assert.True(Assert.Single(report.Placements).Runnable);
    }

    /// <summary>
    /// Two hosts are two disks, though each names its filesystem '/': legs on each are counted apart, and both
    /// fit where either alone does.
    /// </summary>
    [Fact]
    public async Task LegsOnTwoHosts_AreCountedApart_ThoughTheirFilesystemsShareAName()
    {
        var fixture = Create(
            new()
            {
                ["arm"] = new LegConfig { Os = "linux", Processor = "arm64", Config = "debug", BuildSpaceGiB = 5 },
                ["x64"] = new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug", Wsl = "Ubuntu", BuildSpaceGiB = 5 },
            },
            rooms: (_, path) => Room(path, exists: false, recorded: null, free: 8));

        var report = await fixture.Service.CheckAsync(Root, null, LegWorkload.BuildAndTest, here: null, TestContext.Current.CancellationToken);

        Assert.All(report.Placements, placement => Assert.True(placement.Runnable, placement.Reason));
        Assert.Equal(["ssh pi", "wsl Ubuntu"], report.Placements.Select(placement => placement.Host!.Host.ToString()).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// What a leg declares is its need before anything recorded; what its own copy's build recorded is before
    /// the main checkout's copy's.
    /// </summary>
    [Theory]
    [InlineData(2.0, null, true)]
    [InlineData(null, 3L << 30, true)]
    [InlineData(null, null, false)]
    public async Task ALegsNeed_IsWhatItDeclares_ThenWhatItsOwnCopyRecorded_ThenTheMainCheckouts(double? declared, long? own, bool fits)
    {
        var fixture = Create(
            new() { ["arm"] = new LegConfig { Os = "linux", Processor = "arm64", Config = "debug", Worktree = "feature", BuildSpaceGiB = declared } },
            rooms: (_, path) => path.Contains(".worktree-", StringComparison.Ordinal)
                ? Room(path, exists: own is not null, recorded: own, free: 5)
                : Room(path, exists: true, recorded: 10L << 30, free: 5));

        var report = await fixture.Service.CheckAsync(Root, null, LegWorkload.BuildAndTest, here: null, TestContext.Current.CancellationToken);

        Assert.Equal(fits, Assert.Single(report.Placements).Runnable);
    }

    /// <summary>
    /// Two legs that name one build directory need its room once: planning the run refuses them as sharing it,
    /// which a refusal for room the second never takes would otherwise hide.
    /// </summary>
    [Fact]
    public async Task TwoLegsNamingOneBuildDirectory_NeedItsRoomOnce()
    {
        var fixture = Create(
            new()
            {
                ["a"] = new LegConfig { Os = "linux", Processor = "arm64", Config = "debug", BuildSpaceGiB = 5 },
                ["b"] = new LegConfig { Os = "linux", Processor = "arm64", Config = "debug", BuildSpaceGiB = 5 },
            },
            rooms: (_, path) => Room(path, exists: false, recorded: null, free: 8));

        var report = await fixture.Service.CheckAsync(Root, null, LegWorkload.BuildAndTest, here: null, TestContext.Current.CancellationToken);

        Assert.All(report.Placements, placement => Assert.True(placement.Runnable, placement.Reason));
    }

    /// <summary>
    /// A WSL leg needs its room on this machine's drive where WSL keeps the distribution's disk as well as on the
    /// disk itself, and shares that drive with this machine's own legs: turned away where the drive lacks it,
    /// however much the virtual disk says it has.
    /// </summary>
    [Fact]
    public async Task AWslLeg_NeedsItsRoomOnTheDriveWhereWslKeepsItsDisk_BesideThisMachinesOwnLegs()
    {
        var drive = new DiskSpace(8L << 30, 500L << 30, "C:\\");
        var fixture = Create(
            new()
            {
                ["native"] = new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug", BuildSpaceGiB = 5 },
                ["wsl"] = new LegConfig { Os = "linux", Processor = "x86_64", Config = "release", Wsl = "Ubuntu", BuildSpaceGiB = 5 },
            },
            configure: config => config.BuildConfigs["release"] = new BuildConfiguration(),
            inspect: host => host.Kind == HostKind.Wsl
                ? Measurements[host] with { DiskImageSpace = drive }
                : Measurements[host],
            rooms: (host, path) => host.Kind == HostKind.Local
                ? new BuildDirectoryRoom(path, false, null, drive, null)
                : Room(path, exists: false, recorded: null, free: 800));

        var report = await fixture.Service.CheckAsync(Root, null, LegWorkload.BuildAndTest, here: null, TestContext.Current.CancellationToken);

        Assert.True(report.Placements.Single(placement => placement.Leg.Name == "native").Runnable);

        var turned = report.Placements.Single(placement => placement.Leg.Name == "wsl");
        Assert.False(turned.Runnable);
        Assert.Equal(
            "wsl Ubuntu: 8 GiB free on 'C:\\', where WSL keeps its disk, and this leg needs ~5 GiB, as its buildSpaceGiB, 5, declares, beside the ~5 GiB 'native' need there",
            turned.Reason);
    }

    /// <summary>
    /// A leg whose need is known, on a host whose room could not be measured, is placed - nothing says it will
    /// not fit - and said to be unchecked, with why, rather than placed as though it had been checked.
    /// </summary>
    [Fact]
    public async Task ALegWhoseRoomCouldNotBeMeasured_IsPlaced_AndSaidToBeUnchecked()
    {
        var fixture = Create(
            new() { ["arm"] = new LegConfig { Os = "linux", Processor = "arm64", Config = "debug", BuildSpaceGiB = 5 } },
            rooms: (_, path) => new BuildDirectoryRoom(path, false, null, null, "the drive of a UNC path cannot be measured"));

        var report = await fixture.Service.CheckAsync(Root, null, LegWorkload.BuildAndTest, here: null, TestContext.Current.CancellationToken);

        Assert.True(Assert.Single(report.Placements).Runnable);
        Assert.Contains(
            "leg 'arm' was placed on ssh pi without its room checked, which could not be measured there: the drive of a UNC path cannot be measured",
            fixture.Error.ToString(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A host is asked the room where its copies are kept and each build directory a leg would fill there; a
    /// command that builds nothing asks about none, and turns nothing away for room: clean is how room is made.
    /// </summary>
    [Fact]
    public async Task ACommandThatBuildsNothing_AsksAboutNoBuildDirectory_AndTurnsNothingAwayForRoom()
    {
        var fixture = Create(
            new() { ["arm"] = new LegConfig { Os = "linux", Processor = "arm64", Config = "debug", BuildSpaceGiB = 100 } },
            rooms: (_, path) => Room(path, exists: false, recorded: null, free: 1));

        var copying = await fixture.Service.CheckAsync(Root, null, LegWorkload.Copy, here: null, TestContext.Current.CancellationToken);

        Assert.True(Assert.Single(copying.Placements).Runnable);

        var asked = fixture.Inspector.RoomAsked.Single(entry => entry.Host == HostId.Ssh("pi")).Room;
        Assert.Equal("/home/pi/repo", asked.SpaceAt);
        Assert.Empty(asked.Builds);

        await fixture.Service.CheckAsync(Root, null, LegWorkload.BuildAndTest, here: null, TestContext.Current.CancellationToken);

        var building = fixture.Inspector.RoomAsked.Last(entry => entry.Host == HostId.Ssh("pi")).Room;
        Assert.Equal("/home/pi/repo/build/arm64-none-debug", Assert.Single(building.Builds));
    }

    /// <summary>What a host answers about a build directory: whether it is there, what it recorded, the room on '/'.</summary>
    private static BuildDirectoryRoom Room(string path, bool exists, long? recorded, long free)
        => new(path, exists, recorded, new DiskSpace(free << 30, 48L << 30, "/"), null);

    private static Fixture Create(
        Dictionary<string, LegConfig> legs,
        Action<HarnessConfig>? configure = null,
        Func<HostId, HostReport>? inspect = null,
        Func<HostId, string, BuildDirectoryRoom>? rooms = null)
    {
        var config = new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Hosts = new HostsConfig
            {
                Wsl = { ["Ubuntu"] = new WslHostConfig { RepositoryPath = "/home/dev/repo" } },
                Ssh = { ["pi"] = new SshHostConfig { RepositoryPath = "/home/pi/repo" } },
            },
        };

        foreach (var (name, leg) in legs)
        {
            config.Legs[name] = leg;
        }

        configure?.Invoke(config);

        var inspector = new RecordingInspector(inspect ?? (host => Measurements[host])) { BuildRooms = rooms };
        var output = new StringWriter();
        var error = new StringWriter();

        var service = new LegsService(HostDoubles.Loader(config, Root), inspector, HostDoubles.Platform(), new ConsoleHarnessOutput(output, error, verbose: false));

        return new Fixture(service, inspector, output, error);
    }

    private sealed record Fixture(LegsService Service, RecordingInspector Inspector, StringWriter Output, StringWriter Error);
}
