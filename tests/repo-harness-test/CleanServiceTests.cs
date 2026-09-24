using System.Text.Json;
using NSubstitute;
using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Runs;
using RepoHarness.Core.Sync;

namespace RepoHarness.Tests;

/// <summary>
/// A consumer's host filled its disk with two builds at once, and nothing could free it: a build from clean
/// fills it again, deleting the worktree takes its local tree too. clean removes a leg's build directory
/// where the leg runs, writing nothing there first, and never from under a build of it.
/// </summary>
public sealed class CleanServiceTests
{
    private const string HostName = "pi";

    private const string HostTree = "/home/pi/repo";

    /// <summary>
    /// A leg on this machine has its build directory removed, and its line says what it held and the room
    /// left on its filesystem, in words and as data. Nothing is written first: no lock file, no records.
    /// </summary>
    [Fact]
    public async Task ALegsBuildDirectory_IsRemoved_SayingWhatItHeldAndTheRoomLeft_AndNothingIsWritten()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = OneLocalLeg(harness);
        var directory = BuildDirectory(temp, harness, config);
        File.WriteAllText(Written(Path.Combine(directory, "obj", "deep", "a.o")), new string('a', 1000));

        var (outcome, leg) = await CleanAsync(temp, harness, config);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal("passed", leg.GetProperty("verdict").GetString());
        Assert.StartsWith($"removed 1000 bytes from '{directory}'; ", leg.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Contains(" free of ", leg.GetProperty("detail").GetString(), StringComparison.Ordinal);

        var space = leg.GetProperty("space");
        Assert.Equal(directory, space.GetProperty("directory").GetString());
        Assert.Equal(1000, space.GetProperty("buildBytes").GetInt64());
        Assert.True(space.GetProperty("removed").GetBoolean());
        Assert.True(space.GetProperty("disk").GetProperty("totalBytes").GetInt64() > 0);

        Assert.False(Directory.Exists(directory));
        Assert.Equal([], Directory.GetDirectories(Path.GetDirectoryName(directory)!));
        Assert.False(File.Exists(new HarnessLayout(temp.Path, temp.Path).LockFile));
    }

    /// <summary>A dry run says what each build directory holds and the room beside it, and removes nothing.</summary>
    [Fact]
    public async Task ADryRun_SaysWhatTheBuildDirectoryHolds_AndRemovesNothing()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = OneLocalLeg(harness);
        var directory = BuildDirectory(temp, harness, config);
        var file = Written(Path.Combine(directory, "a.o"));
        File.WriteAllText(file, new string('a', 2048));

        var (outcome, leg) = await CleanAsync(temp, harness, config, dryRun: true);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.StartsWith($"2 KiB in '{directory}'; ", leg.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.False(leg.GetProperty("space").GetProperty("removed").GetBoolean());
        Assert.Equal(2048, leg.GetProperty("space").GetProperty("buildBytes").GetInt64());
        Assert.True(File.Exists(file));
    }

    /// <summary>
    /// A leg a run is building is refused-locked, naming the run, and its build directory is left whole: a
    /// build's objects are not removed from under it.
    /// </summary>
    [Fact]
    public async Task ALegARunIsBuilding_IsRefusedLocked_AndItsBuildDirectoryIsLeftWhole()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = OneLocalLeg(harness);
        var directory = BuildDirectory(temp, harness, config);
        var file = Written(Path.Combine(directory, "a.o"));
        File.WriteAllText(file, "object");
        var runLock = new RunLock(harness.FileSystem, harness.Output, harness.Identity);

        await using var held = await runLock.AcquireAsync(
            new HarnessLayout(temp.Path, temp.Path),
            new LockRequest
            {
                Host = HostId.Local.ToString(),
                Tree = temp.Path,
                Variant = Path.GetFileName(directory),
                Scope = LockScope.TreeShared,
                RunId = RunId.New(),
                Command = "build",
            },
            TestContext.Current.CancellationToken);

        var (outcome, leg) = await CleanAsync(temp, harness, config, runLock: runLock);

        Assert.Equal(HarnessExit.Refused, outcome.ExitCode);
        Assert.Equal("refused-locked", leg.GetProperty("verdict").GetString());
        Assert.Contains(held.Entry.Holder.RunId, leg.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.True(File.Exists(file));
    }

    /// <summary>
    /// What an interrupted removal left aside is removed by the next clean of the leg, and counted with what
    /// it removes: nothing builds in a directory already moved out of a build's way.
    /// </summary>
    [Fact]
    public async Task WhatAnInterruptedRemovalLeftAside_IsRemovedByTheNextClean()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = OneLocalLeg(harness);
        var directory = BuildDirectory(temp, harness, config);
        var aside = Path.Combine(Path.GetDirectoryName(directory)!, "." + Path.GetFileName(directory) + ".removing");
        File.WriteAllText(Written(Path.Combine(aside, "left.o")), new string('l', 100));
        File.WriteAllText(Written(Path.Combine(directory, "new.o")), new string('n', 20));

