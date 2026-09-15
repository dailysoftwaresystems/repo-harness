using System.Text.Json;
using NSubstitute;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// Inspection decides whether anything runs on a host, and changes what is installed there. Every
/// command a host receives is answered by a script, so each rule is exercised exactly, and none of
/// these tests needs WSL, ssh or a network.
/// </summary>
public sealed class HostInspectorTests
{
    private static readonly ToolIdentity Root = new("1.2.0", "roothash");

    [Fact]
    public async Task Local_IsMeasuredInProcess_WithoutRunningAnything()
    {
        var fixture = new Fixture(PlatformId.Linux);

        var report = await fixture.InspectAsync(HostId.Local);

        Assert.True(report.Available, report.Reason);
        Assert.Equal("linux", report.Os);
        Assert.Equal("x86_64", report.Processor);
        Assert.Empty(fixture.Commands.Calls);
    }

    [Fact]
    public async Task Wsl_IsUnavailable_OnAMachineThatIsNotWindows()
    {
        var fixture = new Fixture(PlatformId.Linux);

        var report = await fixture.InspectAsync(HostId.Wsl("Ubuntu"));

        Assert.Contains("WSL exists only on Windows", report.Reason, StringComparison.Ordinal);
        Assert.Empty(fixture.Commands.Calls);
    }

    [Fact]
    public async Task Wsl_NamesADistributionThatDoesNotExist()
    {
        var fixture = new Fixture(
            PlatformId.Windows,
            respond: (_, command) => command.Program == "uname"
                ? HostResults.Failed(127, "Código de erro: Wsl/Service/WSL_E_DISTRO_NOT_FOUND")
                : throw HostResults.Unexpected(command));

        var report = await fixture.InspectAsync(HostId.Wsl("Debian"));

        Assert.Equal("WSL has no distribution named 'Debian'", report.Reason);
    }

    [Fact]
    public async Task Wsl_WithoutTheSdk_SaysTheSdkIsMissing()
    {
        var fixture = new Fixture(
            PlatformId.Windows,
            respond: HostThat(listSdks: HostResults.Failed(1, "<3>WSL (708) ERROR: CreateProcessCommon:559: execvpe(dotnet) failed: No such file or directory")));

        var report = await fixture.InspectAsync(HostId.Wsl("Ubuntu"));

        Assert.Equal("the .NET 10 SDK is not installed in the distribution", report.Reason);
        Assert.Equal("linux", report.Os);
    }

    [Fact]
    public async Task AnSdkOlderThanTheToolNeeds_IsNamed()
    {
        var fixture = new Fixture(PlatformId.Windows, respond: HostThat(sdks: "8.0.414 [/usr/lib/dotnet/sdk]\n"));

        var report = await fixture.InspectAsync(HostId.Wsl("Ubuntu"));

        Assert.Equal("repo-harness needs the .NET 10 SDK there, and it has 8.0.414", report.Reason);
    }

    [Fact]
    public async Task AHostWithoutTheTool_GetsThisMachinesVersionInstalled_ThenAnswers()
    {
        var fixture = new Fixture(PlatformId.Windows, respond: HostThat(installed: null));

        var report = await fixture.InspectAsync(HostId.Wsl("Ubuntu"));

        Assert.True(report.Available, report.Reason);
        Assert.Equal(["installed repo-harness 1.2.0"], report.Actions);
        Assert.Equal(
            ["tool", "install", "--global", "RepoHarness", "--version", "1.2.0", "--source", "https://api.nuget.org/v3/index.json"],
            fixture.Commands.Single("tool", "install").Arguments);
        Assert.Equal(".dotnet/tools/repo-harness", report.Session?.ToolPath);
        Assert.Equal("linux", report.Os);
        Assert.Equal("x86_64", report.Processor);
    }

