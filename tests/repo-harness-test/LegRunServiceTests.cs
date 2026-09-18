using System.Text.Json;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Runs;
using RepoHarness.Core.Sync;

namespace RepoHarness.Tests;

/// <summary>
/// What becomes of a leg whose host's copy cannot be brought up to date before it runs, and where a
/// leg goes. Each copy problem is about one tree at one moment, so the legs on that tree say so under
/// their own verdict and every other leg still reports. A tree another run held ended the whole run,
/// and a transport that would not start failed every leg on the tree, as though its code had.
/// </summary>
public sealed class LegRunServiceTests
{
    private const string HostName = "pi";

    private const string HostTree = "/home/pi/repo";

    /// <summary>
    /// A tree another run is building on cannot be replaced, so the legs that need it are
    /// refused-locked, naming the run in the way, and nothing is carried there. Their own variant's
    /// lock is free - another run holds another variant - so it is the refused sync alone that keeps
    /// them off a copy this run never brought up to date.
    /// </summary>
    [Fact]
    public async Task ATreeAnotherRunIsUsing_RefusesItsLegs_AndTheOtherLegsStillReport()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var sync = Substitute.For<ISyncService>();
        var runLock = new RunLock(harness.FileSystem, harness.Output, harness.Identity);

        await using var held = await runLock.AcquireAsync(
            new HarnessLayout(temp.Path, temp.Path),
            new LockRequest
            {
                Host = HostId.Ssh(HostName).ToString(),
                Tree = HostTree,
                Variant = "arm64-other-release",
                Scope = LockScope.TreeShared,
                RunId = RunId.New(),
                Command = "build",
            },
            TestContext.Current.CancellationToken);

        var verdicts = await RunAsync(temp, harness, TwoLegs(harness), SshAndLocal(harness), new LegRunRequest(temp.Path, null, Json: true) { Workload = LegWorkload.Copy }, runLock, sync);

        Assert.Equal("passed", verdicts["native"].Verdict);
        Assert.Equal("refused-locked", verdicts["arm"].Verdict);
        Assert.Contains(held.Entry.Holder.RunId, verdicts["arm"].Detail, StringComparison.Ordinal);
        await sync.DidNotReceiveWithAnyArgs().SyncAsync(default!, default!, default!, default!, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// ssh or wsl.exe that would not start on this machine never reached the host - which the runner
    /// that starts it says, as that host being unavailable: the legs on that tree are unavailable
    /// there, saying why, and no verdict is claimed about code that never ran.
    /// </summary>
    [Fact]
    public async Task ATransportThatWouldNotStart_LeavesItsLegsUnavailable_AndTheOtherLegsStillReport()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var sync = Substitute.For<ISyncService>();

        // The copy starts ssh through the runner every host command goes through, so what reaches the
        // run is what that runner makes of ssh not starting - never a refusal written for the test.
        var processes = Substitute.For<IProcessRunner>();
        processes.RunAsync(Arg.Any<ProcessRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ProgramStartException("ssh", "'ssh' could not be started: No such file or directory"));
        var hosts = new HostCommandRunner(processes);
        var pi = new HostConnection
        {
            Host = HostId.Ssh(HostName),
            Address = "192.0.2.10",
            User = "harness",
            Port = 22,
            KeyFile = temp.Combine(".harness-config", "sshItems", HostName, ".key"),
            KnownHostsFile = temp.Combine(".harness-config", "sshItems", HostName, "known_hosts"),
            ConnectTimeoutSeconds = 10,
            KeepAliveSeconds = 15,
            LocalDirectory = temp.Path,
        };

        var unreached = await Assert.ThrowsAnyAsync<Exception>(
            () => hosts.RunAsync(pi, new HostCommand { Program = "true" }, TestContext.Current.CancellationToken));

        sync.SyncAsync(default!, default!, default!, default!, TestContext.Current.CancellationToken)
            .ThrowsAsyncForAnyArgs(unreached);

        var verdicts = await RunAsync(temp, harness, TwoLegs(harness), SshAndLocal(harness), new LegRunRequest(temp.Path, null, Json: true) { Workload = LegWorkload.Copy }, sync: sync);

        Assert.Equal("passed", verdicts["native"].Verdict);
        Assert.Equal("skipped-unavailable", verdicts["arm"].Verdict);
        Assert.Equal($"ssh {HostName} could not be reached: 'ssh' could not be started: No such file or directory", verdicts["arm"].Detail);
    }

