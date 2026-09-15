using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Hosts;

/// <summary>How the harness reaches DssHarness on a host that passed inspection.</summary>
/// <param name="Connection">How programs are started there.</param>
/// <param name="ToolPath">Where DssHarness is there, from the home directory programs start in.</param>
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

    /// <summary>The version of DssHarness there, when it runs.</summary>
    public string? ToolVersion { get; init; }

    /// <summary>What checking each emulator found there, by name.</summary>
    public IReadOnlyDictionary<string, EmulatorCheck> Emulators { get; init; }
        = new Dictionary<string, EmulatorCheck>(StringComparer.OrdinalIgnoreCase);

    /// <summary>What inspection changed on the host, such as installing DssHarness.</summary>
    public IReadOnlyList<string> Actions { get; init; } = [];

    /// <summary>How to reach DssHarness there; <see langword="null"/> for this machine and for a host that is unavailable.</summary>
    public HostSession? Session { get; init; }
}

/// <summary>Measures whether legs can run on a host.</summary>
public interface IHostInspector
{
    /// <summary>
    /// Measures <paramref name="host"/>: whether it can be reached, what it is, which of
    /// <paramref name="emulators"/> work there, and, for a WSL distribution or an ssh host, that
    /// DssHarness there is this machine's build, installing or updating it when it is behind.
    /// </summary>
    /// <exception cref="HarnessException">
    /// The host has a newer DssHarness than this machine. Versions only move up, so nothing runs until
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

    /// <summary>Longest installing or updating DssHarness may take, which includes downloading it.</summary>
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
        return Answered(new HostReport { Host = HostId.Local }, info, session: null);
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

        string? problem;

        try
        {
            problem = CheckSshFiles(context, host, configFile, shownConfig);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // This host's problem, not every host's: a file this user cannot read, or whose permissions cannot
            // be read, stops only the hosts that depend on it.
            problem = $"{shownConfig}, or a key it names, could not be read: {ex.Message}";
        }