    [Fact]
    public async Task AHostThatIsBehind_IsUpdated_AndNeverWithADowngradeAllowed()
    {
        var fixture = new Fixture(PlatformId.Windows, respond: HostThat(installed: "1.1.9"));

        var report = await fixture.InspectAsync(HostId.Wsl("Ubuntu"));

        Assert.True(report.Available, report.Reason);
        Assert.Equal(["updated repo-harness 1.1.9 to 1.2.0"], report.Actions);
        // nuget.org is named as the only source, so no feed configured on the host can supply another
        // package under the same name.
        Assert.Equal(
            ["tool", "update", "--global", "RepoHarness", "--version", "1.2.0", "--source", "https://api.nuget.org/v3/index.json"],
            fixture.Commands.Single("tool", "update").Arguments);
        Assert.DoesNotContain(fixture.Commands.Calls, call => call.Command.Arguments.Contains("--allow-downgrade"));
    }

    [Fact]
    public async Task AHostThatIsBehind_IsNotUpdated_WhileRepoHarnessRunsThere()
    {
        var fixture = new Fixture(PlatformId.Windows, respond: HostThat(installed: "1.1.9", processes: "bash\nrepo-harness\n"));

        var report = await fixture.InspectAsync(HostId.Wsl("Ubuntu"));

        Assert.Contains("repo-harness is running there, so it was not updated", report.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Commands.Calls, call => call.Command.Arguments.Contains("update"));
    }

    [Fact]
    public async Task AHostThatIsAhead_StopsEverything_WithTheCommandThatUpdatesThisMachine()
    {
        var fixture = new Fixture(PlatformId.Windows, respond: HostThat(installed: "1.3.0"));

        var exception = await Assert.ThrowsAsync<HarnessException>(() => fixture.InspectAsync(HostId.Wsl("Ubuntu")));

        Assert.Equal(HarnessExit.Refused, exception.ExitCode);
        Assert.Contains("dotnet tool update --global RepoHarness --version 1.3.0", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            fixture.Commands.Calls,
            call => call.Command.Arguments.Contains("update") || call.Command.Arguments.Contains("install"));
    }

    [Fact]
    public async Task AHostWithADifferentBuildOfTheSameVersion_IsUnavailable()
    {
        // A build from source reports the same version as the published package; only its bytes differ.
        var fixture = new Fixture(PlatformId.Windows, respond: HostThat(installed: "1.2.0", answeredHash: "otherhash"));

        var report = await fixture.InspectAsync(HostId.Wsl("Ubuntu"));

        Assert.Contains("is a different build from this machine's", report.Reason, StringComparison.Ordinal);
        Assert.Null(report.Session);
    }

