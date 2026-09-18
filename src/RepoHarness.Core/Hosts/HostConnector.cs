using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Secrets;

namespace RepoHarness.Core.Hosts;

/// <summary>What opening a connection to one host found.</summary>
public sealed record HostConnectionResult
{
    /// <summary>How programs are started there, or <see langword="null"/> when none can be.</summary>
    public HostConnection? Connection { get; init; }

    /// <summary>
    /// The superuser credential the host's item declares, for an install that needs one; kept off the
    /// connection so that it travels only to the one call that writes it to standard input.
    /// </summary>
    public HostCredential? Superuser { get; init; }

    /// <summary>The host's operating system, when opening the connection measured it.</summary>
    public string? Os { get; init; }

    /// <summary>The host's processor, when opening the connection measured it.</summary>
    public string? Processor { get; init; }

    /// <summary>Why the host cannot be reached; empty when it can.</summary>
    public string Problem { get; init; } = string.Empty;

    /// <summary>A host that cannot be reached, and why.</summary>
    /// <param name="problem">Why, as a lower-case fragment with its remedy after a semicolon.</param>
    public static HostConnectionResult Refused(string problem) => new() { Problem = problem };
}

/// <summary>
/// Opens a connection to a host: reads the host's own item, refuses before connecting when that item
/// is not safe to use, establishes that its name resolves, measures which shell answers, and measures
/// where the programs it will be asked to run actually are.
/// </summary>
/// <remarks>
/// Separate from inspection because the two are needed apart: inspection then asks whether DssHarness
/// can run there, while provisioning has to reach a host that cannot yet run DssHarness at all, which
/// is the whole point of installing the SDK on it.
/// </remarks>
public interface IHostConnector
{
    /// <summary>Opens a connection to <paramref name="host"/>, or says why it cannot be opened.</summary>
    /// <param name="context">The repository whose configuration and secrets declare the host.</param>
    /// <param name="host">The host.</param>
    /// <param name="programs">Programs whose place on the host is measured while the connection is open.</param>
    /// <param name="cancellationToken">Stops the probes.</param>
    Task<HostConnectionResult> ConnectAsync(
        HarnessContext context,
        HostId host,
        IReadOnlyList<string> programs,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IHostConnector"/>
public sealed class HostConnector(
    IHostPlatform platform,
    IProcessRunner processRunner,
    IHostCommandRunner hostCommands,
    IHostSecretsStore secrets,
    IHostAddressResolver addresses,
    IHostProgramResolver programs) : IHostConnector
{
    /// <summary>Longest one probe of a connected host may take.</summary>
    public static readonly TimeSpan ProbeBudget = TimeSpan.FromMinutes(2);

    /// <summary>ssh's own exit code for a failure of ssh itself, such as a connection or authentication failure.</summary>
    private const int SshFailed = 255;

    private readonly IHostPlatform _platform = platform;
    private readonly IProcessRunner _processRunner = processRunner;
    private readonly IHostCommandRunner _hostCommands = hostCommands;
    private readonly IHostSecretsStore _secrets = secrets;
    private readonly IHostAddressResolver _addresses = addresses;
    private readonly IHostProgramResolver _programs = programs;

    public Task<HostConnectionResult> ConnectAsync(
        HarnessContext context,
        HostId host,
        IReadOnlyList<string> programs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(programs);

        return host.Kind switch
        {
            HostKind.Local => LocalAsync(programs, cancellationToken),
            HostKind.Wsl => WslAsync(context, host, programs, cancellationToken),
            _ => SshAsync(context, host, programs, cancellationToken),
        };
    }

    private async Task<HostConnectionResult> LocalAsync(IReadOnlyList<string> wanted, CancellationToken cancellationToken)
    {
        var connection = await _programs
            .ResolveAsync(
                new HostConnection { Host = HostId.Local },
                wanted,
                ToolSearchDirectories.BuiltIn(_platform.PlatformKey),
                ProbeBudget,
                cancellationToken)
            .ConfigureAwait(false);

        return new HostConnectionResult
        {
            Connection = connection,
            Os = _platform.PlatformKey,
            Processor = _platform.Processor,
        };
    }

    private async Task<HostConnectionResult> WslAsync(
        HarnessContext context,
        HostId host,
        IReadOnlyList<string> wanted,
        CancellationToken cancellationToken)
    {
        if (_platform.Current != PlatformId.Windows)
        {
            return HostConnectionResult.Refused($"WSL exists only on Windows, and this machine runs {_platform.PlatformKey}");
        }

        if (_processRunner.FindExecutable(HostCommandRunner.WslProgram) is null)
        {
            return HostConnectionResult.Refused("wsl.exe was not found, so WSL is not installed on this machine");
        }

        var read = _secrets.ReadWslItem(context.Layout, host.Name);

        if (read.Item is not { } item)
        {
            return HostConnectionResult.Refused(read.Problem);
        }

        // Running a program in the distribution is the test that it exists and starts; the program
        // chosen also measures what the distribution is.
        var connection = new HostConnection { Host = host, Distribution = item.Distribution };
        var uname = await RunAsync(connection, "uname", ["-sm"], ProbeBudget, cancellationToken).ConfigureAwait(false);

        if (!uname.Succeeded)
        {
            return HostConnectionResult.Refused(
                HostProbes.IsMissingDistribution(uname.StandardOutput + uname.StandardError)
                    ? $"WSL has no distribution named '{item.Distribution}'"
                    : HostProbes.Failure("the distribution did not start a program", uname));
        }

        var (os, processor) = HostProbes.ReadUname(uname.StandardOutput);

        return new HostConnectionResult
        {
            Connection = await _programs
                .ResolveAsync(connection, wanted, ToolSearchDirectories.Posix, ProbeBudget, cancellationToken)
                .ConfigureAwait(false),
            Superuser = item.Superuser,
            Os = os,
            Processor = processor,
        };
    }

    private async Task<HostConnectionResult> SshAsync(
        HarnessContext context,
        HostId host,
        IReadOnlyList<string> wanted,
        CancellationToken cancellationToken)
    {
        if (!context.Config.Hosts.Ssh.TryGetValue(host.Name, out var settings))
        {
            return HostConnectionResult.Refused("it is not declared under hosts.ssh");
        }

        var read = _secrets.ReadSshItem(context.Layout, host.Name);

        if (read.Item is not { } item)
        {
            return HostConnectionResult.Refused(read.Problem);
        }

        if (_processRunner.FindExecutable(HostCommandRunner.SshProgram) is null)
        {
            return HostConnectionResult.Refused("ssh was not found on this machine");
        }

        var resolution = await _addresses.ResolveAsync(item.Address, cancellationToken).ConfigureAwait(false);

        if (!resolution.Resolved)
        {
            return HostConnectionResult.Refused(HostAddressResolver.Unresolved(resolution));
        }

        var connection = new HostConnection
        {
            Host = host,
            Address = item.Address,
            User = item.User,
            Port = item.Port,
            KeyFile = item.KeyFile,
            KnownHostsFile = item.KnownHostsFile,
            ConnectTimeoutSeconds = settings.ConnectTimeoutSeconds,
            KeepAliveSeconds = settings.KeepAliveSeconds,
            LocalDirectory = context.Layout.MainCheckoutRoot,
        };

        var budget = TimeSpan.FromSeconds(settings.ConnectTimeoutSeconds) + ProbeBudget;
        var probe = await _hostCommands.ProbeShellAsync(connection, budget, cancellationToken).ConfigureAwait(false);

        if (!probe.Succeeded)
        {
            // The client is named in every one of these. Which ssh ran is decided by PATH order and
            // is invisible in the failure itself: measured, a Windows machine carried three, and the
            // in-box 9.5p2 could agree no key exchange with a 10.x server while a newer one sitting
            // elsewhere on the same machine connected without trouble. "Could not be reached" sends
            // the reader to the server; the client is what had changed.
            var client = await SshClientAsync(cancellationToken).ConfigureAwait(false);

            return HostConnectionResult.Refused(probe switch
            {
                { TimedOut: true } => $"the host could not be reached: it did not answer within {budget.TotalSeconds:0} seconds ({client})",
                { ExitCode: SshFailed } => $"the host could not be reached: ssh said {HostProbes.Excerpt(probe.StandardError)} ({client})",
                _ => $"{HostProbes.Failure("its shell could not run echo", probe)} ({client})",
            });
        }

        connection = connection with { Shell = RemoteCommandLine.ReadShellProbe(probe.StandardOutput) };

        return new HostConnectionResult
        {
            // Before the host's platform is known, so the built-in list: what is looked for here is
            // the harness's own SDK, which a repository's toolSearchDirectories must never hide. cmd
            // is Windows's own shell, and Windows's built-in list is empty: a POSIX directory is none a
            // Windows host has, and the SDK there is on the machine PATH.
            Connection = await _programs
                .ResolveAsync(
                    connection,
                    wanted,
                    connection.Shell == RemoteShell.Cmd ? ToolSearchDirectories.BuiltIn(PlatformNames.Windows) : ToolSearchDirectories.Posix,
                    budget,
                    cancellationToken)
                .ConfigureAwait(false),
            Superuser = item.Superuser,
        };
    }

    /// <summary>
    /// Which ssh this machine actually ran, and what version it is.
    /// </summary>
    /// <remarks>
    /// Measured rather than assumed, and only when something has already failed: several ssh
    /// clients commonly sit on one PATH and the first wins silently. Reported as best it can be —
    /// a client that will not say its version still has its path named, which is the half that
    /// tells a reader which one to replace.
    /// </remarks>
    private async Task<string> SshClientAsync(CancellationToken cancellationToken)
    {
        var path = _processRunner.FindExecutable(HostCommandRunner.SshProgram);

        if (path is null)
        {
            return $"no '{HostCommandRunner.SshProgram}' on PATH";
        }

        try
        {
            var version = await _processRunner
                .RunAsync(
                    // Bounded like every other probe here. This runs whatever PATH resolved first,
                    // on the failure path of every ssh connection, and a client that prompts or sits
                    // on a stalled mount would otherwise hang a run whose outcome is already decided.
                    new ProcessRequest { FileName = path, Arguments = ["-V"], Timeout = ProbeBudget },
                    cancellationToken)
                .ConfigureAwait(false);

            // ssh -V writes its banner to standard error, and has done for as long as it has had one.
            var banner = HostProbes.Excerpt(
                version.StandardError.Length > 0 ? version.StandardError : version.StandardOutput);

            return banner.Length > 0 ? $"using {path}: {banner}" : $"using {path}";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A client that will not say its version is itself worth reporting: not executable, the
            // wrong architecture, or refused. Naming it matters more than the reason, so the reason
            // is summarised rather than dropped.
            return $"using {path}, which would not report its version ({exception.Message})";
        }
    }

    private Task<ProcessResult> RunAsync(
        HostConnection connection,
        string program,
        IReadOnlyList<string> arguments,
        TimeSpan budget,
        CancellationToken cancellationToken)
        => _hostCommands.RunAsync(
            connection,
            new HostCommand { Program = program, Arguments = arguments, Timeout = budget },
            cancellationToken);
}
