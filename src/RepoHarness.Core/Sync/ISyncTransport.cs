using RepoHarness.Core.Hosts;

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
    /// <param name="cancellationToken">Stops the work.</param>
    /// <exception cref="Results.HarnessException">
    /// The path exists and is not a directory, or it could not be created. Never a silent fallback
    /// to somewhere else: a sync that writes to a directory nobody named is worse than one that
    /// refuses, because the leg's verdict then describes a tree the reader cannot find.
    /// </exception>
    Task CreateRootAsync(string root, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the harness created this copy. A directory it did not create is never adopted,
    /// because sync deletes whatever the source does not have and a checkout someone made by hand
    /// holds work nobody told the harness about.
    /// </summary>
    /// <param name="root">The copy's root.</param>
    /// <param name="cancellationToken">Stops the check.</param>
    Task<bool> IsHarnessCopyAsync(string root, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes the copy a git repository, because the DssHarness on that host finds everything through
    /// git. Does nothing when it already is one.
    /// </summary>
    /// <param name="root">The copy's root.</param>
    /// <param name="cancellationToken">Stops the work.</param>
    Task InitialiseRepositoryAsync(string root, CancellationToken cancellationToken = default);

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

    /// <summary>Deletes one file from the copy.</summary>
    /// <param name="root">The copy's root.</param>
    /// <param name="relativePath">The file to delete, relative to the root.</param>
    /// <param name="cancellationToken">Stops the deletion.</param>
    Task DeleteFileAsync(string root, string relativePath, CancellationToken cancellationToken = default);

    /// <summary>Reads one file out of the copy, for bringing a leg's output home.</summary>
    /// <param name="root">The copy's root.</param>
    /// <param name="relativePath">The file to read, relative to the root.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    Task<byte[]> ReadFileAsync(string root, string relativePath, CancellationToken cancellationToken = default);
}
