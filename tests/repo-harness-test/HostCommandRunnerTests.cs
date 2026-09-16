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

    /// <summary>An ssh host whose item declares every part of the connection. Fictitious throughout.</summary>
    private static HostConnection Ssh(string name = "build-box") => new()
    {
        Host = HostId.Ssh(name),
        Address = "host.invalid",
        User = "harness",
        Port = 2222,
        KeyFile = "/repo/.harness-config/sshItems/build-box/.key",
        KnownHostsFile = "/repo/.harness-config/sshItems/build-box/known_hosts",
        ConnectTimeoutSeconds = 10,
        KeepAliveSeconds = 15,
        LocalDirectory = "/repo",
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
        var connection = new HostConnection { Host = HostId.Wsl("lane-a"), Distribution = "Example-Linux" };

        var request = HostCommandRunner.BuildRequest(connection, ListSdks);

        Assert.Equal(HostCommandRunner.WslProgram, request.FileName);

        // The distribution comes from the host's own item, never from the tracked configuration, which
        // is keyed by the name --wsl selects it by.
        Assert.Equal(["--distribution", "Example-Linux", "--cd", "~", "--exec", "dotnet", "--list-sdks"], request.Arguments);

        // wsl.exe writes its own messages in UTF-16 otherwise, and they are read as UTF-8.
        Assert.Equal("1", request.Environment["WSL_UTF8"]);
        Assert.Equal("{}", request.StandardInput);
    }

    [Fact]
    public void Wsl_WithoutItsDistribution_StartsNothing()
    {
        // Left to wsl.exe, a call with no --distribution reaches whichever distribution is the default,
        // which is a machine nobody selected.
        Assert.Throws<InvalidOperationException>(
            () => HostCommandRunner.BuildRequest(new HostConnection { Host = HostId.Wsl("lane-a") }, ListSdks));
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
    public void Ssh_IsBuiltFromTheHostsOwnItem_AndReadsNoSshConfigurationFile()
    {
        var request = HostCommandRunner.BuildRequest(Ssh(), ListSdks);

        Assert.Equal(HostCommandRunner.SshProgram, request.FileName);
        Assert.Equal(
            [
                "-i", "/repo/.harness-config/sshItems/build-box/.key",
                "-o", "IdentitiesOnly=yes",
                "-o", "UserKnownHostsFile=/repo/.harness-config/sshItems/build-box/known_hosts",
                "-o", "BatchMode=yes",
                "-o", "ConnectTimeout=10",
                "-o", "ServerAliveInterval=15",
                "-o", "ServerAliveCountMax=1",
                "-p", "2222",
                "-T",
                "harness@host.invalid",
                "dotnet --list-sdks",
            ],
            request.Arguments);

        // Nothing points ssh at a configuration file: one passed with -F is read whatever its
        // permissions, and a tracked config.json naming one would decide which machine a clone reaches.
        Assert.DoesNotContain("-F", request.Arguments);
        Assert.Equal("/repo", request.WorkingDirectory);
        Assert.Equal("{}", request.StandardInput);
    }

    [Fact]
    public void Ssh_WithoutAKey_StartsNothing()
    {
        var connection = Ssh() with { KeyFile = null };

        Assert.Throws<InvalidOperationException>(() => HostCommandRunner.BuildRequest(connection, ListSdks));
    }

    [Fact]
    public void Ssh_RefusesAnArgument_ThatAShellCouldReinterpret()
    {
        Assert.Throws<ArgumentException>(() => HostCommandRunner.BuildRequest(
            Ssh(),
            new HostCommand { Program = "DssHarness", Arguments = ["-C", "/path with space"] }));
    }

    [Fact]
    public void Ssh_SpellsAPathWithBackslashes_WhenTheShellIsCmd()
    {
        var connection = Ssh("win-box") with { Shell = RemoteShell.Cmd };

        var request = HostCommandRunner.BuildRequest(
            connection,
            new HostCommand { Program = @".dotnet\tools\DssHarness.exe", Arguments = ["host-agent"] });

        Assert.Equal(@".dotnet\tools\DssHarness.exe host-agent", request.Arguments[^1]);
    }

    [Fact]
    public void AProgramOnAHost_IsGivenNoInput_UnlessItIsGivenSome()
    {
        var request = HostCommandRunner.BuildRequest(Ssh(), new HostCommand { Program = "dotnet", Arguments = ["--list-sdks"] });

        // ssh forwards whatever input it has, so input inherited from this process would go to whichever probe ran first.
        Assert.Equal(string.Empty, request.StandardInput);
        Assert.False(request.HoldStandardInputOpen);
    }

    [Fact]
    public void InputHeldOpen_IsPassedOnHeldOpen()
    {
        var request = HostCommandRunner.BuildRequest(
            new HostConnection { Host = HostId.Wsl("lane-a"), Distribution = "Example-Linux" },
            new HostCommand { Program = "DssHarness", Arguments = ["host-agent"], StandardInput = "{}\n", HoldStandardInputOpen = true });

        Assert.Equal("{}\n", request.StandardInput);
        Assert.True(request.HoldStandardInputOpen);
    }

    [Fact]
    public void AResolvedProgram_IsStartedByItsAbsolutePath_AndOneOnPathByItsName()
    {
        var connection = Ssh() with
        {
            Programs = new Dictionary<string, ProgramLocation>(StringComparer.Ordinal)
            {
                ["dotnet"] = new("dotnet", ProgramFound.OffPath, "/home/harness/.dotnet/dotnet"),
                ["git"] = new("git", ProgramFound.OnPath, "/usr/bin/git"),
                ["cmake"] = new("cmake", ProgramFound.Nowhere),
            },
        };

        Assert.Equal("/home/harness/.dotnet/dotnet", connection.Spell("dotnet"));

        // A program the PATH already names needs no path, and a Windows one holds spaces a command line
        // built here may not carry.
        Assert.Equal("git", connection.Spell("git"));
        Assert.Equal("cmake", connection.Spell("cmake"));
        Assert.Null(connection.Located("ninja"));
        Assert.Equal(ProgramFound.Unknown, connection.Forget("dotnet").Located("dotnet")?.Found ?? ProgramFound.Unknown);
    }

    [Fact]
    public async Task TheProbes_AreGivenNoInput()
    {
        var processRunner = Substitute.For<IProcessRunner>();
        processRunner.RunAsync(Arg.Any<ProcessRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(HostResults.Ok(string.Empty)));
        var hosts = new HostCommandRunner(processRunner);

        await hosts.ProbeShellAsync(Ssh(), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await hosts.ProbeDefaultWslDistributionAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        _ = processRunner.Received(2).RunAsync(
            Arg.Is<ProcessRequest>(request => request.StandardInput == string.Empty && !request.HoldStandardInputOpen),
            Arg.Any<CancellationToken>());
    }
}
