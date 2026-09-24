using System.Text.Json;
using NSubstitute;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// A host that sleeps is held awake between commands, and never during one: each command, finishing with it,
/// leaves it a hold, which the host runs on its own once the connection has ended, and which the next command's
/// own keepAwake ends there.
/// </summary>
public sealed class HoldAwakeTests
{
    private static readonly string[] Caffeinate = ["caffeinate", "-dimsu", "-w", "{pid}"];

    /// <summary>A hold is read back as written; one that is not there, or cannot be read, is no hold; ending one removes it.</summary>
    [Fact]
    public void AHold_IsReadBackAsWritten_AndEndingItRemovesIt()
    {
        using var temp = new TempDirectory();
        var store = Store(temp);
        var state = State("abc", TimeSpan.FromMinutes(10));

        Assert.Null(store.Read());
        store.End();

        store.Write(state);

        var read = store.Read()!;
        Assert.Equal(state.Generation, read.Generation);
        Assert.Equal(state.Until, read.Until);
        Assert.Equal(Caffeinate, read.KeepAwake);
        Assert.Equal("mac", read.Environment["RH_HOST"]);

        store.End();
        Assert.Null(store.Read());

        File.WriteAllText(store.Location, "not a hold");
        Assert.Null(store.Read());
    }

    /// <summary>
    /// A command that starts its own keepAwake ends the hold standing on the machine: a hold exists between
    /// commands, never during one. One that holds nothing leaves it standing, and so does the hold's own.
    /// </summary>
    [Fact]
    public async Task AKeepAwakeStarting_EndsTheHold_AndOneThatHoldsNothingLeavesIt()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var store = Store(temp);
        var keepAwake = new KeepAwake(new HeldProcesses(), factory.Output, store);
        var host = new LocalHostConfig { KeepAwake = [.. Caffeinate] };

        store.Write(State("abc", TimeSpan.FromMinutes(10)));

        await using (keepAwake.Hold("test", "mac", new LocalHostConfig(), [], TestContext.Current.CancellationToken))
        {
            Assert.NotNull(store.Read());
        }

        await using (keepAwake.Hold("host-hold", "between commands", host, [], TestContext.Current.CancellationToken, endsHold: false))
        {
            Assert.NotNull(store.Read());
        }

