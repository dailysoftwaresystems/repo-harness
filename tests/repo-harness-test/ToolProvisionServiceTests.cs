using System.Runtime.CompilerServices;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Secrets;
using RepoHarness.Core.Tools;

namespace RepoHarness.Tests;

/// <summary>
/// Installing what a leg's host is missing, before anything tries to run there. Every host is
/// answered by a script, so no test needs WSL, ssh, a package manager or a network, and every
/// address, user, distribution and credential here is fictitious.
/// </summary>
public sealed class ToolProvisionServiceTests
{
    private const string Distro = "wsl-a";

    /// <summary>A second distribution, so two hosts that each need a password can be told apart.</summary>
    private const string OtherDistro = "wsl-b";

    /// <summary>The ssh host a fixture declares when asked to reach its leg over ssh.</summary>
    private const string SshName = "build-box";

    private const string Home = "/home/harness";

    private const string Credential = "not-a-real-password";

    /// <summary>What a tool reports once this run has installed it.</summary>
    private const string InstalledVersion = "1.12.1";

    [Fact]
    public async Task AHostWithNoDotnetOnItsLoginFreePath_HasTheSdkInstalled_AndIsThenReady()
    {
        using var fixture = new Fixture();

        var report = await fixture.ProvisionAsync();

        var leg = Assert.Single(report.Legs);
        var dotnet = Assert.Single(leg.Tools, tool => tool.Tool == "dotnet");
        Assert.Equal(ToolState.Installed, dotnet.State);
        Assert.Equal("10.0.100", dotnet.Version);
        Assert.True(report.Passed);

        // The installer is a shell script travelling on standard input, so nothing in it has to survive
        // a shell's word splitting on the way to the host.
        var script = Assert.Single(fixture.Host.Calls, call => call.Program == "sh" && call.Arguments.Count == 0);
        Assert.Contains("dotnet-install.sh", script.StandardInput, StringComparison.Ordinal);
        Assert.Contains("--channel 10.0", script.StandardInput, StringComparison.Ordinal);

        // Measured again after the install: the SDK lands in ~/.dotnet, which is on no PATH of a
        // command run without a login shell, so only its absolute path starts it.
        Assert.Contains(
            fixture.Host.Calls,
            call => call.Program == $"{Home}/.dotnet/dotnet" && call.Arguments.Contains("--list-sdks"));
    }

    [Fact]
    public async Task ASecondRun_ReportsAlreadyCurrent_AndChangesNothing()
    {
        using var fixture = new Fixture();

        _ = await fixture.ProvisionAsync();
        fixture.Host.Calls.Clear();

        var report = await fixture.ProvisionAsync();

        Assert.True(report.Passed);
        Assert.All(Assert.Single(report.Legs).Tools, tool => Assert.Equal("already current", tool.StateName));
        Assert.DoesNotContain(fixture.Host.Calls, call => call.Program == "sh" && call.Arguments.Count == 0);
        Assert.DoesNotContain(fixture.Host.Calls, call => call.Program == "sudo");
    }

    [Fact]
    public async Task Legs_RestrictsWhatIsProvisioned()
    {
        using var fixture = new Fixture(twoLegs: true);

        var report = await fixture.ProvisionAsync(["only-local"]);

        var leg = Assert.Single(report.Legs);
        Assert.Equal("only-local", leg.Leg);
        Assert.Equal(HostId.Local, leg.Host);

        // Nothing reached the distribution at all: the leg that runs there was not selected.
        Assert.Empty(fixture.Host.Calls);
    }

    [Fact]
    public async Task AnUnreachableHost_IsNamed_AndTheOtherLegsStillGoAhead()
    {
        using var fixture = new Fixture(twoLegs: true, distributionExists: false);

        var report = await fixture.ProvisionAsync();

        Assert.Equal(2, report.Legs.Count);
        var remote = Assert.Single(report.Legs, leg => leg.Host.Kind == HostKind.Wsl);
        Assert.NotNull(remote.Unreachable);
        Assert.Contains("WSL has no distribution named 'Example-Linux'", remote.Unreachable, StringComparison.Ordinal);
        Assert.Contains("could not be reached", fixture.Error.ToString(), StringComparison.Ordinal);

        var local = Assert.Single(report.Legs, leg => leg.Host.Kind == HostKind.Local);
        Assert.True(local.Provisioned);

        // A host that could not be reached is a different answer from a tool that is missing.
        var outcome = ToolProvisionReports.Render(report, json: false);
        Assert.Equal(HarnessExit.HostUnavailable, outcome.ExitCode);
    }

