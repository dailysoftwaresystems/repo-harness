using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Hosts;

/// <summary>How the harness reaches repo-harness on a host that passed inspection.</summary>
/// <param name="Connection">How programs are started there.</param>
/// <param name="ToolPath">Where repo-harness is there, from the home directory programs start in.</param>
public sealed record HostSession(HostConnection Connection, string ToolPath);

/// <summary>What inspecting one host found.</summary>
public sealed record HostReport
{
    /// <summary>The host.</summary>
    public required HostId Host { get; init; }

    /// <summary>Why nothing can run on the host, or <see langword="null"/> when legs can.</summary>
    public string? Reason { get; init; }

    /// <summary>Whether legs can run on the host.</summary>
    public bool Available => Reason is null;

    /// <summary>The host's operating system, when it was measured.</summary>
    public string? Os { get; init; }

    /// <summary>The host's processor, when it was measured.</summary>
    public string? Processor { get; init; }

    /// <summary>The version of repo-harness there, when it runs.</summary>
    public string? ToolVersion { get; init; }

    /// <summary>What checking each emulator found there, by name.</summary>
    public IReadOnlyDictionary<string, EmulatorCheck> Emulators { get; init; }
        = new Dictionary<string, EmulatorCheck>(StringComparer.OrdinalIgnoreCase);

    /// <summary>What inspection changed on the host, such as installing repo-harness.</summary>
    public IReadOnlyList<string> Actions { get; init; } = [];

    /// <summary>How to reach repo-harness there; <see langword="null"/> for this machine and for a host that is unavailable.</summary>
    public HostSession? Session { get; init; }
}

