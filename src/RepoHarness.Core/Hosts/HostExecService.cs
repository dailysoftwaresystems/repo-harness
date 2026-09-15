using System.Text.Json;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Hosts;

/// <summary>
/// Runs one repo-harness command on a WSL distribution or an ssh host, in that host's copy of the
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

    /// <summary>Runs <paramref name="arguments"/> with repo-harness on the host.</summary>
    /// <param name="directory">A directory in the repository whose configuration declares the host.</param>
    /// <param name="ssh">The ssh host, or <see langword="null"/> when the host is a WSL distribution.</param>
    /// <param name="wsl">
    /// The WSL distribution, the empty string for WSL's default distribution, or <see langword="null"/>
    /// when the host is an ssh host.
    /// </param>
    /// <param name="arguments">The command and its arguments, exactly as they would be typed after <c>repo-harness</c>.</param>
    /// <param name="cancellationToken">Stops the command.</param>
    /// <returns>An outcome carrying the command's own exit code, unchanged.</returns>
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
            throw new HarnessException(HarnessExit.UsageError, "name the repo-harness command to run there, after --");
        }

        if (arguments[0] is CommandName or HostAgentProtocol.CommandName)
        {
            throw new HarnessException(
                HarnessExit.UsageError,
                $"'{arguments[0]}' runs on the machine that reaches the hosts, not on a host");
        }

        var context = await _contextLoader.LoadAsync(directory, cancellationToken).ConfigureAwait(false);

        var (host, repositoryPath) = ssh is not null
            ? ResolveSsh(context.Config, ssh)
            : await ResolveWslAsync(context.Config, wsl!, cancellationToken).ConfigureAwait(false);

        var report = await _inspector
            .InspectAsync(context, host, new Dictionary<string, EmulatorConfig>(StringComparer.OrdinalIgnoreCase), cancellationToken)
            .ConfigureAwait(false);

        foreach (var action in report.Actions)
        {
            _output.Info(CommandName, $"{host}: {action}");
        }

        if (report.Session is not { } session)
        {
            return CommandOutcome.Failed(HarnessExit.HostUnavailable, $"{host} cannot run repo-harness: {report.Reason}");
        }

        var request = JsonSerializer.Serialize(
            new HostAgentRequest
            {
                Kind = HostAgentRequestKind.Run,
                Directory = repositoryPath,
                Arguments = [.. arguments],
            },
            HostAgentProtocol.JsonOptions);

        var result = await _hostCommands.RunAsync(
            session.Connection,
            new HostCommand
            {
                Program = session.ToolPath,
                Arguments = [HostAgentProtocol.CommandName],
                StandardInput = request,
                OnOutputLine = _output.Raw,
                OnErrorLine = _output.RawError,
            },
            cancellationToken).ConfigureAwait(false);

        var shown = string.Join(' ', arguments);

        return result.ExitCode == HarnessExit.Success
            ? CommandOutcome.Ok($"{host}: '{shown}' succeeded") with { Quiet = true }
            : CommandOutcome.Failed(result.ExitCode, $"{host}: '{shown}' exited {result.ExitCode}");
    }

    private static (HostId Host, string RepositoryPath) ResolveSsh(HarnessConfig config, string name)
    {
        if (!config.Hosts.Ssh.TryGetValue(name, out var declared))
        {
            throw new HarnessException(HarnessExit.UsageError, $"--ssh {name} is not declared under hosts.ssh{Declared(config.Hosts.Ssh.Keys)}");
        }

        return (HostId.Ssh(DeclaredName(config.Hosts.Ssh.Keys, name)), declared.RepositoryPath);
    }

    private async Task<(HostId Host, string RepositoryPath)> ResolveWslAsync(
        HarnessConfig config,
        string distribution,
        CancellationToken cancellationToken)
    {
        var name = distribution.Length > 0
            ? distribution
            : await DefaultDistributionAsync(cancellationToken).ConfigureAwait(false);

        if (!config.Hosts.Wsl.TryGetValue(name, out var declared))
        {
            throw new HarnessException(HarnessExit.UsageError, $"--wsl {name} is not declared under hosts.wsl{Declared(config.Hosts.Wsl.Keys)}");
        }

        return (HostId.Wsl(DeclaredName(config.Hosts.Wsl.Keys, name)), declared.RepositoryPath);
    }

    /// <summary>WSL's default distribution, as that distribution itself reports it.</summary>
    private async Task<string> DefaultDistributionAsync(CancellationToken cancellationToken)
    {
        if (_platform.Current != PlatformId.Windows)
        {
            throw new HarnessException(HarnessExit.HostUnavailable, $"WSL exists only on Windows, and this machine runs {_platform.PlatformKey}");
        }

        ProcessResult result;

        try
        {
            result = await _hostCommands
                .ProbeDefaultWslDistributionAsync(DefaultDistributionBudget, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ExecutableNotFoundException)
        {
            throw new HarnessException(HarnessExit.HostUnavailable, "wsl.exe was not found, so WSL is not installed on this machine");
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

    private static string DeclaredName(IEnumerable<string> names, string typed)
        => names.First(name => string.Equals(name, typed, StringComparison.OrdinalIgnoreCase));

    private static string Declared(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count == 0 ? "; none are declared" : $"; declared: {string.Join(", ", list)}";
    }
}