    /// <summary>
    /// A directory another platform's machine names - a Windows one under 'all', here for a Linux
    /// distribution - is not one this host is asked to look in. Handed over anyway, it could not be
    /// listed there, and a tool that is simply missing would read as unknown and never be installed.
    /// </summary>
    [Fact]
    public async Task ADirectoryForAnotherPlatform_IsNeverLookedForOnAHost_SoAMissingToolIsStillInstalled()
    {
        using var fixture = new Fixture(
            tools: [Apt("ninja")],
            searchDirectories: new() { ["all"] = [@"C:\tools", "/opt/tools"] });

        var report = await fixture.ProvisionAsync();

        var ninja = Assert.Single(Assert.Single(report.Legs).Tools, tool => tool.Tool == "ninja");
        Assert.Equal(ToolState.Installed, ninja.State);

        var listed = fixture.Host.Calls.Where(call => call.Program == "ls").SelectMany(call => call.Arguments).ToList();
        Assert.Contains("/opt/tools/ninja", listed);
        Assert.DoesNotContain(listed, path => path.StartsWith(@"C:\", StringComparison.Ordinal));
    }

    /// <summary>
    /// A tool the host could not look for everywhere it was told to is not known either way. It is
    /// reported with the search's own reason, and nothing is installed: a second copy of a tool a
    /// leg there can already start is the one outcome this must never produce.
    /// </summary>
    [Fact]
    public async Task AToolTheHostCouldNotLookFor_IsUnknown_WithTheSearchsOwnReason_AndNothingIsInstalled()
    {
        using var fixture = new Fixture(
            tools: [Apt("ninja")],
            searchDirectories: new() { ["linux"] = ["~/tools"] },
            homeUnreadable: true);

        var report = await fixture.ProvisionAsync();

        var ninja = Assert.Single(Assert.Single(report.Legs).Tools, tool => tool.Tool == "ninja");
        Assert.Equal(ToolState.Unknown, ninja.State);
        Assert.Equal(
            "'ninja' is not on the PATH there, and '~/tools' could not be looked in, because its home directory could not be read",
            ninja.Detail);
        Assert.DoesNotContain(fixture.Host.Calls, call => call.Program == "sudo");
    }

    [Fact]
    public async Task ATool_IsInstalledThroughItsManager_WithTheCredentialOnStandardInputAlone()
    {
        // Somebody is at the terminal, and must not be interrupted for a password this host already
        // declares: the credential is looked for before anyone is asked, not after.
        using var fixture = new Fixture(tools: [Apt("ninja")], prompting: PromptAvailability.Available);

        var report = await fixture.ProvisionAsync();

        var ninja = Assert.Single(Assert.Single(report.Legs).Tools, tool => tool.Tool == "ninja");
        Assert.Equal(ToolState.Installed, ninja.State);

        var install = Assert.Single(fixture.Host.Calls, call => call.Program == "sudo");

        // -S makes sudo read the password from standard input, which is the only place it travels: an
        // argument list is visible in that host's own process table to every user on it.
        Assert.Equal(["-S", "apt-get", "install", "-y", "ninja-build"], install.Arguments);
        Assert.Equal(Credential + "\n", install.StandardInput);
        Assert.Empty(fixture.Prompt.Asked);
    }

    [Fact]
    public async Task TheCredential_ReachesNoLog_NoArgumentList_AndNoMessage()
    {
        // A host that echoes what it was given is exactly how a credential reaches a log nobody meant
        // to hold one, so what it printed is scrubbed before it can be reported.
        using var fixture = new Fixture(
            tools: [Apt("ninja")],
            installFails: $"sudo: a password was supplied: {Credential}");

        var report = await fixture.ProvisionAsync();

        var ninja = Assert.Single(Assert.Single(report.Legs).Tools, tool => tool.Tool == "ninja");
        Assert.Equal(ToolState.Failed, ninja.State);

        var rendered = ToolProvisionReports.Render(report, json: true);
        var everythingSaid = string.Join('\n', [.. rendered.Data, rendered.Message, fixture.Output.ToString(), fixture.Error.ToString()]);

        Assert.DoesNotContain(Credential, everythingSaid, StringComparison.Ordinal);
        Assert.DoesNotContain(Credential, ninja.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(
            fixture.Host.Calls,
            call => call.Arguments.Any(argument => argument.Contains(Credential, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task APrivilegedInstallWithNoCredential_IsRefusedByName_RatherThanAttemptedWithNothing()
    {
        using var fixture = new Fixture(tools: [Apt("ninja")], credential: null, passwordlessSudo: false);

        var report = await fixture.ProvisionAsync();

        var ninja = Assert.Single(Assert.Single(report.Legs).Tools, tool => tool.Tool == "ninja");
        Assert.Equal(ToolState.Failed, ninja.State);
        Assert.Contains($"wslDistros/{Distro}/.env", ninja.Detail, StringComparison.Ordinal);
        Assert.Contains("SUDO_PASSWORD", ninja.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Host.Calls, call => call.Arguments.Contains("apt-get"));
    }

    [Fact]
    public async Task AHostWhereSudoNeedsNoPassword_IsUsedWithoutOne()
    {
        // As above, for the other way a host needs no answer from anybody: whether sudo wants a
        // password at all is measured before a person is interrupted for one.
        using var fixture = new Fixture(
            tools: [Apt("ninja")],
            credential: null,
            passwordlessSudo: true,
            prompting: PromptAvailability.Available);

        var report = await fixture.ProvisionAsync();

        Assert.Equal(ToolState.Installed, Assert.Single(Assert.Single(report.Legs).Tools, tool => tool.Tool == "ninja").State);
        Assert.Contains(fixture.Host.Calls, call => call.Program == "sudo" && call.Arguments[0] == "-n" && call.Arguments.Contains("apt-get"));
        Assert.Empty(fixture.Prompt.Asked);
    }

    /// <summary>
    /// What holding one in memory is for: a host with two privileged tools is asked once, not once for
    /// each of them.
    /// </summary>
    [Fact]
    public async Task APromptedPassword_IsAskedOnceForAHost_ThenUsedForEveryToolOnIt()
    {
        using var fixture = new Fixture(
            tools: [Apt("ninja"), Apt("cmake")],
            credential: null,
            prompting: PromptAvailability.Available,
            typed: _ => Credential);

        var report = await fixture.ProvisionAsync();

        var installed = Assert.Single(report.Legs).Tools.Where(tool => tool.Tool is "ninja" or "cmake").ToList();
        Assert.Equal(2, installed.Count);
        Assert.All(installed, tool => Assert.Equal(ToolState.Installed, tool.State));

        Assert.Equal([$"wsl {Distro}"], fixture.Prompt.Asked);

        // Checked against the host once, before either install ran on the strength of it.
        Assert.Single(fixture.Host.Calls, call => call.Program == "sudo" && call.Arguments is ["-S", "-v"]);

        // Both installs carried it, on standard input, exactly as a declared credential does.
        var sudo = fixture.Host.Calls.Where(call => call.Program == "sudo" && call.Arguments.Contains("apt-get")).ToList();
        Assert.Equal(2, sudo.Count);
        Assert.All(sudo, call => Assert.Equal("-S", call.Arguments[0]));
        Assert.All(sudo, call => Assert.Equal(Credential + "\n", call.StandardInput));
    }

    /// <summary>
    /// Checked against the host before an install that may run for half an hour rides on it, and
    /// checked once. Asking again for the next tool would spend a second failed authentication on the
    /// same mistake, and a host may count those toward locking the account.
    /// </summary>
    [Fact]
    public async Task AWrongPromptedPassword_RefusesTheTool_WithoutInstalling_AndWithoutAskingTwice()
    {
        using var fixture = new Fixture(
            tools: [Apt("ninja"), Apt("cmake")],
            credential: null,
            prompting: PromptAvailability.Available,
            typed: _ => "not-what-that-host-has");

        var report = await fixture.ProvisionAsync();

        var refused = Assert.Single(report.Legs).Tools.Where(tool => tool.Tool is "ninja" or "cmake").ToList();
        Assert.Equal(2, refused.Count);
        Assert.All(refused, tool => Assert.Equal(ToolState.Failed, tool.State));
        Assert.All(refused, tool => Assert.Contains("was not accepted", tool.Detail, StringComparison.Ordinal));

        Assert.Single(fixture.Prompt.Asked);
        Assert.Single(fixture.Host.Calls, call => call.Program == "sudo" && call.Arguments is ["-S", "-v"]);
        Assert.DoesNotContain(fixture.Host.Calls, call => call.Arguments.Contains("apt-get"));
    }

    /// <summary>
    /// Answering with nothing is declining, and declining is an answer: the next tool on that host is
    /// refused rather than asked the same question again.
    /// </summary>
    [Fact]
    public async Task ADeclinedPrompt_IsNotPutAgainToTheSameHost()
    {
        using var fixture = new Fixture(
            tools: [Apt("ninja"), Apt("cmake")],
            credential: null,
            prompting: PromptAvailability.Available,
            typed: _ => string.Empty);

        var report = await fixture.ProvisionAsync();

        var refused = Assert.Single(report.Legs).Tools.Where(tool => tool.Tool is "ninja" or "cmake").ToList();
        Assert.Equal(2, refused.Count);
        Assert.All(refused, tool => Assert.Equal(ToolState.Failed, tool.State));
        Assert.All(refused, tool => Assert.Contains("no password for one", tool.Detail, StringComparison.Ordinal));

        Assert.Single(fixture.Prompt.Asked);
        Assert.DoesNotContain(fixture.Host.Calls, call => call.Program == "sudo" && call.Arguments is ["-S", "-v"]);
        Assert.DoesNotContain(fixture.Host.Calls, call => call.Arguments.Contains("apt-get"));
    }

    /// <summary>
    /// The constraint the design turns on. A password belongs to the host it was typed for; two hosts
    /// are two questions, because trying the first answer on the second spends somebody's failed-login
    /// budget on a machine they never meant to touch.
    /// </summary>
    [Fact]
    public async Task APasswordTypedForOneHost_IsNeverOfferedToAnother()
    {
        const string First = "password-for-the-first-distribution";
        const string Second = "password-for-the-second-distribution";

        // Each host has its own correct password, so both can succeed. A leak then shows up as a host
        // installing with the other one's answer, not merely as one host failing.
        using var fixture = new Fixture(
            tools: [Apt("ninja")],
            credential: null,
            prompting: PromptAvailability.Available,
            typed: host => host.Name == Distro ? First : Second,
            secondDistro: true,
            rootPassword: host => host.Name == Distro ? First : Second);

        var report = await fixture.ProvisionAsync();

        // Both got there, each on its own answer.
        Assert.Equal(2, report.Legs.Count);
        Assert.All(
            report.Legs,
            leg => Assert.Equal(ToolState.Installed, Assert.Single(leg.Tools, tool => tool.Tool == "ninja").State));

        var installs = fixture.Host.Calls
            .Where(call => call.Program == "sudo" && call.Arguments.Contains("apt-get"))
            .ToDictionary(call => call.Host.ToString(), call => call.StandardInput, StringComparer.Ordinal);

        Assert.Equal(First + "\n", installs[$"wsl {Distro}"]);
        Assert.Equal(Second + "\n", installs[$"wsl {OtherDistro}"]);

        // Each host was asked on its own account rather than one answer being reused for both.
        Assert.Equal(2, fixture.Prompt.Asked.Count);
        Assert.Contains($"wsl {Distro}", fixture.Prompt.Asked);
        Assert.Contains($"wsl {OtherDistro}", fixture.Prompt.Asked);

        var checks = fixture.Host.Calls
            .Where(call => call.Program == "sudo" && call.Arguments is ["-S", "-v"])
            .ToDictionary(call => call.Host.ToString(), call => call.StandardInput, StringComparer.Ordinal);

        Assert.Equal(First + "\n", checks[$"wsl {Distro}"]);
        Assert.Equal(Second + "\n", checks[$"wsl {OtherDistro}"]);

        // Neither host ever saw the other's password, on any command at all.
        Assert.DoesNotContain(
            fixture.Host.Calls,
            call => call.Host.Name == OtherDistro && call.StandardInput.Contains(First, StringComparison.Ordinal));

        Assert.DoesNotContain(
            fixture.Host.Calls,
            call => call.Host.Name == Distro && call.StandardInput.Contains(Second, StringComparison.Ordinal));
    }

    /// <summary>
    /// A host that stopped answering is not somebody who mistyped. Told apart because the verdict is
    /// remembered for every later tool on that host, and remembering the wrong one sends the reader
    /// to check their typing while the machine is off.
    /// </summary>
    [Fact]
    public async Task APasswordCheckThatNeverFinished_IsNotReportedAsAWrongPassword()
    {
        using var fixture = new Fixture(
            tools: [Apt("ninja")],
            credential: null,
            prompting: PromptAvailability.Available,
            typed: _ => Credential,
            checkNeverFinishes: true);

        var report = await fixture.ProvisionAsync();

        var ninja = Assert.Single(Assert.Single(report.Legs).Tools, tool => tool.Tool == "ninja");
        Assert.Equal(ToolState.Failed, ninja.State);
        Assert.Contains("never finished", ninja.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("was not accepted", ninja.Detail ?? string.Empty, StringComparison.Ordinal);

        // Nothing was installed on the strength of a password nobody confirmed.
        Assert.DoesNotContain(fixture.Host.Calls, call => call.Arguments.Contains("apt-get"));
    }

    /// <summary>
    /// This machine has no item of its own and so can declare no credential at all. Before there was
    /// anyone to ask, a privileged install here could only be done by hand, or by a user whose sudo
    /// needs no password.
    /// </summary>
    [Fact]
    public async Task TheLocalMachine_CanBeAskedForAPassword_WhereNoItemCouldEverDeclareOne()
    {
        using var fixture = new Fixture(
            tools: [Apt("ninja")],
            twoLegs: true,
            credential: null,
            prompting: PromptAvailability.Available,
            typed: _ => Credential,
            localPlatform: PlatformId.Linux);

        var report = await fixture.ProvisionAsync(["only-local"]);

        Assert.Equal(
            ToolState.Installed,
            Assert.Single(Assert.Single(report.Legs).Tools, tool => tool.Tool == "ninja").State);

        Assert.Equal(["local"], fixture.Prompt.Asked);

        var install = Assert.Single(fixture.Host.Calls, call => call.Program == "sudo" && call.Arguments.Contains("apt-get"));
        Assert.Equal(HostId.Local, install.Host);
        Assert.Equal(Credential + "\n", install.StandardInput);
    }

    /// <summary>
    /// A run with nobody to ask refuses as it always has, so what a script reads still parses — and is
    /// told the remedy that suits it, which is to run as a user that needs no password at all.
    /// </summary>
    [Fact]
    public async Task WithNobodyToAsk_TheRefusalStillNamesTheEnvFile_AndOffersRunningAsRoot()
    {
        using var fixture = new Fixture(
            tools: [Apt("ninja")],
            credential: null,
            prompting: PromptAvailability.Unavailable);

        var report = await fixture.ProvisionAsync();

        var ninja = Assert.Single(Assert.Single(report.Legs).Tools, tool => tool.Tool == "ninja");
        Assert.Equal(ToolState.Failed, ninja.State);
        Assert.Contains("SUDO_PASSWORD", ninja.Detail, StringComparison.Ordinal);
        Assert.Contains($"wslDistros/{Distro}/.env", ninja.Detail, StringComparison.Ordinal);
        Assert.Contains("as root", ninja.Detail, StringComparison.Ordinal);

        // Nobody was asked, nothing was attempted in place of asking, and no flag is blamed for it.
        Assert.Empty(fixture.Prompt.Asked);
        Assert.DoesNotContain("--no-prompt", ninja.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Host.Calls, call => call.Arguments.Contains("apt-get"));
    }

    /// <summary>
    /// Told apart from having nobody to ask: somebody is there, and a flag said not to. Naming it is
    /// the difference between a message that explains itself and one that looks like a bug.
    /// </summary>
    [Fact]
    public async Task WhenAskingIsRefusedByTheFlag_TheRefusalNamesTheFlag()
    {
        using var fixture = new Fixture(
            tools: [Apt("ninja")],
            credential: null,
            prompting: PromptAvailability.Suppressed);

        var report = await fixture.ProvisionAsync();

        var ninja = Assert.Single(Assert.Single(report.Legs).Tools, tool => tool.Tool == "ninja");
        Assert.Equal(ToolState.Failed, ninja.State);
        Assert.Contains("--no-prompt", ninja.Detail, StringComparison.Ordinal);
        Assert.Empty(fixture.Prompt.Asked);
    }

    /// <summary>
    /// A typed password is a credential like any other, and reaches a report by exactly the same route
    /// a declared one would: a host that echoes what it was given.
    /// </summary>
    [Fact]
    public async Task APromptedPassword_ReachesNoLogOrReport_JustAsADeclaredOneDoes()
    {
        const string Typed = "typed-at-the-terminal";

        using var fixture = new Fixture(
            tools: [Apt("ninja")],
            credential: null,
            prompting: PromptAvailability.Available,
            typed: _ => Typed,
            rootPassword: _ => Typed,
            installFails: $"sudo: a password was supplied: {Typed}");

        var report = await fixture.ProvisionAsync();

        var ninja = Assert.Single(Assert.Single(report.Legs).Tools, tool => tool.Tool == "ninja");
        Assert.Equal(ToolState.Failed, ninja.State);

        var rendered = ToolProvisionReports.Render(report, json: true);
        var everythingSaid = string.Join('\n', [.. rendered.Data, rendered.Message, fixture.Output.ToString(), fixture.Error.ToString()]);

        // The host's own text reached the report, and only the password was taken out of it: absences
        // alone would still hold if the detail had simply been blanked.
        Assert.Contains("a password was supplied", ninja.Detail, StringComparison.Ordinal);

        Assert.DoesNotContain(Typed, everythingSaid, StringComparison.Ordinal);
        Assert.DoesNotContain(Typed, ninja.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(
            fixture.Host.Calls,
            call => call.Arguments.Any(argument => argument.Contains(Typed, StringComparison.Ordinal)));
    }

    /// <summary>
    /// Ctrl+C at the prompt stops the run. Turned into a refusal for that host instead, it would carry
    /// on to the next one and ask again for an answer somebody has just declined to give.
    /// </summary>
    [Fact]
    public async Task AnInterruptedPrompt_StopsTheRun_RatherThanMovingOnToTheNextHost()
    {
        using var fixture = new Fixture(
            tools: [Apt("ninja")],
            credential: null,
            prompting: PromptAvailability.Available,
            typed: host => host.Name == Distro
                ? throw new OperationCanceledException()
                : "never-asked-for",
            secondDistro: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.ProvisionAsync());

        // The host that was interrupted is the only one anybody was asked about.
        Assert.Equal([$"wsl {Distro}"], fixture.Prompt.Asked);
    }

    [Fact]
    public async Task AToolWithNoInstall_IsAnAllowlistEntry_ReportedWhenMissingAndNeverInstalled()
    {
        var tool = new ToolConfig { Name = "clang-tidy", Why = "the static analysis gate reads its output" };
        using var fixture = new Fixture(tools: [tool]);

        var report = await fixture.ProvisionAsync();

        var outcome = Assert.Single(Assert.Single(report.Legs).Tools, entry => entry.Tool == "clang-tidy");
        Assert.Equal(ToolState.Missing, outcome.State);
        Assert.Contains("the static analysis gate reads its output", outcome.Detail, StringComparison.Ordinal);
        Assert.Contains("install it by hand", outcome.Detail, StringComparison.Ordinal);
        Assert.False(report.Passed);
        Assert.DoesNotContain(fixture.Host.Calls, call => call.Program == "sudo");

        Assert.Equal(ToolsExit.NotProvisioned, ToolProvisionReports.Render(report, json: false).ExitCode);
    }

    [Fact]
    public async Task AToolBelowItsMinimumVersion_IsUpdatedThroughItsInstall()
    {
        using var fixture = new Fixture(
            tools: [Apt("ninja", minVersion: "1.12.0")],
            present: new() { ["ninja"] = "1.10.0" });

        var report = await fixture.ProvisionAsync();

        var outcome = Assert.Single(Assert.Single(report.Legs).Tools, entry => entry.Tool == "ninja");
        Assert.Equal(ToolState.Updated, outcome.State);
        Assert.Equal("1.12.1", outcome.Version);
    }

    [Fact]
    public async Task AToolAtItsMinimumVersion_IsLeftAlone()
    {
        using var fixture = new Fixture(
            tools: [Apt("ninja", minVersion: "1.12.0")],
            present: new() { ["ninja"] = "1.12.0" });

        var report = await fixture.ProvisionAsync();

        Assert.Equal(ToolState.AlreadyCurrent, Assert.Single(Assert.Single(report.Legs).Tools, entry => entry.Tool == "ninja").State);
        Assert.DoesNotContain(fixture.Host.Calls, call => call.Program == "sudo");
    }

    [Fact]
    public async Task AVersionThatCannotBeComparedWithTheMinimum_IsReported_RatherThanPassedOffAsCurrent()
    {
        using var fixture = new Fixture(
            tools: [Apt("ninja", minVersion: "1.12.0", regex: "never matches this")],
            present: new() { ["ninja"] = "1.10.0" });

        var report = await fixture.ProvisionAsync();

        var outcome = Assert.Single(Assert.Single(report.Legs).Tools, entry => entry.Tool == "ninja");
        Assert.Equal(ToolState.Unknown, outcome.State);
        Assert.False(report.Passed);
    }

    /// <summary>
    /// The two sides fail to parse for unrelated reasons and have unrelated remedies. One message
    /// for both sent everyone who wrote a two-component minVersion — which this parser refuses,
    /// because the field is a semantic version — to adjust a probe regex that had just worked.
    /// </summary>
    [Fact]
    public async Task AMinVersionThatCannotBeParsed_BlamesTheMinVersion_NotTheProbe()
    {
        using var fixture = new Fixture(
            tools: [Apt("ninja", minVersion: "1.12")],
            present: new() { ["ninja"] = "1.12.0" });

        var report = await fixture.ProvisionAsync();

        var outcome = Assert.Single(Assert.Single(report.Legs).Tools, entry => entry.Tool == "ninja");

        Assert.Equal(ToolState.Unknown, outcome.State);
        Assert.Contains("minVersion '1.12'", outcome.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("major.minor.patch", outcome.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("probe", outcome.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVersionTheProbeCouldNotRead_StillBlamesTheProbe()
    {
        using var fixture = new Fixture(
            tools: [Apt("ninja", minVersion: "1.12.0", regex: "never matches this")],
            present: new() { ["ninja"] = "1.10.0" });

        var report = await fixture.ProvisionAsync();

        var outcome = Assert.Single(Assert.Single(report.Legs).Tools, entry => entry.Tool == "ninja");

        Assert.Contains("probe", outcome.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// A tool needed only on one platform is not probed on the others, so a repository declaring
    /// both a Windows compiler and a POSIX one can have every leg provisioned. Before this, each
    /// was reported missing on the other's hosts and no leg was ever green.
    /// </summary>
    [Fact]
    public async Task AToolScopedToAnotherPlatform_IsNeitherProbedNorCountedAgainstTheLeg()
    {
        using var fixture = new Fixture(
            tools:
            [
                new ToolConfig { Name = "cl", Platforms = ["windows"] },
                Apt("ninja"),
            ],
            present: new() { ["ninja"] = "1.12.0" });

        var report = await fixture.ProvisionAsync();
        var tools = Assert.Single(report.Legs).Tools;

        Assert.DoesNotContain(tools, entry => entry.Tool == "cl");
        Assert.True(report.Passed, "a tool this platform does not need must not hold the leg back");
    }

    [Fact]
    public async Task AToolScopedToThisPlatform_IsStillProbed()
    {
        using var fixture = new Fixture(
            tools: [Apt("ninja", platforms: ["linux"])],
            present: new() { ["ninja"] = "1.12.0" });

        var report = await fixture.ProvisionAsync();

        Assert.Contains(Assert.Single(report.Legs).Tools, entry => entry.Tool == "ninja");
    }

    /// <summary>
    /// Two legs on one host share what is installed there and differ in what they need: each is told
    /// only about the tools it needs, whichever scope narrows them, and each tool is asked about
    /// there once. Scoped to the platform alone, a compiler one leg needs was reported missing on
    /// every other leg of that operating system.
    /// </summary>
    [Fact]
    public async Task LegsSharingAHost_AreEachToldOnlyWhatTheyNeed_AndEachToolIsAskedOnce()
    {
        using var fixture = new Fixture(
            tools:
            [
                Apt("ninja"),
                Apt("gcc", toolchains: ["gcc"]),
                Apt("cross", processors: ["arm64"]),
                Apt("qemu", emulators: ["qemu-arm64"]),
                Apt("probe", legs: ["on-distro"]),
            ],
            present: new() { ["ninja"] = "1.12.0", ["gcc"] = "13.2.0", ["cross"] = "2.0.0", ["qemu"] = "8.2.0", ["probe"] = "1.0.0" },
            configure: config => config.Legs["arm-gcc"] = new LegConfig
            {
                Os = "linux",
                Processor = "arm64",
                Config = "debug",
                Toolchain = "gcc",
                Emulator = "qemu-arm64",
                Wsl = Distro,
            });

        var report = await fixture.ProvisionAsync();

        IReadOnlyList<string> Declared(string leg)
            => [.. Assert.Single(report.Legs, entry => entry.Leg == leg).Tools.Select(tool => tool.Tool).Where(tool => tool != "dotnet")];

        Assert.Equal(["ninja", "probe"], Declared("on-distro"));
        Assert.Equal(["ninja", "gcc", "cross", "qemu"], Declared("arm-gcc"));
        Assert.True(report.Passed);

        foreach (var tool in new[] { "ninja", "gcc", "cross", "qemu", "probe" })
        {
            Assert.Single(fixture.Host.Calls, call => Path.GetFileName(call.Program) == tool && call.Arguments is ["--version"]);
        }
    }

    /// <summary>
    /// A tool no selected leg on a host needs is never asked about there: scoped to a leg --legs left
    /// out, it is neither probed nor installed.
    /// </summary>
    [Fact]
    public async Task AToolNoSelectedLegNeeds_IsNeverAskedAbout()
    {
        using var fixture = new Fixture(tools: [Apt("ninja"), Apt("probe", legs: ["only-local"])], present: new() { ["ninja"] = "1.12.0" }, twoLegs: true);

        var report = await fixture.ProvisionAsync(["on-distro"]);

        Assert.DoesNotContain(Assert.Single(report.Legs).Tools, tool => tool.Tool == "probe");
        Assert.DoesNotContain(fixture.Host.Calls, call => call.Arguments.Any(argument => argument.Contains("probe", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A dry run reaches the host and asks it what a run asks, and installs nothing there: each tool
    /// it would install or update names the command that would, sudo and all, and nobody is asked for
    /// a password - or whether one is needed.
    /// </summary>
    [Fact]
    public async Task ADryRun_NamesWhatItWouldInstall_AndRunsNothingThere()
    {
        using var fixture = new Fixture(
            tools: [Apt("ninja"), Apt("cmake", minVersion: "3.20.0")],
            present: new() { ["cmake"] = "3.16.0" },
            prompting: PromptAvailability.Available,
            typed: _ => Credential);

        var report = await fixture.ProvisionAsync(dryRun: true);
        var tools = Assert.Single(report.Legs).Tools;

        Assert.Equal(ToolState.WouldInstall, Assert.Single(tools, tool => tool.Tool == "dotnet").State);

        var ninja = Assert.Single(tools, tool => tool.Tool == "ninja");
        Assert.Equal(ToolState.WouldInstall, ninja.State);
        Assert.Equal("would run 'sudo apt-get install -y ninja-build'", ninja.Detail);

        var cmake = Assert.Single(tools, tool => tool.Tool == "cmake");
        Assert.Equal(ToolState.WouldUpdate, cmake.State);
        Assert.Equal("3.16.0", cmake.Version);

        Assert.DoesNotContain(fixture.Host.Calls, call => call.Program == "sudo" || (call.Program == "sh" && call.Arguments.Count == 0));
        Assert.Empty(fixture.Prompt.Asked);
        Assert.False(fixture.Host.Has(HostId.Wsl(Distro), "ninja"));

        Assert.True(report.DryRun);
        Assert.False(report.Passed);

        var table = ToolProvisionReports.Render(report, json: false);
        var document = ToolProvisionReports.Render(report, json: true);

        Assert.Equal(ToolsExit.NotProvisioned, table.ExitCode);
        Assert.EndsWith("--dry-run installed nothing", table.Message, StringComparison.Ordinal);
        Assert.Contains(table.Details!, line => line.EndsWith("ninja would install: would run 'sudo apt-get install -y ninja-build'", StringComparison.Ordinal));

        using var parsed = System.Text.Json.JsonDocument.Parse(Assert.Single(document.Data!));
        Assert.True(parsed.RootElement.GetProperty("dryRun").GetBoolean());
    }

    /// <summary>
    /// dotnet-install.sh uses bash features that dash does not have, and dash is /bin/sh on Debian
    /// and Ubuntu. Handed to sh it fails with "Illegal option -o pipefail", which says nothing about
    /// a shell and sends the reader after the SDK instead.
    /// </summary>
    [Fact]
    public async Task TheSdkInstaller_HandsTheScriptToBash_NotToBinSh()
    {
        using var fixture = new Fixture();

        _ = await fixture.ProvisionAsync();

        var script = Assert.Single(fixture.Host.Calls, call => call.Program == "sh" && call.Arguments.Count == 0);

        Assert.Contains("bash \"$script\"", script.StandardInput, StringComparison.Ordinal);
        Assert.DoesNotContain("\nsh \"$script\"", script.StandardInput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSdkInstaller_RefusesOnAHostWithNoBash_SayingWhatToInstall()
    {
        using var fixture = new Fixture();

        _ = await fixture.ProvisionAsync();

        var script = Assert.Single(fixture.Host.Calls, call => call.Program == "sh" && call.Arguments.Count == 0);

        // The guard runs before anything is downloaded, and quotes back what to install.
        Assert.Contains("command -v bash", script.StandardInput, StringComparison.Ordinal);
        Assert.Contains(ToolProvisionService.BashMissing, script.StandardInput, StringComparison.Ordinal);
    }

    /// <summary>
    /// A host that answered <c>uname</c> with a system this build has no name for is some POSIX
    /// machine, not a Windows one. Guessing Windows there would skip every tool scoped to Linux and
    /// then report the leg as having everything it needs.
    /// </summary>
    [Fact]
    public async Task AHostWhoseSystemCouldNotBeNamed_StillHasEveryToolChecked()
    {
        using var fixture = new Fixture(
            tools: [Apt("ninja", platforms: ["linux"])],
            present: new() { ["ninja"] = "1.12.0" },
            kernel: "Minix");

        var report = await fixture.ProvisionAsync();

        Assert.Contains(Assert.Single(report.Legs).Tools, entry => entry.Tool == "ninja");
    }

    /// <summary>
    /// An ssh host that did not answer <c>uname</c> at all said nothing about what it is. Read as
    /// Windows, it would have been asked about no tool scoped to Linux and reported as having
    /// everything it needs.
    /// </summary>
    [Fact]
    public async Task AHostThatDidNotAnswerWhatItIs_StillHasEveryToolChecked()
    {
        using var fixture = new Fixture(
            tools: [Apt("ninja", platforms: ["linux"])],
            present: new() { ["ninja"] = "1.12.0" },
            kernel: null,
            ssh: true);

        var report = await fixture.ProvisionAsync();

        Assert.Equal(ToolState.AlreadyCurrent, Assert.Single(Assert.Single(report.Legs).Tools, entry => entry.Tool == "ninja").State);
    }

    /// <summary>
    /// A host whose transport stops starting part way through is named as unreachable, and the other
    /// hosts are still provisioned: ended there, the command lost what it had done everywhere else.
    /// </summary>
    [Fact]
    public async Task AHostWhoseTransportStopsStarting_IsNamed_AndTheOtherHostsAreStillProvisioned()
    {
        using var fixture = new Fixture(
            tools: [Apt("ninja")],
            present: new() { ["ninja"] = "1.12.0" },
            secondDistro: true,
            transportStops: (host, command) => host.Equals(HostId.Wsl(Distro)) && command.Program == "sh" && command.Arguments.Count == 0);

        var report = await fixture.ProvisionAsync();

        Assert.Equal(
            "'wsl' could not be started: The file cannot be accessed by the system.",
            Assert.Single(report.Legs, leg => leg.Leg == "on-distro").Unreachable);
        Assert.Null(Assert.Single(report.Legs, leg => leg.Leg == "on-other").Unreachable);
    }

    [Fact]
    public void RepositoryPath_IsRequiredOfEveryRemoteHost()
    {
        // R37: a WSL distribution and an ssh host each have to say where their copy of the repository
        // is, so nothing falls back to a directory nobody chose. Confirmed rather than assumed.
        foreach (var type in new[] { typeof(WslHostConfig), typeof(SshHostConfig) })
        {
            var property = type.GetProperty(nameof(RemoteHostConfig.RepositoryPath));

            Assert.NotNull(property);
            Assert.Equal(typeof(string), property.PropertyType);
            Assert.NotEmpty(property.GetCustomAttributes(typeof(RequiredMemberAttribute), inherit: true));
        }

        Assert.Null(typeof(LocalHostConfig).GetProperty(nameof(RemoteHostConfig.RepositoryPath)));
    }

    /// <summary>
    /// A tool a leg's developer environment provides - cl, where the leg builds with msvc - is looked
    /// for on the PATH that environment sets up, as the run finds it, and never reported missing for
    /// being off the PATH a plain shell has.
    /// </summary>
    [Fact]
    public async Task AToolTheLegsDeveloperEnvironmentProvides_IsNotReportedMissing()
    {
        using var visualStudio = new ScriptedVisualStudio().Carrying("x64", "cl");
        using var here = new VisualStudioHere(visualStudio, [new ToolConfig { Name = "cl", Toolchains = ["msvc"] }]);

        var report = await here.ProvisionAsync();

        var leg = Assert.Single(report.Legs);
        Assert.Equal(ToolState.AlreadyCurrent, Assert.Single(leg.Tools).State);
        Assert.True(report.Passed);
        Assert.Single(visualStudio.Captures);
    }

    /// <summary>
    /// Two legs on one host are each told about a tool as the PATH they start it on holds it: CMake,
    /// carried by Visual Studio alone, is there for the leg in its developer environment and missing
    /// for the one that builds from a plain shell.
    /// </summary>
    [Fact]
    public async Task EachLegOnAHost_IsToldAboutATool_AsThePathItStartsItOnHoldsIt()
    {
        using var visualStudio = new ScriptedVisualStudio().Carrying("x64", "cmake");
        using var here = new VisualStudioHere(visualStudio, [new ToolConfig { Name = "cmake" }], plainLeg: true);

        var report = await here.ProvisionAsync();

        Assert.Equal(ToolState.AlreadyCurrent, Assert.Single(report.Legs.Single(leg => leg.Leg == "native").Tools).State);
        Assert.Equal(ToolState.Missing, Assert.Single(report.Legs.Single(leg => leg.Leg == "mingw").Tools).State);
        Assert.False(report.Passed);
    }

    /// <summary>
    /// A tool's install runs once on a host, whichever of its PATHs asked for it first, and each PATH
    /// then looks again with the environment set up afresh. Where Visual Studio's own older copy still
    /// comes first on the PATH its environment sets up, the leg starting it there is told so, since
    /// that copy is the one it starts.
    /// </summary>
    [Fact]
    public async Task AnInstallTwoPathsNeed_RunsOnce_AndEachPathLooksAgain()
    {
        using var visualStudio = new ScriptedVisualStudio().Carrying("x64", "ninja");
        var ninja = new ToolConfig
        {
            Name = "ninja",
            Probe = new ToolProbe { Args = ["--version"] },
            MinVersion = "1.12.0",
            Install = { ["all"] = new ToolInstall { Command = [VisualStudioHere.InstallNinja] } },
        };

        using var here = new VisualStudioHere(visualStudio, [ninja], plainLeg: true);

        var report = await here.ProvisionAsync();

        var plain = Assert.Single(report.Legs.Single(leg => leg.Leg == "mingw").Tools);
        Assert.Equal(ToolState.Installed, plain.State);
        Assert.Equal(VisualStudioHere.InstalledNinja, plain.Version);

        var native = Assert.Single(report.Legs.Single(leg => leg.Leg == "native").Tools);
        Assert.Equal(ToolState.Failed, native.State);
        Assert.Equal(
            $"its install reported success, and it is still {VisualStudioHere.StudioNinja}, where at least 1.12.0 is needed",
            native.Detail);

        Assert.Equal(1, here.Installs);
        Assert.Equal(2, visualStudio.Captures.Count);
    }

    /// <summary>
    /// A tool looked for again after its install, where the PATH its leg starts it on can no longer
    /// be looked at - Visual Studio's environment will not set up the second time, and the install put
    /// the tool nowhere else - is unknown, saying the install reported success and why nobody can say
    /// more: never failed on a guess, nor passed as installed.
    /// </summary>
    [Fact]
    public async Task ATool_WhoseEnvironmentWillNotSetUpWhenLookedAtAgain_IsUnknown_AfterItsInstall()
    {
        const string Full = "There is not enough space on the disk.";

        using var visualStudio = new ScriptedVisualStudio
        {
            Holding = (capture, _) => capture == 1 ? throw new IOException(Full) : Task.CompletedTask,
        };
        var ninja = new ToolConfig
        {
            Name = "ninja",
            Install = { ["all"] = new ToolInstall { Command = [VisualStudioHere.InstallNinjaIntoVisualStudio] } },
        };

        using var here = new VisualStudioHere(visualStudio, [ninja]);

        var report = await here.ProvisionAsync();

        var tool = Assert.Single(Assert.Single(report.Legs).Tools);
        Assert.Equal(ToolState.Unknown, tool.State);
        Assert.StartsWith(
            "its install reported success, and whether 'ninja' is there could not be established: 'ninja' is not on the PATH there",
            tool.Detail,
            StringComparison.Ordinal);
        Assert.EndsWith(Full, tool.Detail, StringComparison.Ordinal);
        Assert.Equal(1, here.Installs);
        Assert.Equal(2, visualStudio.Captures.Count);
    }

    /// <summary>
    /// A developer environment that will not set up leaves a tool the host's own PATH lacks unknown,
    /// saying why, rather than missing: the PATH its legs would find it on could not be looked at.
    /// </summary>
    [Fact]
    public async Task AToolOffThePath_WhereTheDeveloperEnvironmentWillNotSetUp_IsUnknown_SayingWhy()
    {
        using var visualStudio = new ScriptedVisualStudio { ExitCode = "1" }.Carrying("x64", "cl");
        using var here = new VisualStudioHere(visualStudio, [new ToolConfig { Name = "cl", Toolchains = ["msvc"] }]);

        var report = await here.ProvisionAsync();

        var cl = Assert.Single(Assert.Single(report.Legs).Tools);
        Assert.Equal(ToolState.Unknown, cl.State);
        Assert.Equal(
            "'cl' is not on the PATH there, and the PATH developer environment 'vs' sets up, where the legs that need it start it, "
            + "could not be set up: developer environment 'vs': vcvarsall.bat amd64 exited 1 and printed nothing",
            cl.Detail);
    }

    /// <summary>
    /// Where no instance of the developer environment is installed, the host's own PATH is all its legs
    /// could find a tool on, so a tool missing there is missing - and installing it is what would help.
    /// </summary>
    [Fact]
    public async Task AToolOffThePath_WhereNoInstanceIsInstalled_IsMissing()
    {
        using var visualStudio = new ScriptedVisualStudio { Instances = "[]" };
        using var here = new VisualStudioHere(visualStudio, [new ToolConfig { Name = "cl", Toolchains = ["msvc"] }]);

        var report = await here.ProvisionAsync();

        Assert.Equal(ToolState.Missing, Assert.Single(Assert.Single(report.Legs).Tools).State);
        Assert.Empty(visualStudio.Captures);
    }

    /// <summary>
    /// A leg on another host that starts in a developer environment finds its tools on the PATH that
    /// environment sets up there, which this command, reaching that host through its shell, cannot set
    /// up: a tool not on the host's own PATH is unknown, saying so, and never installed a second time.
    /// </summary>
    [Fact]
    public async Task AToolOffAnotherHostsPath_ForALegInADeveloperEnvironment_IsUnknown_NotInstalled()
    {
        using var fixture = new Fixture(
            tools: [Apt("cl")],
            configure: config =>
            {
                config.DeveloperEnvironments["vs"] = new DeveloperEnvironmentConfig { Kind = DeveloperEnvironmentKinds.VisualStudio };
                config.Toolchains["msvc"] = new ToolchainConfig { DeveloperEnvironment = "vs" };
                config.Legs["on-distro"] = new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug", Wsl = Distro, Toolchain = "msvc" };
            });

        var report = await fixture.ProvisionAsync();

        var cl = Assert.Single(Assert.Single(report.Legs).Tools, tool => tool.Tool == "cl");
        Assert.Equal(ToolState.Unknown, cl.State);
        Assert.Equal(
            "'cl' is not on the PATH there, and the PATH developer environment 'vs' sets up, where the legs that need it start it, "
            + "is set up by this command only on the machine it runs on",
            cl.Detail);
        Assert.DoesNotContain(fixture.Host.Calls, call => call.Program == "apt-get" || call.Arguments.Contains("cl-build"));
    }

    /// <summary>A tool apt installs, whose package is named after it.</summary>
    private static ToolConfig Apt(
        string name,
        string? minVersion = null,
        string regex = @"(\d+\.\d+\.\d+)",
        List<string>? platforms = null,
        List<string>? toolchains = null,
        List<string>? legs = null,
        List<string>? processors = null,
        List<string>? emulators = null) => new()
    {
        Name = name,
        Probe = new ToolProbe { Args = ["--version"], Regex = regex },
        MinVersion = minVersion,
        Platforms = platforms ?? [],
        Toolchains = toolchains ?? [],
        Legs = legs ?? [],
        Processors = processors ?? [],
        Emulators = emulators ?? [],
        Install = { ["linux"] = new ToolInstall { Manager = "apt", Id = name + "-build" } },
    };

    /// <summary>
    /// This machine, with a leg that builds with msvc in Visual Studio's developer environment, and,
    /// when asked for, one beside it that builds from a plain shell. Programs are looked for as this
    /// machine looks for them, by file: nothing is on the PATH a plain shell has until an install puts
    /// it there, and Visual Studio carries what a test says it does.
    /// </summary>
    private sealed class VisualStudioHere : IDisposable
    {
        /// <summary>The command that installs ninja on this machine's own PATH.</summary>
        public const string InstallNinja = "install-ninja";

        /// <summary>The command that adds ninja to Visual Studio, as its CMake component carries it.</summary>
        public const string InstallNinjaIntoVisualStudio = "install-ninja-into-visual-studio";

        /// <summary>What the ninja an install puts on this machine's own PATH reports.</summary>
        public const string InstalledNinja = "1.12.1";

        /// <summary>What the ninja Visual Studio carries reports.</summary>
        public const string StudioNinja = "1.10.2";

        private readonly TempDirectory _repository = new();
        private readonly TempDirectory _path = new();
        private readonly TempDirectory _searched = new();
        private readonly ScriptedVisualStudio _visualStudio;
        private readonly ToolProvisionService _service;
        private int _installs;

        public VisualStudioHere(ScriptedVisualStudio visualStudio, IReadOnlyList<ToolConfig> tools, bool plainLeg = false)
        {
            _visualStudio = visualStudio;

            var config = new HarnessConfig
            {
                Tools = [.. tools],

                // Nothing searched beyond a PATH but a directory nothing is in, on any machine: the
                // built-in list would find this machine's own tools, where Linux keeps them.
                ToolSearchDirectories = new(StringComparer.OrdinalIgnoreCase) { ["all"] = [_searched.Path] },
                DeveloperEnvironments = { ["vs"] = new DeveloperEnvironmentConfig { Kind = DeveloperEnvironmentKinds.VisualStudio } },
                Toolchains =
                {
                    ["msvc"] = new ToolchainConfig { DeveloperEnvironment = "vs" },
                    ["mingw"] = new ToolchainConfig(),
                },

                // The PATH Visual Studio's environment is set up over, as this machine's own is: what an
                // install puts on the one is on the other too, behind Visual Studio's own directories.
                Hosts = new HostsConfig
                {
                    Local = new LocalHostConfig { Env = new(StringComparer.OrdinalIgnoreCase) { ["PATH"] = _path.Path } },
                },
                Legs = { ["native"] = new LegConfig { Os = "windows", Processor = "x86_64", Config = "debug", Toolchain = "msvc" } },
            };

            if (plainLeg)
            {
                config.Legs["mingw"] = new LegConfig { Os = "windows", Processor = "x86_64", Config = "debug", Toolchain = "mingw" };
            }

            var platform = new HostPlatform();
            var permissions = FilePermissionsFactory.Create();
            var fileSystem = new PhysicalFileSystem(permissions);
            var commands = new ScriptedHostCommands(Respond);
            var programs = new HostProgramResolver(new LocalProgramResolver(platform, permissions, () => _path.Path), commands);
            var connector = new HostConnector(
                platform,
                Substitute.For<IProcessRunner>(),
                commands,
                new HostSecretsStore(fileSystem, permissions, platform),
                new HostAddressResolver(Substitute.For<INameLookup>(), TimeProvider.System, TimeSpan.Zero),
                programs,
                new SshWakeWindow(Substitute.For<INameLookup>(), TimeProvider.System, TimeSpan.Zero),
                new SyncedCopyRefusals(Substitute.For<IHarnessOutput>()));

            _service = new ToolProvisionService(
                HostDoubles.Loader(config, _repository.Path),
                connector,
                commands,
                programs,
                new DeveloperEnvironmentProbe(HostDoubles.Platform(PlatformId.Windows, "x86_64"), visualStudio),
                visualStudio.Provider(),
                platform,
                permissions,
                new ConsoleHarnessOutput(new StringWriter(), new StringWriter(), verbose: false),
                new FakePrompt(PromptAvailability.Unavailable));
        }

        /// <summary>How many times ninja's install ran.</summary>
        public int Installs => _installs;

        public Task<ToolProvisionReport> ProvisionAsync()
            => _service.ProvisionAsync(_repository.Path, null, dryRun: false, TestContext.Current.CancellationToken);

        public void Dispose()
        {
            _searched.Dispose();
            _path.Dispose();
            _repository.Dispose();
        }

        /// <summary>This machine running what provisioning asks: ninja's install, and ninja telling its version.</summary>
        private ProcessResult Respond(HostConnection connection, HostCommand command)
        {
            if (command.Program == InstallNinja)
            {
                _installs++;
                _path.WriteProgram(".", "ninja");
                return HostResults.Ok("installed\n");
            }

            if (command.Program == InstallNinjaIntoVisualStudio)
            {
                _installs++;
                _visualStudio.Carrying("x64", "ninja");
                return HostResults.Ok("installed\n");
            }

            if (Path.GetFileNameWithoutExtension(command.Program) == "ninja")
            {
                // Visual Studio's copy is started by its path, and the one on the PATH by its name.
                return HostResults.Ok((Path.IsPathRooted(command.Program) ? StudioNinja : InstalledNinja) + "\n");
            }

            throw HostResults.Unexpected(command);
        }
    }

    /// <summary>One command a host was asked to run, and what it was given.</summary>
    private sealed record HostCall(HostId Host, string Program, IReadOnlyList<string> Arguments, string StandardInput);

    /// <summary>
    /// Somebody at a terminal: what they would type for each host, and every host they were asked
    /// about. A prompt that throws is how a test says they pressed Ctrl+C instead of answering.
    /// </summary>
    private sealed class FakePrompt(PromptAvailability availability, Func<HostId, string>? typed = null)
        : ISuperuserPrompt
    {
        private readonly List<string> _asked = [];

        public PromptAvailability Availability { get; } = availability;

        /// <summary>Every host somebody was asked about, in order, named as the command line names it.</summary>
        public IReadOnlyList<string> Asked => _asked;

        public Task<string> ReadPasswordAsync(HostId host, CancellationToken cancellationToken)
        {
            _asked.Add(host.ToString());

            return Task.FromResult(typed is null ? string.Empty : typed(host));
        }
    }

    /// <summary>
    /// A distribution that starts with no .NET SDK and no tools, and changes as commands are run on it,
    /// so that a second run measures what the first one actually did.
    /// </summary>
    private sealed class FakeHost(
        bool exists,
        Dictionary<string, string> present,
        Dictionary<string, string> installs,
        bool passwordlessSudo,
        string? installFails,
        string? kernel,
        Func<HostId, string> rootPassword,
        bool checkNeverFinishes,
        bool homeUnreadable,
        Func<HostId, HostCommand, bool>? transportStops)
    {
        private readonly Dictionary<string, Dictionary<string, string>> _present = new(StringComparer.Ordinal);
        private readonly HashSet<string> _dotnet = new(StringComparer.Ordinal);

        public List<HostCall> Calls { get; } = [];

        /// <summary>Whether one host has a program, which is not what another host has.</summary>
        public bool Has(HostId host, string program) => Present(host).ContainsKey(program);

        public ProcessResult Respond(HostConnection connection, HostCommand command)
        {
            var host = connection.Host;
            Calls.Add(new HostCall(host, command.Program, command.Arguments, command.StandardInput));

            if (transportStops?.Invoke(host, command) == true)
            {
                throw HostResults.TransportWouldNotStart(host);
            }

            if (!exists && host.Kind != HostKind.Local)
            {
                return HostResults.Failed(1, "Wsl/Service/WSL_E_DISTRO_NOT_FOUND");
            }

            if (Lookup(command) is { } name)
            {
                return Has(host, name) ? HostResults.Ok($"/usr/bin/{name}\n") : HostResults.Failed(1, string.Empty);
            }

            var dotnetPath = $"{Home}/.dotnet/dotnet";
            var dotnet = _dotnet.Contains(host.ToString());

            return (command.Program, command.Arguments.FirstOrDefault()) switch
            {
                ("uname", _) => kernel is null
                    ? HostResults.Failed(HostProbes.SshFailed, "Connection closed by remote host")
                    : HostResults.Ok($"{kernel} x86_64\n"),
                ("pwd", _) => homeUnreadable ? HostResults.Failed(1, "pwd: cannot read the current directory") : HostResults.Ok(Home + "\n"),
                ("ls", _) => HostResults.Ok(dotnet
                    ? string.Join('\n', command.Arguments.Where(path => path == dotnetPath)) + "\n"
                    : "\n"),
                ("sh", null) => Install(host, dotnet: true),

                // Matched ahead of the install below, which is every other way sudo is reached: this is
                // the one that only checks a password and installs nothing.
                ("sudo", "-S") when command.Arguments is ["-S", "-v"] => checkNeverFinishes
                    ? new ProcessResult(-1, string.Empty, string.Empty, TimeSpan.FromMinutes(2), TimedOut: true)
                    : string.Equals(command.StandardInput, rootPassword(host) + "\n", StringComparison.Ordinal)
                        ? HostResults.Ok(string.Empty)
                        : HostResults.Failed(1, $"sudo: a password is required: {command.StandardInput.TrimEnd('\n')}"),
                ("sudo", "-n") when command.Arguments.Count == 2 => passwordlessSudo
                    ? HostResults.Ok(string.Empty)
                    : HostResults.Failed(1, "sudo: a password is required"),
                ("sudo", _) => Install(host, dotnet: false, command),
                _ when command.Program == dotnetPath && command.Arguments.FirstOrDefault() == "--list-sdks" =>
                    dotnet ? HostResults.Ok($"10.0.100 [{Home}/.dotnet/sdk]\n") : HostResults.Failed(1, "not found"),
                _ when Present(host).TryGetValue(Path.GetFileName(command.Program), out var version) =>
                    HostResults.Ok($"{Path.GetFileName(command.Program)} version {version}\n"),
                _ => throw HostResults.Unexpected(command),
            };
        }

        /// <summary>What one host has, starting from whatever every host was said to have.</summary>
        private Dictionary<string, string> Present(HostId host)
        {
            if (!_present.TryGetValue(host.ToString(), out var found))
            {
                found = new Dictionary<string, string>(present, StringComparer.Ordinal);
                _present[host.ToString()] = found;
            }

            return found;
        }

        /// <summary>
        /// The program a PATH lookup asked about: a distribution is asked through a shell, and an ssh
        /// host with its shell's own builtin.
        /// </summary>
        private static string? Lookup(HostCommand command) => (command.Program, command.Arguments.FirstOrDefault()) switch
        {
            ("sh", "-c") => command.Arguments[1].Split(' ')[^1],
            ("command", "-v") => command.Arguments[1],
            ("where", not null) => command.Arguments[0],
            _ => null,
        };

        private ProcessResult Install(HostId host, bool dotnet, HostCommand? command = null)
        {
            if (installFails is { } said)
            {
                return HostResults.Failed(1, said);
            }

            if (dotnet)
            {
                _dotnet.Add(host.ToString());
                return HostResults.Ok("dotnet-install: Installation finished.\n");
            }

            // The package id ends in -build; the program it installs is the tool's own name.
            var package = command!.Arguments[^1];
            var name = package[..^"-build".Length];
            Present(host)[name] = installs[name];

            return HostResults.Ok($"Setting up {package}\n");
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory _repository = new();
        private readonly ToolProvisionService _service;

        public Fixture(
            IReadOnlyList<ToolConfig>? tools = null,
            Dictionary<string, string>? present = null,
            bool twoLegs = false,
            bool distributionExists = true,
            string? credential = Credential,
            bool passwordlessSudo = false,
            string? installFails = null,
            string? kernel = "Linux",
            PromptAvailability prompting = PromptAvailability.Unavailable,
            Func<HostId, string>? typed = null,
            PlatformId localPlatform = PlatformId.Windows,
            bool secondDistro = false,
            Func<HostId, string>? rootPassword = null,
            bool checkNeverFinishes = false,
            Dictionary<string, List<string>>? searchDirectories = null,
            bool homeUnreadable = false,
            bool ssh = false,
            Func<HostId, HostCommand, bool>? transportStops = null,
            Action<HarnessConfig>? configure = null)
        {
            var declared = tools ?? [];

            Prompt = new FakePrompt(prompting, typed);

            Host = new FakeHost(
                distributionExists,
                present ?? [],
                declared.ToDictionary(tool => tool.Name, _ => InstalledVersion, StringComparer.Ordinal),
                passwordlessSudo,
                installFails,
                kernel,
                rootPassword ?? (_ => Credential),
                checkNeverFinishes,
                homeUnreadable,
                transportStops);

            _repository.WriteFile(
                Path.Combine(".harness-config", "wslDistros", Distro, ".env"),
                $"DISTRO=Example-Linux\n{(credential is null ? string.Empty : $"SUDO_PASSWORD={credential}\n")}");

            if (ssh)
            {
                _repository.WriteFile(Path.Combine(".harness-config", "sshItems", SshName, ".env"), "ADDRESS=192.0.2.10\nUSER=harness\n");
                _repository.WriteFile(Path.Combine(".harness-config", "sshItems", SshName, ".key"), "not a real key");
                _repository.WriteFile(Path.Combine(".harness-config", "sshItems", SshName, "known_hosts"), "192.0.2.10 ssh-ed25519 AAAA\n");
            }

            if (secondDistro)
            {
                // Declares no credential of its own, so it reaches the prompt exactly as the first does.
                _repository.WriteFile(
                    Path.Combine(".harness-config", "wslDistros", OtherDistro, ".env"),
                    "DISTRO=Other-Linux\n");
            }

            var config = new HarnessConfig
            {
                WslDistros = { Distro },
                Tools = [.. declared],
                ToolSearchDirectories = new(searchDirectories ?? [], StringComparer.OrdinalIgnoreCase),
                Hosts = new HostsConfig { Wsl = { [Distro] = new WslHostConfig { RepositoryPath = "~/repo" } } },
                Legs =
                {
                    ["on-distro"] = new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug", Wsl = Distro },
                },
            };

            if (twoLegs)
            {
                config.Legs["only-local"] = new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug" };
            }

            if (ssh)
            {
                // In place of the distribution's leg, so the one host provisioned is reached over ssh.
                config.SshItems.Add(SshName);
                config.Hosts.Ssh[SshName] = new SshHostConfig { RepositoryPath = "/srv/repo", ConnectTimeoutSeconds = 5 };
                config.Legs.Remove("on-distro");
                config.Legs["on-ssh"] = new LegConfig { Os = "linux", Processor = "x86_64", Config = "debug", Ssh = SshName };
            }

            if (secondDistro)
            {
                config.WslDistros.Add(OtherDistro);
                config.Hosts.Wsl[OtherDistro] = new WslHostConfig { RepositoryPath = "~/repo" };
                config.Legs["on-other"] = new LegConfig
                {
                    Os = "linux",
                    Processor = "x86_64",
                    Config = "debug",
                    Wsl = OtherDistro,
                };
            }

            configure?.Invoke(config);

            var platform = HostDoubles.Platform(localPlatform);

            var processRunner = Substitute.For<IProcessRunner>();

            // This machine finds every program, which is what keeps a local host out of the way of a
            // test about a distribution. A test that provisions the local host itself says so with
            // localPlatform, and then the lookup has to answer from what that host has, so that a tool
            // is missing until this run installs it.
            processRunner.FindExecutable(Arg.Any<string>()).Returns(call =>
                localPlatform == PlatformId.Windows || Host.Has(HostId.Local, call.Arg<string>())
                    ? "/usr/bin/" + call.Arg<string>()
                    : null);

            var permissions = Substitute.For<IFilePermissions>();
            permissions.IsPrivate(Arg.Any<string>()).Returns(true);

            // The same answer, given where this machine's resolver asks it: a file on the one PATH
            // entry below starts exactly when the program it is named for is one this host has.
            permissions.IsExecutable(Arg.Any<string>()).Returns(call =>
                localPlatform == PlatformId.Windows
                || Host.Has(HostId.Local, Path.GetFileNameWithoutExtension(call.Arg<string>())));

            var commands = new ScriptedHostCommands(Host.Respond);
            var fileSystem = new PhysicalFileSystem(FilePermissionsFactory.Create());
            var secrets = new HostSecretsStore(fileSystem, permissions, platform);
            var addresses = new HostAddressResolver(new NoLookup(), TimeProvider.System, TimeSpan.Zero);
            var programs = new HostProgramResolver(new LocalProgramResolver(platform, permissions, () => "/usr/bin"), commands);
            var connector = new HostConnector(
                platform,
                processRunner,
                commands,
                secrets,
                addresses,
                programs,
                new SshWakeWindow(Substitute.For<INameLookup>(), TimeProvider.System, TimeSpan.Zero),
                new SyncedCopyRefusals(Substitute.For<IHarnessOutput>()));

            // No developer environment is looked at or set up here: a leg starts in one only on this
            // machine, and only where a test says so, which it does with VisualStudioHere.
            var windows = HostDoubles.Platform(PlatformId.Windows, "x86_64");
            var output = new ConsoleHarnessOutput(Output, Error, verbose: false);

            _service = new ToolProvisionService(
                HostDoubles.Loader(config, _repository.Path),
                connector,
                commands,
                programs,
                new DeveloperEnvironmentProbe(windows, Unasked()),
                new DeveloperEnvironmentProvider(windows, Unasked(), fileSystem, output),
                platform,
                permissions,
                output,
                Prompt);
        }

        public FakeHost Host { get; }

        public FakePrompt Prompt { get; }

        public StringWriter Output { get; } = new();

        public StringWriter Error { get; } = new();

        public Task<ToolProvisionReport> ProvisionAsync(IReadOnlyList<string>? legs = null, bool dryRun = false)
            => _service.ProvisionAsync(_repository.Path, legs, dryRun, TestContext.Current.CancellationToken);

        public void Dispose() => _repository.Dispose();

        /// <summary>A machine whose Visual Studio nothing here may ask about.</summary>
        private static IProcessRunner Unasked()
        {
            var processes = Substitute.For<IProcessRunner>();
            processes.RunAsync(Arg.Any<ProcessRequest>(), Arg.Any<CancellationToken>())
                .ThrowsAsync(new InvalidOperationException("No developer environment was expected to be looked at or set up."));

            return processes;
        }

        /// <summary>No ssh host is reached here, so no name is ever looked up.</summary>
        private sealed class NoLookup : INameLookup
        {
            public Task<IReadOnlyList<string>> LookupAsync(string name, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("No ssh host is declared, so no name should be looked up.");
        }
    }
}
