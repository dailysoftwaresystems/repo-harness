using System.Text.Json;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
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
    /// A host running a leg another machine dispatched to it runs it with the settings that machine's
    /// configuration gives it - its cores and its environment - and under its name wherever a reader
    /// sees one. Read as 'local', it ran with those of the machine that dispatched it.
    /// </summary>
    [Fact]
    public async Task ALegDispatchedHere_RunsWithTheSettingsOfTheHostItWasSentTo()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var platform = harness.Platform;

        var config = new HarnessConfig
        {
            Toolchains = { ["gcc"] = new ToolchainConfig { Platforms = [platform.PlatformKey] } },
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Projects = { new ProjectConfig { Name = "app", Type = "cmake", Path = "." } },
            Hosts = new HostsConfig
            {
                Local = new LocalHostConfig { BuildCores = 7, Env = { ["RH_HOST"] = "local" } },
                Ssh = { [HostName] = new SshHostConfig { RepositoryPath = HostTree, BuildCores = 3, Env = { ["RH_HOST"] = "pi" } } },
            },
            Legs =
            {
                ["arm"] = new LegConfig { Os = platform.PlatformKey, Processor = platform.Processor, Config = "debug", Toolchain = "gcc", Ssh = HostName },
            },
        };

        PlacedLeg? ran = null;

        var verdicts = await RunAsync(
            temp,
            harness,
            config,
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null, Json: true, Here: HostId.Ssh(HostName)) { Workload = LegWorkload.Copy },
            ran: leg => ran = leg);

        Assert.Equal("passed", verdicts["arm"].Verdict);
        Assert.NotNull(ran);

        // Run here, on this machine, and locked and scheduled as it - but read, and named, as the
        // host the machine that dispatched it knows.
        Assert.Equal(HostId.Local, ran.Host.Host);
        Assert.Equal(HostId.Ssh(HostName), ran.Named);
        Assert.Equal(3, ran.HostSettings.BuildCores);
        Assert.Equal($"ssh {HostName}", ran.IdentityFor("run").Host);
        Assert.Equal($"ssh {HostName}", ran.ToPlan().Host);

        var build = ran.BuildRequestFor(config, temp.Path);

        Assert.Equal(3, build.Cores);
        Assert.Equal("pi", build.HostEnvironment["RH_HOST"]);
    }

    /// <summary>
    /// A refusal is read by whoever typed the command, so it names the host as they know it: on a
    /// host running legs another machine dispatched to it, two legs sharing a build directory share
    /// it on that host, never on 'local' - which, to that reader, is their own machine.
    /// </summary>
    [Fact]
    public async Task ARefusalOnAHostSentLegs_NamesItAsTheMachineThatSentThemKnowsIt()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var platform = harness.Platform;

        var config = new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Hosts = new HostsConfig { Ssh = { [HostName] = new SshHostConfig { RepositoryPath = HostTree } } },
            Legs =
            {
                ["one"] = HostDoubles.Leg(platform.PlatformKey, platform.Processor),
                ["two"] = HostDoubles.Leg(platform.PlatformKey, platform.Processor),
            },
        };

        var refusal = await Assert.ThrowsAsync<HarnessException>(() => OutcomeAsync(
            temp,
            harness,
            config,
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null, Json: true, Here: HostId.Ssh(HostName)) { Workload = LegWorkload.Copy }));

        Assert.Contains($"on ssh {HostName}", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A leg's own work is held awake by the command its host declares, for exactly as long as the
    /// work runs - and on a host running a leg another machine dispatched to it, by that host's own
    /// command, never by the one 'local' declares for the machine that dispatched it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALegsOwnWork_IsHeldAwake_ByItsHostsCommand_UntilItEnds(bool sentHere)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var platform = harness.Platform;
        var held = new HeldProcesses();

        var config = new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Hosts = new HostsConfig
            {
                Local = new LocalHostConfig { KeepAwake = ["local-awake", "-w", "{pid}"] },
                Ssh = { [HostName] = new SshHostConfig { RepositoryPath = HostTree, KeepAwake = ["pi-awake", "-w", "{pid}"] } },
            },
            Legs = { ["native"] = HostDoubles.Leg(platform.PlatformKey, platform.Processor) },
        };

        bool? stoppedDuringTheWork = null;

        var verdicts = await RunAsync(
            temp,
            harness,
            config,
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null, Json: true, Here: sentHere ? HostId.Ssh(HostName) : null) { Workload = LegWorkload.Copy },
            ran: _ => stoppedDuringTheWork = Assert.Single(held.Started).Stopping.IsCancellationRequested,
            keepAwake: held);

        Assert.Equal("passed", verdicts["native"].Verdict);
        Assert.False(stoppedDuringTheWork);

        var (request, stopping) = Assert.Single(held.Started);

        Assert.Equal(sentHere ? "pi-awake" : "local-awake", request.FileName);
        Assert.True(stopping.IsCancellationRequested);
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

    /// <summary>
    /// A run names where its records are - as 'logs:' in the text, as runDirectory in --json - so a
    /// caller never works out which tree a run wrote into. Run in a worktree, they are in the
    /// worktree: kept in the main checkout, a worktree's records were out of its reach.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ARun_NamesWhereItsRecordsAre_InTheTreeThatRanIt(bool json, bool inWorktree)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var tree = inWorktree ? temp.Combine("feature") : temp.Path;

        var outcome = await OutcomeAsync(
            temp,
            harness,
            OneLeg(harness),
            SshAndLocal(harness),
            new LegRunRequest(tree, null, Json: json) { Workload = LegWorkload.Copy },
            tree: tree);

        var directory = RunDirectoryOf(outcome, json);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.StartsWith(Path.Combine(tree, ".harness-config", "runs") + Path.DirectorySeparatorChar, directory, StringComparison.Ordinal);
        Assert.True(Directory.Exists(directory), $"the run named '{directory}', which it never made");
    }

    /// <summary>
    /// A run ended by a refusal after it made its directory still names it, and so does one refused
    /// because another run owns its log path: the text form of that one named nowhere at all.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ARunStoppedAfterItHadADirectory_StillNamesIt(bool json, bool logHeld)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();

        var outcome = await OutcomeAsync(
            temp,
            harness,
            OneLeg(harness),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null, Json: json) { Workload = LegWorkload.Copy },
            work: _ => throw new HarnessException(HarnessExit.Refused, "the leg's configuration cannot be satisfied"),
            logs: logHeld ? new LogOwnership(new LogsOwnedElsewhere(harness.FileSystem), harness.Output, harness.Identity) : null);

        Assert.Equal(logHeld ? LegExit.LogHeld : HarnessExit.Refused, outcome.ExitCode);
        Assert.StartsWith(Path.Combine(temp.Path, ".harness-config", "runs") + Path.DirectorySeparatorChar, RunDirectoryOf(outcome, json), StringComparison.Ordinal);
    }

    /// <summary>
    /// A leg another host ran was run there under a run of its own: its records are in that host's
    /// directory, which the run names beside its own, in the text and on the leg's JSON line.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALegAnotherHostRan_NamesThatHostsOwnDirectory(bool json)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        const string There = HostTree + "/.harness-config/runs/20260919-101500-0a1b2c3d";

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            ScriptedHostCommands.Answer(
                command,
                $$"""{"runDirectory": "{{There}}", "legs": [{"leg": "arm", "verdict": "passed", "durationSeconds": 1, "commandSeconds": 1}]}""");

            return HostResults.Finished(command, 0);
        });

        var outcome = await OutcomeAsync(
            temp,
            harness,
            TwoLegs(harness),
            SshAndLocal(harness),
            new LegRunRequest(temp.Path, null, Json: json) { Workload = LegWorkload.Copy },
            hosts: hosts);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);

        if (json)
        {
            using var document = JsonDocument.Parse(Assert.Single(outcome.Data));
            var arm = document.RootElement.GetProperty("legs").EnumerateArray().Single(leg => leg.GetProperty("leg").GetString() == "arm");

            Assert.Equal(There, arm.GetProperty("runDirectory").GetString());
        }
        else
        {
            Assert.Contains($"logs of arm on ssh {HostName}: {There}", outcome.Details ?? [], StringComparer.Ordinal);
        }
    }

    /// <summary>A run no leg could be placed for made no directory, and names none.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARunNoLegCanTake_NamesNoDirectory(bool json)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var elsewhere = harness.Platform.PlatformKey == "linux" ? "macos" : "linux";
        var config = new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Legs = { ["elsewhere"] = HostDoubles.Leg(elsewhere, "x86_64") },
        };

        var outcome = await OutcomeAsync(
            temp, harness, config, SshAndLocal(harness), new LegRunRequest(temp.Path, null, Json: json) { Workload = LegWorkload.Copy });

        Assert.NotEqual(HarnessExit.Success, outcome.ExitCode);

        if (json)
        {
            using var document = JsonDocument.Parse(Assert.Single(outcome.Data));
            Assert.False(document.RootElement.TryGetProperty("runDirectory", out _));
        }
        else
        {
            Assert.DoesNotContain(outcome.Details ?? [], line => line.StartsWith("logs:", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// A run makes the runs directory it writes into ignore itself, whatever the tree's own .gitignore
    /// says: a tree with no rule for it - a worktree of a branch that predates the harness - showed a
    /// run's records in git status, where the next 'git add -A' committed them.
    /// </summary>
    [Fact]
    public async Task ARun_KeepsItsRecordsOutOfGit_WhereTheTreeHasNoRuleForThem()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;

        await harness.RunGitAsync(temp.Path, ["init", "--quiet", "."], token);

        var outcome = await OutcomeAsync(temp, harness, OneLeg(harness), SshAndLocal(harness), new LegRunRequest(temp.Path, null, Json: true) { Workload = LegWorkload.Copy });
        var status = await harness.RunGitAsync(temp.Path, ["status", "--porcelain", "--untracked-files=all", "--", ".harness-config/runs"], token);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.True(Directory.Exists(RunDirectoryOf(outcome, json: true)));
        Assert.Equal(string.Empty, status.StandardOutput.Trim());
        Assert.Equal(HarnessLayout.SelfIgnoreRule, File.ReadAllText(temp.Combine(".harness-config", "runs", ".gitignore")));
    }

    /// <summary>
    /// A leg whose toolchain names a developer environment runs in it: every process it starts is
    /// given what Visual Studio set up, over what the host declares - the host's own PATH kept behind
    /// Visual Studio's tools, its other variables untouched - and its line names the environment.
    /// </summary>
    [Fact]
    public async Task ALegWhoseToolchainNamesADeveloperEnvironment_RunsInIt_AndNamesIt()
    {
        using var temp = new TempDirectory();
        using var visualStudio = new ScriptedVisualStudio().Carrying("x64", "cl", "cmake");
        var harness = new HarnessFactory();
        IReadOnlyDictionary<string, string>? given = null;
        IReadOnlyDictionary<string, string>? built = null;

        var outcome = await OutcomeAsync(
            temp,
            harness,
            MsvcLeg(),
            WindowsHere(visualStudio),
            new LegRunRequest(temp.Path, null, Json: true) { Workload = LegWorkload.BuildOnly },
            work: leg =>
            {
                given = leg.Leg.Environment;
                built = leg.Leg.BuildRequestFor(leg.Context.Config, leg.RunDirectory).HostEnvironment;
                return new LegEntry { Leg = leg.Leg.Name, Verdict = LegVerdict.Passed };
            },
            developerEnvironments: visualStudio.Provider());

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.NotNull(given);
        Assert.Equal(visualStudio.BinFor("x64") + Path.PathSeparator + @"D:\tools", given["PATH"]);
        Assert.Equal(visualStudio.Include, given["INCLUDE"]);
        Assert.Equal(@"D:\cache", given["CCACHE_DIR"]);
        Assert.Equal(given, built);

        using var document = JsonDocument.Parse(Assert.Single(outcome.Data));
        var environment = Assert.Single(document.RootElement.GetProperty("legs").EnumerateArray()).GetProperty("developerEnvironment");

        Assert.Equal("vs", environment.GetProperty("name").GetString());
        Assert.Equal(visualStudio.InstallationPath, environment.GetProperty("installationPath").GetString());
        Assert.Equal(ScriptedVisualStudio.ToolsVersion, environment.GetProperty("toolsVersion").GetString());
        Assert.Equal("amd64", environment.GetProperty("architecture").GetString());
        Assert.Contains("native: setting up developer environment 'vs'", harness.StandardOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            $"native: passed (developer environment: vs (Visual Studio {ScriptedVisualStudio.InstallationVersion}, MSVC {ScriptedVisualStudio.ToolsVersion}, amd64))",
            harness.StandardOutput.ToString(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A developer environment the survey found that will not set up where the leg runs fails the leg,
    /// saying why, before anything of it starts: as a program that will not start once a leg began
    /// does, never as a skip a gate accepting an incomplete run would pass.
    /// </summary>
    [Fact]
    public async Task ADeveloperEnvironmentThatWillNotSetUp_FailsItsLeg_BeforeAnythingOfItStarts()
    {
        using var temp = new TempDirectory();
        using var visualStudio = new ScriptedVisualStudio
        {
            ExitCode = "1",
            Log = System.Text.Encoding.Unicode.GetBytes("[ERROR:vcvarsall.bat] Invalid argument found : amd64\r\n"),
        };
        var harness = new HarnessFactory();
        var ran = new List<string>();

        var verdicts = await OutcomeAsync(
            temp,
            harness,
            MsvcLeg(),
            WindowsHere(visualStudio),
            new LegRunRequest(temp.Path, null, Json: true) { Workload = LegWorkload.BuildOnly },
            ran: leg => ran.Add(leg.Name),
            developerEnvironments: visualStudio.Provider());

        using var document = JsonDocument.Parse(Assert.Single(verdicts.Data));
        var leg = Assert.Single(document.RootElement.GetProperty("legs").EnumerateArray());

        Assert.Equal("failed", leg.GetProperty("verdict").GetString());
        Assert.Equal(
            "developer environment 'vs': vcvarsall.bat amd64 exited 1: [ERROR:vcvarsall.bat] Invalid argument found : amd64",
            leg.GetProperty("detail").GetString());
        Assert.Empty(ran);
    }

    /// <summary>
    /// A program the leg starts that the PATH its developer environment set up does not hold - CMake,
    /// where Visual Studio's CMake component is not installed - skips the leg as a tool missing, named,
    /// before anything of it starts: the survey could not require it, and the run finds it missing then
    /// rather than halfway through a build. So it does where the host's own env sets a PATH, which the
    /// environment is set up over. Its line still names the environment it looked in.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AProgramTheDeveloperEnvironmentLacks_SkipsItsLeg_NamingIt_BeforeAnythingOfItStarts(bool hostSetsPath)
    {
        using var temp = new TempDirectory();
        using var visualStudio = new ScriptedVisualStudio().Carrying("x64", "cl");
        var harness = new HarnessFactory();
        var ran = new List<string>();

        var outcome = await OutcomeAsync(
            temp,
            harness,
            MsvcLeg(hostSetsPath),
            WindowsHere(visualStudio),
            new LegRunRequest(temp.Path, null, Json: true) { Workload = LegWorkload.BuildOnly },
            ran: leg => ran.Add(leg.Name),
            developerEnvironments: visualStudio.Provider());

        using var document = JsonDocument.Parse(Assert.Single(outcome.Data));
        var leg = Assert.Single(document.RootElement.GetProperty("legs").EnumerateArray());

        Assert.Equal("skipped-tool-missing", leg.GetProperty("verdict").GetString());
        Assert.StartsWith(
            "'cmake' is not installed there: neither on the PATH developer environment 'vs' sets up nor in any directory searched for programs",
            leg.GetProperty("detail").GetString(),
            StringComparison.Ordinal);
        Assert.Equal("vs", leg.GetProperty("developerEnvironment").GetProperty("name").GetString());
        Assert.Empty(ran);
    }

    /// <summary>A copy starts nothing on the host, so the leg's developer environment is never set up for it.</summary>
    [Fact]
    public async Task ACopy_SetsUpNoDeveloperEnvironment()
    {
        using var temp = new TempDirectory();
        using var visualStudio = new ScriptedVisualStudio();
        var harness = new HarnessFactory();
        var inspector = WindowsHere(visualStudio);

        var verdicts = await RunAsync(
            temp,
            harness,
            MsvcLeg(),
            inspector,
            new LegRunRequest(temp.Path, null, Json: true) { Workload = LegWorkload.Copy },
            developerEnvironments: visualStudio.Provider());

        Assert.Equal("passed", verdicts["native"].Verdict);
        Assert.Empty(visualStudio.Probes);
        Assert.Empty(visualStudio.Captures);
        Assert.Empty(Assert.Single(inspector.DeveloperEnvironmentsAsked));
    }

    /// <summary>
    /// A Windows leg built with msvc, whose toolchain names the Visual Studio environment, on a
    /// machine whose own env declares a compiler cache - and a PATH, unless <paramref name="hostSetsPath"/>
    /// says it declares none.
    /// </summary>
    private static HarnessConfig MsvcLeg(bool hostSetsPath = true) => new()
    {
        BuildConfigs = { ["debug"] = new BuildConfiguration() },
        Projects = { new ProjectConfig { Name = "app", Type = "cmake", Path = "." } },
        DeveloperEnvironments = { ["vs"] = new DeveloperEnvironmentConfig { Kind = DeveloperEnvironmentKinds.VisualStudio } },
        Toolchains =
        {
            ["msvc"] = new ToolchainConfig { Platforms = ["windows"], Env = { ["CC"] = "cl" }, DeveloperEnvironment = "vs" },
        },
        Hosts = new HostsConfig
        {
            Local = new LocalHostConfig
            {
                Env = hostSetsPath
                    ? new(StringComparer.OrdinalIgnoreCase) { ["Path"] = @"D:\tools", ["CCACHE_DIR"] = @"D:\cache" }
                    : new(StringComparer.OrdinalIgnoreCase) { ["CCACHE_DIR"] = @"D:\cache" },
            },
        },
        Legs = { ["native"] = new LegConfig { Os = "windows", Processor = "x86_64", Config = "debug", Toolchain = "msvc" } },
    };

    /// <summary>This machine as a Windows x86_64 one, where the survey finds <paramref name="visualStudio"/>.</summary>
    private static RecordingInspector WindowsHere(ScriptedVisualStudio visualStudio) => new(host => new HostReport
    {
        Host = host,
        Os = "windows",
        Processor = "x86_64",
        DeveloperEnvironments = new Dictionary<string, DeveloperEnvironmentCheck>(StringComparer.OrdinalIgnoreCase) { ["vs"] = visualStudio.Found },
    });

    /// <summary>
    /// Developer environments that must never be set up: a test that declares none has no business
    /// reaching Visual Studio.
    /// </summary>
    private static DeveloperEnvironmentProvider NoDeveloperEnvironment(HarnessFactory harness)
    {
        var processes = Substitute.For<IProcessRunner>();
        processes.RunAsync(Arg.Any<ProcessRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("This test declares no developer environment, so none is set up."));

        return new DeveloperEnvironmentProvider(harness.Platform, processes, harness.FileSystem, harness.Output);
    }

    /// <summary>A leg on this machine.</summary>
    private static HarnessConfig OneLeg(HarnessFactory harness) => new()
    {
        BuildConfigs = { ["debug"] = new BuildConfiguration() },
        Legs = { ["native"] = HostDoubles.Leg(harness.Platform.PlatformKey, harness.Platform.Processor) },
    };

    /// <summary>The run directory an outcome names, read the way its caller reads it.</summary>
    private static string RunDirectoryOf(CommandOutcome outcome, bool json)
    {
        if (json)
        {
            using var document = JsonDocument.Parse(Assert.Single(outcome.Data));
            return document.RootElement.GetProperty("runDirectory").GetString()!;
        }

        var logs = Assert.Single(outcome.Details ?? [], line => line.StartsWith("logs: ", StringComparison.Ordinal));
        return logs["logs: ".Length..];
    }

    /// <summary>The real file system, except that every log path reads as owned by a run on another machine.</summary>
    private sealed class LogsOwnedElsewhere(IFileSystem inner) : PassThroughFileSystem(inner)
    {
        private static readonly string Owner = JsonSerializer.Serialize(
            new LogOwner("another-machine", 4242, null, "20260101-000000-00000000", DateTimeOffset.UnixEpoch),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        public override bool FileExists(string path)
            => path.EndsWith(LogOwnership.OwnerSuffix, StringComparison.Ordinal) || base.FileExists(path);

        public override string ReadAllText(string path)
            => path.EndsWith(LogOwnership.OwnerSuffix, StringComparison.Ordinal) ? Owner : base.ReadAllText(path);
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
        ISyncService? sync = null,
        Action<PlacedLeg>? ran = null,
        IProcessRunner? keepAwake = null,
        DeveloperEnvironmentProvider? developerEnvironments = null)
    {
        var outcome = await OutcomeAsync(temp, harness, config, inspector, request, runLock, sync, ran, keepAwake, developerEnvironments: developerEnvironments);

        using var document = JsonDocument.Parse(Assert.Single(outcome.Data));

        return document.RootElement.GetProperty("legs").EnumerateArray().ToDictionary(
            leg => leg.GetProperty("leg").GetString()!,
            leg => (leg.GetProperty("verdict").GetString(), leg.GetProperty("detail").GetString()));
    }

    /// <summary>
    /// Runs the command over <paramref name="config"/>, a leg on this machine passing unless
    /// <paramref name="work"/> says otherwise, and returns how it ended. The tree is the repository's
    /// root unless <paramref name="tree"/> names a worktree of it.
    /// </summary>
    private static async Task<CommandOutcome> OutcomeAsync(
        TempDirectory temp,
        HarnessFactory harness,
        HarnessConfig config,
        RecordingInspector inspector,
        LegRunRequest request,
        RunLock? runLock = null,
        ISyncService? sync = null,
        Action<PlacedLeg>? ran = null,
        IProcessRunner? keepAwake = null,
        string? tree = null,
        Func<LegWork, LegEntry>? work = null,
        LogOwnership? logs = null,
        ScriptedHostCommands? hosts = null,
        DeveloperEnvironmentProvider? developerEnvironments = null)
    {
        var loader = HostDoubles.Loader(config, tree ?? temp.Path, temp.Path);

        var service = new LegRunService(
            loader,
            new LegsService(loader, inspector, harness.Output),
            new LegExecutor(harness.Platform, harness.Output),
            runLock ?? new RunLock(harness.FileSystem, harness.Output, harness.Identity),
            logs ?? new LogOwnership(harness.FileSystem, harness.Output, harness.Identity),
            sync ?? Substitute.For<ISyncService>(),
            Substitute.For<ISyncTransportFactory>(),
            new RemoteLegRunner(hosts ?? new ScriptedHostCommands((_, command) => throw HostResults.Unexpected(command)), harness.Output),
            new KeepAwake(keepAwake ?? new HeldProcesses(), harness.Output),
            developerEnvironments ?? NoDeveloperEnvironment(harness),
            harness.FileSystem,
            harness.FilePermissions,
            harness.Platform,
            harness.Output);

        return await service.RunAsync(
            "test",
            request,
            (leg, _) =>
            {
                ran?.Invoke(leg.Leg);
                return Task.FromResult(work?.Invoke(leg) ?? new LegEntry { Leg = leg.Leg.Name, Verdict = LegVerdict.Passed });
            },
            TestContext.Current.CancellationToken);
    }
}