        var (_, leg) = await CleanAsync(temp, harness, config);

        Assert.Equal("passed", leg.GetProperty("verdict").GetString());
        Assert.Equal(120, leg.GetProperty("space").GetProperty("buildBytes").GetInt64());
        Assert.False(Directory.Exists(aside));
        Assert.False(Directory.Exists(directory));
    }

    /// <summary>
    /// A build directory that is a link was put somewhere on purpose: it is left alone, and so is what it
    /// points at, and the leg says why.
    /// </summary>
    [Fact]
    public async Task ABuildDirectoryThatIsALink_IsLeftAlone_WithWhatItPointsAt()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = OneLocalLeg(harness);
        var directory = BuildDirectory(temp, harness, config);
        var elsewhere = temp.WriteFile(Path.Combine("elsewhere", "a.o"), "object");
        Directory.CreateDirectory(Path.GetDirectoryName(directory)!);

        try
        {
            Directory.CreateSymbolicLink(directory, Path.GetDirectoryName(elsewhere)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"This machine does not allow creating symbolic links: {ex.Message}");
        }

        var (_, leg) = await CleanAsync(temp, harness, config);

        Assert.Equal("failed", leg.GetProperty("verdict").GetString());
        Assert.Contains("is a link, so nothing was removed", leg.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.True(File.Exists(elsewhere));
        Assert.True(Directory.Exists(directory));
    }

    /// <summary>
    /// A leg on a host is cleaned by the DssHarness there, in the tree's copy, and on itself - asked only once
    /// the copy is known to be there - and what that host measured is the leg's line.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALegOnAHost_IsCleanedByTheHost_InTheTreesCopy(bool dryRun)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        HostCommand? sent = null;

        var hosts = new ScriptedHostCommands((_, command) =>
        {
            sent = command;
            ScriptedHostCommands.Answer(command, HostLedger());
            return HostResults.Finished(command, 0);
        });

        var (outcome, leg) = await CleanAsync(temp, harness, OneHostLeg(), OnTheHost(), hosts: hosts, copyThere: true, dryRun: dryRun);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal("removed 8 GiB from '/home/pi/repo/build/arm64-none-debug'; 30 GiB free of 48 GiB on '/'", leg.GetProperty("detail").GetString());
        Assert.Equal(8L << 30, leg.GetProperty("space").GetProperty("buildBytes").GetInt64());
        Assert.Equal("/", leg.GetProperty("space").GetProperty("disk").GetProperty("filesystem").GetString());

        var request = JsonSerializer.Deserialize<HostAgentRequest>(sent!.StandardInput!, HostAgentProtocol.JsonOptions)!;

        Assert.Equal(HostTree, request.Directory);
        Assert.Equal(
            ["clean", "--legs", "arm", "--json", RemoteLegRunner.HereOption, $"ssh {HostName}", .. dryRun ? new[] { CleanService.DryRunOption } : []],
            request.Arguments);
    }

    /// <summary>
    /// A tree never synced to a host has no copy there, and nothing to remove: said as that, and the host is
    /// sent no command, which it would refuse as a copy it cannot run in.
    /// </summary>
    [Fact]
    public async Task ALegOnAHostWithNoCopyOfTheTree_HasNothingRemoved_AndTheHostIsSentNothing()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var hosts = new ScriptedHostCommands((_, command) => throw HostResults.Unexpected(command));

        var (outcome, leg) = await CleanAsync(temp, harness, OneHostLeg(), OnTheHost(), hosts: hosts, copyThere: false);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal($"nothing to remove: ssh {HostName} holds no copy of this tree at '{HostTree}'", leg.GetProperty("detail").GetString());
        Assert.Empty(hosts.Calls);
    }

    /// <summary>
    /// A run of this machine building a leg on a host holds this machine's lock for it: the leg is
    /// refused-locked here, and neither the host nor its copy is asked anything.
    /// </summary>
    [Fact]
    public async Task ALegOnAHostARunOfThisMachineIsBuilding_IsRefusedLocked_BeforeTheHostIsAsked()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var runLock = new RunLock(harness.FileSystem, harness.Output, harness.Identity);
        var hosts = new ScriptedHostCommands((_, command) => throw HostResults.Unexpected(command));
        var transports = Substitute.For<ISyncTransportFactory>();

        await using var held = await runLock.AcquireAsync(
            new HarnessLayout(temp.Path, temp.Path),
            new LockRequest
            {
                Host = HostId.Ssh(HostName).ToString(),
                Tree = HostTree,
                Variant = "arm64-none-debug",
                Scope = LockScope.TreeShared,
                RunId = RunId.New(),
                Command = "test",
            },
            TestContext.Current.CancellationToken);

        var (_, leg) = await CleanAsync(temp, harness, OneHostLeg(), OnTheHost(), runLock, hosts, transports: transports);

        Assert.Equal("refused-locked", leg.GetProperty("verdict").GetString());
        Assert.Contains(held.Entry.Holder.RunId, leg.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Empty(hosts.Calls);
        transports.DidNotReceiveWithAnyArgs().For(default!);
    }

    private static HarnessConfig OneLocalLeg(HarnessFactory harness) => new()
    {
        BuildConfigs = { ["debug"] = new BuildConfiguration() },
        Legs = { ["native"] = HostDoubles.Leg(harness.Platform.PlatformKey, harness.Platform.Processor) },
    };

    private static HarnessConfig OneHostLeg() => new()
    {
        BuildConfigs = { ["debug"] = new BuildConfiguration() },
        Hosts = new HostsConfig { Ssh = { [HostName] = new SshHostConfig { RepositoryPath = HostTree } } },
        Legs = { ["arm"] = new LegConfig { Os = "linux", Processor = "arm64", Config = "debug", Ssh = HostName } },
    };

    /// <summary>This machine as it is, and the ssh host as a Linux arm64 machine that answers.</summary>
    private static RecordingInspector OnTheHost() => new(host => new HostReport
    {
        Host = host,
        Os = "linux",
        Processor = "arm64",
        Session = new HostSession(new HostConnection { Host = host, Address = "192.0.2.10" }, ".dotnet/tools/dssharness"),
    });

    /// <summary>The build directory the one leg of <paramref name="config"/> has on this machine.</summary>
    private static string BuildDirectory(TempDirectory temp, HarnessFactory harness, HarnessConfig config)
        => VariantKey.For(config, config.Legs.Single().Value, harness.Platform.PlatformKey).DirectoryUnder(temp.Path);

    /// <summary><paramref name="path"/>, with the directory it goes in made.</summary>
    private static string Written(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    /// <summary>What the host answers for the leg: its ledger, with what it removed and the room left there.</summary>
    private static string HostLedger() => """
        {
          "exitCode": 0,
          "legs": [
            {
              "leg": "arm",
              "verdict": "passed",
              "detail": "removed 8 GiB from '/home/pi/repo/build/arm64-none-debug'; 30 GiB free of 48 GiB on '/'",
              "durationSeconds": 1.5,
              "commandSeconds": 0,
              "space": {
                "directory": "/home/pi/repo/build/arm64-none-debug",
                "buildBytes": 8589934592,
                "removed": true,
                "disk": { "freeBytes": 32212254720, "totalBytes": 51539607552, "filesystem": "/" }
              }
            }
          ]
        }
        """;

    /// <summary>Runs clean over <paramref name="config"/> with --json, and returns how it ended and its one leg's line.</summary>
    private static async Task<(CommandOutcome Outcome, JsonElement Leg)> CleanAsync(
        TempDirectory temp,
        HarnessFactory harness,
        HarnessConfig config,
        RecordingInspector? inspector = null,
        RunLock? runLock = null,
        ScriptedHostCommands? hosts = null,
        bool dryRun = false,
        bool copyThere = true,
        ISyncTransportFactory? transports = null)
    {
        var loader = HostDoubles.Loader(config, temp.Path);

        if (transports is null)
        {
            var transport = Substitute.For<ISyncTransport>();
            transport.RootExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(copyThere);
            transports = Substitute.For<ISyncTransportFactory>();
            transports.For(Arg.Any<HostReport>()).Returns(transport);
        }

        var service = new CleanService(
            loader,
            new LegsService(loader, inspector ?? new RecordingInspector(host => new HostReport
            {
                Host = host,
                Os = harness.Platform.PlatformKey,
                Processor = harness.Platform.Processor,
            }), harness.Output),
            runLock ?? new RunLock(harness.FileSystem, harness.Output, harness.Identity),
            transports,
            new RemoteLegRunner(hosts ?? new ScriptedHostCommands((_, command) => throw HostResults.Unexpected(command)), harness.Output),
            harness.FileSystem,
            harness.Platform,
            harness.Output);

        var outcome = await service.RunAsync(new CleanRequest(temp.Path, null, dryRun, Json: true), TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(Assert.Single(outcome.Data));

        return (outcome, Assert.Single(document.RootElement.GetProperty("legs").EnumerateArray().ToList()).Clone());
    }
}