    /// <summary>
    /// A program of this machine's own that will not start during the sync - git, in the middle of an
    /// upgrade - is no host being unavailable: the leg has begun, so it fails, naming the program, as
    /// any leg whose program will not start once it is running does.
    /// </summary>
    [Fact]
    public async Task AProgramOfThisMachinesThatWouldNotStartDuringTheSync_FailsTheLeg()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var sync = Substitute.For<ISyncService>();

        sync.SyncAsync(default!, default!, default!, default!, TestContext.Current.CancellationToken)
            .ThrowsAsyncForAnyArgs(new ProgramStartException("/usr/bin/git", "'/usr/bin/git' could not be started: Text file busy"));

        var verdicts = await RunAsync(temp, harness, TwoLegs(harness), SshAndLocal(harness), new LegRunRequest(temp.Path, null, Json: true) { Workload = LegWorkload.Copy }, sync: sync);

        Assert.Equal("passed", verdicts["native"].Verdict);
        Assert.Equal("failed", verdicts["arm"].Verdict);
        Assert.Contains("'/usr/bin/git' could not be started", verdicts["arm"].Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A lock file nobody can use is no lock another run holds: it stops every run on every tree
    /// alike, so it ends this one as the refusal it is, rather than turning each leg away as locked
    /// and sending the reader to wait for a run that does not exist.
    /// </summary>
    [Fact]
    public async Task ALockFileThatCannotBeRead_EndsTheRun_RatherThanLockingEachLeg()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        temp.WriteFile(Path.Combine(".harness-config", "lock.json"), "not a lock file");

        var outcome = await OutcomeAsync(
            temp,
            harness,
            TwoLegs(harness),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null, Json: true) { Workload = LegWorkload.Copy });

        Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
        Assert.Contains("lock.json", outcome.Message, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(Assert.Single(outcome.Data));

        Assert.DoesNotContain(
            document.RootElement.GetProperty("legs").EnumerateArray(),
            leg => leg.GetProperty("verdict").GetString() == "refused-locked");
    }

    /// <summary>
    /// A leg goes where a sync puts its tree whether or not the run syncs, and a program that host
    /// lacks turns it away there. Moved by what each command starts, a run on what a build had staged
    /// went to a host that had the program and never had the tree.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALegIsTurnedAwayWhereItsTreeGoes_NeverMovedToAHostThatHasTheProgram(bool useStaged)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var platform = harness.Platform;