        if (problem is not null)
        {
            return Unavailable(host, problem);
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
    /// What stops ssh from using <paramref name="configFile"/> for <paramref name="host"/> safely, or
    /// <see langword="null"/> when nothing does. Checked before connecting, because what ssh itself says
    /// about either file points nowhere near it.
    /// </summary>
    private string? CheckSshFiles(HarnessContext context, HostId host, string configFile, string shownConfig)
    {
        var entry = SshConfigFile.Find(_fileSystem.ReadAllText(configFile), host.Name);

        if (entry is null)
        {
            return $"{shownConfig} has no 'Host {host.Name}' entry; ssh matches the name exactly, case included";
        }

        // ssh reads a file passed with -F whatever its permissions, and whoever can change that file can make
        // ssh run any command as this user, so a file other users can change is refused here instead.
        if (_filePermissions.IsWritableByOthers(configFile))
        {
            return $"other users can change {shownConfig}, and whoever can change it can make ssh run any command as you; {HowToProtect(configFile)}";
        }

        // A key other users can read, ssh ignores, with a warning that does not say what that does to the connection.
        foreach (var key in entry.IdentityFiles.Select(file => ResolveKey(context, file)).OfType<string>())
        {
            if (_fileSystem.FileExists(key) && !_filePermissions.IsPrivate(key))
            {
                return $"other users can read the key {key}, so ssh ignores it; {HowToProtect(key)}";
            }
        }

        return null;
    }

    /// <summary>Brings DssHarness on a reachable host to this machine's build, then asks it what the host is.</summary>
    private async Task<HostReport> PrepareAsync(
        HostReport found,
        HostConnection connection,
        IReadOnlyDictionary<string, EmulatorConfig> emulators,
        CancellationToken cancellationToken)
    {
        var root = _identity.Current;

        var sdks = await RunAsync(connection, "dotnet", ["--list-sdks"], ProbeBudget, cancellationToken).ConfigureAwait(false);

        if (!sdks.Succeeded)
        {
            return found with { Reason = DotnetMissing(connection, sdks) };
        }

        var listed = HostProbes.ReadSdks(sdks.StandardOutput);

        // Output that holds no SDK line is unreadable, not proof that no SDK is installed.
        if (listed.Count == 0 && !string.IsNullOrWhiteSpace(sdks.StandardOutput))
        {
            return found with
            {
                Reason = $"dotnet --list-sdks printed nothing this build can read as an SDK: {HostProbes.Excerpt(sdks.StandardOutput)}",
            };
        }

        if (!listed.Any(sdk => sdk.Major >= ToolPackage.MinimumSdkMajor))
        {
            var present = listed.Count == 0 ? "none" : string.Join(", ", listed.Select(sdk => sdk.Version));
            return found with { Reason = $"{ToolPackage.Command} needs the .NET {ToolPackage.MinimumSdkMajor} SDK there, and it has {present}" };
        }

        // Where the SDK is installed is how a Windows host is told from any other before DssHarness
        // runs there; the kind of shell alone cannot say, since PowerShell runs on both.
        var windowsHost = listed.Any(sdk => sdk.OnWindows);

        var (reason, action) = await BringToThisBuildAsync(found.Host, connection, windowsHost, root, cancellationToken).ConfigureAwait(false);

        if (reason is not null)
        {
            return found with { Reason = reason };
        }

        return await AskAsync(found with { Actions = action is null ? [] : [action] }, connection, windowsHost, emulators, root, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Installs DssHarness on the host at this machine's version, or updates it to that version, as needed.
    /// Versions only move up: a host that is behind is updated, and a host that is ahead stops everything until
    /// this machine catches up, because moving the host down would undo an update somebody else made.
    /// </summary>
    /// <returns>Why the host cannot be brought to this build, or what bringing it there took, if anything.</returns>
    /// <exception cref="HarnessException">The host has a newer DssHarness than this machine.</exception>
    private async Task<(string? Reason, string? Action)> BringToThisBuildAsync(
        HostId host,
        HostConnection connection,
        bool windowsHost,
        ToolIdentity root,
        CancellationToken cancellationToken)
    {
        var tools = await RunAsync(connection, "dotnet", ["tool", "list", "--global", "--format", "json"], ProbeBudget, cancellationToken)
            .ConfigureAwait(false);

        if (!tools.Succeeded || !HostProbes.TryReadToolVersion(tools.StandardOutput, ToolPackage.Id, out var installed))
        {
            return (Failure("its global .NET tools could not be listed", tools), null);
        }

        if (installed is null)
        {
            var install = await RunToolCommandAsync(connection, "install", root.Version, cancellationToken).ConfigureAwait(false);

            return install.Succeeded
                ? (null, $"installed {ToolPackage.Command} {root.Version}")
                : (Failure($"installing {ToolPackage.Command} {root.Version} from nuget.org there failed; a host runs only a version published on nuget.org", install), null);
        }

        if (!SemanticVersion.TryParse(installed, out var hostVersion) || !SemanticVersion.TryParse(root.Version, out var rootVersion))
        {
            return ($"{ToolPackage.Command} {installed} there cannot be compared with {root.Version} here", null);
        }

        var order = SemanticVersion.Compare(hostVersion, rootVersion);

        if (order > 0)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"{host} has {ToolPackage.Command} {installed}, newer than this machine's {root.Version}. Versions only move up, "
                + $"so update this machine first: dotnet tool update --global {ToolPackage.Id} --version {installed}");
        }

        if (order == 0)
        {
            return (null, null);
        }

        if (await WhyNotUpdateAsync(connection, windowsHost, cancellationToken).ConfigureAwait(false) is { } busy)
        {
            return (busy, null);
        }

        // No --allow-downgrade: the host is behind, so an update can only move it up.
        var update = await RunToolCommandAsync(connection, "update", root.Version, cancellationToken).ConfigureAwait(false);

        return update.Succeeded
            ? (null, $"updated {ToolPackage.Command} {installed} to {root.Version}")
            : (Failure($"updating {ToolPackage.Command} {installed} to {root.Version} there failed; a host runs only a version published on nuget.org", update), null);
    }

    /// <summary>Runs <c>dotnet tool install</c> or <c>update</c> for exactly <paramref name="version"/>, from nuget.org alone.</summary>
    private Task<ProcessResult> RunToolCommandAsync(HostConnection connection, string verb, string version, CancellationToken cancellationToken)
        => RunAsync(
            connection,
            "dotnet",
            ["tool", verb, "--global", ToolPackage.Id, "--version", version, "--source", ToolPackage.Source],
            InstallBudget,
            cancellationToken);

    /// <summary>Asks DssHarness on the host which build it is and what the host is, and checks the build is this machine's.</summary>
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
        var shownTool = $"~/{toolPath.Replace('\\', '/')}";

        var request = JsonSerializer.Serialize(
            new HostAgentRequest
            {
                Kind = HostAgentRequestKind.Info,
                Emulators = new Dictionary<string, EmulatorConfig>(emulators, StringComparer.OrdinalIgnoreCase),
            },
            HostAgentProtocol.JsonOptions);

        var budget = ProbeBudget + (EmulatorProbe.WitnessBudget * emulators.Count);

        // One line, held open until the host has answered: a budget that runs out stops ssh or wsl.exe here,
        // which ends the input there and stops any witness still running.
        var answer = await RunAsync(connection, toolPath, [HostAgentProtocol.CommandName], budget, cancellationToken, request + "\n", holdOpen: true)
            .ConfigureAwait(false);

        if (!answer.Succeeded)
        {
            return found with { Reason = Failure($"{ToolPackage.Command} did not answer from {shownTool}, where global tools are installed", answer) };
        }

        var start = answer.StandardOutput.IndexOf('{', StringComparison.Ordinal);

        if (start < 0)
        {
            return found with { Reason = $"{ToolPackage.Command} at {shownTool} answered with no document: {HostProbes.Excerpt(answer.StandardOutput)}" };
        }

        var document = answer.StandardOutput[start..];

        // The build is identified before the rest of the answer is read, so a build that answers in another
        // shape is still reported as the build it is, with the remedy for that.
        if (!TryReadIdentity(document, out var version, out var assemblySha256, out var problem))
        {
            return found with { Reason = $"{ToolPackage.Command} at {shownTool} answered in a form this build cannot read: {problem}" };
        }

        if (!string.Equals(version, root.Version, StringComparison.Ordinal))
        {
            return found with { Reason = $"{ToolPackage.Command} there reports {version}, and {root.Version} was expected" };
        }

        if (!string.Equals(assemblySha256, root.AssemblySha256, StringComparison.OrdinalIgnoreCase))
        {
            return found with
            {
                Reason = $"{ToolPackage.Command} {version} there is a different build from this machine's although the versions match, "
                    + "so one of the two is not the package published on nuget.org; on the machine that has a local build, run "
                    + $"dotnet tool uninstall --global {ToolPackage.Id}, then dotnet tool install --global {ToolPackage.Id} --version {version}",
            };
        }

        HostAgentInfo? info;

        try
        {
            info = JsonSerializer.Deserialize<HostAgentInfo>(document, HostAgentProtocol.JsonOptions);
        }
        catch (JsonException ex)
        {
            return found with { Reason = $"{ToolPackage.Command} at {shownTool} answered in a form this build cannot read: {ex.Message}" };
        }

        return info is null
            ? found with { Reason = $"{ToolPackage.Command} at {shownTool} answered with an empty document" }
            : Answered(found, info, new HostSession(connection, toolPath));
    }

