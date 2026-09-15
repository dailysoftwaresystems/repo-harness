using NSubstitute;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Output;
using RepoHarness.Core.Repository;

namespace RepoHarness.Tests;

/// <summary>
/// Which hosts are measured for a selection. Reaching a remote host costs a connection, and one that
/// is switched off costs its whole connect timeout, so nothing is measured that no leg needs.
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
        var (service, inspector, _) = Create(new() { ["a"] = Leg("linux", "x86_64"), ["b"] = Leg("linux", "x86_64") });

        var report = await service.CheckAsync(Root, [], TestContext.Current.CancellationToken);

        Assert.True(report.Passed);
        Assert.Equal([HostId.Local], inspector.Inspected);
    }

    [Fact]
    public async Task LegsThisMachineCannotRun_HaveTheirCandidatesMeasured_EachOnce()
    {
        var (service, inspector, _) = Create(new() { ["arm-a"] = Leg("linux", "arm64"), ["arm-b"] = Leg("linux", "arm64") });

        var report = await service.CheckAsync(Root, [], TestContext.Current.CancellationToken);

        Assert.All(report.Placements, placement => Assert.Equal(HostId.Ssh("pi"), placement.Host?.Host));
        Assert.Equal(3, inspector.Inspected.Count);
        Assert.Equal(inspector.Inspected.Count, inspector.Inspected.Distinct().Count());
    }

    [Fact]
    public async Task ALegNoHostCanRun_IsAWarning_ThatNamesItAndSaysWhy()
    {
        var (service, _, error) = Create(new() { ["mac"] = Leg("macos", "arm64"), ["native"] = Leg("linux", "x86_64") });

        var report = await service.CheckAsync(Root, [], TestContext.Current.CancellationToken);

        Assert.True(report.Passed);
        Assert.Contains(
            "legs: WARN - leg 'mac' cannot run: local: it runs linux, and the leg needs macos",
            error.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALegThatNamesItsHost_MeasuresOnlyThatHost()
    {
        var (service, inspector, _) = Create(new()
        {
            ["pinned"] = new LegConfig { Os = "linux", Processor = "arm64", Config = "debug", Ssh = "pi" },
        });

        await service.CheckAsync(Root, [], TestContext.Current.CancellationToken);

        Assert.Equal([HostId.Ssh("pi")], inspector.Inspected);
    }

    private static (LegsService Service, RecordingInspector Inspector, StringWriter Error) Create(Dictionary<string, LegConfig> legs)
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

        var loader = Substitute.For<IHarnessContextLoader>();
        loader.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new HarnessContext(new HarnessLayout(Root, Root), config)));

        var inspector = new RecordingInspector(host => Measurements[host]);
        var error = new StringWriter();
        var output = new ConsoleHarnessOutput(new StringWriter(), error, verbose: false);

        return (new LegsService(loader, inspector, output), inspector, error);
    }

    private static LegConfig Leg(string os, string processor) => new() { Os = os, Processor = processor, Config = "debug" };
}
