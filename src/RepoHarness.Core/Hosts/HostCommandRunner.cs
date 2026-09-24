using System.Collections.Frozen;
using System.Globalization;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Hosts;

/// <summary>Everything needed to start a program on one host.</summary>
public sealed record HostConnection
{
    /// <summary>Nothing has been resolved on a connection yet.</summary>
    private static readonly FrozenDictionary<string, ProgramLocation> NoPrograms =
        new Dictionary<string, ProgramLocation>(StringComparer.Ordinal).ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The host.</summary>
    public required HostId Host { get; init; }

    /// <summary>
    /// The distribution wsl.exe is given, read from the item's <c>.env</c> rather than from
    /// <c>config.json</c>, which git tracks and which therefore names no machine. WSL only.
    /// </summary>
    public string? Distribution { get; init; }

    /// <summary>
    /// The host's address as the item's <c>.env</c> declares it: a name, or an IP address. ssh is always
    /// given this, so that every Host block written for it applies. ssh only.
    /// </summary>
    public string? Address { get; init; }

    /// <summary>
    /// The address ssh dials in place of looking the name up, for every call the connection makes, while it
    /// holds; <see langword="null"/> where ssh looks the name up itself, or a jump host or a command it
    /// reaches the host through does. ssh only.
    /// </summary>
    public SshPin? Pin { get; init; }

    /// <summary>The user ssh logs in as, read from the item's <c>.env</c>. ssh only.</summary>
    public string? User { get; init; }

    /// <summary>The port ssh connects to. ssh only.</summary>
    public int Port { get; init; } = Secrets.SshItem.DefaultPort;

    /// <summary>The private key file ssh offers, and the only one it offers. ssh only.</summary>
    public string? KeyFile { get; init; }

    /// <summary>The file holding the host keys ssh may accept for this host. ssh only.</summary>
    public string? KnownHostsFile { get; init; }

    /// <summary>Seconds ssh may take to open the connection. ssh only.</summary>
    public int ConnectTimeoutSeconds { get; init; } = 25;

    /// <summary>Seconds between keep-alive probes; an unanswered probe ends the connection. ssh only.</summary>
    public int KeepAliveSeconds { get; init; } = 30;

    /// <summary>
    /// The directory on this machine ssh starts in. Every path ssh is given is absolute, so this
    /// decides nothing ssh resolves; it keeps ssh out of a directory this process may be about to
    /// delete. ssh only.
    /// </summary>
    public string? LocalDirectory { get; init; }

    /// <summary>The kind of shell the ssh server runs commands with, once measured. ssh only.</summary>
    public RemoteShell Shell { get; init; } = RemoteShell.Standard;

    /// <summary>
    /// Where each program asked about is on the host, by the name it was asked for, once measured.
    /// Measured per connection and never assumed: the PATH of a command run without a login shell is
    /// not the PATH a login shell shows, and on macOS it does not carry <c>/opt/homebrew/bin</c>.
    /// </summary>
    public IReadOnlyDictionary<string, ProgramLocation> Programs { get; init; } = NoPrograms;

    /// <summary>What was measured about <paramref name="program"/> here, or <see langword="null"/> when it was never asked about.</summary>
    public ProgramLocation? Located(string program)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(program);

        return Programs.TryGetValue(program, out var location) ? location : null;
    }

    /// <summary>
    /// How <paramref name="program"/> has to be spelt to start it here: its absolute path when the
    /// PATH of a command run without a login shell does not name it, and the name itself otherwise.
    /// </summary>
    public string Spell(string program)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(program);

        return Located(program) is { Found: ProgramFound.OffPath, Path: { } path } ? path : program;
    }

    /// <summary>
    /// Drops what was measured about <paramref name="program"/>, so that the next resolution measures
    /// it again. Used after installing one: what was true before the install is exactly what the
    /// install changed, and reusing it would start the copy that was not there.
    /// </summary>
    public HostConnection Forget(string program)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(program);

        return this with
        {
            Programs = Programs
                .Where(entry => !string.Equals(entry.Key, program, StringComparison.Ordinal))
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
        };
    }
}

/// <summary>A program to run on a host, and what to hand it.</summary>
public sealed record HostCommand
{
    /// <summary>The program: a name looked up on the host's PATH, or a path from the home directory.</summary>
    public required string Program { get; init; }

