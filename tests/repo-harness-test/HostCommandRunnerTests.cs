using NSubstitute;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Processes;

namespace RepoHarness.Tests;

/// <summary>How a program is started on each kind of host, which decides what reaches the program there.</summary>
public sealed class HostCommandRunnerTests
{
    private static readonly HostCommand ListSdks = new()
    {
        Program = "dotnet",
        Arguments = ["--list-sdks"],
        StandardInput = "{}",
        Timeout = TimeSpan.FromSeconds(5),
    };

    [Fact]
    public void Local_RunsTheProgramItself()
    {
        var request = HostCommandRunner.BuildRequest(new HostConnection { Host = HostId.Local }, ListSdks);

        Assert.Equal("dotnet", request.FileName);
        Assert.Equal(["--list-sdks"], request.Arguments);
        Assert.Equal("{}", request.StandardInput);
        Assert.Equal(TimeSpan.FromSeconds(5), request.Timeout);
    }

    [Fact]
    public void Wsl_StartsTheProgramWithoutAShell_InTheHomeDirectory()
    {
        var request = HostCommandRunner.BuildRequest(new HostConnection { Host = HostId.Wsl("Ubuntu") }, ListSdks);

        Assert.Equal(HostCommandRunner.WslProgram, request.FileName);
        Assert.Equal(["--distribution", "Ubuntu", "--cd", "~", "--exec", "dotnet", "--list-sdks"], request.Arguments);

        // wsl.exe writes its own messages in UTF-16 otherwise, and they are read as UTF-8.
        Assert.Equal("1", request.Environment["WSL_UTF8"]);
        Assert.Equal("{}", request.StandardInput);
    }

    [Fact]
    public void Wsl_DefaultDistribution_IsNamedByTheDistributionItself()
    {
        var request = HostCommandRunner.DefaultWslDistributionRequest(TimeSpan.FromSeconds(7));

        // wsl.exe's own listing is translated into the machine's language; a distribution names itself
        // the same way everywhere.
        Assert.Equal(HostCommandRunner.WslProgram, request.FileName);
        Assert.Equal(["--exec", "printenv", "WSL_DISTRO_NAME"], request.Arguments);
        Assert.Equal("1", request.Environment["WSL_UTF8"]);
        Assert.Equal(TimeSpan.FromSeconds(7), request.Timeout);
        Assert.Equal(string.Empty, request.StandardInput);
    }

    [Fact]
    public void Ssh_NeverWaitsAtAPrompt_AndHandsTheShellOneLiteralLine()
    {
        var connection = new HostConnection
        {
            Host = HostId.Ssh("vps"),
            SshConfigFile = "/repo/.harness-config/ssh/config",
            ConnectTimeoutSeconds = 10,
            KeepAliveSeconds = 15,
            LocalDirectory = "/repo",
        };

        var request = HostCommandRunner.BuildRequest(connection, ListSdks);

        Assert.Equal(HostCommandRunner.SshProgram, request.FileName);
        Assert.Equal(
            [
                "-F", "/repo/.harness-config/ssh/config",
                "-o", "BatchMode=yes",
                "-o", "ConnectTimeout=10",
                "-o", "ServerAliveInterval=15",
                "-o", "ServerAliveCountMax=1",
                "-T",
                "vps",
                "dotnet --list-sdks",
            ],
            request.Arguments);
        Assert.Equal("/repo", request.WorkingDirectory);
        Assert.Equal("{}", request.StandardInput);
    }

    [Fact]
    public void Ssh_RefusesAnArgument_ThatAShellCouldReinterpret()
    {
        var connection = new HostConnection { Host = HostId.Ssh("vps"), SshConfigFile = "config" };

        Assert.Throws<ArgumentException>(() => HostCommandRunner.BuildRequest(
            connection,
            new HostCommand { Program = "DssHarness", Arguments = ["-C", "/path with space"] }));
    }

    [Fact]
    public void Ssh_SpellsAPathWithBackslashes_WhenTheShellIsCmd()
    {
        var connection = new HostConnection { Host = HostId.Ssh("win"), SshConfigFile = "config", Shell = RemoteShell.Cmd };

        var request = HostCommandRunner.BuildRequest(
            connection,
            new HostCommand { Program = @".dotnet\tools\DssHarness.exe", Arguments = ["host-agent"] });

        Assert.Equal(@".dotnet\tools\DssHarness.exe host-agent", request.Arguments[^1]);
    }

    [Fact]
    public void AProgramOnAHost_IsGivenNoInput_UnlessItIsGivenSome()
    {
        var connection = new HostConnection { Host = HostId.Ssh("vps"), SshConfigFile = "config" };

        var request = HostCommandRunner.BuildRequest(connection, new HostCommand { Program = "dotnet", Arguments = ["--list-sdks"] });

        // ssh forwards whatever input it has, so input inherited from this process would go to whichever probe ran first.
        Assert.Equal(string.Empty, request.StandardInput);
        Assert.False(request.HoldStandardInputOpen);
    }

    [Fact]
    public void InputHeldOpen_IsPassedOnHeldOpen()
    {
        var request = HostCommandRunner.BuildRequest(
            new HostConnection { Host = HostId.Wsl("Ubuntu") },
            new HostCommand { Program = "DssHarness", Arguments = ["host-agent"], StandardInput = "{}\n", HoldStandardInputOpen = true });

        Assert.Equal("{}\n", request.StandardInput);
        Assert.True(request.HoldStandardInputOpen);
    }

    [Fact]
    public async Task TheProbes_AreGivenNoInput()
    {
        var processRunner = Substitute.For<IProcessRunner>();
        processRunner.RunAsync(Arg.Any<ProcessRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(HostResults.Ok(string.Empty)));
        var hosts = new HostCommandRunner(processRunner);

        await hosts.ProbeShellAsync(
            new HostConnection { Host = HostId.Ssh("vps"), SshConfigFile = "config" },
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        await hosts.ProbeDefaultWslDistributionAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        _ = processRunner.Received(2).RunAsync(
            Arg.Is<ProcessRequest>(request => request.StandardInput == string.Empty && !request.HoldStandardInputOpen),
            Arg.Any<CancellationToken>());
    }
}
