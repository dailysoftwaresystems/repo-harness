using RepoHarness.Core.Output;
using RepoHarness.Core.Execution;
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
    private const string Distro = "wsl-a";

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

    /// <summary>
    /// This machine is asked about developer environments in process too, and says whether it can set
    /// each one up: this one runs Linux, where Visual Studio never does.
    /// </summary>
    [Fact]
    public async Task Local_SaysWhichDeveloperEnvironmentsItCanSetUp()
    {
        using var fixture = new Fixture(PlatformId.Linux);

        var report = await fixture.InspectAsync(
            HostId.Local,
            environments: new Dictionary<string, DeveloperEnvironmentConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["vs"] = new() { Kind = DeveloperEnvironmentKinds.VisualStudio },
            });

        Assert.Equal("Visual Studio is set up on windows, and this host runs linux", report.DeveloperEnvironments["VS"].Reason);
        Assert.Empty(fixture.Commands.Calls);
    }

    [Fact]
    public async Task Wsl_IsUnavailable_OnAMachineThatIsNotWindows()
    {
        using var fixture = new Fixture(PlatformId.Linux);

        var report = await fixture.InspectAsync(HostId.Wsl(Distro));

        Assert.Contains("WSL exists only on Windows", report.Reason, StringComparison.Ordinal);
        Assert.Empty(fixture.Commands.Calls);

        // Nothing about a copy, in a checkout somebody works in: there the machine is simply the wrong one.
        Assert.DoesNotContain(HostConnector.SyncedCopyNotice, report.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A host of any kind that a command typed in a synced copy cannot reach is refused in its own words,
    /// then with where the command belongs. Only the ssh kind's missing item once said so: a WSL leg in the
    /// same copy - on Linux, where there is no WSL - was refused as WSL existing solely on Windows, true
    /// and no help to somebody who had typed the command in the wrong tree.
    /// </summary>
    [Theory]
    [InlineData("wsl", "WSL exists only on Windows, and this machine runs linux")]
    [InlineData("ssh", $"'.harness-config/sshItems/{SshName}' does not exist")]
    public async Task AHostACopyCannotReach_IsRefusedWithWhereTheCommandBelongs(string kind, string own)
    {
        using var fixture = new Fixture(PlatformId.Linux, writeItems: false, syncedCopy: true);

        var report = await fixture.InspectAsync(kind == "wsl" ? HostId.Wsl(Distro) : HostId.Ssh(SshName));

        Assert.Equal($"{own}. {HostConnector.SyncedCopyNotice}", report.Reason);
        Assert.DoesNotContain("create it", report.Reason, StringComparison.Ordinal);
        Assert.Empty(fixture.Commands.Calls);
    }

    /// <summary>A refusal that ends its own sentence is joined to the notice with one full stop, not two.</summary>
    [Theory]
    [InlineData("the host could not be reached")]
    [InlineData("the host could not be reached.")]
    [InlineData("the host could not be reached. ")]
    public void ARefusalFromACopy_MeetsTheNoticeWithOneFullStop(string refusal)
        => Assert.Equal($"the host could not be reached. {HostConnector.SyncedCopyNotice}", HostConnector.InACopy(refusal));

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

    /// <summary>
    /// A host ssh never connected to while DssHarness was asked about it is said as that, in ssh's words,
    /// and nothing about DssHarness is: said as DssHarness not answering from its tool path, a name that did
    /// not resolve sent the reader after an install that was fine. A host ssh reached, where DssHarness gave
    /// no answer, is still said as that.
    /// </summary>
    [Theory]
    [InlineData(255, "ssh: Could not resolve hostname host.invalid: No such host is known.", "the host could not be reached: ssh said ssh: Could not resolve hostname host.invalid: No such host is known.")]
    [InlineData(255, "banner exchange: Connection to UNKNOWN port -1: Connection refused", "the host could not be reached: ssh said banner exchange: Connection to UNKNOWN port -1: Connection refused")]
    [InlineData(255, "Connection to host.invalid closed by remote host.", "DssHarness did not answer from")]
    [InlineData(1, "Segmentation fault", "DssHarness did not answer from")]
    public async Task AHostSshNeverConnectedTo_IsSaidAsThat_AndNotAsDssHarnessNotAnswering(int exitCode, string said, string reason)
    {
        using var fixture = new Fixture(PlatformId.Windows, respond: HostThat(agent: _ => HostResults.Failed(exitCode, said)));

        var report = await fixture.InspectAsync(HostId.Ssh(SshName));

        Assert.False(report.Available);
        Assert.Contains(reason, report.Reason, StringComparison.Ordinal);
        Assert.Contains(said, report.Reason, StringComparison.Ordinal);
        Assert.Equal(reason.StartsWith("DssHarness", StringComparison.Ordinal), report.Reason!.Contains("did not answer", StringComparison.Ordinal));
    }

    /// <summary>
    /// A host whose login profile writes before its agent does: what it wrote reaches neither the answer nor
    /// the reason, both of which are read from the agent's own marker on.
    /// </summary>
    /// <remarks>
    /// An inspection's reason is quoted into the host's report, which reaches the reader, <c>--json</c> and
    /// every leg reported unavailable on that host - so a profile printing the account's home layout
    /// published it there, exactly as a relayed leg line did. A profile that prints a brace would also have
    /// been read as the start of the answer.
    /// </remarks>
    [Fact]
    public async Task AProfileThatPrintsBeforeTheAgent_ReachesNeitherTheAnswerNorTheReason()
    {
        const string profile = "PATH += /Users/someone/Library/emsdk {not a document}";

        using var answered = new Fixture(PlatformId.Windows, respond: HostThat(agent: command => HostResults.Ok(
            profile + "\n" + Marker(command) + "\n" + JsonSerializer.Serialize(
                new HostAgentInfo { Version = Root.Version, AssemblySha256 = "roothash", Os = "linux", Processor = "x86_64" },
                HostAgentProtocol.JsonOptions))));

        var reached = await answered.InspectAsync(HostId.Ssh(SshName));

        Assert.True(reached.Available, reached.Reason);

        using var refused = new Fixture(PlatformId.Windows, respond: HostThat(agent: command => HostResults.Failed(
            3,
            profile + "\n" + Marker(command) + "\nhost-agent: this host holds no copy of the repository")));

        var stopped = await refused.InspectAsync(HostId.Ssh(SshName));

        Assert.False(stopped.Available);
        Assert.Contains("this host holds no copy of the repository", stopped.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("someone", stopped.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A host that never reached its agent wrote no marker, so the whole of what it said is quoted: it is
    /// all the reader has to go on.
    /// </summary>
    [Fact]
    public async Task AHostThatNeverReachedItsAgent_IsQuotedWhole()
    {
        using var fixture = new Fixture(PlatformId.Windows, respond: HostThat(
            agent: _ => HostResults.Failed(127, "sh: dssharness: command not found")));

        var report = await fixture.InspectAsync(HostId.Ssh(SshName));

        Assert.False(report.Available);
        Assert.Contains("sh: dssharness: command not found", report.Reason, StringComparison.Ordinal);
    }

    /// <summary>The line the host's agent writes to mark where its own answer begins, for the nonce it was sent.</summary>
    private static string Marker(HostCommand command)
    {
        var request = JsonSerializer.Deserialize<HostAgentRequest>(command.StandardInput!, HostAgentProtocol.JsonOptions);

        return HostAgentProtocol.StartedLine(request!.Nonce!);
    }

    [Fact]
    public async Task AHostThatNeverAnsweredTheLookup_IsNotReportedAsMissingTheSdk()
    {
        using var fixture = new Fixture(PlatformId.Windows, respond: (_, _) => HostResults.Failed(255, "Connection reset"));

        var report = await fixture.InspectAsync(HostId.Ssh(SshName));

        Assert.Contains("could not be established", report.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A host that answered, and could not look everywhere it was told to, gives the search's own
    /// reason - which running again does not change, so nothing tells the reader to.
    /// </summary>
    [Fact]
    public async Task AHostThatCouldNotLookEverywhereForDotnet_SaysWhy_AndSendsNobodyToRunAgain()
    {
        var host = HostThat(dotnet: Where.OffPath);

        using var fixture = new Fixture(
            PlatformId.Windows,
            respond: (connection, command) => command.Program == "pwd"
                ? HostResults.Failed(1, "pwd: cannot read")
                : host(connection, command));

        var report = await fixture.InspectAsync(HostId.Wsl(Distro));

        Assert.Contains("could not be established: 'dotnet' is not on the PATH there, and '~/.dotnet'", report.Reason, StringComparison.Ordinal);
        Assert.Contains("because its home directory could not be read", report.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("run again", report.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A host that did not answer while its SDK was looked for leaves no reason of the search's own,
    /// and running again may well change that: the reader is told so.
    /// </summary>
    [Fact]
    public async Task AHostThatStoppedAnsweringWhileDotnetWasLookedFor_SaysToRunAgain()
    {
        var host = HostThat(dotnet: Where.OffPath);

        using var fixture = new Fixture(
            PlatformId.Windows,
            respond: (connection, command) => command.Program == "pwd"
                ? new ProcessResult(-1, string.Empty, string.Empty, TimeSpan.FromMinutes(2), TimedOut: true)
                : host(connection, command));

        var report = await fixture.InspectAsync(HostId.Wsl(Distro));

        Assert.Contains("the host did not answer when asked where 'dotnet' is; run again once it does", report.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A host whose transport would not start is one host that cannot take legs, in the transport's
    /// own words - whether it failed on the first command or part way through - never an end to the
    /// whole survey, which took every leg on every other host with it.
    /// </summary>
    [Fact]
    public async Task AHostWhoseTransportWouldNotStart_IsUnavailable_AndTheSurveyGoesOn()
    {
        using var atConnect = new Fixture(PlatformId.Windows, respond: (_, _) => throw HostResults.TransportWouldNotStart(HostId.Wsl(Distro)));

        var refused = await atConnect.InspectAsync(HostId.Wsl(Distro));

        Assert.False(refused.Available);
        Assert.Equal("'wsl' could not be started: The file cannot be accessed by the system.", refused.Reason);

        var host = HostThat();

        using var partWay = new Fixture(
            PlatformId.Windows,
            respond: (connection, command) => command.Arguments.Contains("--list-sdks")
                ? throw HostResults.TransportWouldNotStart(HostId.Wsl(Distro))
                : host(connection, command));

        var stopped = await partWay.InspectAsync(HostId.Wsl(Distro));

        Assert.False(stopped.Available);
        Assert.Equal("'wsl' could not be started: The file cannot be accessed by the system.", stopped.Reason);

        // And ssh, whose first command is the probe that learns its shell.
        using var ssh = new Fixture(PlatformId.Windows);
        ssh.Commands.ShellProbeRaises = HostResults.TransportWouldNotStart(HostId.Ssh(SshName));

        var unreached = await ssh.InspectAsync(HostId.Ssh(SshName));

        Assert.False(unreached.Available);
        Assert.Equal("'ssh' could not be started: The file cannot be accessed by the system.", unreached.Reason);
    }

    /// <summary>
    /// Two programs whose names differ only in case are two files on Linux, and the host's answer
    /// about both is kept whole: read into a map that ignored case, it was refused, and every leg on
    /// the host read as unavailable.
    /// </summary>
    [Fact]
    public async Task AnAnswerAboutProgramsDifferingOnlyInCase_KeepsBoth()
    {
        using var fixture = new Fixture(PlatformId.Windows, respond: HostThat(agent: _ => HostResults.Ok(JsonSerializer.Serialize(
            new HostAgentInfo
            {
                Version = Root.Version,
                AssemblySha256 = "roothash",
                Os = "linux",
                Processor = "x86_64",
                Programs = [new("cmake", ProgramFound.OnPath, "/usr/bin/cmake"), new("CMake", ProgramFound.Nowhere)],
            },
            HostAgentProtocol.JsonOptions))));

        var report = await fixture.InspectAsync(HostId.Wsl(Distro));

        Assert.True(report.Available, report.Reason);
        Assert.Equal(ProgramFound.OnPath, report.Programs["cmake"].Found);
        Assert.Equal(ProgramFound.Nowhere, report.Programs["CMake"].Found);
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
        Assert.Equal(".dotnet/tools/dssharness", report.Session?.ToolPath);
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

    /// <summary>
    /// What a host is asked about the room on it travels to it as asked, and what it answers - the room where
    /// its copies are kept, and each build directory's record and room - comes back on its report.
    /// </summary>
    [Fact]
    public async Task TheRoomAHostIsAskedAbout_TravelsToIt_AndWhatItAnswersComesBack()
    {
        HostAgentRequest? asked = null;
        var disk = new DiskSpace(3L << 30, 48L << 30, "/");

        using var fixture = new Fixture(PlatformId.Windows, respond: HostThat(agent: command =>
        {
            asked = JsonSerializer.Deserialize<HostAgentRequest>(command.StandardInput, HostAgentProtocol.JsonOptions);

            return HostResults.Ok(JsonSerializer.Serialize(
                new HostAgentInfo
                {
                    Version = Root.Version,
                    AssemblySha256 = Root.AssemblySha256,
                    Os = "linux",
                    Processor = "x86_64",
                    Space = disk,
                    Builds = [new BuildDirectoryRoom("~/repo/build/x86_64-gcc-debug", true, 4096, disk, null)],
                },
                HostAgentProtocol.JsonOptions));
        }));

        var report = await fixture.InspectAsync(HostId.Wsl(Distro), room: new RoomQuestions("~/repo", ["~/repo/build/x86_64-gcc-debug"]));

        Assert.True(report.Available, report.Reason);
        Assert.Equal("~/repo", asked!.SpaceAt);
        Assert.Equal(["~/repo/build/x86_64-gcc-debug"], asked.Builds);
        Assert.Equal(disk, report.Space);
        Assert.Equal(4096, Assert.Single(report.Builds).RecordedBytes);
    }

    /// <summary>
    /// The developer environments a host is asked about travel to it, what it found comes back under
    /// the names they were asked by, and each one gives the host the time vswhere may take to answer.
    /// </summary>
    [Fact]
    public async Task DeveloperEnvironmentsTravelToTheHost_AndWhatItFoundComesBack()
    {
        var environments = new Dictionary<string, DeveloperEnvironmentConfig>(StringComparer.OrdinalIgnoreCase)
        {
            ["vs"] = new() { Kind = DeveloperEnvironmentKinds.VisualStudio, RequiresComponent = "Microsoft.VisualStudio.Component.VC.Tools.ARM64" },
        };
        var budgets = new List<TimeSpan?>();

        using var fixture = new Fixture(PlatformId.Windows, respond: HostThat(agent: command =>
        {
            var request = JsonSerializer.Deserialize<HostAgentRequest>(command.StandardInput, HostAgentProtocol.JsonOptions);
            budgets.Add(command.Timeout);

            var checks = request!.DeveloperEnvironments.ToDictionary(
                pair => pair.Key,
                pair => DeveloperEnvironmentCheck.Nowhere($"no Visual Studio instance there has the component '{pair.Value.RequiresComponent}'"),
                StringComparer.OrdinalIgnoreCase);

            return HostResults.Ok(JsonSerializer.Serialize(
                new HostAgentInfo { Version = Root.Version, AssemblySha256 = Root.AssemblySha256, Os = "linux", Processor = "x86_64", DeveloperEnvironments = checks },
                HostAgentProtocol.JsonOptions));
        }));

        var report = await fixture.InspectAsync(HostId.Wsl(Distro), environments: environments);
        await fixture.InspectAsync(HostId.Wsl(Distro));

        Assert.True(report.Available, report.Reason);
        Assert.Equal(
            "no Visual Studio instance there has the component 'Microsoft.VisualStudio.Component.VC.Tools.ARM64'",
            report.DeveloperEnvironments["VS"].Reason);
        Assert.Equal(DeveloperEnvironmentProbe.ProbeBudget, budgets[0] - budgets[1]);
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

        Assert.StartsWith(
            "the host could not be reached: ssh said Host key verification failed.",
            report.Reason,
            StringComparison.Ordinal);

        // The client is named too. Which ssh ran is decided by PATH order and is invisible in the
        // failure otherwise: an old client that cannot agree a key exchange with a newer server
        // reads as an unreachable host until the reader is told which ssh was used.
        Assert.Contains("using ", report.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every ssh call a connection makes is given, while the pin holds, the address this machine resolved the
    /// name ssh would look up to, so that name is looked up once, with retries, and not by ssh; the key is
    /// looked up under the name, off port 22 as known_hosts spells such a host. An address declared as one is
    /// dialled as it is, unpinned.
    /// </summary>
    [Theory]
    [InlineData("host.invalid", "192.0.2.10", "[host.invalid]:2222")]
    [InlineData("198.51.100.7", null, null)]
    public async Task Ssh_IsGivenTheAddressThisMachineResolved_ForEveryCall(string address, string? pinned, string? alias)
    {
        using var fixture = new Fixture(PlatformId.Linux, respond: HostThat(), address: address);

        var report = await fixture.InspectAsync(HostId.Ssh(SshName));

        Assert.True(report.Available, report.Reason);
        Assert.NotEmpty(fixture.Commands.Calls);
        Assert.All([.. fixture.Commands.ShellProbes, .. fixture.Commands.Calls.Select(call => call.Connection)], connection =>
        {
            Assert.Equal(address, connection.Address);
            Assert.Equal(pinned, connection.Pin?.Address);
            Assert.Equal(alias, connection.Pin?.KeyAlias);
        });
    }

    /// <summary>
    /// ssh's own configuration decides what is looked up here and what is pinned. A HostName it maps the name
    /// to is what is looked up, and names the key. Through a jump host or a command, which do their own
    /// lookup, nothing is looked up here and nothing pinned - "none" is ssh's word for neither. An
    /// alias of its own is kept; an address it dials anyway is not pinned; and where ssh cannot say what it
    /// would do, the name declared is looked up, and ssh left to itself.
    /// </summary>
    [Theory]
    [InlineData("hostname real.example.test\n", "real.example.test", "192.0.2.10", "[real.example.test]:2222")]
    [InlineData("hostname host.invalid\nproxyjump bastion.test\n", null, null, null)]
    [InlineData("hostname host.invalid\nproxycommand nc %h %p\n", null, null, null)]
    [InlineData("hostname host.invalid\nproxyjump none\n", "host.invalid", "192.0.2.10", "[host.invalid]:2222")]
    [InlineData("hostname host.invalid\nhostkeyalias fixed-alias\n", "host.invalid", "192.0.2.10", "fixed-alias")]
    [InlineData("hostname 192.0.2.10\n", null, null, null)]
    [InlineData(null, "host.invalid", null, null)]
    public async Task WhatSshWouldDo_DecidesWhatIsLookedUp_AndWhatIsPinned(string? configured, string? lookedUp, string? pinned, string? alias)
    {
        using var fixture = new Fixture(PlatformId.Linux, respond: HostThat());
        fixture.Commands.SettingsPrinted = connection => configured is null
            ? HostResults.Failed(255, "ssh: /home/dev/.ssh/config line 3: Bad configuration option: nonsense")
            : HostResults.Ok(AsSshPrintsIt(configured, connection.Pin));

        var report = await fixture.InspectAsync(HostId.Ssh(SshName));

        Assert.True(report.Available, report.Reason);
        Assert.Equal(lookedUp is null ? [] : [lookedUp], fixture.LookedUp);
        Assert.All([.. fixture.Commands.ShellProbes, .. fixture.Commands.Calls.Select(call => call.Connection)], connection =>
        {
            Assert.Equal("host.invalid", connection.Address);
            Assert.Equal(pinned, connection.Pin?.Address);
            Assert.Equal(alias, connection.Pin?.KeyAlias);
        });
    }

    /// <summary>
    /// A pin that would change anything else ssh does is not made: measured, a Match block keyed by the
    /// address turned agent forwarding on for a connection pinned to an address it matched. The name is still
    /// looked up here, and a name that resolves to nothing still refused.
    /// </summary>
    [Fact]
    public async Task APinThatWouldChangeAnythingElseSshDoes_IsNotMade()
    {
        using var fixture = new Fixture(PlatformId.Linux, respond: HostThat());
        fixture.Commands.SettingsPrinted = connection => HostResults.Ok(connection.Pin is { Holds: true } pin
            ? $"hostname {pin.Address}\nhostkeyalias {pin.KeyAlias}\nforwardagent yes\n"
            : "hostname host.invalid\nforwardagent no\n");

        var report = await fixture.InspectAsync(HostId.Ssh(SshName));

        Assert.True(report.Available, report.Reason);
        Assert.Equal(["host.invalid"], fixture.LookedUp);
        Assert.Equal(2, fixture.Commands.SettingsReads.Count);
        Assert.All(fixture.Commands.Calls, call => Assert.Null(call.Connection.Pin));
    }

    /// <summary>
    /// A HostName ssh's own configuration maps the name to, and that resolves to nothing, is refused by that
    /// name, with the name declared beside it: the fix is in ssh's configuration, not in the host's item.
    /// </summary>
    [Fact]
    public async Task AHostNameThatResolvesToNothing_IsRefusedByThatName()
    {
        using var fixture = new Fixture(PlatformId.Linux, respond: HostThat(), resolves: false);
        fixture.Commands.SettingsPrinted = _ => HostResults.Ok("hostname real.example.test\n");

        var report = await fixture.InspectAsync(HostId.Ssh(SshName));

        Assert.False(report.Available);
        Assert.Contains("'real.example.test', the HostName ssh's own configuration gives 'host.invalid', resolved to no address", report.Reason, StringComparison.Ordinal);
        Assert.Empty(fixture.Commands.ShellProbes);
    }

    /// <summary>What ssh -G prints over a connection: its own configuration's lines, a holding pin's address and alias in place of its own.</summary>
    private static string AsSshPrintsIt(string configured, SshPin? pin)
        => pin is not { Holds: true }
            ? configured
            : string.Join('\n', configured.Split('\n').Where(line => !line.StartsWith("hostkeyalias ", StringComparison.Ordinal))
                .Select(line => line.StartsWith("hostname ", StringComparison.Ordinal) ? $"hostname {pin.Address}" : line))
                + $"hostkeyalias {pin.KeyAlias}\n";

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
        Assert.Equal(@".dotnet\tools\dssharness.exe", report.Session?.ToolPath);
        Assert.Equal(RemoteShell.Cmd, report.Session?.Connection.Shell);
        Assert.Equal("windows", report.Os);
    }

    /// <summary>
    /// cmd is Windows's own shell, whose installers put the SDK on the machine PATH, and no POSIX
    /// directory is one a Windows host has. So nothing was left unlooked: an SDK on no PATH there is
    /// missing, with the fix named, rather than unknown for directories that were never its.
    /// </summary>
    [Fact]
    public async Task Ssh_ToACmdShellWithoutDotnetOnItsPath_SaysTheSdkIsNotInstalled()
    {
        using var fixture = new Fixture(PlatformId.Linux, respond: HostThat(dotnet: Where.Nowhere, answeredOs: "windows"));
        fixture.Commands.ShellProbe = HostResults.Ok("C:\\WINDOWS\\system32\\cmd.exe\r\n");

        var report = await fixture.InspectAsync(HostId.Ssh(SshName));

        Assert.StartsWith("the .NET 10 SDK is not installed there", report.Reason, StringComparison.Ordinal);
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
        Assert.Equal(".dotnet/tools/dssharness.exe", report.Session?.ToolPath);
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
        private readonly FixedLookup _lookup;

        public Fixture(
            PlatformId platformId,
            Func<HostConnection, HostCommand, ProcessResult>? respond = null,
            bool writeItems = true,
            bool resolves = true,
            string address = "host.invalid",
            bool syncedCopy = false)
        {
            Repository = new TempDirectory();

            if (syncedCopy)
            {
                Repository.WriteFile(Path.Combine(".harness-config", HarnessLayout.SyncedCopyMarkerName), """{"Adopted":false,"Completed":true}""");
            }

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
                    $"ADDRESS={address}\nUSER=harness\nPORT=2222\n");
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

            _context = new HarnessContext(new HarnessLayout(Repository.Path, Repository.Path), config) { IsSyncedCopy = syncedCopy };

            var fileSystem = new PhysicalFileSystem(FilePermissionsFactory.Create());
            var agent = new HostAgentService(
                platform,
                identity,
                new EmulatorProbe(platform, processRunner, fileSystem),
                new DeveloperEnvironmentProbe(platform, processRunner),
                fileSystem,
                new LocalProgramResolver(platform, FilePermissionsFactory.Create()),
                new KeepAwake(processRunner, new ConsoleHarnessOutput(new StringWriter(), new StringWriter(), verbose: false)));
            var secrets = new HostSecretsStore(fileSystem, Permissions, platform);
            _lookup = new FixedLookup(resolves);
            var addresses = new HostAddressResolver(_lookup, TimeProvider.System, TimeSpan.Zero);
            var programs = new HostProgramResolver(new LocalProgramResolver(platform, FilePermissionsFactory.Create()), Commands);
            var connector = new HostConnector(platform, processRunner, Commands, secrets, addresses, programs);

            _inspector = new HostInspector(Commands, connector, identity, agent);
        }

        public TempDirectory Repository { get; }

        public ScriptedHostCommands Commands { get; }

        /// <summary>Every name this machine looked up, in order, each once: the resolver keeps its answers.</summary>
        public IReadOnlyList<string> LookedUp => _lookup.Names;

        public IFilePermissions Permissions { get; }

        public Task<HostReport> InspectAsync(
            HostId host,
            IReadOnlyDictionary<string, EmulatorConfig>? emulators = null,
            IReadOnlyList<string>? programs = null,
            IReadOnlyDictionary<string, DeveloperEnvironmentConfig>? environments = null,
            RoomQuestions? room = null)
            => _inspector.InspectAsync(
                _context,
                host,
                emulators ?? new Dictionary<string, EmulatorConfig>(StringComparer.OrdinalIgnoreCase),
                environments ?? new Dictionary<string, DeveloperEnvironmentConfig>(StringComparer.OrdinalIgnoreCase),
                programs ?? [],
                room,
                TestContext.Current.CancellationToken);

        public void Dispose() => Repository.Dispose();

        /// <summary>A resolver that either answers for every name or for none, with no network involved.</summary>
        private sealed class FixedLookup(bool resolves) : INameLookup
        {
            private readonly List<string> _names = [];

            public IReadOnlyList<string> Names
            {
                get
                {
                    lock (_names)
                    {
                        return [.. _names.Distinct(StringComparer.Ordinal)];
                    }
                }
            }

            public Task<IReadOnlyList<string>> LookupAsync(string name, CancellationToken cancellationToken = default)
            {
                lock (_names)
                {
                    _names.Add(name);
                }

                return Task.FromResult<IReadOnlyList<string>>(resolves ? ["192.0.2.10"] : []);
            }
        }
    }
}
