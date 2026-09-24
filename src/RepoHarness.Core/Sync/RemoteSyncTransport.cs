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
    IHarnessOutput output,
    IReadOnlyList<string>? keepAwake = null) : ISyncTransport
{
    private readonly HostSession _session = session;
    private readonly IHostCommandRunner _hostCommands = hostCommands;
    private readonly IHarnessOutput _output = output;

    /// <summary>
    /// What keeps the host awake while it serves each of this sync's requests, as the configuration
    /// declares it for that host. A sync to a fresh copy is the longest work a host does with no leg of
    /// its own running there, and a leg's own hold was all that ever kept one awake.
    /// </summary>
    private readonly IReadOnlyList<string> _keepAwake = keepAwake ?? [];

    /// <inheritdoc/>
    public HostId Host { get; } = host;

    /// <inheritdoc/>
    public async Task<bool> RootExistsAsync(string root, CancellationToken cancellationToken = default)
        => (await InspectAsync(root, cancellationToken).ConfigureAwait(false)).Exists;

    /// <inheritdoc/>
    public async Task<CopyMark> ReadMarkAsync(string root, CancellationToken cancellationToken = default)
        => (await InspectAsync(root, cancellationToken).ConfigureAwait(false)).Mark;

    /// <inheritdoc/>
    public Task CreateRootAsync(string root, CopyMark mark = CopyMark.Complete, CancellationToken cancellationToken = default)
        => AskAsync<object>(root, [SyncServe.Create, root, mark.ToString()], cancellationToken);

    /// <inheritdoc/>
    public Task InitialiseRepositoryAsync(string root, CancellationToken cancellationToken = default)
        => AskAsync<object>(root, [SyncServe.InitRepository, root], cancellationToken);

    /// <inheritdoc/>
    /// <remarks>
    /// Asked from the home directory, not from the directory the copy is kept in: a removal is asked where that
    /// directory may be gone - a repositoryPath changed since, a distribution reinstalled - and a copy whose
    /// directory is gone is not there, which is the answer. Asked from it, the agent would refuse to start, and the
    /// copy would stay recorded for good.
    /// </remarks>
    public async Task<CopyRemoval> RemoveCopyAsync(string root, CancellationToken cancellationToken = default)
        => (await AskAsync<SyncRemoveAnswer>(root, [SyncServe.RemoveCopy, root], cancellationToken, HomeDirectory).ConfigureAwait(false))?.Removal
            ?? throw new HarnessException(
                HarnessExit.HostUnavailable,
                $"{Host} did not answer whether it removed '{root}'.");

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

        // An answer that never arrived is refused rather than read as an empty copy. Empty is the
        // most dangerous thing this could return: it disables the deletion bound, which measures
        // against what the copy holds, and it makes a refusal report that taking the directory over
        // would remove nothing — advice somebody acts on, after which the manifest read succeeds and
        // everything the host held goes.
        if (answer is null)
        {
            throw new HarnessException(
                HarnessExit.HostUnavailable,
                $"{Host} did not answer with what '{root}' holds, so what a sync would delete there "
                + "is unknown and nothing was changed.");
        }

        return new SyncManifest(
            root,
            answer.Entries.ToDictionary(entry => entry.Path, entry => entry, StringComparer.Ordinal))
        {
            Links = answer.Links,
        };
    }

    /// <inheritdoc/>
    public Task WriteFileAsync(
        string root,
        string relativePath,
        byte[] contents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contents);

        SyncServe.RefuseAFileTooLargeToCarry(contents.LongLength, relativePath, Host.ToString());

        return SendAsync(root, relativePath, contents, cancellationToken);
    }

    /// <summary>Encodes one file and sends it, as its own method so the encoding is inside a try.</summary>
    /// <param name="root">The copy's root.</param>
    /// <param name="relativePath">Where the file goes, relative to the root.</param>
    /// <param name="contents">Its bytes.</param>
    /// <param name="cancellationToken">Stops the write.</param>
    private async Task SendAsync(
        string root,
        string relativePath,
        byte[] contents,
        CancellationToken cancellationToken)
    {
        try
        {
            await AskAsync<object>(
                    root,
                    [SyncServe.Write, root, relativePath, Convert.ToBase64String(contents)],
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OutOfMemoryException)
        {
            // The bound above is what base64 can express; this is the other ceiling, which is
            // whatever this machine had free. Both mean one thing to the reader - the file does not
            // fit through here - and only this one arrives as "the tool has a defect" if it is left
            // alone. Nothing is retried and nothing continues: the exception ends the command.
            throw new HarnessException(
                HarnessExit.CommandFailed,
                SyncServe.TooLargeToCarry(contents.LongLength, relativePath, Host.ToString()));
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// One request, and so one session: the cost of reaching this host is paid once for the batch rather
    /// than once per file. Each file is refused here if it alone is too large to carry, before anything is
    /// encoded, so the reader is told which file rather than left with this machine out of memory.
    /// </remarks>
    public async Task WriteFilesAsync(
        string root,
        IReadOnlyList<SyncFileContent> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);

        if (files.Count == 0)
        {
            return;
        }

        foreach (var file in files)
        {
            SyncServe.RefuseAFileTooLargeToCarry(file.Contents.LongLength, file.Path, Host.ToString());
        }

        try
        {
            var carried = files
                .Select(file => new SyncFileWrite(file.Path, Convert.ToBase64String(file.Contents)))
                .ToList();

            await AskAsync<object>(
                    root,
                    [SyncServe.WriteMany, root, SyncServe.Carry(carried)],
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OutOfMemoryException)
        {
            // As one file's own write reports it: the batch is bounded by what the caller grouped, and
            // this is the other ceiling - whatever this machine had free. Reported as the transfer being
            // too large for this machine rather than as a defect in the tool, which is where an
            // OutOfMemoryException otherwise arrives.
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"{files.Count} files could not be carried to {Host} in one request: this machine ran out "
                + "of memory holding them. Nothing in the batch was written.");
        }
    }

    /// <inheritdoc/>
    public Task DeleteFileAsync(string root, string relativePath, CancellationToken cancellationToken = default)
        => AskAsync<object>(root, [SyncServe.Delete, root, relativePath], cancellationToken);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<EmptiedDirectory>> RemoveEmptyDirectoriesAsync(
        string root,
        IReadOnlyList<string> directories,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directories);

        if (directories.Count == 0)
        {
            return [];
        }

        var answer = await AskAsync<SyncPruneAnswer>(
                root,
                [SyncServe.Prune, root, string.Join('\n', directories)],
                cancellationToken)
            .ConfigureAwait(false);

        // An answer that never arrived is refused rather than read as "nothing was removed": this
        // reports what a sync did to a host, and a report that quietly under-states it is the one
        // thing running the command again cannot put right.
        if (answer is null)
        {
            throw new HarnessException(
                HarnessExit.HostUnavailable,
                $"{Host} did not answer which directories of '{root}' it removed.");
        }

        return answer.Directories;
    }

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

    /// <inheritdoc/>
    public async Task<SyncInspectAnswer> InspectAsync(string root, CancellationToken cancellationToken = default)
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
    /// <param name="root">The copy the operation acts on.</param>
    /// <param name="arguments">The operation and its arguments.</param>
    /// <param name="cancellationToken">Stops the operation here and there.</param>
    /// <param name="startIn">
    /// Where the agent starts, when not in the directory the copy is kept in, which a first sync needs to be there.
    /// </param>
    private async Task<T?> AskAsync<T>(string root, IReadOnlyList<string> arguments, CancellationToken cancellationToken, string? startIn = null)
        where T : class
    {
        var nonce = HostAgentProtocol.NewNonce();

        var request = JsonSerializer.Serialize(
            new HostAgentRequest
            {
                Kind = HostAgentRequestKind.Run,

                // The operation names the tree it acts on in its own arguments, and the agent starts
                // in a directory that may not exist yet on a first sync.
                Directory = startIn ?? ParentOf(root),
                Arguments = [SyncServe.CommandName, .. arguments],
                KeepAwake = [.. _keepAwake],
                Nonce = nonce,
            },
            HostAgentProtocol.JsonOptions);

        T? answer = null;
        int? finished = null;

        // Whether the host's agent has begun answering, on each stream: until it has, that stream carries
        // the host's login shell, which is the host's business and not this sync's output.
        var serving = false;
        var reporting = false;

        var result = await _hostCommands.RunAsync(
                _session.Connection,
                new HostCommand
                {
                    Program = _session.ToolPath,
                    Arguments = [HostAgentProtocol.CommandName],
                    StandardInput = request + "\n",
                    HoldStandardInputOpen = true,
                    OnOutputLine = line =>
                    {
                        if (HostAgentProtocol.IsStartedLine(line, nonce))
                        {
                            reporting = true;
                            return;
                        }

                        // Nothing the host said before its agent began is an answer: a login shell writes to
                        // the same stream, and what it says is the host's business.
                        if (!reporting)
                        {
                            return;
                        }

                        answer ??= SyncServe.ReadAnswer<T>(line);
                    },
                    OnErrorLine = line =>
                    {
                        // Read before the gate, and never behind it: how the operation finished is the one
                        // thing that must survive a host whose profile writes to this stream, because a line
                        // that never arrives is reported as an operation that may have run only in part.
                        if (HostAgentProtocol.TryReadCompletionLine(line, nonce, out var code))
                        {
                            finished = code;
                            return;
                        }

                        if (HostAgentProtocol.IsStartedLine(line, nonce))
                        {
                            serving = true;
                            return;
                        }

                        // Nothing else the host said before its agent began is this sync's output: a login
                        // shell writes to the same stream, and one consumer's printed the account's home
                        // layout on every session, which a relayed line then published. The agent's own lines
                        // pass all the same, because a request refused before it could be read carries no
                        // nonce to mark, and that refusal is the whole of what the reader has to go on.
                        if (!serving && !HostAgentProtocol.IsAgentsOwnLine(line))
                        {
                            return;
                        }

                        // ssh writes here too, and a pinned connection has it name an address this machine
                        // resolved rather than the one the configuration declares.
                        _output.RawError(HostProbes.AsConfigured(line, _session.Connection));
                    },
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (finished is not { } exitCode)
        {
            throw new HarnessException(
                HarnessExit.HostUnavailable,
                $"{Host}: {HostProbes.NeverFinished($"'{arguments[0]}'", result, _session.Connection)}");
        }

        if (exitCode != HarnessExit.Success)
        {
            throw new HarnessException(
                exitCode,
                $"{Host}: '{arguments[0]}' exited {exitCode}{HostProbes.Detail(result.StandardError)}");
        }

        return answer;
    }

    /// <summary>
    /// The directory the agent starts in. A first sync creates the copy, so the agent cannot start
    /// inside it; its parent is where the operation is served from instead.
    /// </summary>
    /// <summary>The home directory, as the agent is asked to start in it.</summary>
    private const string HomeDirectory = "~";

    private static string ParentOf(string root)
    {
        var trimmed = root.Replace('\\', '/').TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');

        return slash <= 0 ? trimmed : trimmed[..slash];
    }
}
