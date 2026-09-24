using System.Text.Json;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Hosts;

/// <summary>
/// Runs one DssHarness command on a WSL distribution or an ssh host, in that host's copy of the
/// repository, with its output streamed here as it is written.
/// </summary>
public sealed class HostExecService(
    IHarnessContextLoader contextLoader,
    IHostInspector inspector,
    IHostCommandRunner hostCommands,
    IHostPlatform platform,
    IHarnessOutput output)
{
    /// <summary>The command's name, which prefixes what it reports.</summary>
    public const string CommandName = "host-exec";

    /// <summary>Longest asking WSL for its default distribution may take.</summary>
    private static readonly TimeSpan DefaultDistributionBudget = TimeSpan.FromMinutes(2);

    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly IHostInspector _inspector = inspector;
    private readonly IHostCommandRunner _hostCommands = hostCommands;
    private readonly IHostPlatform _platform = platform;
    private readonly IHarnessOutput _output = output;

    /// <summary>Runs <paramref name="arguments"/> with DssHarness on the host.</summary>
    /// <param name="directory">A directory in the repository whose configuration declares the host.</param>
    /// <param name="ssh">The ssh host, or <see langword="null"/> when the host is a WSL distribution.</param>
    /// <param name="wsl">
    /// The WSL distribution, the empty string for WSL's default distribution, or <see langword="null"/>
    /// when the host is an ssh host.
    /// </param>
    /// <param name="arguments">The command and its arguments, exactly as they would be typed after <c>DssHarness</c>.</param>
    /// <param name="cancellationToken">Stops the command, on the host as well as here.</param>
    /// <returns>
    /// An outcome carrying the command's own exit code, unchanged, or <see cref="HarnessExit.HostUnavailable"/>
    /// when the host cannot run DssHarness, or the command never reported how it finished.
    /// </returns>
    public async Task<CommandOutcome> RunAsync(
        string directory,
        string? ssh,
        string? wsl,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(arguments);

        if ((ssh is null) == (wsl is null))
        {
            throw new HarnessException(HarnessExit.UsageError, "name the host with exactly one of --ssh <name> or --wsl [<distro>]");
        }

        if (arguments.Count == 0)
        {
            throw new HarnessException(HarnessExit.UsageError, "name the DssHarness command to run there, after --");
        }

        if (HostAgentProtocol.IsNotForwardable(arguments[0]))
        {
            throw new HarnessException(
                HarnessExit.UsageError,
                $"'{arguments[0]}' runs on the machine that reaches the hosts, not on a host");
        }

        var context = await _contextLoader.LoadAsync(directory, cancellationToken).ConfigureAwait(false);
        var hosts = context.Config.Hosts;

        (HostId Host, string RepositoryPath) target;

        if (ssh is not null)
        {
            target = Resolve(hosts.Ssh, "ssh", ssh, HostId.Ssh);
        }
        else
        {
            var distribution = wsl!.Length > 0 ? wsl : await DefaultDistributionAsync(cancellationToken).ConfigureAwait(false);
            target = Resolve(hosts.Wsl, "wsl", distribution, HostId.Wsl);
        }

        var host = target.Host;

        var report = await _inspector
            .InspectAsync(
                context,
                host,
                new Dictionary<string, EmulatorConfig>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, DeveloperEnvironmentConfig>(StringComparer.OrdinalIgnoreCase),
                [],
                cancellationToken)
            .ConfigureAwait(false);

        foreach (var action in report.Actions)
        {
            _output.Info(CommandName, $"{host}: {action}");
        }

        if (report.Session is not { } session)
        {
            return CommandOutcome.Failed(HarnessExit.HostUnavailable, $"{host} cannot run {ToolPackage.Id}: {report.Reason}");
        }

        var nonce = HostAgentProtocol.NewNonce();

        var request = JsonSerializer.Serialize(
            new HostAgentRequest
            {
                Kind = HostAgentRequestKind.Run,
                Directory = target.RepositoryPath,
                Arguments = [.. arguments],
                Nonce = nonce,
            },
            HostAgentProtocol.JsonOptions);

        int? finished = null;

        var result = await _hostCommands.RunAsync(
            session.Connection,
            new HostCommand
            {
                Program = session.ToolPath,
                Arguments = _output.IsVerbose
                    ? [HostAgentProtocol.CommandName, HostAgentProtocol.VerboseOption]
                    : [HostAgentProtocol.CommandName],

                // One line, with the input then held open while the command runs: stopping this process ends the
                // input on the host, which cancels the command there instead of leaving it running.
                StandardInput = request + "\n",
                HoldStandardInputOpen = true,
                OnOutputLine = _output.Raw,
                OnErrorLine = line =>
                {
                    if (HostAgentProtocol.TryReadCompletionLine(line, nonce, out var code))
                    {
                        finished = code;
                    }
                    else
                    {
                        _output.RawError(line);
                    }
                },
            },
            cancellationToken).ConfigureAwait(false);

        var shown = string.Join(' ', arguments);

        // The command's exit code comes from its completion line, never from the transport: ssh exits 255, and
        // wsl.exe with codes of its own, when the connection fails, and neither is the command's result.
        if (finished is not { } exitCode)
        {
            return CommandOutcome.Failed(
                HarnessExit.HostUnavailable,
                $"{host}: {HostProbes.NeverFinished($"'{shown}'", result, session.Connection)}");
        }

        return exitCode == HarnessExit.Success
            ? CommandOutcome.Ok($"{host}: '{shown}' succeeded") with { Quiet = true }
            : CommandOutcome.Failed(exitCode, $"{host}: '{shown}' exited {exitCode}");
    }

    /// <summary>
    /// The host <paramref name="name"/> selects among <paramref name="hosts"/>, named as the configuration
    /// declares it: its item is read from the directory of that name, which a case-sensitive file system
    /// finds only as spelt, and every record names the host the same way.
    /// </summary>
    private static (HostId Host, string RepositoryPath) Resolve<THost>(
        Dictionary<string, THost> hosts,
        string kind,
        string name,
        Func<string, HostId> toHost)
        where THost : RemoteHostConfig
    {
        if (DeclaredName.In(hosts.Keys, name) is not { } declared)
        {
            throw new HarnessException(HarnessExit.UsageError, $"--{kind} {name} is not declared under hosts.{kind}{Declared(hosts.Keys)}");
        }

        return (toHost(declared), hosts[declared].RepositoryPath);
    }

    /// <summary>WSL's default distribution, as that distribution itself reports it.</summary>
    private async Task<string> DefaultDistributionAsync(CancellationToken cancellationToken)
    {
        if (_platform.Current != PlatformId.Windows)
        {
            throw new HarnessException(HarnessExit.HostUnavailable, $"WSL exists only on Windows, and this machine runs {_platform.PlatformKey}");
        }

        // wsl.exe that would not start - not installed, or mid-update - is raised by the runner that
        // starts it, as WSL not being reachable, in the words the system gave.
        var result = await _hostCommands
            .ProbeDefaultWslDistributionAsync(DefaultDistributionBudget, cancellationToken)
            .ConfigureAwait(false);

        if (result.TimedOut)
        {
            throw new HarnessException(
                HarnessExit.HostUnavailable,
                $"WSL did not name its default distribution within {DefaultDistributionBudget.TotalSeconds:0} seconds");
        }

        var name = result.TrimmedOutput;

        if (!result.Succeeded || name.Length == 0)
        {
            throw new HarnessException(
                HarnessExit.HostUnavailable,
                $"WSL did not name a default distribution (exit {result.ExitCode}): {HostProbes.Excerpt(result.StandardError + "\n" + result.StandardOutput)}");
        }

        return name;
    }

    private static string Declared(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count == 0 ? "; none are declared" : $"; declared: {string.Join(", ", list)}";
    }
}
