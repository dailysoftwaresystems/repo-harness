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
    private readonly IHostPlatform _platform = platform;
    private readonly IToolIdentityProvider _identity = identity;
    private readonly EmulatorProbe _emulatorProbe = emulatorProbe;
    private readonly IFileSystem _fileSystem = fileSystem;

    /// <summary>Reads one request from <paramref name="input"/> and serves it.</summary>
    /// <param name="input">
    /// Where the request is read from: one line, after which the input stays open while the request is
    /// served. Its end means the machine that asked has gone, and cancels what the request started.
    /// </param>
    /// <param name="output">Where an info answer is written.</param>
    /// <param name="error">Where a refused request is explained, and where a run request's completion line is written.</param>
    /// <param name="run">
    /// Runs a repo-harness command line with the given directory as the current one, until it finishes or the
    /// token is cancelled, and returns its exit code. Supplied by the program, which is the only place that
    /// holds the command line parser.
    /// </param>
    /// <param name="cancellationToken">Stops serving.</param>
    /// <returns>The exit code this process reports.</returns>
    public async Task<int> ServeAsync(
        TextReader input,
        TextWriter output,
        TextWriter error,
        Func<string, string[], CancellationToken, Task<int>> run,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(run);

        var line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(line))
        {
            return await RefuseAsync(error, HarnessExit.UsageError, "the request is empty").ConfigureAwait(false);
        }

        // The protocol is read on its own first, so a request from a build that shapes requests differently is
        // refused as exactly that, rather than as whichever of its fields this build happens not to know.
        if (!TryReadProtocol(line, out var protocol, out var problem))
        {
            return await RefuseAsync(error, HarnessExit.UsageError, $"the request is not valid: {problem}").ConfigureAwait(false);
        }

        if (protocol != HostAgentProtocol.Version)
        {
            return await RefuseAsync(
                error,
                HarnessExit.UsageError,
                $"the request speaks protocol {protocol}, and repo-harness {_identity.Current.Version} on this host speaks {HostAgentProtocol.Version}").ConfigureAwait(false);
        }

        HostAgentRequest? request;

        try
        {
            request = JsonSerializer.Deserialize<HostAgentRequest>(line, HostAgentProtocol.JsonOptions);
        }
        catch (JsonException ex)
        {
            return await RefuseAsync(error, HarnessExit.UsageError, $"the request is not valid: {ex.Message}").ConfigureAwait(false);
        }

        if (request is null)
        {
            return await RefuseAsync(error, HarnessExit.UsageError, "the request is empty").ConfigureAwait(false);
        }

        // The machine that asked holds its end open while the request is served, so the end of the input means
        // it has gone: its ssh or wsl.exe was stopped, or the connection dropped. What the request started is
        // then cancelled, rather than left running where nobody waits for it.
        using var abandoned = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = CancelAtEndOfInputAsync(input, abandoned);

        if (request.Kind == HostAgentRequestKind.Info)
        {
            var info = await DescribeAsync(request.Emulators, abandoned.Token).ConfigureAwait(false);
            await output.WriteLineAsync(JsonSerializer.Serialize(info, HostAgentProtocol.JsonOptions)).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            return HarnessExit.Success;
        }

        return await RunAsync(request, error, run, abandoned.Token).ConfigureAwait(false);
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

    /// <summary>
    /// Serves a run request, then writes its completion line last. The machine that asked reads the command's
    /// exit code from that line, and a line that never arrives tells it the connection failed first.
    /// </summary>
    private async Task<int> RunAsync(
        HostAgentRequest request,
        TextWriter error,
        Func<string, string[], CancellationToken, Task<int>> run,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Nonce))
        {
            return await RefuseAsync(
                error,
                HarnessExit.UsageError,
                "the run request carries no nonce, so how its command finished could not be reported").ConfigureAwait(false);
        }

        var exitCode = await ServeRunAsync(request, error, run, cancellationToken).ConfigureAwait(false);

        await error.WriteLineAsync(HostAgentProtocol.CompletionLine(request.Nonce, exitCode)).ConfigureAwait(false);
        await error.FlushAsync(CancellationToken.None).ConfigureAwait(false);

        return exitCode;
    }

    private async Task<int> ServeRunAsync(
        HostAgentRequest request,
        TextWriter error,
        Func<string, string[], CancellationToken, Task<int>> run,
        CancellationToken cancellationToken)
    {
        if (request.Arguments.Count == 0)
        {
            return await RefuseAsync(error, HarnessExit.UsageError, "the request names no command to run").ConfigureAwait(false);
        }

        if (HostAgentProtocol.IsNotForwardable(request.Arguments[0]))
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

        try
        {
            return await run(directory, [.. request.Arguments], cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // A command reports its own failures as exit codes, so what escapes here is entering the copy: a
            // directory this user may not enter exists all the same.
            return await RefuseAsync(
                error,
                HarnessExit.HostUnavailable,
                $"this host's copy of the repository at '{directory}' could not be entered: {ex.Message}").ConfigureAwait(false);
        }
    }

    /// <summary>Expands a leading <c>~</c> to this user's home directory, the way host configuration writes a path.</summary>
    private string ResolveDirectory(string directory)
    {
        if (directory == "~")
        {
            return _platform.HomeDirectory;
        }

        return directory.StartsWith("~/", StringComparison.Ordinal)
            ? Path.GetFullPath(Path.Combine(_platform.HomeDirectory, directory[2..]))
            : directory;
    }

    /// <summary>Reads the protocol a request speaks, and nothing else of it. A request that names none speaks this build's.</summary>
    private static bool TryReadProtocol(string line, out int protocol, out string problem)
    {
        protocol = HostAgentProtocol.Version;
        problem = string.Empty;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                problem = "it is not a JSON object";
                return false;
            }

            if (root.TryGetProperty("protocol", out var value)
                && (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out protocol)))
            {
                problem = "its protocol is not a whole number";
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            problem = ex.Message;
            return false;
        }
    }

    /// <summary>Cancels <paramref name="abandoned"/> once <paramref name="input"/> ends; anything that follows the request is ignored.</summary>
    private static async Task CancelAtEndOfInputAsync(TextReader input, CancellationTokenSource abandoned)
    {
        var buffer = new char[256];

        try
        {
            while (await input.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Input that can no longer be read has ended as surely as input that was closed.
        }

        try
        {
            await abandoned.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The request was served, and its token disposed, before the input ended.
        }
    }

    private static async Task<int> RefuseAsync(TextWriter error, int exitCode, string message)
    {
        await error.WriteLineAsync($"{HostAgentProtocol.CommandName}: FAIL - {message}").ConfigureAwait(false);
        return exitCode;
    }
}
