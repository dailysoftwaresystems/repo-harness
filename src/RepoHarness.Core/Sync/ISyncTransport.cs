using RepoHarness.Core.Hosts;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Sync;

/// <summary>
/// Reaching the tree a sync writes into, wherever it is.
/// </summary>
/// <remarks>
/// One interface for this machine, a WSL distribution and an ssh host, so a sync to a host and a
/// sync to a directory here run the same code and cannot drift apart. Every path a caller passes is
/// relative to the tree root and spelled with forward separators; the transport joins it to whatever
/// the far side calls that root.
/// </remarks>
public interface ISyncTransport
{
    /// <summary>The host this transport reaches, as reports name it.</summary>
    HostId Host { get; }

    /// <summary>Whether the copy's root directory exists.</summary>
    /// <param name="root">The copy's root, as the far side spells it.</param>
    /// <param name="cancellationToken">Stops the check.</param>
    Task<bool> RootExistsAsync(string root, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the copy's root and every missing parent, and records that the harness made it.
    /// </summary>
    /// <param name="root">The copy's root.</param>
    /// <param name="mark">
    /// What to record about how this copy came to be. A takeover is marked before it starts and
    /// again when it finishes, because the two are indistinguishable afterwards and one of them
    /// deleted files that were already there.
    /// </param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <exception cref="Results.HarnessException">
    /// The path exists and is not a directory, or it could not be created. Never a silent fallback
    /// to somewhere else: a sync that writes to a directory nobody named is worse than one that
    /// refuses, because the leg's verdict then describes a tree the reader cannot find.
    /// </exception>
    Task CreateRootAsync(string root, CopyMark mark = CopyMark.Complete, CancellationToken cancellationToken = default);

    /// <summary>
    /// What the harness has recorded about this copy. A directory it did not make is never taken
    /// over on its own, because sync deletes whatever the source does not have and a checkout
    /// somebody made by hand holds work nobody told the harness about.
    /// </summary>
    /// <param name="root">The copy's root.</param>
    /// <param name="cancellationToken">Stops the check.</param>
    Task<CopyMark> ReadMarkAsync(string root, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the copy's root is there and what the harness has recorded about it, in one answer.
    /// </summary>
    /// <param name="root">The copy's root.</param>
    /// <param name="cancellationToken">Stops the question.</param>
    /// <remarks>
    /// Asked as one question rather than as <see cref="RootExistsAsync"/> and then
    /// <see cref="ReadMarkAsync"/>. Over ssh those are two round trips for something the far side
    /// answers in one, and between them the directory can change — so the mark that decides whether
    /// a sync may delete could describe a directory other than the one found.
    /// </remarks>
    Task<SyncInspectAnswer> InspectAsync(string root, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes the copy a git repository, because the DssHarness on that host finds everything through
    /// git. Does nothing when it already is the top of one of its own; a copy inside another
    /// repository's work tree is given one of its own.
    /// </summary>
    /// <param name="root">The copy's root.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    Task InitialiseRepositoryAsync(string root, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes the copy's git index hold exactly <paramref name="paths"/>: the files the sync placed there, so
    /// that what the harness there reads as "the files git tracks" is what the copy was given.
    /// </summary>
    /// <param name="root">The copy's root.</param>
    /// <param name="paths">Every file the copy holds from the sync, relative to its root, with forward separators.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    /// <remarks>
    /// A copy is a git repository the sync made, and the sync wrote its files without staging one, so its
    /// index named nothing. Everything that reads the tracked files there read none: a build fingerprinted
    /// no inputs, so each after the first started from clean, and every guard that watches the inputs
    /// watched nothing - silently. Asked on every sync, rather than only of a new copy, so a copy made
    /// before this is put right by the next sync, whatever that sync carries.
    /// </remarks>
    Task IndexAsync(string root, IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    /// <summary>Reads what the copy currently holds, by content.</summary>
    /// <param name="root">The copy's root.</param>
    /// <param name="withheld">Paths the manifest must not walk, so the copy's own state is never listed.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    Task<SyncManifest> ReadManifestAsync(
        string root,
        IReadOnlyList<string> withheld,
        CancellationToken cancellationToken = default);

    /// <summary>Writes one file into the copy, creating any missing directory above it.</summary>
    /// <param name="root">The copy's root.</param>
    /// <param name="relativePath">Where the file goes, relative to the root.</param>
    /// <param name="contents">Its bytes.</param>
    /// <param name="cancellationToken">Stops the write.</param>
    Task WriteFileAsync(
        string root,
        string relativePath,
        byte[] contents,
        CancellationToken cancellationToken = default);

    /// <summary>Writes several files into the copy, as one exchange with the far side.</summary>
    /// <param name="root">The copy's root, as the far side spells it.</param>
    /// <param name="files">The files, each with its path relative to the root and its bytes.</param>
    /// <param name="cancellationToken">Stops the write.</param>
    /// <remarks>
    /// Over a connection, one exchange is one session, and a session costs far more than the bytes it
    /// carries. The files are written in the order given, so a failure leaves the copy in a state the
    /// caller can reason about: everything before the file named is there, and nothing after it is.
    /// </remarks>
    Task WriteFilesAsync(
        string root,
        IReadOnlyList<SyncFileContent> files,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes one file from the copy.</summary>
    /// <param name="root">The copy's root.</param>
    /// <param name="relativePath">The file to delete, relative to the root.</param>
    /// <param name="cancellationToken">Stops the deletion.</param>
    Task DeleteFileAsync(string root, string relativePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes each of <paramref name="directories"/> that is now empty, and every parent of one
    /// that empties with it, and answers with what went and what stayed.
    /// </summary>
    /// <param name="root">The copy's root, which is never removed.</param>
    /// <param name="directories">Directories to consider, relative to the root.</param>
    /// <param name="cancellationToken">Stops the removal.</param>
    /// <remarks>
    /// A manifest holds files, so a plan can delete every file a directory had and never mention
    /// the directory. Locally that is invisible — <c>git rm</c> takes the directory with the last
    /// file — and it shows up only on a host, where a directory that a wave emptied stays behind
    /// and whatever reads the tree next finds a directory with nothing addressable in it.
    /// <para>
    /// Emptiness is decided HERE, on the side that holds the directory, by looking at what is
    /// actually in it. Deciding it from the manifest would be wrong in two ways that both cost
    /// somebody their files: a manifest lists no ignored file, so a directory holding what
    /// <c>sync.neverTransfer</c> protects would read as empty, and it lists no link, so a directory
    /// holding only a link would read as empty while holding the one thing no plan can speak for.
    /// </para>
    /// <para>
    /// A directory that stayed is answered for too, with what was still in it. A sync's contract is
    /// that the host's checkout matches this tree, and a directory the plan emptied which survives
    /// because protected content remains is a divergence from that — one that arrives later as a
    /// structural check failing on a host, with nothing connecting it to the sync that caused it.
    /// Named here it is visible when it happens, to somebody who can act on it.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<EmptiedDirectory>> RemoveEmptyDirectoriesAsync(
        string root,
        IReadOnlyList<string> directories,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the whole copy at <paramref name="root"/>, where the harness made it. One it took over was
    /// somebody's directory before it was a copy, and one holding no mark of the harness's is nothing it
    /// knows it may remove: each is left where it is, and answered for.
    /// </summary>
    /// <param name="root">The copy's root.</param>
    /// <param name="cancellationToken">Stops the removal.</param>
    /// <exception cref="HarnessException">
    /// The copy could not be removed whole, <see cref="HarnessExit.CommandFailed"/>; or its marker cannot be read,
    /// refused.
    /// </exception>
    Task<CopyRemoval> RemoveCopyAsync(string root, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one file out of the copy: one a sync or a carry sends on, one it checks after writing, or a
    /// leg's output brought home.
    /// </summary>
    /// <param name="root">The copy's root.</param>
    /// <param name="relativePath">The file to read, relative to the root.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <exception cref="HarnessException">
    /// No file is at the path - nothing at it, nothing along it, or a directory - said by name as a transfer
    /// that failed, <see cref="HarnessExit.CommandFailed"/>; or the path leaves the copy, refused.
    /// </exception>
    Task<byte[]> ReadFileAsync(string root, string relativePath, CancellationToken cancellationToken = default);
}

/// <summary>One file on its way into a copy: where it goes, and what it holds.</summary>
/// <param name="Path">Where it goes, relative to the copy's root.</param>
/// <param name="Contents">Its bytes.</param>
public sealed record SyncFileContent(string Path, byte[] Contents);
