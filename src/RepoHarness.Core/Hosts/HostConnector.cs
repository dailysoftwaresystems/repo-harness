using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
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
/// is not safe to use, asks ssh what it would do and - unless ssh reaches the host through a jump host
/// or a command - establishes that the name ssh would look up resolves and pins the address it resolved
/// to, measures which shell answers, and measures where the programs it will be asked to run actually are.
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
            HostKind.Wsl => ReachedAsync(WslAsync(context, host, programs, cancellationToken)),
            _ => ReachedAsync(SshAsync(context, host, programs, cancellationToken)),
        };
    }

    /// <summary>
    /// A host whose transport would not start was never reached, and is refused as that - one host
    /// that cannot be used - rather than ending whatever asked for it, in the transport's own words.
    /// </summary>
    private static async Task<HostConnectionResult> ReachedAsync(Task<HostConnectionResult> connecting)
    {
        try
        {
            return await connecting.ConfigureAwait(false);
        }
        catch (HarnessException ex) when (Unreached(ex) is { } reason)
        {
            return HostConnectionResult.Refused(reason);
        }
    }

    /// <summary>
    /// Why the transport that reaches a host would not start, when that is what
    /// <paramref name="exception"/> says, or <see langword="null"/>.
    /// </summary>
    /// <param name="exception">What reaching the host raised.</param>
    public static string? Unreached(HarnessException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception.ExitCode == HarnessExit.HostUnavailable && exception.InnerException is ProgramStartException start
            ? start.Message
            : null;
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
                    : HostProbes.Failure("the distribution did not start a program", uname, connection));
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

        // What ssh would do, its own configuration read: which name it would look up, and whether it
        // would reach the host through something else. Unsaid where ssh could not say, and ssh is then
        // left to itself, with the name declared looked up here all the same.
        var seen = SshSettings.Read(
            await _hostCommands.ReadSshSettingsAsync(connection, ProbeBudget, cancellationToken).ConfigureAwait(false));

        // Through a jump host or a command, the name is theirs to look up: the jump host looks it up on its
        // side, where an address this machine found means nothing, and a command is given the name to
        // reach as it chooses - a tunnel's client given an address in its place reaches nothing.
        if (seen is not { Proxied: true })
        {
            var resolution = await _addresses.ResolveAsync(seen?.HostName ?? item.Address, cancellationToken).ConfigureAwait(false);

            if (!resolution.Resolved)
            {
                return HostConnectionResult.Refused(HostAddressResolver.Unresolved(resolution, item.Address));
            }

            connection = connection with
            {
                Pin = seen is null ? null : await PinAsync(connection, seen, resolution, cancellationToken).ConfigureAwait(false),
            };
        }

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

            // echo exits 255 nowhere, so here that code is ssh's own, whatever it failed over. And no
            // session has opened yet on this connection, so whatever ssh failed over - a key it refused
            // among them - the host could not be reached for this command.
            return HostConnectionResult.Refused(probe switch
            {
                { TimedOut: true } => $"the host could not be reached: it did not answer within {budget.TotalSeconds:0} seconds ({client})",
                { ExitCode: HostProbes.SshFailed } => $"{HostProbes.CouldNotReach(HostProbes.Excerpt(probe.StandardError))} ({client})",
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
    /// The address <paramref name="resolution"/> found, pinned for every call <paramref name="connection"/>
    /// makes, or <see langword="null"/> where ssh is to look the name up itself: where it would dial that
    /// address anyway, or where giving it the address would change anything else it does.
    /// </summary>
    /// <param name="connection">The connection, unpinned.</param>
    /// <param name="seen">What ssh would do over it unpinned.</param>
    /// <param name="resolution">What this machine resolved the name ssh would look up to.</param>
    /// <param name="cancellationToken">Stops the question.</param>
    /// <remarks>
    /// Asked of ssh a second time, pinned, and kept only where the two answers differ in where the connection
    /// goes and nothing else: a Match block keyed by the host, which a pin can switch on or off, shows there.
    /// </remarks>
    private async Task<SshPin?> PinAsync(
        HostConnection connection,
        SshSettings seen,
        AddressResolution resolution,
        CancellationToken cancellationToken)
    {
        if (resolution.ResolvedTo is not { } address || SshPin.For(seen, address, connection.Port) is not { } pin)
        {
            return null;
        }

        var pinned = SshSettings.Read(
            await _hostCommands.ReadSshSettingsAsync(connection with { Pin = pin }, ProbeBudget, cancellationToken).ConfigureAwait(false));

        return pinned is not null && seen.DifferOnlyInWhereTheyGo(pinned) ? pin : null;
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
