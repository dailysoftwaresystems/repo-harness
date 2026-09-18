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

    /// <summary>
    /// Where the tool is on that host, as this tool invokes it, or <see langword="null"/> where the
    /// host was not reached.
    /// </summary>
    /// <remarks>
    /// Reported because "the host answered" and "somebody can type the tool's name there" are
    /// different facts, and a survey that only shows the first reads as the second. This tool always
    /// invokes the absolute path, so a host whose PATH does not carry it is perfectly runnable here
    /// and unusable by hand.
    /// </remarks>
    public string? ToolPath { get; init; }

    /// <summary>What checking each emulator found there, by name.</summary>
    public IReadOnlyDictionary<string, EmulatorCheck> Emulators { get; init; }
        = new Dictionary<string, EmulatorCheck>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Where each program the survey asked about is there, found by the host itself the way a leg
    /// there will start it.
    /// </summary>
    public IReadOnlyDictionary<string, ProgramLocation> Programs { get; init; }
        = new Dictionary<string, ProgramLocation>(StringComparer.Ordinal);

    /// <summary>
    /// The directories a program asked for by name was found in there, on the PATH or off it, in the
    /// order the search looked. A leg run there appends them to the PATH of every process it starts.
    /// </summary>
    public IReadOnlyList<string> ProgramDirectories { get; init; } = [];

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
    /// <param name="context">The repository and its configuration.</param>
    /// <param name="host">The host to measure.</param>
    /// <param name="emulators">The emulators to check there, by name.</param>
    /// <param name="programs">The programs to find there, the way a leg there will start them.</param>
    /// <param name="cancellationToken">Stops the measuring.</param>
    Task<HostReport> InspectAsync(
        HarnessContext context,
        HostId host,
        IReadOnlyDictionary<string, EmulatorConfig> emulators,
        IReadOnlyList<string> programs,
        CancellationToken cancellationToken = default);
}

