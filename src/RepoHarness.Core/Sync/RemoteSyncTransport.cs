using System.Text.Json;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Output;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Sync;

/// <summary>
/// A sync into a WSL distribution's or an ssh host's copy of the repository, served by the DssHarness
/// already installed there.
/// </summary>
/// <remarks>
/// The far side runs the same <see cref="LocalSyncTransport"/> this machine uses for a local copy, so
/// there is one implementation of what a sync does to a tree and no second one to drift from it.
/// Requests travel inside the host agent's own request on standard input, never on a command line: a
/// command line is bounded, and an ssh server hands it to a shell whose identity is not known in
/// advance, so only a small set of characters may appear there at all.
/// </remarks>
public sealed class RemoteSyncTransport(
    HostId host,
    HostSession session,
    IHostCommandRunner hostCommands,
    IHarnessOutput output) : ISyncTransport
{
    private readonly HostSession _session = session;
    private readonly IHostCommandRunner _hostCommands = hostCommands;
    private readonly IHarnessOutput _output = output;

    /// <inheritdoc/>
    public HostId Host { get; } = host;

    /// <inheritdoc/>
    public async Task<bool> RootExistsAsync(string root, CancellationToken cancellationToken = default)
        => (await InspectAsync(root, cancellationToken).ConfigureAwait(false)).Exists;

    /// <inheritdoc/>
    public async Task<bool> IsHarnessCopyAsync(string root, CancellationToken cancellationToken = default)
        => (await InspectAsync(root, cancellationToken).ConfigureAwait(false)).HarnessCopy;

    /// <inheritdoc/>
    public Task CreateRootAsync(string root, bool adopted = false, CancellationToken cancellationToken = default)
        => AskAsync<object>(root, [SyncServe.Create, root, adopted ? SyncServe.Adopted : string.Empty], cancellationToken);

    /// <inheritdoc/>
    public Task InitialiseRepositoryAsync(string root, CancellationToken cancellationToken = default)
        => AskAsync<object>(root, [SyncServe.InitRepository, root], cancellationToken);

    /// <inheritdoc/>
    public async Task<SyncManifest> ReadManifestAsync(
        string root,
        IReadOnlyList<string> withheld,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(withheld);

        var answer = await AskAsync<SyncManifestAnswer>(
                root,
                [SyncServe.Manifest, root, string.Join('\n', withheld)],
                cancellationToken)
            .ConfigureAwait(false);

        return new SyncManifest(
            root,
            (answer?.Entries ?? []).ToDictionary(entry => entry.Path, entry => entry, StringComparer.Ordinal));
    }

    /// <inheritdoc/>
    public Task WriteFileAsync(
        string root,
        string relativePath,
        byte[] contents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contents);

        return AskAsync<object>(
            root,
            [SyncServe.Write, root, relativePath, Convert.ToBase64String(contents)],
            cancellationToken);
    }

    /// <inheritdoc/>
    public Task DeleteFileAsync(string root, string relativePath, CancellationToken cancellationToken = default)
        => AskAsync<object>(root, [SyncServe.Delete, root, relativePath], cancellationToken);

    /// <inheritdoc/>
    public async Task<byte[]> ReadFileAsync(
        string root,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var answer = await AskAsync<SyncFileAnswer>(
                root,
                [SyncServe.Read, root, relativePath],
                cancellationToken)
            .ConfigureAwait(false);

        if (answer is null)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"{Host} did not answer with the content of '{relativePath}'.");
        }

        var contents = Convert.FromBase64String(answer.Content);
        var arrived = FileContentHash.Of(contents);

        // Checked against the hash the far side took of what it read, not against what arrived here.
        // A file that lost bytes on the way is the failure this catches, and it can only be caught by
        // a number that was computed before the journey.
        return string.Equals(arrived, answer.ContentHash, StringComparison.Ordinal)
            ? contents
            : throw new HarnessException(
                HarnessExit.CommandFailed,
                $"'{relativePath}' changed between {Host} and here: it was read as {answer.ContentHash} "
                + $"and arrived as {arrived}.");
    }

    private async Task<SyncInspectAnswer> InspectAsync(string root, CancellationToken cancellationToken)
        => await AskAsync<SyncInspectAnswer>(root, [SyncServe.Inspect, root], cancellationToken).ConfigureAwait(false)
            ?? throw new HarnessException(
                HarnessExit.HostUnavailable,
                $"{Host} did not answer whether '{root}' exists.");

    /// <summary>
    /// Runs one sync operation on the host and reads its answer.
    /// </summary>
    /// <remarks>
    /// The exit code is read from the agent's completion line, never from the transport's own: ssh
    /// exits 255, and wsl.exe with codes of its own, when a connection fails, and neither is the
    /// command's result. A line that never arrives means the operation may not have run, or run only
    /// in part, which is exactly what must not be reported as a success.
    /// </remarks>
    private async Task<T?> AskAsync<T>(string root, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        where T : class
    {
        var nonce = HostAgentProtocol.NewNonce();

        var request = JsonSerializer.Serialize(
            new HostAgentRequest
            {
                Kind = HostAgentRequestKind.Run,

                // The operation names the tree it acts on in its own arguments, and the agent starts
                // in a directory that may not exist yet on a first sync.
                Directory = ParentOf(root),
                Arguments = [SyncServe.CommandName, .. arguments],
                Nonce = nonce,
            },
            HostAgentProtocol.JsonOptions);

        T? answer = null;
        int? finished = null;

        var result = await _hostCommands.RunAsync(
                _session.Connection,
                new HostCommand
                {
                    Program = _session.ToolPath,
                    Arguments = [HostAgentProtocol.CommandName],
                    StandardInput = request + "\n",
                    HoldStandardInputOpen = true,
                    OnOutputLine = line => answer ??= SyncServe.ReadAnswer<T>(line),
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
                cancellationToken)
            .ConfigureAwait(false);

        if (finished is not { } exitCode)
        {
            throw new HarnessException(
                HarnessExit.HostUnavailable,
                $"{Host}: '{arguments[0]}' never reported how it finished, so it may not have run, or run "
                + $"only in part; the connection ended with exit {result.ExitCode}"
                + $"{Detail(result.StandardError)}");
        }

        if (exitCode != HarnessExit.Success)
        {
            throw new HarnessException(
                exitCode,
                $"{Host}: '{arguments[0]}' exited {exitCode}{Detail(result.StandardError)}");
        }

        return answer;
    }

    /// <summary>
    /// The directory the agent starts in. A first sync creates the copy, so the agent cannot start
    /// inside it; its parent is where the operation is served from instead.
    /// </summary>
    private static string ParentOf(string root)
    {
        var trimmed = root.Replace('\\', '/').TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');

        return slash <= 0 ? trimmed : trimmed[..slash];
    }

    private static string Detail(string standardError)
    {
        var said = HostProbes.Excerpt(standardError);

        return said.Length == 0 ? string.Empty : ": " + said;
    }
}