    /// <summary>
    /// Arguments. Over ssh each one must be a word every shell reads literally; anything else belongs
    /// in <see cref="StandardInput"/>.
    /// </summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>
    /// Text written to the program's standard input, which is then closed unless
    /// <see cref="HoldStandardInputOpen"/> is set; empty by default. ssh forwards whatever input it is
    /// given, so the program on the host reads exactly this, and nothing piped to DssHarness.
    /// </summary>
    public string StandardInput { get; init; } = string.Empty;

    /// <summary>
    /// Keeps standard input open until the program exits, so that a program watching for its end learns when
    /// this process has gone; see <see cref="ProcessRequest.HoldStandardInputOpen"/>.
    /// </summary>
    public bool HoldStandardInputOpen { get; init; }

    /// <summary>Wall clock budget, or <see langword="null"/> for none.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Receives each line the program writes to standard output, as it arrives.</summary>
    public Action<string>? OnOutputLine { get; init; }

    /// <summary>Receives each line the program writes to standard error, as it arrives.</summary>
    public Action<string>? OnErrorLine { get; init; }
}

/// <summary>
/// Starts programs on hosts: this machine, a WSL distribution, or an ssh host. The only place that knows
/// how wsl.exe and ssh are invoked.
/// </summary>
public interface IHostCommandRunner
{
    /// <summary>Runs <paramref name="command"/> on the host <paramref name="connection"/> reaches.</summary>
    /// <exception cref="HarnessException">wsl.exe or ssh would not start, so the host could not be reached.</exception>
    /// <exception cref="ProgramStartException">A program run on this machine is not installed or would not start.</exception>
    Task<ProcessResult> RunAsync(HostConnection connection, HostCommand command, CancellationToken cancellationToken = default);

