using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Tools;

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
    IHostCommandRunner hostCommands,
    IHostConnector connector,
    IToolIdentityProvider identity,
    HostAgentService agent) : IHostInspector
{
    /// <summary>The program every host needs before DssHarness can be installed or run there.</summary>
    public const string DotnetProgram = "dotnet";

    /// <summary>Longest a probe of a connected host may take.</summary>
    private static readonly TimeSpan ProbeBudget = TimeSpan.FromMinutes(2);

    /// <summary>Longest installing or updating DssHarness may take, which includes downloading it.</summary>
    private static readonly TimeSpan InstallBudget = TimeSpan.FromMinutes(10);

    private readonly IHostCommandRunner _hostCommands = hostCommands;
    private readonly IHostConnector _connector = connector;
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

        return host.Kind == HostKind.Local
            ? InspectLocalAsync(emulators, cancellationToken)
            : InspectRemoteAsync(context, host, emulators, cancellationToken);
    }

    /// <summary>This machine is measured in-process: the build doing the measuring is the one that would run its legs.</summary>
    private async Task<HostReport> InspectLocalAsync(
        IReadOnlyDictionary<string, EmulatorConfig> emulators,
        CancellationToken cancellationToken)
    {
        var info = await _agent.DescribeAsync(emulators, cancellationToken).ConfigureAwait(false);
        return Answered(new HostReport { Host = HostId.Local }, info, session: null);
    }

    /// <summary>
    /// A WSL distribution or an ssh host is reached first, so that a host that cannot be reached at all
    /// is reported as that, and never as a host missing something nobody could look for.
    /// </summary>
    private async Task<HostReport> InspectRemoteAsync(
        HarnessContext context,
        HostId host,
        IReadOnlyDictionary<string, EmulatorConfig> emulators,
        CancellationToken cancellationToken)
    {
        var opened = await _connector.ConnectAsync(context, host, [DotnetProgram], cancellationToken).ConfigureAwait(false);
        var found = new HostReport { Host = host, Os = opened.Os, Processor = opened.Processor };

        return opened.Connection is { } connection
            ? await PrepareAsync(found, connection, emulators, cancellationToken).ConfigureAwait(false)
            : found with { Reason = opened.Problem };
    }

    /// <summary>Brings DssHarness on a reachable host to this machine's build, then asks it what the host is.</summary>
    private async Task<HostReport> PrepareAsync(
        HostReport found,
        HostConnection connection,
        IReadOnlyDictionary<string, EmulatorConfig> emulators,
        CancellationToken cancellationToken)
    {
        var root = _identity.Current;
        var dotnet = connection.Located(DotnetProgram);

        var sdks = dotnet is { Present: true }
            ? await RunAsync(connection, connection.Spell(DotnetProgram), ["--list-sdks"], ProbeBudget, cancellationToken).ConfigureAwait(false)
            : null;

        if (sdks is null || !sdks.Succeeded)
        {
            return found with { Reason = NoSdk(dotnet, sdks) };
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
        var tools = await RunAsync(
            connection,
            connection.Spell(DotnetProgram),
            ["tool", "list", "--global", "--format", "json"],
            ProbeBudget,
            cancellationToken).ConfigureAwait(false);

        if (!tools.Succeeded || !HostProbes.TryReadToolVersion(tools.StandardOutput, ToolPackage.Id, out var installed))
        {
            return (HostProbes.Failure("its global .NET tools could not be listed", tools), null);
        }

        if (installed is null)
        {
            var install = await RunToolCommandAsync(connection, "install", root.Version, cancellationToken).ConfigureAwait(false);

            return install.Succeeded
                ? (null, $"installed {ToolPackage.Command} {root.Version}")
                : (HostProbes.Failure($"installing {ToolPackage.Command} {root.Version} from nuget.org there failed; a host runs only a version published on nuget.org", install), null);
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
            : (HostProbes.Failure($"updating {ToolPackage.Command} {installed} to {root.Version} there failed; a host runs only a version published on nuget.org", update), null);
    }

    /// <summary>Runs <c>dotnet tool install</c> or <c>update</c> for exactly <paramref name="version"/>, from nuget.org alone.</summary>
    private Task<ProcessResult> RunToolCommandAsync(HostConnection connection, string verb, string version, CancellationToken cancellationToken)
        => RunAsync(
            connection,
            connection.Spell(DotnetProgram),
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
            return found with { Reason = HostProbes.Failure($"{ToolPackage.Command} did not answer from {shownTool}, where global tools are installed", answer) };
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
            return HostProbes.Failure("its running processes could not be listed, so DssHarness there was not updated", listing);
        }

        return HostProbes.ListsProcess(listing.StandardOutput, ToolPackage.Command)
            ? $"{ToolPackage.Command} is running there, so it was not updated to {_identity.Current.Version}; run again once it has finished"
            : null;
    }

    /// <summary>
    /// Why the host cannot run the SDK, told apart by what looking for <c>dotnet</c> established. "It is
    /// not installed" and "it is installed where a command run without a login shell cannot see it" call
    /// for different things to be done, and reporting the first for the second sends somebody to install
    /// a second copy of what is already there.
    /// </summary>
    /// <param name="dotnet">Where <c>dotnet</c> was found, or <see langword="null"/> when it was never looked for.</param>
    /// <param name="sdks">What running it did, or <see langword="null"/> when it was never run.</param>
    private static string NoSdk(ProgramLocation? dotnet, ProcessResult? sdks)
    {
        var sdk = $"the .NET {ToolPackage.MinimumSdkMajor} SDK";

        if (dotnet is null or { Found: ProgramFound.Unreadable })
        {
            return $"whether {sdk} is installed there could not be established: the host did not answer when asked "
                + $"where 'dotnet' is; run again once it does, and see '{ToolPackage.Command} legs' for what answered";
        }

        if (dotnet.Found == ProgramFound.Nowhere)
        {
            return $"{sdk} is not installed there: 'dotnet' is neither on the PATH of a command run without a login "
                + $"shell nor in any of the directories an installer uses; install it there, or run "
                + $"'{ToolPackage.Command} {ToolProvisionService.CommandName}'";
        }

        // Reached only when dotnet was found and then would not run: a partial install, a broken
        // permission, or an architecture the host cannot execute. What it said is the whole diagnosis.
        var where = dotnet.Found == ProgramFound.OffPath
            ? $"'dotnet' is installed at '{dotnet.Path}', off the PATH of a command run without a login shell, and did not run from there"
            : "'dotnet' is on the PATH of a command run without a login shell there, and did not run";

        return sdks is null ? where : HostProbes.Failure(where, sdks);
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
}
