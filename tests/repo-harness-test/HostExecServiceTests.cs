using System.Text.Json;
using NSubstitute;
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
    public async Task ACommandThatReachesHosts_IsNotRunOnOne(string command)
    {
        var exception = await Assert.ThrowsAsync<HarnessException>(
            () => Create().Service.RunAsync(Root, "vps", null, [command], TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.UsageError, exception.ExitCode);
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
    public async Task AHostThatCannotRunRepoHarness_ReportsWhy_AsUnavailable()
    {
        var fixture = Create(report: host => new HostReport { Host = host, Reason = "ssh could not connect: Connection refused" });

        var outcome = await fixture.Service.RunAsync(Root, "vps", null, ["verify-git"], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.HostUnavailable, outcome.ExitCode);
        Assert.Equal("ssh vps cannot run repo-harness: ssh could not connect: Connection refused", outcome.Message);
        Assert.Empty(fixture.Commands.Calls);
    }

    [Fact]
    public async Task TheCommand_TravelsOnStandardInput_AndItsExitCodeComesBackUnchanged()
    {
        var fixture = Create(respond: (_, _) => HostResults.Failed(3, "the anchor was not found"));

        var outcome = await fixture.Service.RunAsync(Root, "VPS", null, ["read-anchor", "D-A B"], TestContext.Current.CancellationToken);

        Assert.Equal(3, outcome.ExitCode);
        Assert.Equal([HostId.Ssh("vps")], fixture.Inspector.Inspected);

        var (_, command) = Assert.Single(fixture.Commands.Calls);
        Assert.Equal(".dotnet/tools/repo-harness", command.Program);
        Assert.Equal([HostAgentProtocol.CommandName], command.Arguments);

        // The arguments, a space included, never reach a shell: they are inside the request.
        var request = JsonSerializer.Deserialize<HostAgentRequest>(command.StandardInput!, HostAgentProtocol.JsonOptions);
        Assert.NotNull(request);
        Assert.Equal(HostAgentRequestKind.Run, request.Kind);
        Assert.Equal("/srv/repo", request.Directory);
        Assert.Equal(["read-anchor", "D-A B"], request.Arguments);
    }

    [Fact]
    public async Task TheDefaultDistribution_IsTheOneWslItselfReports()
    {
        var fixture = Create(platform: Windows(), respond: (_, _) => HostResults.Ok(string.Empty));
        fixture.Commands.DefaultWslDistribution = () => HostResults.Ok("Ubuntu\n");

        var outcome = await fixture.Service.RunAsync(Root, null, string.Empty, ["verify-git"], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal([HostId.Wsl("Ubuntu")], fixture.Inspector.Inspected);

        var request = JsonSerializer.Deserialize<HostAgentRequest>(fixture.Commands.Calls[0].Command.StandardInput!, HostAgentProtocol.JsonOptions);
        Assert.Equal("~/src/repo", request?.Directory);
    }

    [Fact]
    public async Task TheDefaultDistribution_WithoutWslInstalled_IsUnavailable()
    {
        var fixture = Create(platform: Windows());
        fixture.Commands.DefaultWslDistribution = () => throw new ExecutableNotFoundException(HostCommandRunner.WslProgram);

        var exception = await Assert.ThrowsAsync<HarnessException>(
            () => fixture.Service.RunAsync(Root, null, string.Empty, ["verify-git"], TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.HostUnavailable, exception.ExitCode);
        Assert.Contains("wsl.exe was not found", exception.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Inspector.Inspected);
    }

    [Fact]
    public async Task TheDefaultDistribution_OnAMachineThatIsNotWindows_IsUnavailable()
    {
        var platform = Substitute.For<IHostPlatform>();
        platform.Current.Returns(PlatformId.Linux);
        platform.PlatformKey.Returns("linux");

        var exception = await Assert.ThrowsAsync<HarnessException>(
            () => Create(platform: platform).Service.RunAsync(Root, null, string.Empty, ["verify-git"], TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.HostUnavailable, exception.ExitCode);
    }

    private static IHostPlatform Windows()
    {
        var platform = Substitute.For<IHostPlatform>();
        platform.Current.Returns(PlatformId.Windows);
        return platform;
    }

    private static (HostExecService Service, RecordingInspector Inspector, ScriptedHostCommands Commands) Create(
        Func<HostId, HostReport>? report = null,
        Func<HostConnection, HostCommand, ProcessResult>? respond = null,
        IHostPlatform? platform = null)
    {
        var loader = Substitute.For<IHarnessContextLoader>();
        loader.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new HarnessContext(new HarnessLayout(Root, Root), Config)));

        var inspector = new RecordingInspector(report ?? (host => new HostReport
        {
            Host = host,
            Os = "linux",
            Processor = "x86_64",
            Session = new HostSession(new HostConnection { Host = host, SshConfigFile = "config" }, ".dotnet/tools/repo-harness"),
        }));

        var commands = new ScriptedHostCommands(respond ?? ((_, command) => throw HostResults.Unexpected(command)));

        var service = new HostExecService(
            loader,
            inspector,
            commands,
            platform ?? Substitute.For<IHostPlatform>(),
            new ConsoleHarnessOutput(new StringWriter(), new StringWriter(), verbose: false));

        return (service, inspector, commands);
    }
}