    /// <summary>Runs <see cref="RemoteCommandLine.ShellProbe"/> on an ssh host, to learn which kind of shell it has.</summary>
    /// <exception cref="HarnessException">ssh would not start, so the host could not be reached.</exception>
    Task<ProcessResult> ProbeShellAsync(HostConnection connection, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks ssh what it would do to reach an ssh host over <paramref name="connection"/>, with <c>ssh -G</c>,
    /// which reads its configuration and prints the settings it would connect with, and connects to nothing.
    /// </summary>
    /// <exception cref="HarnessException">ssh would not start, so the host could not be reached.</exception>
    Task<ProcessResult> ReadSshSettingsAsync(HostConnection connection, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks WSL which distribution is its default, by running <c>printenv WSL_DISTRO_NAME</c> in it: the
    /// distribution answers with its own name.
    /// </summary>
    /// <exception cref="HarnessException">wsl.exe would not start, so WSL could not be reached.</exception>
    Task<ProcessResult> ProbeDefaultWslDistributionAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IHostCommandRunner"/>
public sealed class HostCommandRunner(IProcessRunner processRunner) : IHostCommandRunner
{
    /// <summary>The program that reaches WSL distributions.</summary>
    public const string WslProgram = "wsl";

    /// <summary>The program that reaches ssh hosts.</summary>
    public const string SshProgram = "ssh";

    /// <summary>
    /// The environment every wsl.exe call runs with. wsl.exe writes its own messages in UTF-16 unless told
    /// otherwise, and a child's output is read as UTF-8.
    /// </summary>
    private static readonly FrozenDictionary<string, string?> WslEnvironment =
        new Dictionary<string, string?>(StringComparer.Ordinal) { ["WSL_UTF8"] = "1" }.ToFrozenDictionary(StringComparer.Ordinal);

    private readonly IProcessRunner _processRunner = processRunner;

    public async Task<ProcessResult> RunAsync(
        HostConnection connection,
        HostCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(command);

        return connection.Host.Kind switch
        {
            HostKind.Local => await _processRunner.RunAsync(BuildRequest(connection, command), cancellationToken).ConfigureAwait(false),
            HostKind.Ssh => await OverSshAsync(connection, () => BuildRequest(connection, command), cancellationToken).ConfigureAwait(false),
            _ => await ThroughTransportAsync(connection.Host.ToString(), BuildRequest(connection, command), cancellationToken).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// Runs the ssh call <paramref name="build"/> makes for <paramref name="connection"/>; and where it went to a
    /// pinned address and failed before any session began, drops the pin and runs it once more, letting ssh
    /// look the name up as it would have.
    /// </summary>
    /// <remarks>
    /// Nothing ran on the host before a session began, so running the call again runs nothing twice. The
    /// address resolved at the start of a command can stop answering part way through it - a machine that
    /// slept and woke with another lease, one that answers at another of its addresses - and ssh itself tries
    /// every address a name has. What the pinned call printed is kept ahead of what the second one printed.
    /// </remarks>
    private async Task<ProcessResult> OverSshAsync(HostConnection connection, Func<ProcessRequest> build, CancellationToken cancellationToken)
    {
        var pinned = connection.Pin is { Holds: true };
        var result = await ThroughTransportAsync(connection.Host.ToString(), build(), cancellationToken).ConfigureAwait(false);

        if (!pinned || !HostProbes.FailedBeforeAnySession(result))
        {
            return result;
        }

        connection.Pin!.Drop();

        var again = await ThroughTransportAsync(connection.Host.ToString(), build(), cancellationToken).ConfigureAwait(false);

        return again with { StandardError = result.StandardError + again.StandardError };
    }

    /// <summary>
    /// Starts <paramref name="request"/>, whose program is the transport that reaches
    /// <paramref name="reached"/> - ssh, or wsl.exe.
    /// </summary>
    /// <remarks>
    /// What would not start is the transport itself - ssh, or wsl.exe in the middle of an update - so
    /// the host was never reached, whatever was asked of it: it is unavailable, which is no verdict
    /// about anything that runs there. Said here, where the program is known to be the transport,
    /// and not by a caller that cannot tell it from a program of its own; the cause travels with it,
    /// for a caller that names the host itself.
    /// </remarks>
    private async Task<ProcessResult> ThroughTransportAsync(string reached, ProcessRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await _processRunner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (ProgramStartException ex)
        {
            throw new HarnessException(HarnessExit.HostUnavailable, $"{reached} could not be reached: {ex.Message}", ex);
        }
    }

    public Task<ProcessResult> ProbeShellAsync(
        HostConnection connection,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (connection.Host.Kind != HostKind.Ssh)
        {
            throw new ArgumentException("Only an ssh host hands commands to a shell.", nameof(connection));
        }

        return OverSshAsync(
            connection,
            () => SshRequest(connection, RemoteCommandLine.ShellProbe) with { Timeout = timeout, StandardInput = string.Empty },
            cancellationToken);
    }

    public Task<ProcessResult> ReadSshSettingsAsync(
        HostConnection connection,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (connection.Host.Kind != HostKind.Ssh)
        {
            throw new ArgumentException("Only an ssh host has ssh settings.", nameof(connection));
        }

        return ThroughTransportAsync(
            connection.Host.ToString(),
            SshSettingsRequest(connection) with { Timeout = timeout, StandardInput = string.Empty },
            cancellationToken);
    }

    /// <summary>
    /// The process that asks ssh what it would do for <paramref name="connection"/>: <c>ssh -G</c>, with every
    /// option a call over the connection is given, its pin's among them, and the same destination.
    /// </summary>
    public static ProcessRequest SshSettingsRequest(HostConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return new()
        {
            FileName = SshProgram,
            Arguments = ["-G", .. SshOptions(connection), Destination(connection)],
            WorkingDirectory = connection.LocalDirectory,
        };
    }

    public Task<ProcessResult> ProbeDefaultWslDistributionAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => ThroughTransportAsync("WSL", DefaultWslDistributionRequest(timeout), cancellationToken);

    /// <summary>
    /// The process that asks WSL for its default distribution. The name is read from inside that
    /// distribution rather than from wsl.exe's own listing, whose words are translated into the
    /// machine's language.
    /// </summary>
    public static ProcessRequest DefaultWslDistributionRequest(TimeSpan timeout) => new()
    {
        FileName = WslProgram,
        Arguments = ["--exec", "printenv", "WSL_DISTRO_NAME"],
        Environment = WslEnvironment,
        StandardInput = string.Empty,
        Timeout = timeout,
    };

    /// <summary>The process that runs <paramref name="command"/> on the host <paramref name="connection"/> reaches.</summary>
    public static ProcessRequest BuildRequest(HostConnection connection, HostCommand command)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(command);

        var request = connection.Host.Kind switch
        {
            HostKind.Local => new ProcessRequest { FileName = command.Program, Arguments = command.Arguments },
            HostKind.Wsl => new ProcessRequest
            {
                FileName = WslProgram,

                // --exec starts the program without the distribution's shell, so each argument arrives
                // as it is; --cd ~ starts it in the home directory rather than in whatever the Windows
                // directory of this process translates to.
                Arguments =
                [
                    "--distribution",
                    Declared(connection.Distribution, nameof(HostConnection.Distribution)),
                    "--cd",
                    "~",
                    "--exec",
                    command.Program,
                    .. command.Arguments,
                ],
                Environment = WslEnvironment,
            },
            _ => SshRequest(connection, RemoteCommandLine.Join([command.Program, .. command.Arguments], connection.Shell)),
        };

        return request with
        {
            StandardInput = command.StandardInput,
            HoldStandardInputOpen = command.HoldStandardInputOpen,
            Timeout = command.Timeout,
            OnOutputLine = command.OnOutputLine,
            OnErrorLine = command.OnErrorLine,
        };
    }

    /// <summary>
    /// How ssh is invoked: everything the connection needs from the host's own item, on the command line,
    /// where it outranks what ssh's own configuration says. None is named with <c>-F</c>: a file passed so
    /// is read whatever its permissions, and a repository that named one would decide, through git, which
    /// machine a clone reaches. ssh still reads the user's and the system's own configuration for what the
    /// command line leaves unsaid - a ProxyJump there applies, and a HostName there is what is looked up
    /// and pinned - as it does for anybody, and the destination is always the address declared, so a Host
    /// block written for it matches.
    /// </summary>
    private static ProcessRequest SshRequest(HostConnection connection, string commandLine) => new()
    {
        FileName = SshProgram,
        Arguments =
        [
            .. SshOptions(connection),

            // No terminal: standard input then carries its bytes unchanged, and nothing waits for a key.
            "-T",
            Destination(connection),
            commandLine,
        ],
        WorkingDirectory = connection.LocalDirectory,
    };

    /// <summary>Where every ssh call goes: the user and the address the host's item declares.</summary>
    private static string Destination(HostConnection connection)
        => $"{Declared(connection.User, nameof(HostConnection.User))}@{Declared(connection.Address, nameof(HostConnection.Address))}";

    /// <summary>The options every ssh call over <paramref name="connection"/> is given, its pin's while it holds.</summary>
    private static string[] SshOptions(HostConnection connection)
        =>
        [
            "-i", Declared(connection.KeyFile, nameof(HostConnection.KeyFile)),

            // Only that key: without this, ssh offers every key an agent holds, and a host that counts
            // failed authentications locks the account before the right key is reached.
            "-o", "IdentitiesOnly=yes",

            // The host keys this repository set up, never the user's own file: a machine that never
            // connected by hand has none, and one that did may trust a key nobody here checked.
            "-o", $"UserKnownHostsFile={Declared(connection.KnownHostsFile, nameof(HostConnection.KnownHostsFile))}",

            // Batch mode: a host key not yet trusted, or a login that asks for a password, fails at once
            // with ssh's reason instead of waiting for somebody to type.
            "-o", "BatchMode=yes",
            "-o", $"ConnectTimeout={connection.ConnectTimeoutSeconds}",
            "-o", $"ServerAliveInterval={connection.KeepAliveSeconds}",
            "-o", "ServerAliveCountMax=1",

            // Measured with OpenSSH for Windows 10.0p2 against a server whose key known_hosts held under a
            // name alone: reached by its address, ssh found no key; with the alias, it found it.
            .. connection.Pin is { Holds: true } pin ? pin.Options() : [],
            "-p", connection.Port.ToString(CultureInfo.InvariantCulture),
        ];

    /// <summary>
    /// One part of a connection that the host's own item declares. Absent, it stops the call rather
    /// than falling back to a default: ssh's own defaults reach the current user on port 22 at
    /// whatever the name happens to resolve to, which is a machine nobody chose.
    /// </summary>
    private static string Declared(string? value, string part)
        => string.IsNullOrEmpty(value)
            ? throw new InvalidOperationException(
                $"The connection to this host declares no {part}; it is read from the host's item under .harness-config.")
            : value;
}
