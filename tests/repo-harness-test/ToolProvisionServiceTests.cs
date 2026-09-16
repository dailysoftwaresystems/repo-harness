using System.Runtime.CompilerServices;
using NSubstitute;
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
    private const string Distro = "lane-a";

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

    [Fact]
    public async Task ATool_IsInstalledThroughItsManager_WithTheCredentialOnStandardInputAlone()
    {
        using var fixture = new Fixture(tools: [Apt("ninja")]);

        var report = await fixture.ProvisionAsync();

        var ninja = Assert.Single(Assert.Single(report.Legs).Tools, tool => tool.Tool == "ninja");
        Assert.Equal(ToolState.Installed, ninja.State);

        var install = Assert.Single(fixture.Host.Calls, call => call.Program == "sudo");

        // -S makes sudo read the password from standard input, which is the only place it travels: an
        // argument list is visible in that host's own process table to every user on it.
        Assert.Equal(["-S", "apt-get", "install", "-y", "ninja-build"], install.Arguments);
        Assert.Equal(Credential + "\n", install.StandardInput);
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
        using var fixture = new Fixture(tools: [Apt("ninja")], credential: null, passwordlessSudo: true);

        var report = await fixture.ProvisionAsync();

        Assert.Equal(ToolState.Installed, Assert.Single(Assert.Single(report.Legs).Tools, tool => tool.Tool == "ninja").State);
        Assert.Contains(fixture.Host.Calls, call => call.Program == "sudo" && call.Arguments[0] == "-n" && call.Arguments.Contains("apt-get"));
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

    /// <summary>A tool apt installs, whose package is named after it.</summary>
    private static ToolConfig Apt(string name, string? minVersion = null, string regex = @"(\d+\.\d+\.\d+)") => new()
    {
        Name = name,
        Probe = new ToolProbe { Args = ["--version"], Regex = regex },
        MinVersion = minVersion,
        Install = { ["linux"] = new ToolInstall { Manager = "apt", Id = name + "-build" } },
    };

    /// <summary>One command a host was asked to run, and what it was given.</summary>
    private sealed record HostCall(string Program, IReadOnlyList<string> Arguments, string StandardInput);

    /// <summary>
    /// A distribution that starts with no .NET SDK and no tools, and changes as commands are run on it,
    /// so that a second run measures what the first one actually did.
    /// </summary>
    private sealed class FakeHost(
        bool exists,
        Dictionary<string, string> present,
        Dictionary<string, string> installs,
        bool passwordlessSudo,
        string? installFails)
    {
        public List<HostCall> Calls { get; } = [];

        public bool DotnetInstalled { get; private set; }

        public ProcessResult Respond(HostCommand command)
        {
            Calls.Add(new HostCall(command.Program, command.Arguments, command.StandardInput));

            if (!exists)
            {
                return HostResults.Failed(1, "Wsl/Service/WSL_E_DISTRO_NOT_FOUND");
            }

            if (Lookup(command) is { } name)
            {
                return present.ContainsKey(name) ? HostResults.Ok($"/usr/bin/{name}\n") : HostResults.Failed(1, string.Empty);
            }

            var dotnetPath = $"{Home}/.dotnet/dotnet";

            return (command.Program, command.Arguments.FirstOrDefault()) switch
            {
                ("uname", _) => HostResults.Ok("Linux x86_64\n"),
                ("pwd", _) => HostResults.Ok(Home + "\n"),
                ("ls", _) => HostResults.Ok(DotnetInstalled
                    ? string.Join('\n', command.Arguments.Where(path => path == dotnetPath)) + "\n"
                    : "\n"),
                ("sh", null) => Install(dotnet: true),
                ("sudo", "-n") when command.Arguments.Count == 2 => passwordlessSudo
                    ? HostResults.Ok(string.Empty)
                    : HostResults.Failed(1, "sudo: a password is required"),
                ("sudo", _) => Install(dotnet: false, command),
                _ when command.Program == dotnetPath && command.Arguments.FirstOrDefault() == "--list-sdks" =>
                    DotnetInstalled ? HostResults.Ok($"10.0.100 [{Home}/.dotnet/sdk]\n") : HostResults.Failed(1, "not found"),
                _ when present.TryGetValue(Path.GetFileName(command.Program), out var version) =>
                    HostResults.Ok($"{Path.GetFileName(command.Program)} version {version}\n"),
                _ => throw HostResults.Unexpected(command),
            };
        }

        /// <summary>The program a PATH lookup asked about; a distribution is asked through a shell.</summary>
        private static string? Lookup(HostCommand command)
            => command.Program == "sh" && command.Arguments.FirstOrDefault() == "-c"
                ? command.Arguments[1].Split(' ')[^1]
                : null;

        private ProcessResult Install(bool dotnet, HostCommand? command = null)
        {
            if (installFails is { } said)
            {
                return HostResults.Failed(1, said);
            }

            if (dotnet)
            {
                DotnetInstalled = true;
                return HostResults.Ok("dotnet-install: Installation finished.\n");
            }

            // The package id ends in -build; the program it installs is the tool's own name.
            var package = command!.Arguments[^1];
            var name = package[..^"-build".Length];
            present[name] = installs[name];

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
            string? installFails = null)
        {
            var declared = tools ?? [];

            Host = new FakeHost(
                distributionExists,
                present ?? [],
                declared.ToDictionary(tool => tool.Name, _ => InstalledVersion, StringComparer.Ordinal),
                passwordlessSudo,
                installFails);

            _repository.WriteFile(
                Path.Combine(".harness-config", "wslDistros", Distro, ".env"),
                $"DISTRO=Example-Linux\n{(credential is null ? string.Empty : $"SUDO_PASSWORD={credential}\n")}");

            var config = new HarnessConfig
            {
                WslDistros = { Distro },
                Tools = [.. declared],
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

            var platform = HostDoubles.Platform(PlatformId.Windows);

            var processRunner = Substitute.For<IProcessRunner>();
            processRunner.FindExecutable(Arg.Any<string>()).Returns(call => "/usr/bin/" + call.Arg<string>());

            var permissions = Substitute.For<IFilePermissions>();
            permissions.IsPrivate(Arg.Any<string>()).Returns(true);

            var commands = new ScriptedHostCommands((_, command) => Host.Respond(command));
            var fileSystem = new PhysicalFileSystem(FilePermissionsFactory.Create());
            var secrets = new HostSecretsStore(fileSystem, permissions, platform);
            var addresses = new HostAddressResolver(new NoLookup(), TimeProvider.System, TimeSpan.Zero);
            var programs = new HostProgramResolver(processRunner, commands);
            var connector = new HostConnector(platform, processRunner, commands, secrets, addresses, programs);

            _service = new ToolProvisionService(
                HostDoubles.Loader(config, _repository.Path),
                connector,
                commands,
                programs,
                new ConsoleHarnessOutput(Output, Error, verbose: false));
        }

        public FakeHost Host { get; }

        public StringWriter Output { get; } = new();

        public StringWriter Error { get; } = new();

        public Task<ToolProvisionReport> ProvisionAsync(IReadOnlyList<string>? legs = null)
            => _service.ProvisionAsync(_repository.Path, legs, TestContext.Current.CancellationToken);

        public void Dispose() => _repository.Dispose();

        /// <summary>No ssh host is reached here, so no name is ever looked up.</summary>
        private sealed class NoLookup : INameLookup
        {
            public Task<IReadOnlyList<string>> LookupAsync(string name, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("No ssh host is declared, so no name should be looked up.");
        }
    }
}
