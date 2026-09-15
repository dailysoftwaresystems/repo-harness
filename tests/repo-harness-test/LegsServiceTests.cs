using RepoHarness.Core.Configuration;
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

        var report = await fixture.Service.CheckAsync(Root, null, TestContext.Current.CancellationToken);

        Assert.True(report.Passed);
        Assert.Equal([HostId.Local], fixture.Inspector.Inspected);
    }

    [Fact]
    public async Task LegsThisMachineCannotRun_HaveTheirCandidatesMeasured_EachOnce()
    {
        var fixture = Create(new() { ["arm-a"] = HostDoubles.Leg("linux", "arm64"), ["arm-b"] = HostDoubles.Leg("linux", "arm64") });

        var report = await fixture.Service.CheckAsync(Root, null, TestContext.Current.CancellationToken);

        Assert.All(report.Placements, placement => Assert.Equal(HostId.Ssh("pi"), placement.Host?.Host));
        Assert.Equal(3, fixture.Inspector.Inspected.Count);
        Assert.Equal(fixture.Inspector.Inspected.Count, fixture.Inspector.Inspected.Distinct().Count());
    }

    [Fact]
    public async Task ALegNoHostCanRun_IsAWarning_ThatNamesItAndSaysWhy()
    {
        var fixture = Create(new() { ["mac"] = HostDoubles.Leg("macos", "arm64"), ["native"] = HostDoubles.Leg("linux", "x86_64") });

        var report = await fixture.Service.CheckAsync(Root, null, TestContext.Current.CancellationToken);

        Assert.True(report.Passed);
        Assert.Contains(
            "legs: WARN - leg 'mac' cannot run: local: it runs linux, and the leg needs macos",
            fixture.Error.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALegThatIsNotLinux_NeverHasAWslDistributionMeasured()
    {
        // A distribution runs Linux only, and measuring one installs repo-harness there, for a leg it can never run.
        var fixture = Create(new() { ["mac"] = HostDoubles.Leg("macos", "arm64") });

        await fixture.Service.CheckAsync(Root, null, TestContext.Current.CancellationToken);

        Assert.Equal([HostId.Local, HostId.Ssh("pi")], fixture.Inspector.Inspected);
    }

    [Fact]
    public async Task ALegThatNamesItsHost_MeasuresOnlyThatHost()
    {
        var fixture = Create(new()
        {
            ["pinned"] = new LegConfig { Os = "linux", Processor = "arm64", Config = "debug", Ssh = "pi" },
        });

        await fixture.Service.CheckAsync(Root, null, TestContext.Current.CancellationToken);

        Assert.Equal([HostId.Ssh("pi")], fixture.Inspector.Inspected);
    }

    [Fact]
    public async Task AnUnknownName_IsRefused_BeforeAnyHostIsMeasured()
    {
        // Measuring can install software on a host, so a typo must never get that far.
        var fixture = Create(new() { ["a"] = HostDoubles.Leg("linux", "x86_64") });

        var exception = await Assert.ThrowsAsync<HarnessException>(
            () => fixture.Service.CheckAsync(Root, ["nope"], TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.UsageError, exception.ExitCode);
        Assert.Empty(fixture.Inspector.Inspected);
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

        await fixture.Service.CheckAsync(Root, null, TestContext.Current.CancellationToken);

        Assert.NotEmpty(fixture.Inspector.EmulatorsAsked);
        Assert.All(fixture.Inspector.EmulatorsAsked, asked => Assert.Equal(["qemu-arm64"], asked.Keys));
    }

    [Fact]
    public async Task AHostThatRefusesTheRun_StopsTheCheck_AfterWhatTheOtherHostsDidIsReported()
    {
        // Hosts are measured together, so one can be updated while another refuses; that update happened all the same.
        var refusal = new HarnessException(HarnessExit.Refused, "ssh pi has repo-harness 1.3.0, newer than this machine's 1.2.0");

        var fixture = Create(
            new() { ["arm"] = HostDoubles.Leg("linux", "arm64") },
            inspect: host => host.Kind switch
            {
                HostKind.Ssh => throw refusal,
                HostKind.Wsl => Measurements[host] with { Actions = ["updated repo-harness 1.1.9 to 1.2.0"] },
                _ => Measurements[host],
            });

        var exception = await Assert.ThrowsAsync<HarnessException>(
            () => fixture.Service.CheckAsync(Root, null, TestContext.Current.CancellationToken));

        Assert.Same(refusal, exception);
        Assert.Contains("legs: wsl Ubuntu: updated repo-harness 1.1.9 to 1.2.0", fixture.Output.ToString(), StringComparison.Ordinal);
    }

    private static EmulatorConfig Emulator() => new()
    {
        HostOs = "linux",
        HostProcessor = "x86_64",
        Processor = "arm64",
        Witness = new EmulatorWitness { Command = ["/opt/arm64/uname"], Pattern = "aarch64" },
    };

    private static Fixture Create(
        Dictionary<string, LegConfig> legs,
        Action<HarnessConfig>? configure = null,
        Func<HostId, HostReport>? inspect = null)
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

        var inspector = new RecordingInspector(inspect ?? (host => Measurements[host]));
        var output = new StringWriter();
        var error = new StringWriter();

        var service = new LegsService(HostDoubles.Loader(config, Root), inspector, new ConsoleHarnessOutput(output, error, verbose: false));

        return new Fixture(service, inspector, output, error);
    }

    private sealed record Fixture(LegsService Service, RecordingInspector Inspector, StringWriter Output, StringWriter Error);
}