/// <summary>What a host is asked when it is measured.</summary>
/// <param name="Emulators">The emulators to check, by name.</param>
/// <param name="Programs">The programs to find.</param>
/// <param name="SearchDirectories">The repository's <c>toolSearchDirectories</c>.</param>
internal sealed record HostQuestions(
    IReadOnlyDictionary<string, EmulatorConfig> Emulators,
    IReadOnlyList<string> Programs,
    IReadOnlyDictionary<string, List<string>> SearchDirectories);

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
        IReadOnlyList<string> programs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(emulators);
        ArgumentNullException.ThrowIfNull(programs);

        var questions = new HostQuestions(emulators, programs, context.Config.ToolSearchDirectories);

        return host.Kind == HostKind.Local
            ? InspectLocalAsync(questions, cancellationToken)
            : InspectRemoteAsync(context, host, questions, cancellationToken);
    }

    /// <summary>This machine is measured in-process: the build doing the measuring is the one that would run its legs.</summary>
    private async Task<HostReport> InspectLocalAsync(HostQuestions questions, CancellationToken cancellationToken)
    {
        var info = await _agent
            .DescribeAsync(questions.Emulators, questions.Programs, questions.SearchDirectories, cancellationToken)
            .ConfigureAwait(false);

        return Answered(new HostReport { Host = HostId.Local }, info, session: null);
    }

    /// <summary>
    /// A WSL distribution or an ssh host is reached first, so that a host that cannot be reached at all
    /// is reported as that, and never as a host missing something nobody could look for.
    /// </summary>
    private async Task<HostReport> InspectRemoteAsync(
        HarnessContext context,
        HostId host,
        HostQuestions questions,
        CancellationToken cancellationToken)
    {
        var opened = await _connector.ConnectAsync(context, host, [DotnetProgram], cancellationToken).ConfigureAwait(false);
        var found = new HostReport { Host = host, Os = opened.Os, Processor = opened.Processor };

        if (opened.Connection is not { } connection)
        {
            return found with { Reason = opened.Problem };
        }

        try
        {
            return await PrepareAsync(found, connection, questions, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException ex) when (HostConnector.Unreached(ex) is { } reason)
        {
            // Its transport stopped starting part way through: this host cannot take legs, and the
            // others are still measured.
            return found with { Reason = reason };
        }
    }

    /// <summary>Brings DssHarness on a reachable host to this machine's build, then asks it what the host is.</summary>
    private async Task<HostReport> PrepareAsync(
        HostReport found,
        HostConnection connection,
        HostQuestions questions,
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
            return found with { Reason = $"{ToolPackage.Id} needs the .NET {ToolPackage.MinimumSdkMajor} SDK there, and it has {present}" };
        }

        // Where the SDK is installed is how a Windows host is told from any other before DssHarness
        // runs there; the kind of shell alone cannot say, since PowerShell runs on both.
        var windowsHost = listed.Any(sdk => sdk.OnWindows);

        var (reason, action) = await BringToThisBuildAsync(found.Host, connection, windowsHost, root, cancellationToken).ConfigureAwait(false);

        if (reason is not null)
        {
            return found with { Reason = reason };
        }

        return await AskAsync(found with { Actions = action is null ? [] : [action] }, connection, windowsHost, questions, root, cancellationToken)
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
                ? (null, $"installed {ToolPackage.Id} {root.Version}")
                : (HostProbes.Failure($"installing {ToolPackage.Id} {root.Version} from nuget.org there failed; a host runs only a version published on nuget.org", install), null);
        }

        if (!SemanticVersion.TryParse(installed, out var hostVersion) || !SemanticVersion.TryParse(root.Version, out var rootVersion))
        {
            return ($"{ToolPackage.Id} {installed} there cannot be compared with {root.Version} here", null);
        }

        var order = SemanticVersion.Compare(hostVersion, rootVersion);

        if (order > 0)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"{host} has {ToolPackage.Id} {installed}, newer than this machine's {root.Version}. Versions only move up, "
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
            ? (null, $"updated {ToolPackage.Id} {installed} to {root.Version}")
            : (HostProbes.Failure($"updating {ToolPackage.Id} {installed} to {root.Version} there failed; a host runs only a version published on nuget.org", update), null);
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
        HostQuestions questions,
        ToolIdentity root,
        CancellationToken cancellationToken)
    {
        var emulators = questions.Emulators;

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
                Programs = [.. questions.Programs],
                ToolSearchDirectories = new Dictionary<string, List<string>>(
                    questions.SearchDirectories,
                    StringComparer.OrdinalIgnoreCase),
            },
            HostAgentProtocol.JsonOptions);

        var budget = ProbeBudget + (EmulatorProbe.WitnessBudget * emulators.Count);

        // One line, held open until the host has answered: a budget that runs out stops ssh or wsl.exe here,
        // which ends the input there and stops any witness still running.
        var answer = await RunAsync(connection, toolPath, [HostAgentProtocol.CommandName], budget, cancellationToken, request + "\n", holdOpen: true)
            .ConfigureAwait(false);

        if (!answer.Succeeded)
        {
            return found with { Reason = HostProbes.Failure($"{ToolPackage.Id} did not answer from {shownTool}, where global tools are installed", answer) };
        }

        var start = answer.StandardOutput.IndexOf('{', StringComparison.Ordinal);

        if (start < 0)
        {
            return found with { Reason = $"{ToolPackage.Id} at {shownTool} answered with no document: {HostProbes.Excerpt(answer.StandardOutput)}" };
        }

        var document = answer.StandardOutput[start..];

        // The build is identified before the rest of the answer is read, so a build that answers in another
        // shape is still reported as the build it is, with the remedy for that.
        if (!TryReadIdentity(document, out var version, out var assemblySha256, out var problem))
        {
            return found with { Reason = $"{ToolPackage.Id} at {shownTool} answered in a form this build cannot read: {problem}" };
        }

        if (!string.Equals(version, root.Version, StringComparison.Ordinal))
        {
            return found with { Reason = $"{ToolPackage.Id} there reports {version}, and {root.Version} was expected" };
        }

        if (!string.Equals(assemblySha256, root.AssemblySha256, StringComparison.OrdinalIgnoreCase))
        {
            return found with
            {
                Reason = $"{ToolPackage.Id} {version} there is a different build from this machine's although the versions match, "
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
            return found with { Reason = $"{ToolPackage.Id} at {shownTool} answered in a form this build cannot read: {ex.Message}" };
        }

        return info is null
            ? found with { Reason = $"{ToolPackage.Id} at {shownTool} answered with an empty document" }
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
            ? $"{ToolPackage.Id} is running there, so it was not updated to {_identity.Current.Version}; run again once it has finished"
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
            // The search's own reason where it has one: the host answered, and the search could not
            // look where it was told to, which running again does not change. A host that did not
            // answer leaves no reason, and running again may well change that.
            var located = dotnet ?? new ProgramLocation(DotnetProgram, ProgramFound.Unreadable);

            return $"whether {sdk} is installed there could not be established: {located.WhyUnestablished()}"
                + (located.Reason is null ? $"; run again once it does, and see '{ToolPackage.Id} legs' for what answered" : string.Empty);
        }

        if (dotnet.Found == ProgramFound.Nowhere)
        {
            return $"{sdk} is not installed there: 'dotnet' is neither on the PATH of a command run without a login "
                + $"shell nor in any of the directories an installer uses; install it there, or run "
                + $"'{ToolPackage.Id} {ToolProvisionService.CommandName}'";
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
        ToolPath = session?.ToolPath,
        Emulators = info.Emulators,

        // By each program's own name, compared exactly: cmake and CMake are two files on Linux. A
        // program answered twice is the same answer twice, and the later one stands.
        Programs = info.Programs
            .GroupBy(location => location.Program, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal),
        ProgramDirectories = info.ProgramDirectories,
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