    [Fact]
    public async Task AnInstallThatFails_SaysAHostRunsOnlyPublishedVersions()
    {
        var fixture = new Fixture(
            PlatformId.Windows,
            respond: HostThat(installed: null, install: HostResults.Failed(1, "error NU1101: Unable to find package RepoHarness")));

        var report = await fixture.InspectAsync(HostId.Wsl("Ubuntu"));

        Assert.Contains("a host runs only a version published on nuget.org", report.Reason, StringComparison.Ordinal);
        Assert.Contains("NU1101", report.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ssh_IsUnavailable_WithoutTheHarnessSshConfiguration()
    {
        using var repository = new TempDirectory();
        var fixture = new Fixture(PlatformId.Linux, repository: repository.Path);

        var report = await fixture.InspectAsync(HostId.Ssh("vps"));

        Assert.Contains(".harness-config/ssh/config does not exist", report.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ssh_IsUnavailable_WhenTheConfigurationDoesNotDeclareTheHost()
    {
        using var repository = new TempDirectory();
        repository.WriteFile(Path.Combine(".harness-config", "ssh", "config"), "Host other\n");
        var fixture = new Fixture(PlatformId.Linux, repository: repository.Path);

        var report = await fixture.InspectAsync(HostId.Ssh("vps"));

        Assert.Equal(".harness-config/ssh/config has no 'Host vps' entry", report.Reason);
    }

    [Fact]
    public async Task Ssh_IsUnavailable_WhenOtherUsersCanReadItsKey_BeforeConnecting()
    {
        using var repository = new TempDirectory();
        repository.WriteFile(Path.Combine(".harness-config", "ssh", "config"), "Host vps\n  IdentityFile .harness-config/ssh/vps_key\n");
        repository.WriteFile(Path.Combine(".harness-config", "ssh", "vps_key"), "not really a key");
        var fixture = new Fixture(PlatformId.Linux, repository: repository.Path);
        fixture.Permissions.IsPrivate(Arg.Any<string>()).Returns(false);

        var report = await fixture.InspectAsync(HostId.Ssh("vps"));

        Assert.Contains("other users can read the key", report.Reason, StringComparison.Ordinal);
        Assert.Contains("chmod 600", report.Reason, StringComparison.Ordinal);
        Assert.Empty(fixture.Commands.Calls);
    }

    [Fact]
    public async Task Ssh_ChecksAKey_ThatAWildcardEntryGivesTheHost()
    {
        // ssh offers every key whose Host entry selects the host, Host * included.
        using var repository = new TempDirectory();
        repository.WriteFile(
            Path.Combine(".harness-config", "ssh", "config"),
            "Host vps\n  HostName vps.example\n\nHost *\n  IdentityFile .harness-config/ssh/shared_key\n");
        repository.WriteFile(Path.Combine(".harness-config", "ssh", "shared_key"), "not really a key");
        var fixture = new Fixture(PlatformId.Linux, repository: repository.Path);
        fixture.Permissions.IsPrivate(Arg.Any<string>()).Returns(false);

        var report = await fixture.InspectAsync(HostId.Ssh("vps"));

        Assert.Contains("other users can read the key", report.Reason, StringComparison.Ordinal);
        Assert.Contains("shared_key", report.Reason, StringComparison.Ordinal);
        Assert.Empty(fixture.Commands.Calls);
    }

    [Fact]
    public async Task Ssh_ReportsWhatSshSaid_WhenItCannotConnect()
    {
        using var repository = new TempDirectory();
        repository.WriteFile(Path.Combine(".harness-config", "ssh", "config"), "Host vps\n  HostName vps.example\n");
        var fixture = new Fixture(PlatformId.Linux, repository: repository.Path);
        fixture.Commands.ShellProbe = HostResults.Failed(255, "Host key verification failed.\n");

        var report = await fixture.InspectAsync(HostId.Ssh("vps"));

        Assert.Equal("ssh could not connect: Host key verification failed.", report.Reason);
    }

    [Fact]
    public async Task Ssh_ToACmdShell_SpellsTheToolsPathWithBackslashes()
    {
        using var repository = new TempDirectory();
        repository.WriteFile(Path.Combine(".harness-config", "ssh", "config"), "Host vps\n  HostName vps.example\n");
        var fixture = new Fixture(
            PlatformId.Linux,
            repository: repository.Path,
            respond: HostThat(sdks: "10.0.100 [C:\\Program Files\\dotnet\\sdk]\n", answeredOs: "windows"));
        fixture.Commands.ShellProbe = HostResults.Ok("C:\\WINDOWS\\system32\\cmd.exe\r\n");

        var report = await fixture.InspectAsync(HostId.Ssh("vps"));

        Assert.True(report.Available, report.Reason);
        Assert.Equal(@".dotnet\tools\repo-harness.exe", report.Session?.ToolPath);
        Assert.Equal(RemoteShell.Cmd, report.Session?.Connection.Shell);
        Assert.Equal("windows", report.Os);
    }

    [Fact]
    public async Task Ssh_ToAWindowsHostWhoseShellIsNotCmd_IsStillTreatedAsWindows()
    {
        // PowerShell leaves %COMSPEC% unexpanded, as sh does, and runs on Windows all the same. Where the SDK
        // is installed says what the host is; the shell says only which separator a path takes.
        using var repository = new TempDirectory();
        repository.WriteFile(Path.Combine(".harness-config", "ssh", "config"), "Host vps\n  HostName vps.example\n");
        var fixture = new Fixture(
            PlatformId.Linux,
            repository: repository.Path,
            respond: HostThat(installed: "1.1.9", sdks: "10.0.100 [C:\\Program Files\\dotnet\\sdk]\n", answeredOs: "windows"));

        var report = await fixture.InspectAsync(HostId.Ssh("vps"));

        Assert.True(report.Available, report.Reason);
        Assert.Equal(RemoteShell.Standard, report.Session?.Connection.Shell);
        Assert.Equal(".dotnet/tools/repo-harness.exe", report.Session?.ToolPath);
        Assert.Equal("tasklist", fixture.Commands.Single("/FO").Program);
    }

    /// <summary>A host with the .NET 10 SDK that answers each probe as scripted.</summary>
    private static Func<HostConnection, HostCommand, ProcessResult> HostThat(
        string? installed = "1.2.0",
        ProcessResult? listSdks = null,
        string sdks = "10.0.100 [/usr/lib/dotnet/sdk]\n",
        string processes = "bash\nsshd\n",
        string answeredHash = "roothash",
        string answeredOs = "linux",
        ProcessResult? install = null)
        => (_, command) => (command.Program, command.Arguments.FirstOrDefault()) switch
        {
            ("uname", _) => HostResults.Ok("Linux x86_64\n"),
            ("dotnet", "--list-sdks") => listSdks ?? HostResults.Ok(sdks),
            ("dotnet", "tool") when command.Arguments[1] == "list" => HostResults.Ok(ToolList(installed)),
            ("dotnet", "tool") when command.Arguments[1] is "install" or "update" => install ?? HostResults.Ok("done"),
            ("ps" or "tasklist", _) => HostResults.Ok(processes),
            (_, HostAgentProtocol.CommandName) => HostResults.Ok(JsonSerializer.Serialize(
                new HostAgentInfo { Version = Root.Version, AssemblySha256 = answeredHash, Os = answeredOs, Processor = "x86_64" },
                HostAgentProtocol.JsonOptions)),
            _ => throw HostResults.Unexpected(command),
        };

    private static string ToolList(string? installed) => installed is null
        ? """{"version":1,"data":[]}"""
        : $$"""{"version":1,"data":[{"packageId":"repoharness","version":"{{installed}}","commands":["repo-harness"]}]}""";

    private sealed class Fixture
    {
        private readonly HarnessContext _context;
        private readonly HostInspector _inspector;

        public Fixture(
            PlatformId platformId,
            Func<HostConnection, HostCommand, ProcessResult>? respond = null,
            string? repository = null)
        {
            var platform = Substitute.For<IHostPlatform>();
            platform.Current.Returns(platformId);
            platform.PlatformKey.Returns(platformId == PlatformId.Windows ? "windows" : "linux");
            platform.Processor.Returns("x86_64");
            platform.HomeDirectory.Returns(TestHost.TemporaryRoot);

            var processRunner = Substitute.For<IProcessRunner>();
            processRunner.FindExecutable(Arg.Any<string>()).Returns(call => "/usr/bin/" + call.Arg<string>());

            Permissions = Substitute.For<IFilePermissions>();
            Permissions.IsPrivate(Arg.Any<string>()).Returns(true);

            var identity = Substitute.For<IToolIdentityProvider>();
            identity.Current.Returns(Root);

            Commands = new ScriptedHostCommands(respond ?? ((_, command) => throw HostResults.Unexpected(command)));

            var root = repository ?? TestHost.TemporaryRoot;
            var config = new HarnessConfig
            {
                Hosts = new HostsConfig
                {
                    Wsl = { ["Ubuntu"] = new WslHostConfig { RepositoryPath = "~/repo" } },
                    Ssh = { ["vps"] = new SshHostConfig { RepositoryPath = "/srv/repo", ConnectTimeoutSeconds = 5 } },
                },
            };

            _context = new HarnessContext(new HarnessLayout(root, root), config);

            var fileSystem = new PhysicalFileSystem(FilePermissionsFactory.Create());
            var agent = new HostAgentService(platform, identity, new EmulatorProbe(platform, processRunner, fileSystem), fileSystem);
            _inspector = new HostInspector(platform, processRunner, Commands, fileSystem, Permissions, identity, agent);
        }

        public ScriptedHostCommands Commands { get; }

        public IFilePermissions Permissions { get; }

        public Task<HostReport> InspectAsync(HostId host)
            => _inspector.InspectAsync(
                _context,
                host,
                new Dictionary<string, EmulatorConfig>(StringComparer.OrdinalIgnoreCase),
                TestContext.Current.CancellationToken);
    }
}
