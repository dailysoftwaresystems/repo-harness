using System.Text.Json;
using NSubstitute;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Secrets;

namespace RepoHarness.Tests;

/// <summary>
/// Inspection decides whether anything runs on a host, and changes what is installed there. Every
/// command a host receives is answered by a script, so each rule is exercised exactly, and none of
/// these tests needs WSL, ssh or a network. Every address, user and distribution here is fictitious.
/// </summary>
public sealed class HostInspectorTests
{
    private const string Distro = "lane-a";

    private const string SshName = "build-box";

    private const string Home = "/home/harness";

    private static readonly ToolIdentity Root = new("1.2.0", "roothash");

    private static readonly string[] ToolChanges = ["install", "update"];

    [Fact]
    public async Task Local_IsMeasuredInProcess_WithoutRunningAnything()
    {
        using var fixture = new Fixture(PlatformId.Linux);

        var report = await fixture.InspectAsync(HostId.Local);

        Assert.True(report.Available, report.Reason);
        Assert.Equal("linux", report.Os);
        Assert.Equal("x86_64", report.Processor);
        Assert.Empty(fixture.Commands.Calls);
    }

    [Fact]
    public async Task Wsl_IsUnavailable_OnAMachineThatIsNotWindows()
    {
        using var fixture = new Fixture(PlatformId.Linux);

        var report = await fixture.InspectAsync(HostId.Wsl(Distro));

        Assert.Contains("WSL exists only on Windows", report.Reason, StringComparison.Ordinal);
        Assert.Empty(fixture.Commands.Calls);
    }

    [Fact]
    public async Task Wsl_IsUnavailable_WhenNoItemDeclaresWhichDistributionItIs()
    {
        using var fixture = new Fixture(PlatformId.Windows, writeItems: false);

        var report = await fixture.InspectAsync(HostId.Wsl(Distro));

        Assert.Contains($".harness-config/wslDistros/{Distro}' does not exist", report.Reason, StringComparison.Ordinal);
        Assert.Empty(fixture.Commands.Calls);
    }

    [Fact]
    public async Task Wsl_NamesADistributionThatDoesNotExist()
    {
        using var fixture = new Fixture(
            PlatformId.Windows,
            respond: (_, command) => command.Program == "uname"
                ? HostResults.Failed(127, "Código de erro: Wsl/Service/WSL_E_DISTRO_NOT_FOUND")
                : throw HostResults.Unexpected(command));

        var report = await fixture.InspectAsync(HostId.Wsl(Distro));

        Assert.Equal("WSL has no distribution named 'Example-Linux'", report.Reason);
    }

    [Fact]
    public async Task AHostWithoutDotnetAnywhere_SaysTheSdkIsNotInstalled_AndHowToInstallIt()
    {
        using var fixture = new Fixture(PlatformId.Windows, respond: HostThat(dotnet: Where.Nowhere));

        var report = await fixture.InspectAsync(HostId.Wsl(Distro));

        Assert.StartsWith("the .NET 10 SDK is not installed there", report.Reason, StringComparison.Ordinal);
        Assert.Contains("install-missing-tools", report.Reason, StringComparison.Ordinal);
        Assert.Equal("linux", report.Os);
    }

    [Fact]
    public async Task AHostWhoseDotnetIsOffThePath_RunsItByItsAbsolutePath()
    {
        // Measured: ~/.dotnet is absent from the PATH of a command run with wsl.exe --exec, and
        // /opt/homebrew/bin from an ssh command's PATH on macOS. Reported as "not installed", it sends
        // somebody to install a second copy of what is already there.
        using var fixture = new Fixture(PlatformId.Windows, respond: HostThat(dotnet: Where.OffPath));

        var report = await fixture.InspectAsync(HostId.Wsl(Distro));

        Assert.True(report.Available, report.Reason);
        Assert.All(
            fixture.Commands.Calls.Where(call => call.Command.Arguments.Contains("--list-sdks")),
            call => Assert.Equal($"{Home}/.dotnet/dotnet", call.Command.Program));
    }

