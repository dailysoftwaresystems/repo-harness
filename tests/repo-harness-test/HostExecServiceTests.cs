using System.Text.Json;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>What host-exec refuses, what it sends, and what it reports back.</summary>
public sealed class HostExecServiceTests
{
    private static readonly string Root = TestHost.TemporaryRoot;

    private static readonly HarnessConfig Config = new()
    {
        Hosts = new HostsConfig
        {
            Wsl = { ["Ubuntu"] = new WslHostConfig { RepositoryPath = "~/src/repo" } },
            Ssh = { ["vps"] = new SshHostConfig { RepositoryPath = "/srv/repo" } },
        },
    };

    [Theory]
    [InlineData(null, null)]
    [InlineData("vps", "")]
    public async Task NamingNoHost_OrTwo_IsAUsageError(string? ssh, string? wsl)
    {
        var exception = await Assert.ThrowsAsync<HarnessException>(
            () => Create().Service.RunAsync(Root, ssh, wsl, ["verify-git"], TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.UsageError, exception.ExitCode);
    }

    [Fact]
    public async Task NamingNoCommand_IsAUsageError()
    {
        var exception = await Assert.ThrowsAsync<HarnessException>(
            () => Create().Service.RunAsync(Root, "vps", null, [], TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.UsageError, exception.ExitCode);
    }

    [Theory]
    [InlineData(HostExecService.CommandName)]
    [InlineData(HostAgentProtocol.CommandName)]
    [InlineData("HOST-EXEC")]
    public async Task ACommandThatReachesHosts_IsNotRunOnOne_NorIsAnyHostInspectedForIt(string command)
    {
        var fixture = Create();

        var exception = await Assert.ThrowsAsync<HarnessException>(
            () => fixture.Service.RunAsync(Root, "vps", null, [command], TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.UsageError, exception.ExitCode);
        Assert.Empty(fixture.Inspector.Inspected);
    }

    [Fact]
    public async Task AnUndeclaredHost_IsAUsageError_ThatListsTheDeclaredOnes()
    {
        var exception = await Assert.ThrowsAsync<HarnessException>(
            () => Create().Service.RunAsync(Root, "nope", null, ["verify-git"], TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.UsageError, exception.ExitCode);
        Assert.Equal("--ssh nope is not declared under hosts.ssh; declared: vps", exception.Message);
    }

    [Fact]
    public async Task AHostThatCannotRunDssHarness_ReportsWhy_AsUnavailable()
    {
        var fixture = Create(report: host => new HostReport { Host = host, Reason = "ssh could not connect: Connection refused" });

        var outcome = await fixture.Service.RunAsync(Root, "vps", null, ["verify-git"], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.HostUnavailable, outcome.ExitCode);
        Assert.Equal("ssh vps cannot run DssHarness: ssh could not connect: Connection refused", outcome.Message);
        Assert.Empty(fixture.Commands.Calls);
    }

    [Fact]
    public async Task ARefusalWhileTheHostIsInspected_StopsEverything_BeforeAnythingRuns()
    {
        var refusal = new HarnessException(HarnessExit.Refused, "ssh vps has DssHarness 1.3.0, newer than this machine's 1.2.0");
        var fixture = Create(report: _ => throw refusal);

        var exception = await Assert.ThrowsAsync<HarnessException>(
            () => fixture.Service.RunAsync(Root, "vps", null, ["verify-git"], TestContext.Current.CancellationToken));

        Assert.Same(refusal, exception);
        Assert.Empty(fixture.Commands.Calls);
    }

    [Fact]
    public async Task TheCommand_TravelsAsOneLineHeldOpen_AndItsExitCodeComesFromItsCompletionLine()
    {
        var fixture = Create(respond: (_, command) => HostResults.Finished(command, 3, "read-anchor: FAIL - the anchor was not found\n"));

        var outcome = await fixture.Service.RunAsync(Root, "VPS", null, ["read-anchor", "D-A B"], TestContext.Current.CancellationToken);

        Assert.Equal(3, outcome.ExitCode);

        // Reached under the name the configuration declares: ssh applies a Host entry only to that spelling.
        Assert.Equal("vps", Assert.Single(fixture.Inspector.Inspected).Name);

        var (_, command) = Assert.Single(fixture.Commands.Calls);
        Assert.Equal(".dotnet/tools/dssharness", command.Program);
        Assert.Equal([HostAgentProtocol.CommandName], command.Arguments);

        // One line, with the input held open: stopping this process ends it on the host, which cancels the command.
        Assert.True(command.HoldStandardInputOpen);
        Assert.Single(command.StandardInput, character => character == '\n');
        Assert.EndsWith("\n", command.StandardInput, StringComparison.Ordinal);

        // The arguments, a space included, never reach a shell: they are inside the request.
        var request = JsonSerializer.Deserialize<HostAgentRequest>(command.StandardInput, HostAgentProtocol.JsonOptions);
        Assert.NotNull(request);
        Assert.Equal(HostAgentRequestKind.Run, request.Kind);
        Assert.Equal("/srv/repo", request.Directory);
        Assert.Equal(["read-anchor", "D-A B"], request.Arguments);
        Assert.False(string.IsNullOrEmpty(request.Nonce));

        // What the command wrote is passed on; the completion line is read, and not shown.
        Assert.Contains("read-anchor: FAIL - the anchor was not found", fixture.Error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(": finished ", fixture.Error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// What a host's login shell writes before its agent runs is the host's own business, and never reaches
    /// this command's output on either stream.
    /// </summary>
    /// <remarks>
    /// A shell startup writes to the same streams the command does, and does it first. One consumer's Mac
    /// sources emsdk's environment script on every session, which prints the account's home layout - the
    /// user's name among it - and a machine relaying that output published it into a ledger, a CI log and a
    /// chat transcript. Only what the agent wrote, from its own marker on, is this command's.
    /// </remarks>
    [Fact]
    public async Task WhatTheHostsLoginShellPrintsBeforeItsAgentRuns_ReachesNoOutput()
    {
        const string profile = "PATH += /Users/someone/Library/emsdk";

        var fixture = Create(respond: (_, command) => HostResults.Finished(command, 0, "create-worktree: OK\n"));

        // Written before the agent runs, and so before its marker, as a login shell writes.
        fixture.Commands.LoginShellPrints = _ => profile;

        var outcome = await fixture.Service.RunAsync(Root, "vps", null, ["create-worktree", "x"], TestContext.Current.CancellationToken);

        Assert.Equal(0, outcome.ExitCode);
        Assert.DoesNotContain("someone", fixture.Error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("someone", fixture.Output.ToString(), StringComparison.Ordinal);

        // And what the agent did say still travels, so nothing is suppressed but the host's own noise.
        Assert.Contains("create-worktree: OK", fixture.Error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A profile whose last write has no newline glues its bytes onto the first line the agent writes, which
    /// is the marker. The gate still opens, the glued text is not relayed, and the command's exit code is
    /// read as it always was.
    /// </summary>
    /// <remarks>
    /// Held to the whole line, such a host would open the gate on nothing at all, and every command on it
    /// would report as one that never said how it finished - a far worse failure than the leak, and one that
    /// no host with a quiet profile would ever show.
    /// </remarks>
    [Fact]
    public async Task AProfileThatLeavesItsLastLineOpen_StillOpensTheGate_AndIsNotRelayed()
    {
        var fixture = Create(respond: (_, command) => HostResults.Finished(command, 7, "create-worktree: FAIL - no\n"));
        fixture.Commands.LoginShellPrints = _ => "PATH += /Users/someone/Library/emsdk";
        fixture.Commands.LoginShellLeavesALineOpen = true;

        var outcome = await fixture.Service.RunAsync(Root, "vps", null, ["create-worktree", "x"], TestContext.Current.CancellationToken);

        // The command's own exit code, not the connection's: the completion line was still read.
        Assert.Equal(7, outcome.ExitCode);
        Assert.Contains("create-worktree: FAIL - no", fixture.Error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("someone", fixture.Error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("someone", fixture.Output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The command that holds the host awake travels with the request, as a sync's own requests carry it: a
    /// command run here is the host's work for as long as it takes, and a host that sleeps part way through
    /// leaves the reader a command that never said how it finished.
    /// </summary>
    [Fact]
    public async Task ACommandRunOnAHost_CarriesWhatHoldsThatHostAwake()
    {
        HostAgentRequest? asked = null;

        var fixture = Create(
            report: host => Reachable(host) with
            {
                KeepAwake = ["caffeinate", "-dimsu", "-w", "{pid}"],
                KeepAwakeEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["HOMEBREW_PREFIX"] = "/opt/homebrew" },
                ProgramDirectories = ["/opt/homebrew/bin"],
            },
            respond: (_, command) =>
            {
                asked = JsonSerializer.Deserialize<HostAgentRequest>(command.StandardInput!, HostAgentProtocol.JsonOptions);
                return HostResults.Finished(command, 0);
            });

        await fixture.Service.RunAsync(Root, "vps", null, ["create-worktree", "x"], TestContext.Current.CancellationToken);

        Assert.Equal(["caffeinate", "-dimsu", "-w", "{pid}"], asked!.KeepAwake);
        Assert.Equal("/opt/homebrew", asked.KeepAwakeEnvironment["HOMEBREW_PREFIX"]);
        Assert.Equal(["/opt/homebrew/bin"], asked.KeepAwakeDirectories);
    }

    /// <summary>
    /// A host that refuses before it can read the request writes no marker - it has no nonce to mark with -
    /// and its refusal is the whole of what the reader has to go on, so it is relayed all the same.
    /// </summary>
    [Fact]
    public async Task AHostThatRefusesBeforeItCanMarkItsOutput_IsStillHeard()
    {
        var refusal = FailureLine.For(HostAgentProtocol.CommandName, "the request speaks protocol 5, and this host speaks 4");

        var fixture = Create(respond: (_, command) =>
        {
            command.OnErrorLine?.Invoke(refusal);
            return HostResults.Failed(HarnessExit.UsageError, refusal + "\n");
        });

        fixture.Commands.Marks = false;
        fixture.Commands.LoginShellPrints = _ => "PATH += /Users/someone/Library/emsdk";

        var outcome = await fixture.Service.RunAsync(Root, "vps", null, ["create-worktree", "x"], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.HostUnavailable, outcome.ExitCode);

        // Why it refused reaches the reader, and the host's profile still does not.
        Assert.Contains("the request speaks protocol 5", fixture.Error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("someone", fixture.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACommandThatNeverSaysHowItFinished_IsUnavailable_RatherThanTheConnectionsExitCode()
    {
        // ssh exits 255 when the connection fails, and 255 is not the command's result.
        var fixture = Create(respond: (_, _) => HostResults.Failed(255, "Connection to vps.example closed by remote host.\n"));

        var outcome = await fixture.Service.RunAsync(Root, "vps", null, ["create-worktree", "x"], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.HostUnavailable, outcome.ExitCode);
        Assert.Contains("never reported how it finished, so it may not have run, or run only in part", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("exit 255: Connection to vps.example closed by remote host.", outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A command on a host ssh never connected to is said as that host not reached, in ssh's words: the
    /// command never ran, so saying it may have run in part would send somebody to check the host.
    /// </summary>
    [Fact]
    public async Task ACommandOnAHostSshNeverConnectedTo_IsSaidAsThatHostNotReached()
    {
        var fixture = Create(respond: (_, _) => HostResults.Failed(255, "ssh: connect to host host.invalid port 22: Connection timed out\n"));

        var outcome = await fixture.Service.RunAsync(Root, "vps", null, ["create-worktree", "x"], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.HostUnavailable, outcome.ExitCode);
        Assert.EndsWith(": the host could not be reached: ssh said ssh: connect to host host.invalid port 22: Connection timed out", outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("never reported", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerboseOutput_AsksTheHostToReportItsOwnDefectsInFull()
    {
        var fixture = Create(respond: (_, command) => HostResults.Finished(command, 0), verbose: true);

        await fixture.Service.RunAsync(Root, "vps", null, ["verify-git"], TestContext.Current.CancellationToken);

        Assert.Equal(
            [HostAgentProtocol.CommandName, HostAgentProtocol.VerboseOption],
            Assert.Single(fixture.Commands.Calls).Command.Arguments);
    }

    [Fact]
    public async Task TheDefaultDistribution_IsTheOneWslItselfReports()
    {
        var fixture = Create(platform: HostDoubles.Platform(PlatformId.Windows), respond: (_, command) => HostResults.Finished(command, 0));
        fixture.Commands.DefaultWslDistribution = () => HostResults.Ok("Ubuntu\n");

        var outcome = await fixture.Service.RunAsync(Root, null, string.Empty, ["verify-git"], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal([HostId.Wsl("Ubuntu")], fixture.Inspector.Inspected);

        var request = JsonSerializer.Deserialize<HostAgentRequest>(fixture.Commands.Calls[0].Command.StandardInput, HostAgentProtocol.JsonOptions);
        Assert.Equal("~/src/repo", request?.Directory);
    }

    /// <summary>
    /// wsl.exe that is not installed is raised by the runner that starts it as WSL not being
    /// reachable, and the command ends as that - never as a program of this machine's own that is
    /// missing - with nothing measured.
    /// </summary>
    [Fact]
    public async Task TheDefaultDistribution_WithoutWslInstalled_IsUnavailable()
    {
        const string Unreachable = "WSL could not be reached: Executable 'wsl.exe' was not found on PATH.";
        var fixture = Create(platform: HostDoubles.Platform(PlatformId.Windows));
        fixture.Commands.DefaultWslDistribution = () => throw new HarnessException(
            HarnessExit.HostUnavailable,
            Unreachable,
            new ExecutableNotFoundException(HostCommandRunner.WslProgram));

        var exception = await Assert.ThrowsAsync<HarnessException>(
            () => fixture.Service.RunAsync(Root, null, string.Empty, ["verify-git"], TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.HostUnavailable, exception.ExitCode);
        Assert.Equal(Unreachable, exception.Message);
        Assert.Empty(fixture.Inspector.Inspected);
    }

    [Theory]
    [InlineData(true, "WSL did not name its default distribution within 120 seconds")]
    [InlineData(false, "WSL did not name a default distribution (exit 0)")]
    public async Task ADefaultDistributionThatIsNeverNamed_IsUnavailable_AndSaysWhy(bool timedOut, string expected)
    {
        var fixture = Create(platform: HostDoubles.Platform(PlatformId.Windows));
        fixture.Commands.DefaultWslDistribution = () => new ProcessResult(timedOut ? -1 : 0, string.Empty, string.Empty, TimeSpan.Zero, timedOut);

        var exception = await Assert.ThrowsAsync<HarnessException>(
            () => fixture.Service.RunAsync(Root, null, string.Empty, ["verify-git"], TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.HostUnavailable, exception.ExitCode);
        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDefaultDistribution_OnAMachineThatIsNotWindows_IsUnavailable()
    {
        var exception = await Assert.ThrowsAsync<HarnessException>(
            () => Create(platform: HostDoubles.Platform(PlatformId.Linux)).Service.RunAsync(Root, null, string.Empty, ["verify-git"], TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.HostUnavailable, exception.ExitCode);
        Assert.DoesNotContain(HostConnector.SyncedCopyNotice, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Asked for WSL's default distribution inside a synced copy on Linux, where there is no WSL at all,
    /// the refusal says where the command belongs, as every host a copy cannot reach does. WSL is asked
    /// before any connection is opened, so the connector - which says it for every other refusal - never
    /// sees this one.
    /// </summary>
    [Fact]
    public async Task TheDefaultDistribution_AskedForInASyncedCopy_SaysWhereTheCommandBelongs()
    {
        var exception = await Assert.ThrowsAsync<HarnessException>(
            () => Create(
                    platform: HostDoubles.Platform(PlatformId.Linux),
                    loader: HostDoubles.Loader(Config, Root, syncedCopy: true))
                .Service.RunAsync(Root, null, string.Empty, ["verify-git"], TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.HostUnavailable, exception.ExitCode);
        Assert.Equal($"WSL exists only on Windows, and this machine runs linux. {HostConnector.SyncedCopyNotice}", exception.Message);
    }

    /// <summary>
    /// host-exec typed in a worktree runs in that worktree's own copy on the host, beside the main checkout's, as
    /// the worktree's legs do there: the main checkout's copy holds another tree.
    /// </summary>
    [Fact]
    public async Task FromAWorktree_TheCommandRunsInThatWorktreesOwnCopy()
    {
        HostCommand? sent = null;
        var worktree = Path.Combine(Root, ".harness-config", "worktrees", "feature");
        var fixture = Create(
            respond: (_, command) =>
            {
                sent = command;
                return HostResults.Finished(command, HarnessExit.Success);
            },
            loader: HostDoubles.Loader(Config, worktree, Root));

        var outcome = await fixture.Service.RunAsync(worktree, "vps", null, ["verify-git"], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.NotNull(sent);
        Assert.Equal("/srv/repo.worktree-feature", JsonSerializer.Deserialize<HostAgentRequest>(sent.StandardInput, HostAgentProtocol.JsonOptions)!.Directory);
    }

    /// <summary>A host reached, with a session a request can travel on.</summary>
    private static HostReport Reachable(HostId host) => new()
    {
        Host = host,
        Os = "linux",
        Processor = "x86_64",
        Session = new HostSession(
            new HostConnection
            {
                Host = host,
                Distribution = "Example-Linux",
                Address = "host.invalid",
                User = "harness",
                KeyFile = "/repo/.key",
                KnownHostsFile = "/repo/known_hosts",
            },
            ".dotnet/tools/dssharness"),
    };

    private static Fixture Create(
        Func<HostId, HostReport>? report = null,
        Func<HostConnection, HostCommand, ProcessResult>? respond = null,
        IHostPlatform? platform = null,
        bool verbose = false,
        IHarnessContextLoader? loader = null)
    {
        var inspector = new RecordingInspector(report ?? Reachable);

        var commands = new ScriptedHostCommands(respond ?? ((_, command) => throw HostResults.Unexpected(command)));
        var error = new StringWriter();
        var output = new StringWriter();

        var service = new HostExecService(
            loader ?? HostDoubles.Loader(Config, Root),
            inspector,
            commands,
            platform ?? HostDoubles.Platform(),
            new ConsoleHarnessOutput(output, error, verbose));

        return new Fixture(service, inspector, commands, error, output);
    }

    private sealed record Fixture(HostExecService Service, RecordingInspector Inspector, ScriptedHostCommands Commands, StringWriter Error, StringWriter Output);
}
