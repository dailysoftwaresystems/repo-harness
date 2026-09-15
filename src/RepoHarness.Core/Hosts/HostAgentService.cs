using System.Text.Json;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Hosts;

/// <summary>
/// Serves what another repo-harness asks of this machine: which build this is and what the machine
/// is, or to run one of this build's own commands in a directory here. Reached only through
/// <see cref="HostAgentProtocol.CommandName"/>, with the request on standard input.
/// </summary>
public sealed class HostAgentService(
    IHostPlatform platform,
    IToolIdentityProvider identity,
    EmulatorProbe emulatorProbe,
    IFileSystem fileSystem)
{
    /// <summary>
    /// Commands a run request cannot name. A host that passed the work on to another host would
    /// leave the machine that asked unable to say where anything ran.
    /// </summary>
    private static readonly string[] NotForwardable = [HostAgentProtocol.CommandName, HostExecService.CommandName];

    private readonly IHostPlatform _platform = platform;
    private readonly IToolIdentityProvider _identity = identity;
    private readonly EmulatorProbe _emulatorProbe = emulatorProbe;
    private readonly IFileSystem _fileSystem = fileSystem;

    /// <summary>Reads one request from <paramref name="input"/> and serves it.</summary>
    /// <param name="input">Where the request is read from.</param>
    /// <param name="output">Where an info answer is written.</param>
    /// <param name="error">Where a refused request is explained.</param>
    /// <param name="run">
    /// Runs a repo-harness command line with the given directory as the current one, returning its exit
    /// code. Supplied by the program, which is the only place that holds the command line parser.
    /// </param>
    /// <param name="cancellationToken">Stops serving.</param>
    /// <returns>The exit code this process reports.</returns>
    public async Task<int> ServeAsync(
        TextReader input,
        TextWriter output,
        TextWriter error,
        Func<string, string[], Task<int>> run,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(run);

        HostAgentRequest? request;

        try
        {
            var text = await input.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            request = JsonSerializer.Deserialize<HostAgentRequest>(text, HostAgentProtocol.JsonOptions);
        }
        catch (JsonException ex)
        {
            return await RefuseAsync(error, HarnessExit.UsageError, $"the request is not valid: {ex.Message}").ConfigureAwait(false);
        }

        if (request is null)
        {
            return await RefuseAsync(error, HarnessExit.UsageError, "the request is empty").ConfigureAwait(false);
        }

        if (request.Protocol != HostAgentProtocol.Version)
        {
            return await RefuseAsync(
                error,
                HarnessExit.UsageError,
                $"the request speaks protocol {request.Protocol}, and this build speaks {HostAgentProtocol.Version}").ConfigureAwait(false);
        }

        if (request.Kind == HostAgentRequestKind.Info)
        {
            var info = await DescribeAsync(request.Emulators, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync(JsonSerializer.Serialize(info, HostAgentProtocol.JsonOptions)).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            return HarnessExit.Success;
        }

        return await RunAsync(request, error, run).ConfigureAwait(false);
    }

    /// <summary>Which build this is, what this machine is, and which of <paramref name="emulators"/> work here.</summary>
    public async Task<HostAgentInfo> DescribeAsync(
        IReadOnlyDictionary<string, EmulatorConfig> emulators,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(emulators);

        var checks = new Dictionary<string, EmulatorCheck>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, emulator) in emulators)
        {
            checks[name] = await _emulatorProbe.CheckAsync(emulator, cancellationToken).ConfigureAwait(false);
        }

        var current = _identity.Current;

        return new HostAgentInfo
        {
            Version = current.Version,
            AssemblySha256 = current.AssemblySha256,
            Os = _platform.PlatformKey,
            Processor = _platform.Processor,
            Emulators = checks,
        };
    }

    /// <summary>Expands a leading <c>~</c> to this user's home directory, the way host configuration writes a path.</summary>
    public string ResolveDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (directory == "~")
        {
            return _platform.HomeDirectory;
        }

        return directory.StartsWith("~/", StringComparison.Ordinal)
            ? Path.GetFullPath(Path.Combine(_platform.HomeDirectory, directory[2..]))
            : directory;
    }

    private async Task<int> RunAsync(HostAgentRequest request, TextWriter error, Func<string, string[], Task<int>> run)
    {
        if (request.Arguments.Count == 0)
        {
            return await RefuseAsync(error, HarnessExit.UsageError, "the request names no command to run").ConfigureAwait(false);
        }

        if (NotForwardable.Contains(request.Arguments[0], StringComparer.OrdinalIgnoreCase))
        {
            return await RefuseAsync(
                error,
                HarnessExit.UsageError,
                $"'{request.Arguments[0]}' cannot be run on a host; run it on the machine that reaches the hosts").ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(request.Directory))
        {
            return await RefuseAsync(error, HarnessExit.UsageError, "the request names no directory to run in").ConfigureAwait(false);
        }

        var directory = ResolveDirectory(request.Directory);

        if (!_fileSystem.DirectoryExists(directory))
        {
            return await RefuseAsync(
                error,
                HarnessExit.HostUnavailable,
                $"this host has no copy of the repository at '{directory}'").ConfigureAwait(false);
        }

        return await run(directory, [.. request.Arguments]).ConfigureAwait(false);
    }

    private static async Task<int> RefuseAsync(TextWriter error, int exitCode, string message)
    {
        await error.WriteLineAsync($"{HostAgentProtocol.CommandName}: FAIL - {message}").ConfigureAwait(false);
        return exitCode;
    }
}
