using System.Text.Json;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
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
    }

    private static Fixture Create(
        Func<HostId, HostReport>? report = null,
        Func<HostConnection, HostCommand, ProcessResult>? respond = null,
        IHostPlatform? platform = null,
        bool verbose = false)
    {
        var inspector = new RecordingInspector(report ?? (host => new HostReport
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
        }));

        var commands = new ScriptedHostCommands(respond ?? ((_, command) => throw HostResults.Unexpected(command)));
        var error = new StringWriter();

        var service = new HostExecService(
            HostDoubles.Loader(Config, Root),
            inspector,
            commands,
            platform ?? HostDoubles.Platform(),
            new ConsoleHarnessOutput(new StringWriter(), error, verbose));

        return new Fixture(service, inspector, commands, error);
    }

    private sealed record Fixture(HostExecService Service, RecordingInspector Inspector, ScriptedHostCommands Commands, StringWriter Error);
}