        var config = new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Hosts = new HostsConfig { Ssh = { [HostName] = new SshHostConfig { RepositoryPath = HostTree } } },
            Legs = { ["here"] = HostDoubles.Leg(platform.PlatformKey, platform.Processor) },
        };

        // This machine lacks what the command starts; the ssh host, which is the same kind of
        // machine, has it.
        var inspector = new RecordingInspector(host => new HostReport
        {
            Host = host,
            Os = platform.PlatformKey,
            Processor = platform.Processor,
            Programs = new Dictionary<string, ProgramLocation>(StringComparer.Ordinal)
            {
                ["rh-probe"] = new("rh-probe", host.Kind == HostKind.Local ? ProgramFound.Nowhere : ProgramFound.OnPath, "/usr/bin/rh-probe"),
            },
            Session = host.Kind == HostKind.Local ? null : Session(host),
        });

        var verdicts = await RunAsync(
            temp,
            harness,
            config,
            inspector,
            new LegRunRequest(temp.Path, null, Json: true, UseStaged: useStaged) { Workload = new LegWorkload(Build: false, Test: false, ["rh-probe"]) });

        Assert.Equal("skipped-tool-missing", verdicts["here"].Verdict);
        Assert.StartsWith("local: 'rh-probe' is not installed there", verdicts["here"].Detail, StringComparison.Ordinal);
    }

    /// <summary>A leg on this machine and one on the ssh host.</summary>
    private static HarnessConfig TwoLegs(HarnessFactory harness) => new()
    {
        BuildConfigs = { ["debug"] = new BuildConfiguration() },
        Hosts = new HostsConfig { Ssh = { [HostName] = new SshHostConfig { RepositoryPath = HostTree } } },
        Legs =
        {
            ["native"] = HostDoubles.Leg(harness.Platform.PlatformKey, harness.Platform.Processor),
            ["arm"] = new LegConfig { Os = "linux", Processor = "arm64", Config = "debug", Ssh = HostName },
        },
    };

    /// <summary>This machine as it is, and the ssh host as a Linux arm64 machine that answers.</summary>
    private static RecordingInspector SshAndLocal(HarnessFactory harness) => new(host => host.Kind == HostKind.Local
        ? new HostReport { Host = host, Os = harness.Platform.PlatformKey, Processor = harness.Platform.Processor }
        : new HostReport { Host = host, Os = "linux", Processor = "arm64", Session = Session(host) });

    private static HostSession Session(HostId host)
        => new(new HostConnection { Host = host, Address = "192.0.2.10" }, ".dotnet/tools/dssharness");

    /// <summary>
    /// Runs the command over <paramref name="config"/>, a leg on this machine passing, and returns
    /// each leg's verdict and detail as the ledger a script reads reports them. Nothing may run on
    /// another machine: a leg that reached one would be a leg this run should not have started.
    /// </summary>
    private static async Task<Dictionary<string, (string? Verdict, string? Detail)>> RunAsync(
        TempDirectory temp,
        HarnessFactory harness,
        HarnessConfig config,
        RecordingInspector inspector,
        LegRunRequest request,
        RunLock? runLock = null,
        ISyncService? sync = null)
    {
        var outcome = await OutcomeAsync(temp, harness, config, inspector, request, runLock, sync);

        using var document = JsonDocument.Parse(Assert.Single(outcome.Data));

        return document.RootElement.GetProperty("legs").EnumerateArray().ToDictionary(
            leg => leg.GetProperty("leg").GetString()!,
            leg => (leg.GetProperty("verdict").GetString(), leg.GetProperty("detail").GetString()));
    }

    /// <summary>Runs the command over <paramref name="config"/>, a leg on this machine passing, and returns how it ended.</summary>
    private static async Task<CommandOutcome> OutcomeAsync(
        TempDirectory temp,
        HarnessFactory harness,
        HarnessConfig config,
        RecordingInspector inspector,
        LegRunRequest request,
        RunLock? runLock = null,
        ISyncService? sync = null)
    {
        var loader = HostDoubles.Loader(config, temp.Path);

        var service = new LegRunService(
            loader,
            new LegsService(loader, inspector, harness.Output),
            new LegExecutor(harness.Platform, harness.Output),
            runLock ?? new RunLock(harness.FileSystem, harness.Output, harness.Identity),
            new LogOwnership(harness.FileSystem, harness.Output, harness.Identity),
            sync ?? Substitute.For<ISyncService>(),
            Substitute.For<ISyncTransportFactory>(),
            new RemoteLegRunner(new ScriptedHostCommands((_, command) => throw HostResults.Unexpected(command)), harness.Output),
            harness.Platform,
            harness.Output);

        return await service.RunAsync(
            "test",
            request,
            (work, _) => Task.FromResult(new LegEntry { Leg = work.Leg.Name, Verdict = LegVerdict.Passed }),
            TestContext.Current.CancellationToken);
    }
}
