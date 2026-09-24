using RepoHarness.Core.Hosts;
using RepoHarness.Core.Results;
using RepoHarness.Core.Sync;

namespace RepoHarness.Tests;

/// <summary>
/// Everything the transport it wraps does, counting what it was asked and holding what it was
/// given. A real <see cref="LocalSyncTransport"/> does the work against a directory here, so what
/// a test asserts about a host's copy is what a copy actually became rather than what a stand-in
/// was told to say.
/// </summary>
/// <param name="inner">The transport that does the work.</param>
/// <param name="losesAFileWhenVerifying">
/// Whether to drop a file from the verification's manifest, which is what a copy that did not land
/// intact looks like from here.
/// </param>
/// <param name="reports">
/// The host to answer as, since the local transport is always <c>local</c> and the one thing
/// <c>--adopt</c> decides is which host a directory belongs to.
/// </param>
internal sealed class RecordingTransport(
    LocalSyncTransport inner,
    bool losesAFileWhenVerifying = false,
    HostId? reports = null) : ISyncTransport
{
    private int _manifests;
    private int _writes;

    /// <summary>How many times both halves were asked together.</summary>
    public int Inspections { get; private set; }

    /// <summary>How many times existence was asked on its own.</summary>
    public int RootExistsAsked { get; private set; }

    /// <summary>How many times the mark was asked on its own.</summary>
    public int MarksAsked { get; private set; }

    /// <summary>Every path written, in order, whether or not the write was then undone.</summary>
    public List<string> Written { get; } = [];

    /// <summary>Every path deleted, in order.</summary>
    public List<string> Deleted { get; } = [];

    /// <summary>
    /// What to answer about the copy instead of looking, or <see langword="null"/> to look. A host
    /// whose copy is missing or is a directory the harness never made is a state a test cannot
    /// reach by writing files, because the answer is about a marker that is not there.
    /// </summary>
    public SyncInspectAnswer? Answers { get; init; }

    /// <summary>
    /// Which write to fail, counting from one, or zero to fail none. What a link that dropped part
    /// way through a transfer looks like from here.
    /// </summary>
    public int FailsWrite { get; init; }

    /// <summary>Whether a deletion should fail, for the cleanup that could not finish either.</summary>
    public bool RefusesToDelete { get; init; }

    public HostId Host => reports ?? inner.Host;

    public Task<bool> RootExistsAsync(string root, CancellationToken cancellationToken = default)
    {
        RootExistsAsked++;
        return Answers is { } answer ? Task.FromResult(answer.Exists) : inner.RootExistsAsync(root, cancellationToken);
    }

    public Task<SyncInspectAnswer> InspectAsync(string root, CancellationToken cancellationToken = default)
    {
        Inspections++;
        return Answers is { } answer ? Task.FromResult(answer) : inner.InspectAsync(root, cancellationToken);
    }

    public Task<CopyMark> ReadMarkAsync(string root, CancellationToken cancellationToken = default)
    {
        MarksAsked++;
        return Answers is { } answer ? Task.FromResult(answer.Mark) : inner.ReadMarkAsync(root, cancellationToken);
    }

    public Task CreateRootAsync(string root, CopyMark mark = CopyMark.Complete, CancellationToken cancellationToken = default)
        => inner.CreateRootAsync(root, mark, cancellationToken);

    public Task InitialiseRepositoryAsync(string root, CancellationToken cancellationToken = default)
        => inner.InitialiseRepositoryAsync(root, cancellationToken);

    public async Task<SyncManifest> ReadManifestAsync(
        string root,
        IReadOnlyList<string> withheld,
        CancellationToken cancellationToken = default)
    {
        var manifest = await inner.ReadManifestAsync(root, withheld, cancellationToken).ConfigureAwait(false);

        // The first read builds the plan; the second is the verification.
        if (!losesAFileWhenVerifying || ++_manifests < 2 || manifest.Entries.Count == 0)
        {
            return manifest;
        }

        var short_ = manifest.Entries
            .Where(entry => entry.Key != manifest.Paths.First())
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

        return new SyncManifest(root, short_) { Links = manifest.Links };
    }

    public async Task WriteFileAsync(string root, string relativePath, byte[] contents, CancellationToken cancellationToken = default)
    {
        if (++_writes == FailsWrite)
        {
            throw new HarnessException(HarnessExit.HostUnavailable, $"the link dropped writing '{relativePath}'");
        }

        await inner.WriteFileAsync(root, relativePath, contents, cancellationToken).ConfigureAwait(false);
        Written.Add(relativePath);
    }

    public async Task DeleteFileAsync(string root, string relativePath, CancellationToken cancellationToken = default)
    {
        if (RefusesToDelete)
        {
            throw new HarnessException(HarnessExit.HostUnavailable, $"'{relativePath}' could not be removed");
        }

        await inner.DeleteFileAsync(root, relativePath, cancellationToken).ConfigureAwait(false);
        Deleted.Add(relativePath);
    }

    public Task<IReadOnlyList<EmptiedDirectory>> RemoveEmptyDirectoriesAsync(
        string root,
        IReadOnlyList<string> directories,
        CancellationToken cancellationToken = default)
        => inner.RemoveEmptyDirectoriesAsync(root, directories, cancellationToken);

    public Task<byte[]> ReadFileAsync(string root, string relativePath, CancellationToken cancellationToken = default)
        => inner.ReadFileAsync(root, relativePath, cancellationToken);

    public Task<CopyRemoval> RemoveCopyAsync(string root, CancellationToken cancellationToken = default)
        => inner.RemoveCopyAsync(root, cancellationToken);
}
