using System.Collections.Frozen;
using RepoHarness.Core.Processes;

namespace RepoHarness.Core.Hosts;

/// <summary>Everything needed to start a program on one host.</summary>
public sealed record HostConnection
{
    /// <summary>The host.</summary>
    public required HostId Host { get; init; }

    /// <summary>The ssh configuration file the host is declared in. ssh only.</summary>
    public string? SshConfigFile { get; init; }

    /// <summary>Seconds ssh may take to open the connection. ssh only.</summary>
    public int ConnectTimeoutSeconds { get; init; } = 25;

    /// <summary>Seconds between keep-alive probes; an unanswered probe ends the connection. ssh only.</summary>
    public int KeepAliveSeconds { get; init; } = 30;

    /// <summary>
    /// The directory on this machine ssh starts in, which a relative <c>IdentityFile</c> in the ssh
    /// configuration is resolved against. ssh only.
    /// </summary>
    public string? LocalDirectory { get; init; }

    /// <summary>The kind of shell the ssh server runs commands with, once measured. ssh only.</summary>
    public RemoteShell Shell { get; init; } = RemoteShell.Standard;
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

    /// <summary>Text written to the program's standard input, which is then closed.</summary>
    public string? StandardInput { get; init; }

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
    /// <exception cref="ExecutableNotFoundException">wsl.exe or ssh, or a local program, is not installed.</exception>
    Task<ProcessResult> RunAsync(HostConnection connection, HostCommand command, CancellationToken cancellationToken = default);

    /// <summary>Runs <see cref="RemoteCommandLine.ShellProbe"/> on an ssh host, to learn which kind of shell it has.</summary>
    Task<ProcessResult> ProbeShellAsync(HostConnection connection, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks WSL which distribution is its default, by running <c>printenv WSL_DISTRO_NAME</c> in it: the
    /// distribution answers with its own name.
    /// </summary>
    /// <exception cref="ExecutableNotFoundException">wsl.exe is not installed.</exception>
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

    public Task<ProcessResult> RunAsync(
        HostConnection connection,
        HostCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(command);

        return _processRunner.RunAsync(BuildRequest(connection, command), cancellationToken);
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

        return _processRunner.RunAsync(SshRequest(connection, RemoteCommandLine.ShellProbe) with { Timeout = timeout }, cancellationToken);
    }

    public Task<ProcessResult> ProbeDefaultWslDistributionAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => _processRunner.RunAsync(DefaultWslDistributionRequest(timeout), cancellationToken);

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
                Arguments = ["--distribution", connection.Host.Name, "--cd", "~", "--exec", command.Program, .. command.Arguments],
                Environment = WslEnvironment,
            },
            _ => SshRequest(connection, RemoteCommandLine.Join([command.Program, .. command.Arguments], connection.Shell)),
        };

        return request with
        {
            StandardInput = command.StandardInput,
            Timeout = command.Timeout,
            OnOutputLine = command.OnOutputLine,
            OnErrorLine = command.OnErrorLine,
        };
    }

    private static ProcessRequest SshRequest(HostConnection connection, string commandLine) => new()
    {
        FileName = SshProgram,
        Arguments =
        [
            "-F", connection.SshConfigFile ?? throw new ArgumentException("An ssh host needs its configuration file.", nameof(connection)),

            // Batch mode: a host key not yet trusted, or a login that asks for a password, fails at once
            // with ssh's reason instead of waiting for somebody to type.
            "-o", "BatchMode=yes",
            "-o", $"ConnectTimeout={connection.ConnectTimeoutSeconds}",
            "-o", $"ServerAliveInterval={connection.KeepAliveSeconds}",
            "-o", "ServerAliveCountMax=1",

            // No terminal: standard input then carries its bytes unchanged, and nothing waits for a key.
            "-T",
            connection.Host.Name,
            commandLine,
        ],
        WorkingDirectory = connection.LocalDirectory,
    };
}