    [Fact]
    public async Task AHostWhoseDotnetIsOffThePathAndWillNotRun_SaysWhereItWasFound()
    {
        using var fixture = new Fixture(
            PlatformId.Windows,
            respond: HostThat(dotnet: Where.OffPath, listSdks: HostResults.Failed(126, "Permission denied")));

        var report = await fixture.InspectAsync(HostId.Wsl(Distro));

        Assert.Contains($"'dotnet' is installed at '{Home}/.dotnet/dotnet'", report.Reason, StringComparison.Ordinal);
        Assert.Contains("off the PATH of a command run without a login shell", report.Reason, StringComparison.Ordinal);
        Assert.Contains("Permission denied", report.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHostThatNeverAnsweredTheLookup_IsNotReportedAsMissingTheSdk()
    {
        using var fixture = new Fixture(PlatformId.Windows, respond: (_, _) => HostResults.Failed(255, "Connection reset"));

        var report = await fixture.InspectAsync(HostId.Ssh(SshName));

        Assert.Contains("could not be established", report.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnSdkOlderThanTheToolNeeds_IsNamed()
    {
        using var fixture = new Fixture(PlatformId.Windows, respond: HostThat(sdks: "8.0.414 [/usr/lib/dotnet/sdk]\n"));

        var report = await fixture.InspectAsync(HostId.Wsl(Distro));

        Assert.Equal("DssHarness needs the .NET 10 SDK there, and it has 8.0.414", report.Reason);
    }

    [Fact]
    public async Task AnSdkListingThatCannotBeRead_IsReportedAsUnreadable_RatherThanAsNoSdk()
    {
        using var fixture = new Fixture(PlatformId.Windows, respond: HostThat(sdks: "Welcome to .NET! Telemetry is collected.\n"));

        var report = await fixture.InspectAsync(HostId.Wsl(Distro));

        Assert.StartsWith("dotnet --list-sdks printed nothing this build can read as an SDK", report.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHostWithoutTheTool_GetsThisMachinesVersionInstalled_ThenAnswers()
    {
        using var fixture = new Fixture(PlatformId.Windows, respond: HostThat(installed: null));

        var report = await fixture.InspectAsync(HostId.Wsl(Distro));

        Assert.True(report.Available, report.Reason);
        Assert.Equal(["installed DssHarness 1.2.0"], report.Actions);
        Assert.Equal(
            ["tool", "install", "--global", "DssHarness", "--version", "1.2.0", "--source", "https://api.nuget.org/v3/index.json"],
            fixture.Commands.Single("tool", "install").Arguments);
        Assert.Equal(".dotnet/tools/DssHarness", report.Session?.ToolPath);
        Assert.Equal("linux", report.Os);
        Assert.Equal("x86_64", report.Processor);
    }

    [Fact]
    public async Task TheQuestionToTheHost_IsOneLine_WhoseInputStaysOpenUntilItIsAnswered()
    {
        using var fixture = new Fixture(PlatformId.Windows, respond: HostThat());

        var report = await fixture.InspectAsync(HostId.Wsl(Distro));

        Assert.True(report.Available, report.Reason);

        var question = fixture.Commands.Single(HostAgentProtocol.CommandName);
        Assert.True(question.HoldStandardInputOpen);
        Assert.Single(question.StandardInput, character => character == '\n');
        Assert.EndsWith("\n", question.StandardInput, StringComparison.Ordinal);

        // Every other probe is given no input, so none can take input meant for something else.
        Assert.All(
            fixture.Commands.Calls.Where(call => !ReferenceEquals(call.Command, question)),
            call => Assert.Equal(string.Empty, call.Command.StandardInput));
    }

    [Fact]
    public async Task AHostThatIsBehind_IsUpdated_AndNeverWithADowngradeAllowed()
    {
        using var fixture = new Fixture(PlatformId.Windows, respond: HostThat(installed: "1.1.9"));

        var report = await fixture.InspectAsync(HostId.Wsl(Distro));

        Assert.True(report.Available, report.Reason);
        Assert.Equal(["updated DssHarness 1.1.9 to 1.2.0"], report.Actions);

        // nuget.org is named as the only source, so no feed configured on the host can supply another
        // package under the same name.
        Assert.Equal(
            ["tool", "update", "--global", "DssHarness", "--version", "1.2.0", "--source", "https://api.nuget.org/v3/index.json"],
            fixture.Commands.Single("tool", "update").Arguments);
        Assert.DoesNotContain(fixture.Commands.Calls, call => call.Command.Arguments.Contains("--allow-downgrade"));
    }

    [Fact]
    public async Task AHostThatIsBehind_IsNotUpdated_WhileDssHarnessRunsThere()
    {
        using var fixture = new Fixture(PlatformId.Windows, respond: HostThat(installed: "1.1.9", processes: "bash\nDssHarness\n"));

        var report = await fixture.InspectAsync(HostId.Wsl(Distro));

        Assert.Contains("DssHarness is running there, so it was not updated", report.Reason, StringComparison.Ordinal);
        AssertToolUntouched(fixture);
    }

    [Fact]
    public async Task AHostThatIsBehind_IsNotUpdated_WhenItsProcessesCannotBeListed()
    {
        // Read as "nothing is running", a listing that failed would replace a tool in the middle of a run.
        using var fixture = new Fixture(
            PlatformId.Windows,
            respond: HostThat(installed: "1.1.9", listProcesses: HostResults.Failed(127, "ps: command not found")));

        var report = await fixture.InspectAsync(HostId.Wsl(Distro));

        Assert.Contains("its running processes could not be listed", report.Reason, StringComparison.Ordinal);
        AssertToolUntouched(fixture);
    }

    [Fact]
    public async Task AToolListThatCannotBeRead_IsReported_AndNothingIsInstalled()
    {
        // Read as "not installed", it would install over a tool that may be newer, and skip the refusal.
        using var fixture = new Fixture(PlatformId.Windows, respond: HostThat(toolList: HostResults.Ok("Tool list failed\n")));

        var report = await fixture.InspectAsync(HostId.Wsl(Distro));

        Assert.Contains("its global .NET tools could not be listed", report.Reason, StringComparison.Ordinal);
        AssertToolUntouched(fixture);
    }

    [Fact]
    public async Task AnInstalledVersionThatCannotBeCompared_IsReported_AndNothingIsInstalled()
    {
        using var fixture = new Fixture(PlatformId.Windows, respond: HostThat(installed: "latest"));

        var report = await fixture.InspectAsync(HostId.Wsl(Distro));

        Assert.Contains("DssHarness latest there cannot be compared with 1.2.0 here", report.Reason, StringComparison.Ordinal);
        AssertToolUntouched(fixture);
    }

    [Fact]
    public async Task AHostOnThisVersion_IsLeftAlone_EvenWhileDssHarnessRunsThere()
    {
        using var fixture = new Fixture(PlatformId.Windows, respond: HostThat(installed: "1.2.0", processes: "DssHarness\n"));

        var report = await fixture.InspectAsync(HostId.Wsl(Distro));

        Assert.True(report.Available, report.Reason);
        Assert.Empty(report.Actions);
        AssertToolUntouched(fixture);
        Assert.DoesNotContain(fixture.Commands.Calls, call => call.Command.Program is "ps" or "tasklist");
    }

    [Fact]
    public async Task AHostThatIsAhead_StopsEverything_WithTheCommandThatUpdatesThisMachine()
    {
        using var fixture = new Fixture(PlatformId.Windows, respond: HostThat(installed: "1.3.0"));

        var exception = await Assert.ThrowsAsync<HarnessException>(() => fixture.InspectAsync(HostId.Wsl(Distro)));

        Assert.Equal(HarnessExit.Refused, exception.ExitCode);
        Assert.Contains("dotnet tool update --global DssHarness --version 1.3.0", exception.Message, StringComparison.Ordinal);
        AssertToolUntouched(fixture);
    }

    [Fact]
    public async Task AHostWithADifferentBuildOfTheSameVersion_IsUnavailable()
    {
        // A build from source reports the same version as the published package; only its bytes differ.
        using var fixture = new Fixture(PlatformId.Windows, respond: HostThat(installed: "1.2.0", answeredHash: "otherhash"));

        var report = await fixture.InspectAsync(HostId.Wsl(Distro));

        Assert.Contains("is a different build from this machine's", report.Reason, StringComparison.Ordinal);
        Assert.Null(report.Session);
    }

    [Fact]
    public async Task ADifferentBuild_IsReportedAsThat_ThoughItsAnswerHasFieldsThisBuildDoesNotKnow()
    {
        // Identified before the rest of the answer is read, so a build that answers in another shape still gets the remedy.
        using var fixture = new Fixture(
            PlatformId.Windows,
            respond: HostThat(agent: _ => HostResults.Ok("""{"version":"1.2.0","assemblySha256":"otherhash","os":"linux","processor":"x86_64","addedLater":true}""")));

        var report = await fixture.InspectAsync(HostId.Wsl(Distro));

        Assert.Contains("is a different build from this machine's", report.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAnswerFromThisBuild_ThatCannotBeRead_IsReportedAsUnreadable()
    {
        using var fixture = new Fixture(
            PlatformId.Windows,
            respond: HostThat(agent: _ => HostResults.Ok("""{"version":"1.2.0","assemblySha256":"roothash","os":"linux","processor":"x86_64","addedLater":true}""")));

        var report = await fixture.InspectAsync(HostId.Wsl(Distro));

        Assert.Contains("answered in a form this build cannot read", report.Reason, StringComparison.Ordinal);
        Assert.Null(report.Session);
    }

    [Theory]
    [InlineData("""{"version":"1.1.9","assemblySha256":"roothash","os":"linux","processor":"x86_64"}""", "DssHarness there reports 1.1.9, and 1.2.0 was expected")]
    [InlineData("Segmentation fault", "answered with no document")]
    public async Task AnAnswerThatIsNotFromThisBuild_LeavesTheHostUnavailable(string answer, string expected)
    {
        using var fixture = new Fixture(PlatformId.Windows, respond: HostThat(agent: _ => HostResults.Ok(answer)));

        var report = await fixture.InspectAsync(HostId.Wsl(Distro));

        Assert.Contains(expected, report.Reason, StringComparison.Ordinal);
        Assert.Null(report.Session);
    }

    [Fact]
    public async Task EmulatorsTravelToTheHost_AndWhatItFoundComesBack()
    {
        var emulators = new Dictionary<string, EmulatorConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["qemu-arm64"] = new()
            {
                HostOs = "linux",
                HostProcessor = "x86_64",
                Processor = "arm64",
                Witness = new EmulatorWitness { Command = ["/opt/arm64/uname"], Pattern = "aarch64" },
            },
        };

        using var fixture = new Fixture(PlatformId.Windows, respond: HostThat(agent: command =>
        {
            var request = JsonSerializer.Deserialize<HostAgentRequest>(command.StandardInput, HostAgentProtocol.JsonOptions);

            var checks = request!.Emulators.Keys.ToDictionary(
                name => name,
                _ => new EmulatorCheck(true, null, "aarch64"),
                StringComparer.OrdinalIgnoreCase);

            return HostResults.Ok(JsonSerializer.Serialize(
                new HostAgentInfo { Version = Root.Version, AssemblySha256 = Root.AssemblySha256, Os = "linux", Processor = "x86_64", Emulators = checks },
                HostAgentProtocol.JsonOptions));
        }));

        var report = await fixture.InspectAsync(HostId.Wsl(Distro), emulators);

        Assert.True(report.Available, report.Reason);
        Assert.True(report.Emulators["QEMU-ARM64"].Available);
    }

    [Fact]
    public async Task AnInstallThatFails_SaysAHostRunsOnlyPublishedVersions()
    {
        using var fixture = new Fixture(
            PlatformId.Windows,
            respond: HostThat(installed: null, install: HostResults.Failed(1, "error NU1101: Unable to find package DssHarness")));

        var report = await fixture.InspectAsync(HostId.Wsl(Distro));

        Assert.Contains("a host runs only a version published on nuget.org", report.Reason, StringComparison.Ordinal);
        Assert.Contains("NU1101", report.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUpdateThatFails_SaysAHostRunsOnlyPublishedVersions()
    {
        // A machine running a beta asks for a version nuget.org never had; an update says why, as an install does.
        using var fixture = new Fixture(
            PlatformId.Windows,
            respond: HostThat(installed: "1.1.9", install: HostResults.Failed(1, "error NU1102: Unable to find package DssHarness with version (= 1.2.0)")));

        var report = await fixture.InspectAsync(HostId.Wsl(Distro));

        Assert.Contains("updating DssHarness 1.1.9 to 1.2.0 there failed", report.Reason, StringComparison.Ordinal);
        Assert.Contains("a host runs only a version published on nuget.org", report.Reason, StringComparison.Ordinal);
        Assert.Contains("NU1102", report.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ssh_IsUnavailable_WithoutTheHostsOwnItem()
    {
        using var fixture = new Fixture(PlatformId.Linux, writeItems: false);

        var report = await fixture.InspectAsync(HostId.Ssh(SshName));

        Assert.Contains($".harness-config/sshItems/{SshName}' does not exist", report.Reason, StringComparison.Ordinal);
        Assert.Empty(fixture.Commands.ShellProbes);
    }

    [Fact]
    public async Task Ssh_IsUnavailable_WhenItIsNotDeclaredUnderHostsSsh()
    {
        using var fixture = new Fixture(PlatformId.Linux);

        var report = await fixture.InspectAsync(HostId.Ssh("another-box"));

        Assert.Equal("it is not declared under hosts.ssh", report.Reason);
        Assert.Empty(fixture.Commands.ShellProbes);
    }

    [Fact]
    public async Task Ssh_IsUnavailable_WhenOtherUsersCanReadItsKey_BeforeConnecting()
    {
        using var fixture = new Fixture(PlatformId.Linux);
        fixture.Permissions.IsPrivate(Arg.Any<string>()).Returns(false);

        var report = await fixture.InspectAsync(HostId.Ssh(SshName));

        Assert.Contains("so ssh ignores it", report.Reason, StringComparison.Ordinal);
        Assert.Contains("chmod 600", report.Reason, StringComparison.Ordinal);
        Assert.Empty(fixture.Commands.ShellProbes);
        Assert.Empty(fixture.Commands.Calls);
    }

    [Fact]
    public async Task Ssh_IsUnavailable_WhenItsNameResolvesToNothing()
    {
        using var fixture = new Fixture(PlatformId.Linux, resolves: false);

        var report = await fixture.InspectAsync(HostId.Ssh(SshName));

        Assert.Contains("resolved to no address", report.Reason, StringComparison.Ordinal);
        Assert.Empty(fixture.Commands.ShellProbes);
    }

    [Fact]
    public async Task Ssh_ReportsWhatSshSaid_WhenItCannotConnect()
    {
        using var fixture = new Fixture(PlatformId.Linux);
        fixture.Commands.ShellProbe = HostResults.Failed(255, "Host key verification failed.\n");

        var report = await fixture.InspectAsync(HostId.Ssh(SshName));

        Assert.Equal("the host could not be reached: ssh said Host key verification failed.", report.Reason);
    }

    [Fact]
    public async Task Ssh_ConnectsWithTheHostsOwnItem_AndTheHostsSettings()
    {
        using var fixture = new Fixture(PlatformId.Linux, respond: HostThat());

        var report = await fixture.InspectAsync(HostId.Ssh(SshName));

        Assert.True(report.Available, report.Reason);

        var connection = Assert.Single(fixture.Commands.ShellProbes);
        Assert.Equal("host.invalid", connection.Address);
        Assert.Equal("harness", connection.User);
        Assert.Equal(2222, connection.Port);
        PathAssert.Same(fixture.Repository.Combine(".harness-config", "sshItems", SshName, ".key"), connection.KeyFile!);
        PathAssert.Same(fixture.Repository.Combine(".harness-config", "sshItems", SshName, "known_hosts"), connection.KnownHostsFile!);
        PathAssert.Same(fixture.Repository.Path, connection.LocalDirectory!);
        Assert.Equal(5, connection.ConnectTimeoutSeconds);
        Assert.All(fixture.Commands.Calls, call => Assert.Equal(connection.Address, call.Connection.Address));
    }

    [Fact]
    public async Task Ssh_ToACmdShell_SpellsTheToolsPathWithBackslashes()
    {
        using var fixture = new Fixture(
            PlatformId.Linux,
            respond: HostThat(sdks: "10.0.100 [C:\\Program Files\\dotnet\\sdk]\n", answeredOs: "windows"));
        fixture.Commands.ShellProbe = HostResults.Ok("C:\\WINDOWS\\system32\\cmd.exe\r\n");

        var report = await fixture.InspectAsync(HostId.Ssh(SshName));

        Assert.True(report.Available, report.Reason);
        Assert.Equal(@".dotnet\tools\DssHarness.exe", report.Session?.ToolPath);
        Assert.Equal(RemoteShell.Cmd, report.Session?.Connection.Shell);
        Assert.Equal("windows", report.Os);
    }

    [Fact]
    public async Task Ssh_ToAWindowsHostWhoseShellIsNotCmd_IsStillTreatedAsWindows()
    {
        // PowerShell leaves %COMSPEC% unexpanded, as sh does, and runs on Windows all the same. Where the SDK
        // is installed says what the host is; the shell says only which separator a path takes.
        using var fixture = new Fixture(
            PlatformId.Linux,
            respond: HostThat(installed: "1.1.9", sdks: "10.0.100 [C:\\Program Files\\dotnet\\sdk]\n", answeredOs: "windows"));

        var report = await fixture.InspectAsync(HostId.Ssh(SshName));

        Assert.True(report.Available, report.Reason);
        Assert.Equal(RemoteShell.Standard, report.Session?.Connection.Shell);
        Assert.Equal(".dotnet/tools/DssHarness.exe", report.Session?.ToolPath);
        Assert.Equal("tasklist", fixture.Commands.Single("/FO").Program);
    }

    /// <summary>Where a host keeps <c>dotnet</c>, which decides how a command there has to spell it.</summary>
    private enum Where
    {
        OnPath,
        OffPath,
        Nowhere,
    }

    /// <summary>A host with the .NET 10 SDK that answers each probe as scripted.</summary>
    private static Func<HostConnection, HostCommand, ProcessResult> HostThat(
        string? installed = "1.2.0",
        Where dotnet = Where.OnPath,
        ProcessResult? listSdks = null,
        string sdks = "10.0.100 [/usr/lib/dotnet/sdk]\n",
        ProcessResult? toolList = null,
        string processes = "bash\nsshd\n",
        ProcessResult? listProcesses = null,
        string answeredHash = "roothash",
        string answeredOs = "linux",
        ProcessResult? install = null,
        Func<HostCommand, ProcessResult>? agent = null)
    {
        var dotnetPath = dotnet == Where.OffPath ? $"{Home}/.dotnet/dotnet" : "dotnet";

        return (_, command) =>
        {
            // The lookup answers where dotnet is; every other program on these hosts is on the PATH.
            if (Lookup(command) is { } name)
            {
                return name != "dotnet" || dotnet == Where.OnPath
                    ? HostResults.Ok($"/usr/bin/{name}\n")
                    : HostResults.Failed(1, string.Empty);
            }

            if (command.Program == "pwd")
            {
                return HostResults.Ok(Home + "\n");
            }

            if (command.Program == "ls")
            {
                var present = dotnet == Where.OffPath ? command.Arguments.Where(path => path == dotnetPath) : [];
                return HostResults.Ok(string.Join('\n', present) + "\n");
            }

            var program = command.Program == dotnetPath ? "dotnet" : command.Program;

            return (program, command.Arguments.FirstOrDefault()) switch
            {
                ("uname", _) => HostResults.Ok("Linux x86_64\n"),
                ("dotnet", "--list-sdks") => listSdks ?? HostResults.Ok(sdks),
                ("dotnet", "tool") when command.Arguments[1] == "list" => toolList ?? HostResults.Ok(ToolList(installed)),
                ("dotnet", "tool") when command.Arguments[1] is "install" or "update" => install ?? HostResults.Ok("done"),
                ("ps" or "tasklist", _) => listProcesses ?? HostResults.Ok(processes),
                (_, HostAgentProtocol.CommandName) => agent?.Invoke(command) ?? HostResults.Ok(JsonSerializer.Serialize(
                    new HostAgentInfo { Version = Root.Version, AssemblySha256 = answeredHash, Os = answeredOs, Processor = "x86_64" },
                    HostAgentProtocol.JsonOptions)),
                _ => throw HostResults.Unexpected(command),
            };
        };
    }

    /// <summary>The program a PATH lookup asked about, or <see langword="null"/> when this is not one.</summary>
    private static string? Lookup(HostCommand command) => (command.Program, command.Arguments.FirstOrDefault()) switch
    {
        ("command", "-v") => command.Arguments[1],
        ("where", not null) => command.Arguments[0],
        ("sh", "-c") => command.Arguments[1].Split(' ')[^1],
        _ => null,
    };

    private static string ToolList(string? installed) => installed is null
        ? """{"version":1,"data":[]}"""
        : $$"""{"version":1,"data":[{"packageId":"dssharness","version":"{{installed}}","commands":["DssHarness"]}]}""";

    /// <summary>Asserts that nothing installed or updated DssHarness on the host.</summary>
    private static void AssertToolUntouched(Fixture fixture)
        => Assert.DoesNotContain(
            fixture.Commands.Calls,
            call => Path.GetFileName(call.Command.Program) == "dotnet"
                && call.Command.Arguments.Count > 1
                && ToolChanges.Contains(call.Command.Arguments[1]));

    private sealed class Fixture : IDisposable
    {
        private readonly HarnessContext _context;
        private readonly HostInspector _inspector;

        public Fixture(
            PlatformId platformId,
            Func<HostConnection, HostCommand, ProcessResult>? respond = null,
            bool writeItems = true,
            bool resolves = true)
        {
            Repository = new TempDirectory();

            var platform = HostDoubles.Platform(platformId);

            var processRunner = Substitute.For<IProcessRunner>();
            processRunner.FindExecutable(Arg.Any<string>()).Returns(call => "/usr/bin/" + call.Arg<string>());

            Permissions = Substitute.For<IFilePermissions>();
            Permissions.IsPrivate(Arg.Any<string>()).Returns(true);

            var identity = Substitute.For<IToolIdentityProvider>();
            identity.Current.Returns(Root);

            Commands = new ScriptedHostCommands(respond ?? ((_, command) => throw HostResults.Unexpected(command)));

            if (writeItems)
            {
                Repository.WriteFile(Path.Combine(".harness-config", "wslDistros", Distro, ".env"), "DISTRO=Example-Linux\n");
                Repository.WriteFile(
                    Path.Combine(".harness-config", "sshItems", SshName, ".env"),
                    "ADDRESS=host.invalid\nUSER=harness\nPORT=2222\n");
                Repository.WriteFile(Path.Combine(".harness-config", "sshItems", SshName, ".key"), "not a real key");
                Repository.WriteFile(Path.Combine(".harness-config", "sshItems", SshName, "known_hosts"), "host.invalid ssh-ed25519 AAAA\n");
            }

            var config = new HarnessConfig
            {
                SshItems = { SshName },
                WslDistros = { Distro },
                Hosts = new HostsConfig
                {
                    Wsl = { [Distro] = new WslHostConfig { RepositoryPath = "~/repo" } },
                    Ssh = { [SshName] = new SshHostConfig { RepositoryPath = "/srv/repo", ConnectTimeoutSeconds = 5 } },
                },
            };

            _context = new HarnessContext(new HarnessLayout(Repository.Path, Repository.Path), config);

            var fileSystem = new PhysicalFileSystem(FilePermissionsFactory.Create());
            var agent = new HostAgentService(platform, identity, new EmulatorProbe(platform, processRunner, fileSystem), fileSystem);
            var secrets = new HostSecretsStore(fileSystem, Permissions, platform);
            var addresses = new HostAddressResolver(new FixedLookup(resolves), TimeProvider.System, TimeSpan.Zero);
            var programs = new HostProgramResolver(processRunner, Commands);
            var connector = new HostConnector(platform, processRunner, Commands, secrets, addresses, programs);

            _inspector = new HostInspector(Commands, connector, identity, agent);
        }

        public TempDirectory Repository { get; }

        public ScriptedHostCommands Commands { get; }

        public IFilePermissions Permissions { get; }

        public Task<HostReport> InspectAsync(HostId host, IReadOnlyDictionary<string, EmulatorConfig>? emulators = null)
            => _inspector.InspectAsync(
                _context,
                host,
                emulators ?? new Dictionary<string, EmulatorConfig>(StringComparer.OrdinalIgnoreCase),
                TestContext.Current.CancellationToken);

        public void Dispose() => Repository.Dispose();

        /// <summary>A resolver that either answers for every name or for none, with no network involved.</summary>
        private sealed class FixedLookup(bool resolves) : INameLookup
        {
            public Task<IReadOnlyList<string>> LookupAsync(string name, CancellationToken cancellationToken = default)
                => Task.FromResult<IReadOnlyList<string>>(resolves ? ["192.0.2.10"] : []);
        }
    }
}