    /// <summary>
    /// Why DssHarness on the host must not be updated now, or <see langword="null"/> when nothing is
    /// running it. An update replaces the files of a DssHarness that is running on Linux and macOS,
    /// and fails part way on Windows; either way a run in progress there would be harmed.
    /// </summary>
    private async Task<string?> WhyNotUpdateAsync(HostConnection connection, bool windowsHost, CancellationToken cancellationToken)
    {
        var listing = windowsHost
            ? await RunAsync(connection, "tasklist", ["/FO", "CSV", "/NH"], ProbeBudget, cancellationToken).ConfigureAwait(false)
            : await RunAsync(connection, "ps", ["-A", "-o", "comm="], ProbeBudget, cancellationToken).ConfigureAwait(false);

        if (!listing.Succeeded)
        {
            return Failure("its running processes could not be listed, so DssHarness there was not updated", listing);
        }

        return HostProbes.ListsProcess(listing.StandardOutput, ToolPackage.Command)
            ? $"{ToolPackage.Command} is running there, so it was not updated to {_identity.Current.Version}; run again once it has finished"
            : null;
    }

    private Task<ProcessResult> RunAsync(
        HostConnection connection,
        string program,
        IReadOnlyList<string> arguments,
        TimeSpan budget,
        CancellationToken cancellationToken,
        string input = "",
        bool holdOpen = false)
        => _hostCommands.RunAsync(
            connection,
            new HostCommand
            {
                Program = program,
                Arguments = arguments,
                Timeout = budget,
                StandardInput = input,
                HoldStandardInputOpen = holdOpen,
            },
            cancellationToken);

    /// <summary>What the DssHarness on a host said about it, recorded in the report.</summary>
    private static HostReport Answered(HostReport found, HostAgentInfo info, HostSession? session) => found with
    {
        Os = info.Os,
        Processor = info.Processor,
        ToolVersion = info.Version,
        Emulators = info.Emulators,
        Session = session,
    };

    /// <summary>Reads which build an answer came from, and nothing else of it.</summary>
    private static bool TryReadIdentity(
        string document,
        [NotNullWhen(true)] out string? version,
        [NotNullWhen(true)] out string? assemblySha256,
        out string problem)
    {
        version = null;
        assemblySha256 = null;
        problem = string.Empty;

        try
        {
            using var parsed = JsonDocument.Parse(document);
            var answer = parsed.RootElement;

            if (answer.ValueKind == JsonValueKind.Object
                && answer.TryGetProperty("version", out var versionValue)
                && versionValue.ValueKind == JsonValueKind.String
                && answer.TryGetProperty("assemblySha256", out var hashValue)
                && hashValue.ValueKind == JsonValueKind.String)
            {
                version = versionValue.GetString()!;
                assemblySha256 = hashValue.GetString()!;
                return true;
            }

            problem = "it names no version and assembly hash";
            return false;
        }
        catch (JsonException ex)
        {
            problem = ex.Message;
            return false;
        }
    }

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