/// <summary>Measures whether legs can run on a host.</summary>
public interface IHostInspector
{
    /// <summary>
    /// Measures <paramref name="host"/>: whether it can be reached, what it is, which of
    /// <paramref name="emulators"/> work there, and, for a WSL distribution or an ssh host, that
    /// repo-harness there is this machine's build, installing or updating it when it is behind.
    /// </summary>
    /// <exception cref="HarnessException">
    /// The host has a newer repo-harness than this machine. Versions only move up, so nothing runs until
    /// this machine is updated.
    /// </exception>
    Task<HostReport> InspectAsync(
        HarnessContext context,
        HostId host,
        IReadOnlyDictionary<string, EmulatorConfig> emulators,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IHostInspector"/>
public sealed class HostInspector(
    IHostPlatform platform,
    IProcessRunner processRunner,
    IHostCommandRunner hostCommands,
    IFileSystem fileSystem,
    IFilePermissions filePermissions,
    IToolIdentityProvider identity,
    HostAgentService agent) : IHostInspector
{
    /// <summary>The ssh configuration file's name, inside the harness's ssh directory.</summary>
    public const string SshConfigFileName = "config";

    /// <summary>ssh's own exit code for a failure of ssh itself, such as a connection or authentication failure.</summary>
    private const int SshFailed = 255;

    /// <summary>Longest a probe of a connected host may take.</summary>
    private static readonly TimeSpan ProbeBudget = TimeSpan.FromMinutes(2);

    /// <summary>Longest installing or updating repo-harness may take, which includes downloading it.</summary>
    private static readonly TimeSpan InstallBudget = TimeSpan.FromMinutes(10);

    private readonly IHostPlatform _platform = platform;
    private readonly IProcessRunner _processRunner = processRunner;
    private readonly IHostCommandRunner _hostCommands = hostCommands;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IFilePermissions _filePermissions = filePermissions;
    private readonly IToolIdentityProvider _identity = identity;
    private readonly HostAgentService _agent = agent;

    public Task<HostReport> InspectAsync(
        HarnessContext context,
        HostId host,
        IReadOnlyDictionary<string, EmulatorConfig> emulators,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(emulators);

        return host.Kind switch
        {
            HostKind.Local => InspectLocalAsync(emulators, cancellationToken),
            HostKind.Wsl => InspectWslAsync(host, emulators, cancellationToken),
            _ => InspectSshAsync(context, host, emulators, cancellationToken),
        };
    }

    /// <summary>This machine is measured in-process: the build doing the measuring is the one that would run its legs.</summary>
    private async Task<HostReport> InspectLocalAsync(
        IReadOnlyDictionary<string, EmulatorConfig> emulators,
        CancellationToken cancellationToken)
    {
        var info = await _agent.DescribeAsync(emulators, cancellationToken).ConfigureAwait(false);

        return new HostReport
        {
            Host = HostId.Local,
            Os = info.Os,
            Processor = info.Processor,
            ToolVersion = info.Version,
            Emulators = info.Emulators,
        };
    }

    private async Task<HostReport> InspectWslAsync(
        HostId host,
        IReadOnlyDictionary<string, EmulatorConfig> emulators,
        CancellationToken cancellationToken)
    {
        if (_platform.Current != PlatformId.Windows)
        {
            return Unavailable(host, $"WSL exists only on Windows, and this machine runs {_platform.PlatformKey}");
        }

        if (_processRunner.FindExecutable(HostCommandRunner.WslProgram) is null)
        {
            return Unavailable(host, "wsl.exe was not found, so WSL is not installed on this machine");
        }

        // Running a program in the distribution is the test that it exists and starts; the program
        // chosen also measures what the distribution is.
        var connection = new HostConnection { Host = host };
        var uname = await RunAsync(connection, "uname", ["-sm"], ProbeBudget, cancellationToken).ConfigureAwait(false);

        if (!uname.Succeeded)
        {
            return Unavailable(host, HostProbes.IsMissingDistribution(uname.StandardOutput + uname.StandardError)
                ? $"WSL has no distribution named '{host.Name}'"
                : Failure("the distribution did not start a program", uname));
        }

        var (os, processor) = HostProbes.ReadUname(uname.StandardOutput);

        return await PrepareAsync(
            new HostReport { Host = host, Os = os, Processor = processor },
            connection,
            emulators,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<HostReport> InspectSshAsync(
        HarnessContext context,
        HostId host,
        IReadOnlyDictionary<string, EmulatorConfig> emulators,
        CancellationToken cancellationToken)
    {
        if (!context.Config.Hosts.Ssh.TryGetValue(host.Name, out var settings))
        {
            return Unavailable(host, "it is not declared under hosts.ssh");
        }

        var configFile = Path.Combine(context.Layout.SshDirectory, SshConfigFileName);
        var shownConfig = Show(context, configFile);

        if (!_fileSystem.FileExists(configFile))
        {
            return Unavailable(host, $"{shownConfig} does not exist; declare 'Host {host.Name}' there, with its address, user and key");
        }

        var entry = SshConfigFile.Find(_fileSystem.ReadAllText(configFile), host.Name);

        if (entry is null)
        {
            return Unavailable(host, $"{shownConfig} has no 'Host {host.Name}' entry");
        }

        // ssh refuses a configuration file other users can change, and ignores a key they can read,
        // with messages that point nowhere near either file. Both are checked here instead.
        if (_filePermissions.IsWritableByOthers(configFile))
        {
            return Unavailable(host, $"other users can change {shownConfig}, so ssh refuses to read it; {HowToProtect(configFile)}");
        }

        foreach (var key in entry.IdentityFiles.Select(file => ResolveKey(context, file)).OfType<string>())
        {
            if (_fileSystem.FileExists(key) && !_filePermissions.IsPrivate(key))
            {
                return Unavailable(host, $"other users can read the key {key}, so ssh ignores it; {HowToProtect(key)}");
            }
        }

        if (_processRunner.FindExecutable(HostCommandRunner.SshProgram) is null)
        {
            return Unavailable(host, "ssh was not found on this machine");
        }

        var connection = new HostConnection
        {
            Host = host,
            SshConfigFile = configFile,
            ConnectTimeoutSeconds = settings.ConnectTimeoutSeconds,
            KeepAliveSeconds = settings.KeepAliveSeconds,
            LocalDirectory = context.Layout.MainCheckoutRoot,
        };

        var budget = TimeSpan.FromSeconds(settings.ConnectTimeoutSeconds) + ProbeBudget;
        var probe = await _hostCommands.ProbeShellAsync(connection, budget, cancellationToken).ConfigureAwait(false);

        if (!probe.Succeeded)
        {
            return Unavailable(host, probe switch
            {
                { TimedOut: true } => $"it did not answer within {budget.TotalSeconds:0} seconds",
                { ExitCode: SshFailed } => $"ssh could not connect: {HostProbes.Excerpt(probe.StandardError)}",
                _ => Failure("its shell could not run echo", probe),
            });
        }

        return await PrepareAsync(
            new HostReport { Host = host },
            connection with { Shell = RemoteCommandLine.ReadShellProbe(probe.StandardOutput) },
            emulators,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Brings repo-harness on a reachable host to this machine's build, then asks it what the host is.
    /// Versions only move up: a host that is behind is updated, and a host that is ahead stops
    /// everything until this machine catches up, because moving the host down would undo an update
    /// somebody else made.
    /// </summary>
    private async Task<HostReport> PrepareAsync(
        HostReport found,
        HostConnection connection,
        IReadOnlyDictionary<string, EmulatorConfig> emulators,
        CancellationToken cancellationToken)
    {
        var host = found.Host;
        var root = _identity.Current;
        var actions = new List<string>();

        var sdks = await RunAsync(connection, "dotnet", ["--list-sdks"], ProbeBudget, cancellationToken).ConfigureAwait(false);

        if (!sdks.Succeeded)
        {
            return found with { Reason = DotnetMissing(connection, sdks) };
        }

        var listed = HostProbes.ReadSdks(sdks.StandardOutput);

        if (!listed.Any(sdk => sdk.Major >= ToolPackage.MinimumSdkMajor))
        {
            var present = listed.Count == 0 ? "none" : string.Join(", ", listed.Select(sdk => sdk.Version));
            return found with { Reason = $"repo-harness needs the .NET {ToolPackage.MinimumSdkMajor} SDK there, and it has {present}" };
        }

        // Where the SDK is installed is how a Windows host is told from any other before repo-harness
        // runs there; the kind of shell alone cannot say, since PowerShell runs on both.
        var windowsHost = listed.Any(sdk => sdk.OnWindows);

        var tools = await RunAsync(connection, "dotnet", ["tool", "list", "--global", "--format", "json"], ProbeBudget, cancellationToken)
            .ConfigureAwait(false);

        if (!tools.Succeeded || !HostProbes.TryReadToolVersion(tools.StandardOutput, ToolPackage.Id, out var installed))
        {
            return found with { Reason = Failure("its global .NET tools could not be listed", tools) };
        }

        if (installed is null)
        {
            var install = await RunAsync(
                connection,
                "dotnet",
                ["tool", "install", "--global", ToolPackage.Id, "--version", root.Version, "--source", ToolPackage.Source],
                InstallBudget,
                cancellationToken).ConfigureAwait(false);

            if (!install.Succeeded)
            {
                return found with
                {
                    Reason = Failure($"installing repo-harness {root.Version} from nuget.org there failed; a host runs only a version published on nuget.org", install),
                };
            }

            actions.Add($"installed repo-harness {root.Version}");
        }
        else
        {
            if (!SemanticVersion.TryParse(installed, out var hostVersion) || !SemanticVersion.TryParse(root.Version, out var rootVersion))
            {
                return found with { Reason = $"repo-harness {installed} there cannot be compared with {root.Version} here" };
            }

            var order = SemanticVersion.Compare(hostVersion, rootVersion);

            if (order > 0)
            {
                throw new HarnessException(
                    HarnessExit.Refused,
                    $"{host} has repo-harness {installed}, newer than this machine's {root.Version}. Versions only move up, "
                    + $"so update this machine first: dotnet tool update --global {ToolPackage.Id} --version {installed}");
            }

            if (order < 0)
            {
                var busy = await WhyNotUpdateAsync(connection, windowsHost, cancellationToken).ConfigureAwait(false);

                if (busy is not null)
                {
                    return found with { Reason = busy };
                }

                // No --allow-downgrade: the host is behind, so an update can only move it up.
                var update = await RunAsync(
                    connection,
                    "dotnet",
                    ["tool", "update", "--global", ToolPackage.Id, "--version", root.Version, "--source", ToolPackage.Source],
                    InstallBudget,
                    cancellationToken).ConfigureAwait(false);

                if (!update.Succeeded)
                {
                    return found with { Reason = Failure($"updating repo-harness {installed} to {root.Version} there failed", update) };
                }

                actions.Add($"updated repo-harness {installed} to {root.Version}");
            }
        }

        return await AskAsync(found with { Actions = actions }, connection, windowsHost, emulators, root, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Asks repo-harness on the host which build it is and what the host is, and checks the build is this machine's.</summary>
    private async Task<HostReport> AskAsync(
        HostReport found,
        HostConnection connection,
        bool windowsHost,
        IReadOnlyDictionary<string, EmulatorConfig> emulators,
        ToolIdentity root,
        CancellationToken cancellationToken)
    {
        // Global tools are installed under the home directory, which is where programs start on every
        // kind of host. They are usually not on the PATH of a command run over ssh or with wsl.exe
        // --exec, which reads no login profile, so the path is spelt out.
        var toolPath = ToolPackage.PathFromHome(windowsHost, connection.Shell);

        var request = JsonSerializer.Serialize(
            new HostAgentRequest
            {
                Kind = HostAgentRequestKind.Info,
                Emulators = new Dictionary<string, EmulatorConfig>(emulators, StringComparer.OrdinalIgnoreCase),
            },
            HostAgentProtocol.JsonOptions);

        var budget = ProbeBudget + (EmulatorProbe.WitnessBudget * emulators.Count);
        var answer = await RunAsync(connection, toolPath, [HostAgentProtocol.CommandName], budget, cancellationToken, request)
            .ConfigureAwait(false);

        if (!answer.Succeeded || !TryReadInfo(answer.StandardOutput, out var info))
        {
            return found with
            {
                Reason = Failure($"repo-harness did not answer from ~/{toolPath.Replace('\\', '/')}, where global tools are installed", answer),
            };
        }

        if (!string.Equals(info.Version, root.Version, StringComparison.Ordinal))
        {
            return found with { Reason = $"repo-harness there reports {info.Version}, and {root.Version} was expected" };
        }

        if (!string.Equals(info.AssemblySha256, root.AssemblySha256, StringComparison.OrdinalIgnoreCase))
        {
            return found with
            {
                Reason = $"repo-harness {info.Version} there is a different build from this machine's although the versions match, "
                    + "so one of the two is not the package published on nuget.org; on the machine that has a local build, run "
                    + $"dotnet tool uninstall --global {ToolPackage.Id}, then dotnet tool install --global {ToolPackage.Id} --version {info.Version}",
            };
        }

        return found with
        {
            Os = info.Os,
            Processor = info.Processor,
            ToolVersion = info.Version,
            Emulators = info.Emulators,
            Session = new HostSession(connection, toolPath),
        };
    }

    /// <summary>
    /// Why repo-harness on the host must not be updated now, or <see langword="null"/> when nothing is
    /// running it. An update replaces the files of a repo-harness that is running on Linux and macOS,
    /// and fails part way on Windows; either way a run in progress there would be harmed.
    /// </summary>
    private async Task<string?> WhyNotUpdateAsync(HostConnection connection, bool windowsHost, CancellationToken cancellationToken)
    {
        var listing = windowsHost
            ? await RunAsync(connection, "tasklist", ["/FO", "CSV", "/NH"], ProbeBudget, cancellationToken).ConfigureAwait(false)
            : await RunAsync(connection, "ps", ["-A", "-o", "comm="], ProbeBudget, cancellationToken).ConfigureAwait(false);

        if (!listing.Succeeded)
        {
            return Failure("its running processes could not be listed, so repo-harness there was not updated", listing);
        }

        return HostProbes.ListsProcess(listing.StandardOutput, ToolPackage.Command)
            ? $"repo-harness is running there, so it was not updated to {_identity.Current.Version}; run again once it has finished"
            : null;
    }

    private Task<ProcessResult> RunAsync(
        HostConnection connection,
        string program,
        IReadOnlyList<string> arguments,
        TimeSpan budget,
        CancellationToken cancellationToken,
        string? input = null)
        => _hostCommands.RunAsync(
            connection,
            new HostCommand { Program = program, Arguments = arguments, Timeout = budget, StandardInput = input },
            cancellationToken);

    /// <summary>
    /// Where a key the ssh configuration names is, resolved the way ssh resolves it when started from the
    /// main checkout; <see langword="null"/> for a path holding a token only ssh can expand.
    /// </summary>
    private string? ResolveKey(HarnessContext context, string file)
    {
        if (file.Contains('%', StringComparison.Ordinal))
        {
            return null;
        }

        if (file.StartsWith("~/", StringComparison.Ordinal))
        {
            return Path.GetFullPath(Path.Combine(_platform.HomeDirectory, file[2..]));
        }

        return Path.GetFullPath(file, context.Layout.MainCheckoutRoot);
    }

    private string HowToProtect(string path) => _platform.Current == PlatformId.Windows
        ? $"make it private with: icacls \"{path}\" /inheritance:r /grant:r \"%USERNAME%:F\""
        : $"make it private with: chmod 600 '{path}'";

    private static string DotnetMissing(HostConnection connection, ProcessResult result)
        => connection.Host.Kind == HostKind.Wsl
            && HostProbes.IsMissingProgramInWsl(result.StandardOutput + result.StandardError, "dotnet")
                ? $"the .NET {ToolPackage.MinimumSdkMajor} SDK is not installed in the distribution"
                : Failure(
                    $"dotnet did not run there; install the .NET {ToolPackage.MinimumSdkMajor} SDK so that dotnet is on the PATH of commands run without a login shell",
                    result);

    private static bool TryReadInfo(string output, [NotNullWhen(true)] out HostAgentInfo? info)
    {
        info = null;

        var start = output.IndexOf('{', StringComparison.Ordinal);
        if (start < 0)
        {
            return false;
        }

        try
        {
            info = JsonSerializer.Deserialize<HostAgentInfo>(output.AsSpan(start), HostAgentProtocol.JsonOptions);
            return info is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Failure(string what, ProcessResult result)
    {
        if (result.TimedOut)
        {
            return $"{what}: there was no answer within {result.Duration.TotalSeconds:0} seconds";
        }

        var said = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        return $"{what} (exit {result.ExitCode}): {HostProbes.Excerpt(said)}";
    }

    private static HostReport Unavailable(HostId host, string reason) => new() { Host = host, Reason = reason };

    private static string Show(HarnessContext context, string path)
        => Path.GetRelativePath(context.Layout.MainCheckoutRoot, path).Replace('\\', '/');
}