        await using (keepAwake.Hold("test", "mac", host, [], TestContext.Current.CancellationToken))
        {
            Assert.Null(store.Read());
        }
    }

    /// <summary>
    /// Asked for a hold, a host makes it the hold that stands - for the seconds asked, with the command, the
    /// environment and the directories it was sent - starts the process that holds it, detached, and answers at
    /// once that it has.
    /// </summary>
    [Fact]
    public async Task AHoldRequest_IsMadeTheHoldThatStands_AndItsProcessStarted()
    {
        using var temp = new TempDirectory();
        var store = Store(temp);
        var launcher = new RecordingLauncher();
        using var error = new StringWriter();
        var before = DateTimeOffset.UtcNow;

        var exitCode = await Agent(store, launcher).ServeAsync(
            new StringReader(HoldRequest(seconds: 600)),
            new StringWriter(),
            error,
            (_, _, _) => throw new InvalidOperationException("A hold runs no command."),
            TestContext.Current.CancellationToken);

        var state = store.Read()!;

        Assert.Equal(HarnessExit.Success, exitCode);
        Assert.Contains(HostAgentProtocol.CompletionLine(Nonce, HarnessExit.Success), error.ToString(), StringComparison.Ordinal);
        Assert.Equal(Caffeinate, state.KeepAwake);
        Assert.Equal("mac", state.Environment["RH_HOST"]);
        Assert.Equal(["/opt/homebrew/bin"], state.ProgramDirectories);
        Assert.InRange(state.Until, before.AddSeconds(600), DateTimeOffset.UtcNow.AddSeconds(600));
        Assert.Equal([HoldAwakeService.CommandName, state.Generation], Assert.Single(launcher.Started));
    }

    /// <summary>
    /// A hold with nothing to hold the host with, or no time to hold it for, is refused, and nothing is started;
    /// one whose process would not start is refused, and leaves no hold standing for a process that never ran.
    /// </summary>
    [Fact]
    public async Task AHoldThatCannotBeKept_IsRefused_AndLeavesNoHoldStanding()
    {
        using var temp = new TempDirectory();
        var store = Store(temp);
        var launcher = new RecordingLauncher();

        var empty = await Agent(store, launcher).ServeAsync(
            new StringReader(HoldRequest(seconds: 0)), new StringWriter(), new StringWriter(), (_, _, _) => Task.FromResult(0), TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, empty);
        Assert.Empty(launcher.Started);
        Assert.Null(store.Read());

        var failing = new RecordingLauncher { Raises = new ProgramStartException("dssharness", "'dssharness' could not be started: gone") };
        using var error = new StringWriter();

        var unstarted = await Agent(store, failing).ServeAsync(
            new StringReader(HoldRequest(seconds: 600)), new StringWriter(), error, (_, _, _) => Task.FromResult(0), TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.HostUnavailable, unstarted);
        Assert.Contains("this host could not be held awake: 'dssharness' could not be started: gone", error.ToString(), StringComparison.Ordinal);
        Assert.Null(store.Read());
    }

    /// <summary>
    /// The hold's process holds the machine with its keepAwake, filled in with itself, for as long as its hold
    /// stands, and stops once it does not: ended by a command's own keepAwake, or replaced by a newer hold. A
    /// process started for a hold that no longer stands holds nothing.
    /// </summary>
    [Theory]
    [InlineData("ended")]
    [InlineData("replaced")]
    public async Task TheHoldsProcess_HoldsTheMachine_UntilItsHoldNoLongerStands(string how)
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var store = Store(temp);
        var processes = new HeldProcesses();
        var service = new HoldAwakeService(store, new KeepAwake(processes, factory.Output, store), TimeProvider.System, TimeSpan.FromMilliseconds(20));

        store.Write(State("abc", TimeSpan.FromMinutes(10)));
        // A process started for a hold that no longer stands - a newer one replaced it before it began - ends at once.
        await service.RunAsync("other", TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Empty(processes.Started);

        var holding = service.RunAsync("abc", TestContext.Current.CancellationToken);
        await Until(() => processes.Started.Count == 1);

        var (request, stopping) = Assert.Single(processes.Started);
        Assert.Equal(["-dimsu", "-w", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)], request.Arguments);
        Assert.False(holding.IsCompleted);

        if (how == "ended")
        {
            store.End();
        }
        else
        {
            store.Write(State("newer", TimeSpan.FromMinutes(10)));
        }

        await holding.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(stopping.IsCancellationRequested);
    }

    /// <summary>A hold ends by itself when its time is up.</summary>
    [Fact]
    public async Task AHold_EndsByItselfWhenItsTimeIsUp()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var store = Store(temp);
        var processes = new HeldProcesses();
        var service = new HoldAwakeService(store, new KeepAwake(processes, factory.Output, store), TimeProvider.System, TimeSpan.FromMilliseconds(20));

        store.Write(State("abc", TimeSpan.FromMilliseconds(200)));

        await service.RunAsync("abc", TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(Assert.Single(processes.Started).Stopping.IsCancellationRequested);
    }

    /// <summary>
    /// A command leaves a hold only with the ssh hosts it reached that ask for one - never a host of another kind,
    /// one that declares no hold or no keepAwake, or one it never reached - and asks each with what that host
    /// declares, saying so only when asked to be verbose.
    /// </summary>
    [Fact]
    public async Task ACommand_LeavesAHoldWithEachSshHostItReachedThatAsksForOne()
    {
        var sent = new List<HostAgentRequest>();
        var hosts = new ScriptedHostCommands((_, command) =>
        {
            var request = JsonSerializer.Deserialize<HostAgentRequest>(command.StandardInput, HostAgentProtocol.JsonOptions)!;

            lock (sent)
            {
                sent.Add(request);
            }

            command.OnErrorLine?.Invoke(HostAgentProtocol.CompletionLine(request.Nonce!, HarnessExit.Success));
            return HostResults.Ok(string.Empty);
        });

        var output = new StringWriter();
        var registry = new HoldAwakeRegistry(hosts, new ConsoleHarnessOutput(output, output, verbose: true));

        registry.Reached(Reached(HostId.Ssh("mac"), hold: 600));
        registry.Reached(Reached(HostId.Ssh("vps"), hold: 0));
        registry.Reached(Reached(HostId.Wsl("Ubuntu"), hold: 600));
        registry.Reached(Reached(HostId.Ssh("mini"), hold: 600) with { KeepAwake = [] });
        registry.Reached(Reached(HostId.Ssh("off"), hold: 600) with { Session = null, Reason = "it did not answer" });

        await registry.LeaveHoldsAsync("test", TestContext.Current.CancellationToken);

        var request = Assert.Single(sent);
        Assert.Equal(HostAgentRequestKind.Hold, request.Kind);
        Assert.Equal(600, request.HoldAwakeSeconds);
        Assert.Equal(Caffeinate, request.KeepAwake);
        Assert.Equal("mac", request.KeepAwakeEnvironment["RH_HOST"]);
        Assert.Equal(["/opt/homebrew/bin"], request.KeepAwakeDirectories);
        Assert.Contains("ssh mac: held awake for up to 600 seconds", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A hold a host refused, or never answered, is said, and fails nothing: the command's own work is done.</summary>
    [Fact]
    public async Task AHoldAHostRefused_IsSaid_AndFailsNothing()
    {
        var hosts = new ScriptedHostCommands((_, command) =>
        {
            var request = JsonSerializer.Deserialize<HostAgentRequest>(command.StandardInput, HostAgentProtocol.JsonOptions)!;

            command.OnErrorLine?.Invoke(FailureLine.For(HostAgentProtocol.CommandName, "this host could not be held awake: the disk is full"));
            command.OnErrorLine?.Invoke(HostAgentProtocol.CompletionLine(request.Nonce!, HarnessExit.HostUnavailable));
            return HostResults.Ok(string.Empty);
        });

        var error = new StringWriter();
        var registry = new HoldAwakeRegistry(hosts, new ConsoleHarnessOutput(new StringWriter(), error, verbose: false));

        registry.Reached(Reached(HostId.Ssh("mac"), hold: 600));
        await registry.LeaveHoldsAsync("test", TestContext.Current.CancellationToken);

        Assert.Contains("ssh mac: could not be held awake until the next command: this host could not be held awake: the disk is full", error.ToString(), StringComparison.Ordinal);
    }

    private const string Nonce = "0123456789abcdef0123456789abcdef";

    private static HoldAwakeStore Store(TempDirectory temp)
        => new(new PhysicalFileSystem(FilePermissionsFactory.Create()), temp.Combine(Path.Combine("state", "hold-awake.json")));

    private static HoldAwakeState State(string generation, TimeSpan lasting)
        => new(generation, DateTimeOffset.UtcNow + lasting, [.. Caffeinate], new() { ["RH_HOST"] = "mac" }, ["/opt/homebrew/bin"]);

    private static string HoldRequest(int seconds)
        => JsonSerializer.Serialize(
            new HostAgentRequest
            {
                Kind = HostAgentRequestKind.Hold,
                HoldAwakeSeconds = seconds,
                KeepAwake = [.. Caffeinate],
                KeepAwakeEnvironment = new() { ["RH_HOST"] = "mac" },
                KeepAwakeDirectories = ["/opt/homebrew/bin"],
                Nonce = Nonce,
            },
            HostAgentProtocol.JsonOptions);

    private static HostReport Reached(HostId host, int hold) => new()
    {
        Host = host,
        Os = "macos",
        Processor = "arm64",
        HoldAwakeSeconds = hold,
        KeepAwake = [.. Caffeinate],
        KeepAwakeEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["RH_HOST"] = "mac" },
        ProgramDirectories = ["/opt/homebrew/bin"],
        Session = new HostSession(new HostConnection { Host = host, Address = "192.0.2.10" }, ".dotnet/tools/dssharness"),
    };

    private static HostAgentService Agent(HoldAwakeStore store, IDetachedProcessLauncher launcher)
    {
        var platform = HostDoubles.Platform(PlatformId.Linux, "arm64");
        var identity = Substitute.For<IToolIdentityProvider>();
        identity.Current.Returns(new ToolIdentity("1.2.3", "abc123"));

        var fileSystem = new PhysicalFileSystem(FilePermissionsFactory.Create());
        var processRunner = new ProcessRunner(new HostPlatform(), FilePermissionsFactory.Create());

        return new HostAgentService(
            platform,
            identity,
            new EmulatorProbe(platform, processRunner, fileSystem),
            new DeveloperEnvironmentProbe(platform, processRunner),
            fileSystem,
            new LocalProgramResolver(platform, FilePermissionsFactory.Create()),
            new KeepAwake(new HeldProcesses(), new ConsoleHarnessOutput(new StringWriter(), new StringWriter(), verbose: false), store),
            store,
            launcher);
    }

    private static async Task Until(Func<bool> done)
    {
        for (var tries = 0; tries < 500 && !done(); tries++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(done(), "what was waited for did not happen");
    }
}
